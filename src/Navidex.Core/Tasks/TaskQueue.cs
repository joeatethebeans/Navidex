using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Core.Data;

namespace Navidex.Core.Tasks;

/// <summary>A kind of background work.</summary>
public interface ITaskHandler
{
    string Kind { get; }

    Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct);
}

/// <summary>Queues internal work in the database, so it survives restarts, and wakes the worker.</summary>
public sealed class TaskQueue(NavidexDbContext db, TaskSignal signal)
{
    /// <summary>Queues a task, or returns the queued or running one of the same kind and payload.</summary>
    public async Task<BackgroundTask> EnqueueAsync(string kind, string? payload, CancellationToken ct)
    {
        var existing = await db.BackgroundTasks
            .Where(t => t.Kind == kind && t.PayloadJson == payload && (t.Status == BackgroundTaskStatus.Queued || t.Status == BackgroundTaskStatus.Running))
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return existing;

        var task = new BackgroundTask { Kind = kind, PayloadJson = payload };
        db.BackgroundTasks.Add(task);
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return task;
    }

    public static TaskInfo ToInfo(BackgroundTask t) => new(t.Id, t.Kind, (TaskState)(int)t.Status, t.Progress, t.Error, t.CreatedAt, t.StartedAt, t.FinishedAt);
}

public sealed class TaskSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Wake() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Runs queued tasks one at a time, oldest first. Tasks interrupted by a restart run again.</summary>
public sealed class TaskWorker(IServiceScopeFactory scopes, TaskSignal signal, ILogger<TaskWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
            await db.BackgroundTasks.Where(t => t.Status == BackgroundTaskStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, BackgroundTaskStatus.Queued), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await RunNextAsync(stoppingToken))
                await signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task<bool> RunNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
        var task = await db.BackgroundTasks.Where(t => t.Status == BackgroundTaskStatus.Queued).OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        if (task is null)
            return false;

        var handler = scope.ServiceProvider.GetServices<ITaskHandler>().FirstOrDefault(h => h.Kind == task.Kind);
        task.Status = BackgroundTaskStatus.Running;
        task.StartedAt = DateTime.UtcNow;
        task.Error = null;
        await db.SaveChangesAsync(ct);

        var progress = new TaskProgress(scopes, task.Id);
        try
        {
            if (handler is null)
                throw new InvalidOperationException($"No handler for task kind {task.Kind}.");
            logger.LogInformation("Started {Kind}", task.Kind);
            await handler.RunAsync(task, progress, ct);
            await progress.FlushAsync();
            await FinishAsync(task.Id, BackgroundTaskStatus.Succeeded, null);
            logger.LogInformation("Finished {Kind}: {Summary}", task.Kind, progress.Latest ?? "done");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the task stays Running and is re-queued on the next start.
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Kind} failed: {Reason}", task.Kind, e.Message);
            await FinishAsync(task.Id, BackgroundTaskStatus.Failed, e.Message);
        }
        return true;
    }

    private async Task FinishAsync(Guid id, BackgroundTaskStatus status, string? error)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
        await db.BackgroundTasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, status)
            .SetProperty(t => t.Error, error)
            .SetProperty(t => t.FinishedAt, DateTime.UtcNow));
    }

    /// <summary>Writes progress to the task row from its own scope, at most once a second.</summary>
    private sealed class TaskProgress(IServiceScopeFactory scopes, Guid taskId) : IProgress<string>
    {
        private string? _latest;
        private DateTime _lastWrite = DateTime.MinValue;

        public string? Latest => _latest;

        public void Report(string value)
        {
            _latest = value;
            if (DateTime.UtcNow - _lastWrite > TimeSpan.FromSeconds(1))
                FlushAsync().GetAwaiter().GetResult();
        }

        public async Task FlushAsync()
        {
            if (_latest is null)
                return;
            _lastWrite = DateTime.UtcNow;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
            var value = _latest;
            await db.BackgroundTasks.Where(t => t.Id == taskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Progress, value));
        }
    }
}
