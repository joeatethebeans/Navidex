using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Navidex.Client;

namespace Navidex.Core.Library;

/// <summary>
/// The library-wide views (every artist, album or track you have) compare the whole catalogue with every file, which
/// takes a while on a big library. They're kept in memory and marked stale by any write to library data, which this
/// watches for as a database interceptor so no write path can forget to say so.
/// </summary>
/// <remarks>
/// A stale view is recomputed when next asked for, one computation at a time, and in the background once writes pause
/// for a moment, so it's usually fresh by the time a page asks. The caller waits a moment for a computation and
/// otherwise gets the last answer, so pages load at once even during a scan and catch up on their next refresh.
/// </remarks>
public sealed class LibraryCache(IServiceScopeFactory scopes, ILogger<LibraryCache> logger) : DbCommandInterceptor
{
    private static readonly string[] LibraryTables =
        ["\"Artists\"", "\"Albums\"", "\"Editions\"", "\"Tracks\"", "\"EditionTracks\"", "\"Crosswalk\"", "\"LibraryFiles\"", "\"Settings\""];

    /// <summary>How long a caller waits for a fresher answer before taking the last one.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(500);

    /// <summary>How long writes must pause before stale views are recomputed without being asked for.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    private long _generation;
    private long _lastWrite;
    private int _warming;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private static readonly TimeSpan SlowView = TimeSpan.FromSeconds(1);

    private sealed class Entry(string name)
    {
        public readonly string Name = name;
        public readonly Lock Gate = new();
        public object? Value;
        public long Generation = -1;
        public Task? Refresh;

        /// <summary>Starts a computation; set by the first caller, so the view can be warmed later.</summary>
        public Func<Entry, Task>? Start;
    }

    public Task<IReadOnlyList<LibraryArtist>> GetArtistsAsync(CancellationToken ct) =>
        GetAsync("artists", (view, c) => view.GetLibraryArtistsAsync(c), ct);

    public Task<IReadOnlyList<LibraryAlbum>> GetAlbumsAsync(CancellationToken ct) =>
        GetAsync("albums", (view, c) => view.GetLibraryAlbumsAsync(c), ct);

    public Task<IReadOnlyList<LibraryTrack>> GetTracksAsync(CancellationToken ct) =>
        GetAsync("tracks", (view, c) => view.GetLibraryTracksAsync(c), ct);

    /// <summary>Works out every view in the background, so the Library page has them ready from the start.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        try
        {
            await GetArtistsAsync(ct);
            await GetAlbumsAsync(ct);
            await GetTracksAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Logged by the refresh; the first page to ask tries again.
        }
    }

    /// <summary>Raised after library data is written, as views are marked stale. Handlers must return quickly.</summary>
    public event Action? Changed;

    /// <summary>Marks every view stale.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _lastWrite, Environment.TickCount64);
        Changed?.Invoke();
        if (Interlocked.Exchange(ref _warming, 1) == 0)
            _ = WarmStaleAsync();
    }

    // Once writes pause, recomputes the views that have been asked for before.
    private async Task WarmStaleAsync()
    {
        try
        {
            long wait;
            while ((wait = (long)Quiet.TotalMilliseconds - (Environment.TickCount64 - Interlocked.Read(ref _lastWrite))) > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(wait));
        }
        finally
        {
            Interlocked.Exchange(ref _warming, 0);
        }
        foreach (var entry in _entries.Values)
        {
            Task? refresh;
            lock (entry.Gate)
            {
                if (entry.Start is null || entry.Generation == Interlocked.Read(ref _generation))
                    continue;
                refresh = entry.Refresh ??= entry.Start(entry);
            }
            try
            {
                await refresh;
            }
            catch (Exception)
            {
                // Logged by the refresh; the next caller tries again.
            }
        }
    }

    private async Task<T> GetAsync<T>(string key, Func<LibraryView, CancellationToken, Task<T>> compute, CancellationToken ct) where T : class
    {
        var entry = _entries.GetOrAdd(key, k => new Entry(k));
        Task refresh;
        lock (entry.Gate)
        {
            entry.Start ??= e => RefreshAsync(e, compute);
            if (entry.Value is T current && entry.Generation == Interlocked.Read(ref _generation))
                return current;
            refresh = entry.Refresh ??= entry.Start(entry);
        }

        if (entry.Value is null)
            await refresh.WaitAsync(ct);
        else
            await Task.WhenAny(refresh, Task.Delay(Patience, ct));
        ct.ThrowIfCancellationRequested();
        return (T)entry.Value!;
    }

    private async Task RefreshAsync<T>(Entry entry, Func<LibraryView, CancellationToken, Task<T>> compute) where T : class
    {
        await Task.Yield();
        try
        {
            // Read before computing, so a write while it runs leaves the answer stale.
            var generation = Interlocked.Read(ref _generation);
            using var scope = scopes.CreateScope();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var value = await compute(scope.ServiceProvider.GetRequiredService<LibraryView>(), CancellationToken.None);
            if (watch.Elapsed > SlowView)
                logger.LogInformation("Working out the library's {View} took {Seconds:0.0} s", entry.Name, watch.Elapsed.TotalSeconds);
            lock (entry.Gate)
            {
                entry.Value = value;
                entry.Generation = generation;
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Couldn't work out the library view: {Message}", e.Message);
            throw;
        }
        finally
        {
            lock (entry.Gate)
                entry.Refresh = null;
        }
    }

    // ---- Watching for writes ----

    private void Saw(DbCommand command)
    {
        // A command can hold several statements, so look for a write anywhere in it.
        var sql = command.CommandText;
        if (!(sql.Contains("INSERT INTO", StringComparison.Ordinal) || sql.Contains("UPDATE ", StringComparison.Ordinal)
              || sql.Contains("DELETE FROM", StringComparison.Ordinal)))
            return;
        foreach (var table in LibraryTables)
        {
            if (sql.Contains(table, StringComparison.Ordinal))
            {
                Invalidate();
                return;
            }
        }
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Saw(command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
    {
        Saw(command);
        return ValueTask.FromResult(result);
    }

    // SaveChanges sends inserts and updates as readers, to read generated values back.
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Saw(command);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken ct = default)
    {
        Saw(command);
        return ValueTask.FromResult(result);
    }
}
