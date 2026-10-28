using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Core.Data;
using Navidex.Core.Diagnostics;

namespace Navidex.Core.Tasks;

/// <summary>A kind of background work.</summary>
public interface ITaskHandler
{
    string Kind { get; }

    Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct);
}

/// <summary>Queues internal work in the database, so it survives restarts, and wakes the worker.</summary>
public sealed class TaskQueue(NavidexDbContext db, TaskSignal signal, TaskCancellations cancellations)
{
    /// <summary>
    /// Queues a task, or returns the queued or running one of the same kind and payload. Asking again for a paused
    /// one resumes it.
    /// </summary>
    public async Task<BackgroundTask> EnqueueAsync(string kind, string? payload, CancellationToken ct)
    {
        var existing = await db.BackgroundTasks
            .Where(t => t.Kind == kind && t.PayloadJson == payload && (t.Status == BackgroundTaskStatus.Queued || t.Status == BackgroundTaskStatus.Running))
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (existing.Paused)
                await ResumeAsync(existing, ct);
            return existing;
        }

        var task = new BackgroundTask { Kind = kind, PayloadJson = payload };
        db.BackgroundTasks.Add(task);
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return task;
    }

    /// <summary>Stops a task for good. False if it already finished.</summary>
    public async Task<bool> CancelAsync(BackgroundTask task, CancellationToken ct)
    {
        if (task.Status is not (BackgroundTaskStatus.Queued or BackgroundTaskStatus.Running))
            return false;
        task.Status = BackgroundTaskStatus.Cancelled;
        task.Paused = false;
        task.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        cancellations.Cancel(task.Id);
        signal.Wake();
        return true;
    }

    /// <summary>
    /// Holds a task back. One that's running stops and goes back in the queue: a scan carries on from where it was
    /// when resumed, as files already read and matched are skipped; shorter tasks start again.
    /// </summary>
    public async Task<bool> PauseAsync(BackgroundTask task, CancellationToken ct)
    {
        if (task.Status is not (BackgroundTaskStatus.Queued or BackgroundTaskStatus.Running))
            return false;
        task.Paused = true;
        task.Status = BackgroundTaskStatus.Queued;
        await db.SaveChangesAsync(ct);
        cancellations.Cancel(task.Id);
        signal.Wake();
        return true;
    }

    public async Task<bool> ResumeAsync(BackgroundTask task, CancellationToken ct)
    {
        if (!task.Paused)
            return false;
        task.Paused = false;
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return true;
    }

    /// <summary>Moves a queued task to just before another, or to the end when there's none.</summary>
    public async Task<bool> MoveAsync(BackgroundTask task, Guid? before, CancellationToken ct)
    {
        if (task.Status != BackgroundTaskStatus.Queued)
            return false;
        var queue = await db.BackgroundTasks.Where(t => t.Status == BackgroundTaskStatus.Queued && t.Id != task.Id)
            .OrderBy(t => t.QueueOrder).ThenBy(t => t.CreatedAt).Select(t => new { t.Id, t.QueueOrder }).ToListAsync(ct);
        var index = before is { } id ? queue.FindIndex(t => t.Id == id) : queue.Count;
        if (index < 0)
            return false;
        task.QueueOrder = QueueOrders.Between(index > 0 ? queue[index - 1].QueueOrder : null, index < queue.Count ? queue[index].QueueOrder : null);
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return true;
    }

    public static TaskInfo ToInfo(BackgroundTask t) =>
        new(t.Id, t.Kind, (TaskState)(int)t.Status, t.Progress, t.Error, t.CreatedAt, t.StartedAt, t.FinishedAt, Paused: t.Paused, QueueOrder: t.QueueOrder);
}

/// <summary>Stops a running task when it's cancelled or paused.</summary>
public sealed class TaskCancellations
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public CancellationTokenSource Register(Guid taskId, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[taskId] = cts;
        return cts;
    }

    public void Unregister(Guid taskId)
    {
        if (_running.TryRemove(taskId, out var cts))
            cts.Dispose();
    }

    public void Cancel(Guid taskId)
    {
        if (_running.TryGetValue(taskId, out var cts))
            cts.Cancel();
    }
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

/// <summary>
/// Runs queued tasks one at a time, in queue order (oldest first unless moved), skipping paused ones. Tasks
/// interrupted by a restart run again.
/// </summary>
public sealed class TaskWorker(IServiceScopeFactory scopes, TaskSignal signal, TaskCancellations cancellations, ILogger<TaskWorker> logger) : BackgroundService
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
        var task = await db.BackgroundTasks.Where(t => t.Status == BackgroundTaskStatus.Queued && !t.Paused)
            .OrderBy(t => t.QueueOrder).ThenBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        if (task is null)
            return false;

        var handler = scope.ServiceProvider.GetServices<ITaskHandler>().FirstOrDefault(h => h.Kind == task.Kind);
        task.Status = BackgroundTaskStatus.Running;
        task.StartedAt = DateTime.UtcNow;
        task.Error = null;
        await db.SaveChangesAsync(ct);

        var progress = new TaskProgress(scopes, task.Id);
        using var cts = cancellations.Register(task.Id, ct);
        try
        {
            if (handler is null)
                throw new InvalidOperationException($"No handler for task kind {task.Kind}.");
            logger.LogInformation("Started {Kind}", task.Kind);
            await handler.RunAsync(task, progress, cts.Token);
            await progress.FlushAsync();
            await FinishAsync(task.Id, BackgroundTaskStatus.Succeeded, null);
            logger.LogInformation(LogEvents.Success, "Finished {Kind}: {Summary}", task.Kind, progress.Latest ?? "done");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the task stays Running and is re-queued on the next start.
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled or paused, which already said so.
            await progress.FlushAsync();
            logger.LogInformation("Stopped {Kind}", task.Kind);
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Kind} failed: {Reason}", task.Kind, e.Message);
            await FinishAsync(task.Id, BackgroundTaskStatus.Failed, e.Message);
        }
        finally
        {
            cancellations.Unregister(task.Id);
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
