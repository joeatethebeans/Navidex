using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Listening;
using Melodex.Core.Requests;
using Melodex.Core.Tasks;

namespace Melodex.Core.Events;

/// <summary>
/// Turns changes into events: a request finishing, failing or needing review, a listen-now track becoming playable,
/// a scan finishing. It compares each request's status with the last one announced, rather than being told by the
/// code that changes it, so nothing is missed, even across a restart. Each event goes to the event stream and, as a
/// delivery row saved with the announcement, to every webhook that wants it.
/// </summary>
public sealed class EventAnnouncer(IServiceScopeFactory scopes, EventFeed feed, WebhookSender sender, ILogger<EventAnnouncer> log)
    : BackgroundService
{
    /// <summary>How often to look even without a change being seen, as a backstop.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        feed.Raised += OnRaised;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    while (await AnnounceAsync(stoppingToken))
                    {
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    log.LogError(e, "Couldn't announce events");
                }
                await _wake.WaitAsync(Every, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
        finally
        {
            feed.Raised -= OnRaised;
        }
    }

    private void OnRaised(string kind)
    {
        if (kind is not (EventFeed.Requests or EventFeed.Tasks))
            return;
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    private const int Batch = 100;

    /// <summary>Announces what changed since last time. True if there may be more.</summary>
    public async Task<bool> AnnounceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var requests = scope.ServiceProvider.GetRequiredService<RequestService>();
        var listening = scope.ServiceProvider.GetRequiredService<ListenNowService>();
        var events = new List<MelodexEvent>();

        var changed = await db.Requests.Where(r => r.AnnouncedStatus != null && r.Status != r.AnnouncedStatus)
            .OrderBy(r => r.UpdatedAt).Take(Batch).ToListAsync(ct);
        foreach (var request in changed)
        {
            // Parts of an artist or album request are announced through the request itself.
            if (request.ParentRequestId is null)
            {
                var type = request.Status switch
                {
                    RequestStatus.InLibrary => EventTypes.RequestCompleted,
                    RequestStatus.Failed => EventTypes.RequestFailed,
                    RequestStatus.NeedsReview => EventTypes.RequestNeedsReview,
                    RequestStatus.Playable => EventTypes.ListenReady,
                    _ => null,
                };
                if (type == EventTypes.ListenReady)
                {
                    if (await db.ListenNowItems.FirstOrDefaultAsync(i => i.RequestId == request.Id, ct) is { } item)
                        events.Add(New(type, request.UpdatedAt, new ListenEventData((await listening.ToInfoAsync([item], ct))[0], request.Requester)));
                }
                else if (type is not null)
                    events.Add(New(type, request.UpdatedAt, new RequestEventData(await requests.ToInfoAsync(request, ct), request.Requester)));
            }
            request.AnnouncedStatus = request.Status;
        }

        var tasks = await db.BackgroundTasks
            .Where(t => !t.Announced && (t.Status == BackgroundTaskStatus.Succeeded || t.Status == BackgroundTaskStatus.Failed))
            .OrderBy(t => t.FinishedAt).Take(Batch).ToListAsync(ct);
        foreach (var task in tasks)
        {
            if (task.Kind == LibraryScanTask.TaskKind)
                events.Add(New(task.Status == BackgroundTaskStatus.Succeeded ? EventTypes.ScanCompleted : EventTypes.ScanFailed,
                    task.FinishedAt ?? DateTime.UtcNow, new ScanEventData(TaskQueue.ToInfo(task))));
            task.Announced = true;
        }

        if (changed.Count == 0 && tasks.Count == 0)
            return false;

        var webhooks = events.Count == 0 ? [] : await db.Webhooks.Where(w => w.Enabled).ToListAsync(ct);
        foreach (var @event in events)
        foreach (var webhook in webhooks.Where(w => Wants(w, @event.Type)))
            db.WebhookDeliveries.Add(Delivery(webhook, @event));
        await db.SaveChangesAsync(ct);

        foreach (var @event in events)
            feed.Publish(@event);
        if (webhooks.Count > 0)
            sender.Wake();
        return changed.Count == Batch || tasks.Count == Batch;
    }

    public static MelodexEvent New<T>(string type, DateTime occurredAt, T data) =>
        new(Guid.CreateVersion7(), type, DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc), JsonSerializer.SerializeToElement(data, MelodexJson.Options));

    public static WebhookDelivery Delivery(Webhook webhook, MelodexEvent @event) => new()
    {
        WebhookId = webhook.Id,
        Webhook = webhook,
        Event = @event.Type,
        PayloadJson = JsonSerializer.Serialize(@event, MelodexJson.Options),
    };

    public static bool Wants(Webhook webhook, string type) =>
        webhook.Events.Length == 0 || webhook.Events.Split(',').Contains(type, StringComparer.Ordinal);
}
