using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Plugins;

namespace Melodex.Core.Playlists;

/// <summary>
/// Keeps a user's playlists the same in Melodex and on the media server, as that user, when the connector's "sync
/// playlists" box is ticked and they've linked their login. Changed on one side since the last sync, a playlist is
/// copied to the other; changed on both, the later change wins. Deleted on one side, it's deleted on the other, unless
/// it's been changed on that side since.
/// </summary>
public sealed class PlaylistSync(MelodexDbContext db, ConnectorPlugins connectors, IDataProtectionProvider protection, ILogger<PlaylistSync> logger)
{
    // One sync at a time, so a sync asked for now and the syncer's don't both send the same playlist.
    private static readonly SemaphoreSlim Running = new(1, 1);

    private IDataProtector Protector => protection.CreateProtector("Melodex.MediaServerLogin");

    /// <summary>The media server, if it syncs playlists.</summary>
    public async Task<(PluginInstance Instance, IMediaServerConnector Connector, IPlaylistSync Playlists)?> ServerAsync(CancellationToken ct)
    {
        try
        {
            return await connectors.GetActiveAsync(ct) is { Connector: IPlaylistSync { SyncsPlaylists: true } playlists } active
                ? (active.Instance, active.Connector, playlists)
                : null;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException)
        {
            // The connector can't be started, for instance after its plugin was removed.
            return null;
        }
    }

    public string Protect(string password) => Protector.Protect(password);

    public ServerLogin? LoginOf(User user)
    {
        if (user.MediaServerUsername is not { } username || user.MediaServerPassword is not { } password)
            return null;
        try
        {
            return new ServerLogin(username, Protector.Unprotect(password));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The keys it was kept with are gone: it has to be linked again.
            return null;
        }
    }

    public async Task<PlaylistSyncStatus> StatusAsync(User user, CancellationToken ct)
    {
        var server = await ServerAsync(ct);
        return new PlaylistSyncStatus(server is not null, server?.Instance.Name, user.MediaServerUsername, user.PlaylistsSyncedAt, user.PlaylistSyncError);
    }

