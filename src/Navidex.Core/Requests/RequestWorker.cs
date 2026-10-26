using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Navidex.Contracts.Plugins.Index;
using Navidex.Contracts.Plugins.Retrieval;
using Navidex.Core.Data;
using Navidex.Core.Library;
using Navidex.Core.Plugins;

namespace Navidex.Core.Requests;

/// <summary>
/// Works through queued requests, oldest first, a few at a time (Settings: parallel fetches). Planning splits artist
/// and album requests into parts, which join the queue; everything else goes through the pipeline. Requests a restart
/// interrupted start again from the top. Nothing starts while a scan is reading the library.
/// </summary>
public sealed class RequestWorker(IServiceScopeFactory scopes, RequestSignal signal, RequestCancellations cancellations, ILogger<RequestWorker> logger)
    : BackgroundService
{
    private static readonly RequestStatus[] InFlight =
        [RequestStatus.Searching, RequestStatus.Fetching, RequestStatus.Verifying, RequestStatus.Importing, RequestStatus.Notifying];

    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var done in _running.Where(r => r.Value.IsCompleted).Select(r => r.Key).ToList())
                _running.TryRemove(done, out _);

            int parallel;
            List<Guid> next;
            using (var scope = scopes.CreateScope())
            {
                parallel = Math.Clamp((await scope.ServiceProvider.GetRequiredService<SettingsService>().GetFetchAsync(stoppingToken)).ParallelFetches, 1, 8);
                var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
                var busy = _running.Keys.ToList();
                // While a scan reads the library, queued requests wait: the scan may find they're already there.
                next = await scope.ServiceProvider.GetRequiredService<LibraryReading>().IsBusyAsync(db, stoppingToken) ? [] : await db.Requests.Where(r => r.Status == RequestStatus.Queued && !busy.Contains(r.Id))
                    .OrderBy(r => r.CreatedAt).Select(r => r.Id).Take(Math.Max(0, parallel - _running.Count)).ToListAsync(stoppingToken);
            }

            foreach (var id in next)
                _running[id] = Task.Run(() => RunAsync(id, stoppingToken), CancellationToken.None);

            // Wake on new work, or when a slot frees up.
            var wait = signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            if (_running.IsEmpty)
                await wait;
            else
                await Task.WhenAny(wait, Task.WhenAny(_running.Values));
        }
        await Task.WhenAll(_running.Values);
    }

    private async Task RunAsync(Guid id, CancellationToken stopping)
    {
        using var cts = cancellations.Register(id, stopping);
        try
        {
            using var scope = scopes.CreateScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<NavidexDbContext>();
            var request = await db.Requests.FindAsync([id], cts.Token);
            if (request is null || request.Status != RequestStatus.Queued)
                return;

            var albumFetch = (await services.GetRequiredService<RetrievalPlugins>().GetChainAsync(cts.Token))
                .Any(p => p.Capabilities.Contains(RetrievalCapabilities.FetchAlbum));
            var plan = await services.GetRequiredService<RequestPlanner>().PlanAsync(request, albumFetch, cts.Token);
            var requests = services.GetRequiredService<RequestService>();
            if (plan.Done is not null)
            {
                await SetAsync(db, request, RequestStatus.InLibrary, plan.Done, cts.Token);
                await requests.UpdateAncestorsAsync(request.ParentRequestId, cts.Token);
                return;
            }
            if (!plan.Fetch)
            {
                db.Requests.AddRange(plan.Children);
                request.Status = RequestStatus.InProgress;
                request.Detail = $"0 of {plan.Children.Count} in the library";
                request.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cts.Token);
                logger.LogInformation("[{Request}] Split into {Count} parts", request.Title, plan.Children.Count);
                signal.Wake();
                return;
            }
            await db.SaveChangesAsync(cts.Token); // the planner may have settled the edition
            await services.GetRequiredService<RequestPipeline>().RunAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled by the user (already marked), or shutting down (picked up again on start).
        }
        catch (IndexUnavailableException e)
        {
            await FailAsync(id, $"MusicBrainz is busy; retry in a while ({e.Message})", queueAgain: false);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Request {Id} failed: {Reason}", id, e.Message);
            await FailAsync(id, e.Message, queueAgain: false);
        }
        finally
        {
            cancellations.Unregister(id);
            signal.Wake();
        }
    }

    private async Task FailAsync(Guid id, string reason, bool queueAgain)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
        var request = await db.Requests.FindAsync(id);
        if (request is null || request.Status == RequestStatus.Cancelled)
            return;
        await SetAsync(db, request, queueAgain ? RequestStatus.Queued : RequestStatus.Failed, reason, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<RequestService>().UpdateAncestorsAsync(request.ParentRequestId, CancellationToken.None);
    }

    private static async Task SetAsync(NavidexDbContext db, Request request, RequestStatus status, string? detail, CancellationToken ct)
    {
        request.Status = status;
        request.Detail = detail;
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A restart interrupts whatever was in flight: those requests start again, and their jobs are closed.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
        var jobs = await db.Jobs.Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed).SetProperty(j => j.FinishedAt, DateTime.UtcNow)
                .SetProperty(j => j.Log, j => j.Log + "Interrupted by a restart\n"), ct);
        var requests = await db.Requests.Where(r => InFlight.Contains(r.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Queued).SetProperty(r => r.Detail, "Starting again after a restart"), ct);
        if (requests > 0)
            logger.LogInformation("Picking up {Count} requests a restart interrupted ({Jobs} fetches start again)", requests, jobs);
    }
}
