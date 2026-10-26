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
/// A stale view is recomputed when next asked for, one computation at a time. The caller waits a moment for it and
/// otherwise gets the last answer, so pages load at once even during a scan and catch up on their next refresh.
/// </remarks>
public sealed class LibraryCache(IServiceScopeFactory scopes, ILogger<LibraryCache> logger) : DbCommandInterceptor
{
    private static readonly string[] LibraryTables =
        ["\"Artists\"", "\"Albums\"", "\"Editions\"", "\"Tracks\"", "\"EditionTracks\"", "\"Crosswalk\"", "\"LibraryFiles\"", "\"Settings\""];

    /// <summary>How long a caller waits for a fresher answer before taking the last one.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    private long _generation;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed class Entry
    {
        public readonly Lock Gate = new();
        public object? Value;
        public long Generation = -1;
        public Task? Refresh;
    }

    public Task<IReadOnlyList<LibraryArtist>> GetArtistsAsync(CancellationToken ct) =>
        GetAsync("artists", (view, c) => view.GetLibraryArtistsAsync(c), ct);

    public Task<IReadOnlyList<LibraryAlbum>> GetAlbumsAsync(CancellationToken ct) =>
        GetAsync("albums", (view, c) => view.GetLibraryAlbumsAsync(c), ct);

    public Task<IReadOnlyList<LibraryTrack>> GetTracksAsync(CancellationToken ct) =>
        GetAsync("tracks", (view, c) => view.GetLibraryTracksAsync(c), ct);

    /// <summary>Raised after library data is written, as views are marked stale. Handlers must return quickly.</summary>
    public event Action? Changed;

    /// <summary>Marks every view stale.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Changed?.Invoke();
    }

    private async Task<T> GetAsync<T>(string key, Func<LibraryView, CancellationToken, Task<T>> compute, CancellationToken ct) where T : class
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        Task refresh;
        lock (entry.Gate)
        {
            if (entry.Value is T current && entry.Generation == Interlocked.Read(ref _generation))
                return current;
            refresh = entry.Refresh ??= RefreshAsync(entry, compute);
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
            var value = await compute(scope.ServiceProvider.GetRequiredService<LibraryView>(), CancellationToken.None);
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
