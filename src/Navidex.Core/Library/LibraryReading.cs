using Microsoft.EntityFrameworkCore;
using Navidex.Core.Data;
using Navidex.Core.Requests;

namespace Navidex.Core.Library;

/// <summary>
/// Whether a library scan is reading and matching files. Until it's done Navidex can't tell what you already have,
/// so nothing new can be asked for, and queued requests wait instead of fetching songs the scan would have found.
/// </summary>
public sealed class LibraryReading(RequestSignal signal)
{
    private int _active;

    private void End()
    {
        Interlocked.Decrement(ref _active);
        signal.Wake();
    }

    /// <summary>Marks reading as started; disposing the result marks it done and lets waiting requests go.</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Done(this);
    }

    /// <summary>Reading now, or a scan is queued to start.</summary>
    public async Task<bool> IsBusyAsync(NavidexDbContext db, CancellationToken ct) =>
        Volatile.Read(ref _active) > 0
        || await db.BackgroundTasks.AnyAsync(t => t.Kind == LibraryScanTask.TaskKind && t.Status == BackgroundTaskStatus.Queued, ct);

    private sealed class Done(LibraryReading reading) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            reading.End();
        }
    }
}

/// <summary>A request was asked for while the library is being read.</summary>
public sealed class LibraryBusyException() : Exception("The library is being scanned. Try again once the scan has read your files.");