    /// <summary>Syncs the user's playlists now, if they're synced, and says how it went.</summary>
    public async Task<PlaylistSyncStatus> SyncAsync(User user, CancellationToken ct)
    {
        if (await ServerAsync(ct) is not { } server || user.MediaServerUsername is null)
            return await StatusAsync(user, ct);
        if (LoginOf(user) is not { } login)
        {
            user.PlaylistSyncError = "Your media server login needs linking again: the keys it was kept with have changed.";
            await db.SaveChangesAsync(ct);
            return await StatusAsync(user, ct);
        }

        await Running.WaitAsync(ct);
        try
        {
            user.PlaylistSyncError = await SyncWithAsync(user, login, server.Instance.Id, server.Playlists, server.Connector, ct);
            user.PlaylistsSyncedAt = DateTime.UtcNow;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            db.Attach(user);
            user.PlaylistSyncError = e is DbUpdateConcurrencyException ? "Playlists changed while they were syncing; they'll be synced again shortly." : e.Message;
            logger.LogWarning("Couldn't sync {User}'s playlists with {Server}: {Message}", user.Username, server.Instance.Name, e.Message);
        }
        finally
        {
            Running.Release();
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return await StatusAsync(user, CancellationToken.None);
    }

    /// <summary>
    /// The sync itself, with the server given. Returns the first problem with a single playlist, if any, having
    /// carried on with the rest; a server that can't be reached at all throws.
    /// </summary>
    public async Task<string?> SyncWithAsync(User user, ServerLogin login, Guid instanceId, IPlaylistSync server, IMediaServerConnector connector, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var remote = (await server.GetPlaylistsAsync(login, ct)).ToDictionary(p => p.Id);
        var local = await db.Playlists.Include(p => p.Entries).Where(p => p.UserId == user.Id).ToListAsync(ct);
        string? problem = null;
        int sent = 0, brought = 0, removed = 0;

        // Linked to another server, or to the same one before a reset: they start again here.
        foreach (var playlist in local.Where(p => p.ServerId is not null && p.ServerInstanceId != instanceId))
            Forget(playlist);
        var linked = local.Where(p => p.ServerId is not null).Select(p => p.ServerId!).ToHashSet();

        foreach (var playlist in local)
        {
            try
            {
                if (playlist.DeletedAt is not null)
                {
                    if (playlist.ServerId is { } id && remote.ContainsKey(id))
                        await server.DeletePlaylistAsync(login, id, ct);
                    db.Playlists.Remove(playlist);
                    removed++;
                }
                else if (playlist.ServerId is null || !remote.TryGetValue(playlist.ServerId, out var there))
                {
                    // New here, or deleted there: sent if it's changed here since, and otherwise deleted here too.
                    if (playlist.ServerId is null || Changed(playlist))
                    {
                        Forget(playlist);
                        if (await PushAsync(playlist, null))
                            sent++;
                    }
                    else
                    {
                        db.Playlists.Remove(playlist);
                        removed++;
                    }
                }
                else
                {
                    var theirs = there.Changed?.UtcDateTime;
                    var changedThere = theirs is { } t && (playlist.ServerChangedAt is not { } seen || Math.Abs((t - seen).TotalSeconds) >= 1);
                    if (Changed(playlist) && (!changedThere || playlist.UpdatedAt >= theirs))
                    {
                        if (await PushAsync(playlist, there.Id))
                            sent++;
                    }
                    else if (changedThere)
                    {
                        await PullAsync(playlist, there.Id);
                        brought++;
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException && e is not HttpRequestException)
            {
                problem ??= $"{playlist.Name}: {e.Message}";
                logger.LogWarning("Couldn't sync the playlist {Name} for {User}: {Message}", playlist.Name, user.Username, e.Message);
            }
        }

        // New there.
        foreach (var there in remote.Values.Where(p => !linked.Contains(p.Id)))
        {
            var playlist = new Playlist { UserId = user.Id, Name = there.Name, ServerInstanceId = instanceId, ServerId = there.Id };
            db.Playlists.Add(playlist);
            await PullAsync(playlist, there.Id);
            brought++;
        }

        await db.SaveChangesAsync(ct);
        if (sent + brought + removed > 0)
            logger.LogInformation("Synced {User}'s playlists with the media server: {Sent} sent, {Brought} brought over, {Removed} deleted",
                user.Username, sent, brought, removed);
        return problem;

        // Sends the playlist as it is here. One whose songs moved is held back until the server has found them all,
        // rather than losing them there, unless it has changed here.
        async Task<bool> PushAsync(Playlist playlist, string? serverId)
        {
            var entries = playlist.Entries.OrderBy(e => e.Position).ToList();
            await FindOnServerAsync(entries, connector, ct);
            var ids = entries.Select(e => e.ServerTrackId).OfType<string>().ToList();
            var missing = entries.Count - ids.Count;
            if (serverId is not null && missing > 0 && !(playlist.SyncedAt is null || playlist.UpdatedAt > playlist.SyncedAt))
                return false;

            var content = new PlaylistContent(playlist.Name, playlist.Comment, ids);
            if (serverId is null)
            {
                playlist.ServerId = serverId = await server.CreatePlaylistAsync(login, content, ct);
                playlist.ServerInstanceId = instanceId;
            }
            else
            {
                await server.UpdatePlaylistAsync(login, serverId, content, ct);
            }
            playlist.ServerChangedAt = (await server.GetPlaylistAsync(login, serverId, ct))?.Playlist.Changed?.UtcDateTime;
            playlist.SyncedAt = started;
            playlist.NeedsPush = missing > 0;
            return true;
        }

        // Takes the server's copy: songs already in it keep what they were found as, and new ones are looked for in the library.
        async Task PullAsync(Playlist playlist, string serverId)
        {
            if (await server.GetPlaylistAsync(login, serverId, ct) is not { } there)
                return;
            playlist.Name = there.Playlist.Name;
            playlist.Comment = there.Playlist.Comment;
            var unused = playlist.Entries.Where(e => e.ServerTrackId is not null).GroupBy(e => e.ServerTrackId!)
                .ToDictionary(g => g.Key, g => new Queue<PlaylistEntry>(g.OrderBy(e => e.Position)));
            var kept = new List<PlaylistEntry>();
            var matches = await MatchAsync(user.Id, there.Songs.Where(s => !unused.ContainsKey(s.Id)).ToList(), ct);
            for (var i = 0; i < there.Songs.Count; i++)
            {
                var song = there.Songs[i];
                var entry = unused.TryGetValue(song.Id, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
                if (entry is null)
                {
                    var match = matches.GetValueOrDefault(song.Id);
                    entry = new PlaylistEntry
                    {
                        TrackId = match?.TrackId, FileId = match?.TrackId is null ? match?.FileId : null, ServerTrackId = song.Id,
                        Title = song.Title, Artist = song.Artist ?? "", Album = song.Album, DurationSeconds = song.Duration?.TotalSeconds,
                    };
                    playlist.Entries.Add(entry);
                    db.PlaylistEntries.Add(entry);
                }
                entry.Position = i;
                kept.Add(entry);
            }
            db.PlaylistEntries.RemoveRange(playlist.Entries.Except(kept).ToList());
            playlist.Entries.RemoveAll(e => !kept.Contains(e));
            playlist.ServerChangedAt = there.Playlist.Changed?.UtcDateTime;
            playlist.UpdatedAt = started;
            playlist.SyncedAt = started;
            playlist.NeedsPush = false;
        }
    }

    private static bool Changed(Playlist playlist) => playlist.SyncedAt is null || playlist.UpdatedAt > playlist.SyncedAt || playlist.NeedsPush;

    private static void Forget(Playlist playlist)
    {
        playlist.ServerId = null;
        playlist.ServerInstanceId = null;
        playlist.ServerChangedAt = null;
        playlist.SyncedAt = null;
        foreach (var entry in playlist.Entries.Where(e => e.TrackId is not null || e.FileId is not null))
            entry.ServerTrackId = null;
    }

    /// <summary>Finds the server's ID for each song that hasn't one yet: by the IDs in its file's tags, then its tags and length.</summary>
    private async Task FindOnServerAsync(IReadOnlyList<PlaylistEntry> entries, IMediaServerConnector connector, CancellationToken ct)
    {
        var wanted = entries.Where(e => e.ServerTrackId is null).ToList();
        if (wanted.Count == 0)
            return;
        var fileIds = wanted.Select(e => e.FileId).OfType<Guid>().ToList();
        var trackIds = wanted.Select(e => e.TrackId).OfType<Guid>().ToList();
        var files = await db.LibraryFiles.AsNoTracking()
            .Where(f => f.HiddenAt == null && (fileIds.Contains(f.Id) || f.TrackId != null && trackIds.Contains(f.TrackId.Value)))
            .Select(f => new { f.Id, f.TrackId, f.Path, f.TagsJson, f.Duration, f.MediaServerTrackId, Album = f.Edition == null ? null : f.Edition.Album.Title })
            .ToListAsync(ct);
        var recordings = await db.Crosswalk.AsNoTracking()
            .Where(m => m.EntityKind == EntityKind.Track && m.Provider == Providers.MusicBrainz && trackIds.Contains(m.EntityId))
            .ToDictionaryAsync(m => m.EntityId, m => m.Value, ct);

        foreach (var entry in wanted)
        {
            var file = files.FirstOrDefault(f => f.Id == entry.FileId) ?? files.FirstOrDefault(f => f.TrackId is not null && f.TrackId == entry.TrackId);
            if (file is null)
                continue;
            if (file.MediaServerTrackId is { } known)
            {
                entry.ServerTrackId = known;
                continue;
            }
            // The server knows the file by its tags, which can differ from the catalogue's names.
            var tags = FileTags.Parse(file.TagsJson);
            var lookup = new TrackLookup(
                file.Path,
                tags.MusicBrainzRecordingId ?? (entry.TrackId is { } t ? recordings.GetValueOrDefault(t) : null),
                tags.MusicBrainzReleaseId,
                tags.Title ?? entry.Title,
                tags.Artist ?? entry.Artist,
                tags.Album ?? file.Album,
                file.Duration ?? (entry.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null),
                tags.Isrc);
            entry.ServerTrackId = await connector.ResolveTrackAsync(lookup, ct);
        }
    }

    private sealed record Match(Guid? TrackId, Guid? FileId);

    /// <summary>
    /// Which library song each server song is: one already found as it in another playlist, then by its MusicBrainz
    /// recording, its path, or its title, artist and length. Songs not found stay the server's only.
    /// </summary>
    private async Task<Dictionary<string, Match>> MatchAsync(Guid userId, IReadOnlyList<ServerTrack> songs, CancellationToken ct)
    {
        var matches = new Dictionary<string, Match>();
        if (songs.Count == 0)
            return matches;
        var ids = songs.Select(s => s.Id).ToList();
        foreach (var known in await db.PlaylistEntries.AsNoTracking()
                     .Where(e => e.Playlist.UserId == userId && e.ServerTrackId != null && ids.Contains(e.ServerTrackId) && (e.TrackId != null || e.FileId != null))
                     .Select(e => new { e.ServerTrackId, e.TrackId, e.FileId }).ToListAsync(ct))
            matches.TryAdd(known.ServerTrackId!, new Match(known.TrackId, known.FileId));

        var rest = songs.Where(s => !matches.ContainsKey(s.Id)).ToList();
        var recordings = rest.Select(s => s.MusicBrainzRecordingId).OfType<string>().ToList();
        var byRecording = await db.Crosswalk.AsNoTracking()
            .Where(m => m.EntityKind == EntityKind.Track && m.Provider == Providers.MusicBrainz && recordings.Contains(m.Value))
            .Where(m => db.LibraryFiles.Any(f => f.TrackId == m.EntityId && f.HiddenAt == null))
            .Select(m => new { m.Value, m.EntityId }).ToListAsync(ct);
        var paths = rest.Select(s => s.Path).OfType<string>().ToList();
        var byPath = await db.LibraryFiles.AsNoTracking().Where(f => paths.Contains(f.Path) && f.HiddenAt == null)
            .Select(f => new { f.Path, f.Id, f.TrackId }).ToListAsync(ct);
        var titles = rest.Select(s => s.Title).Distinct().ToList();
        var byTitle = await db.LibraryFiles.AsNoTracking().Where(f => f.TrackId != null && f.HiddenAt == null && titles.Contains(f.Track!.Title))
            .Select(f => new { f.TrackId, f.Track!.Title, f.Track.ArtistCredit, Duration = f.Duration ?? f.Track.Duration }).ToListAsync(ct);

        foreach (var song in rest)
        {
            if (song.MusicBrainzRecordingId is { } recording && byRecording.FirstOrDefault(r => string.Equals(r.Value, recording, StringComparison.OrdinalIgnoreCase)) is { } r)
                matches[song.Id] = new Match(r.EntityId, null);
            else if (song.Path is { } path && byPath.FirstOrDefault(f => f.Path == path) is { } p)
                matches[song.Id] = new Match(p.TrackId, p.Id);
            else if (byTitle.FirstOrDefault(f => f.Title == song.Title && Same(f.ArtistCredit, song.Artist)
                                                && (f.Duration is null || song.Duration is null || Math.Abs((f.Duration.Value - song.Duration.Value).TotalSeconds) <= 3)) is { } t)
                matches[song.Id] = new Match(t.TrackId, null);
        }
        return matches;
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Files moved by a format may have new IDs on the media server, so the playlists they're in look for them again,
    /// and are sent once the server has them. Returns the users whose playlists these are.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> FilesMovedAsync(MelodexDbContext db, IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
            return [];
        var trackIds = await db.LibraryFiles.Where(f => fileIds.Contains(f.Id) && f.TrackId != null).Select(f => f.TrackId!.Value).Distinct().ToListAsync(ct);
        var entries = db.PlaylistEntries.Where(e => e.FileId != null && fileIds.Contains(e.FileId.Value) || e.TrackId != null && trackIds.Contains(e.TrackId.Value));
        var playlists = await entries.Select(e => e.PlaylistId).Distinct().ToListAsync(ct);
        if (playlists.Count == 0)
            return [];
        await entries.ExecuteUpdateAsync(e => e.SetProperty(x => x.ServerTrackId, (string?)null), ct);
        await db.Playlists.Where(p => playlists.Contains(p.Id) && p.ServerId != null).ExecuteUpdateAsync(p => p.SetProperty(x => x.NeedsPush, true), ct);
        return await db.Playlists.Where(p => playlists.Contains(p.Id)).Select(p => p.UserId).Distinct().ToListAsync(ct);
    }
}

/// <summary>
/// Syncs playlists in the background: a few seconds after a user changes theirs, and every user's every
/// <see cref="Every"/>, to bring over changes made on the media server.
/// </summary>
public sealed class PlaylistSyncer(IServiceScopeFactory scopes, ILogger<PlaylistSyncer> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    /// <summary>How long after a change to wait for more before syncing, so a run of edits goes in one sync.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    // A user to sync, or null for everyone.
    private readonly Channel<Guid?> _wanted = Channel.CreateUnbounded<Guid?>();

    public void Changed(Guid userId) => _wanted.Writer.TryWrite(userId);

    public void Everyone() => _wanted.Writer.TryWrite(null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var users = new HashSet<Guid?>();
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                wait.CancelAfter(Every);
                try
                {
                    users.Add(await _wanted.Reader.ReadAsync(wait.Token));
                    await Task.Delay(Settle, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    users.Add(null);
                }
            }
            while (_wanted.Reader.TryRead(out var more))
                users.Add(more);

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
                var sync = scope.ServiceProvider.GetRequiredService<PlaylistSync>();
                if (await sync.ServerAsync(stoppingToken) is null)
                    continue;
                var everyone = users.Contains(null);
                var ids = users.OfType<Guid>().ToList();
                foreach (var user in await db.Users.Where(u => u.MediaServerUsername != null && (everyone || ids.Contains(u.Id))).ToListAsync(stoppingToken))
                    await sync.SyncAsync(user, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Couldn't sync playlists");
            }
        }
    }
}
