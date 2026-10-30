using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Core.Data;
using Melodex.Core.Playlists;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Each user's playlists, and keeping them the same on the media server.</summary>
public sealed class PlaylistTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;
    private readonly FakeCoverArt _coverArt = new(new ReplayHandler(TestLibrary.MusicBrainzFixtures));

    public PlaylistTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath)
            .UseSetting("Melodex:LibraryPath", TestLibrary.Music)
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => _coverArt;
                http.RateLimit = false;
            })));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [TestLibraryFact]
    public async Task Playlists_hold_songs_from_the_library_in_folders()
    {
        await ScanAsync();
        var (a, b) = await TwoTracksAsync();

        var made = await _api.CreatePlaylistAsync(new CreatePlaylist(" Moods/Calm ", Songs: [new PlaylistItem(a.TrackId), new PlaylistItem(b.TrackId)]));
        Assert.Equal("Moods/Calm", made.Playlist.Name);
        Assert.Equal([a.Title, b.Title], made.Songs.Select(s => s.Title));
        Assert.All(made.Songs, s => Assert.Equal(PlaylistSongState.Library, s.State));
        Assert.Equal(PlaylistSyncState.Off, made.Playlist.Sync);

        // A song already in it is left out, unless duplicates are asked for.
        var id = made.Playlist.Id;
        Assert.Equal(new PlaylistAdded(0, 1, 0), await _api.AddToPlaylistAsync(id, new AddToPlaylist([new PlaylistItem(a.TrackId)])));
        Assert.Equal(new PlaylistAdded(1, 0, 0), await _api.AddToPlaylistAsync(id, new AddToPlaylist([new PlaylistItem(a.TrackId)], Duplicates: true)));
        Assert.Equal(new PlaylistAdded(0, 0, 1), await _api.AddToPlaylistAsync(id, new AddToPlaylist([new PlaylistItem(Guid.NewGuid())])));

        // Reordered and trimmed by giving the entries to keep.
        var songs = (await _api.GetPlaylistAsync(id))!.Songs;
        var arranged = await _api.ArrangePlaylistAsync(id, new ArrangePlaylist([songs[1].EntryId, songs[0].EntryId]));
        Assert.Equal([b.Title, a.Title], arranged.Songs.Select(s => s.Title));

        await _api.RenamePlaylistFolderAsync(new RenamePlaylistFolder("Moods", "Feelings/Soft", "/"));
        Assert.Equal("Feelings/Soft/Calm", Assert.Single(await _api.ListPlaylistsAsync()).Name);
        await _api.RenamePlaylistFolderAsync(new RenamePlaylistFolder("Feelings/Soft", "", "/"));
        Assert.Equal("Calm", Assert.Single(await _api.ListPlaylistsAsync()).Name);

        var refused = await Assert.ThrowsAsync<MelodexApiException>(() => _api.UpdatePlaylistAsync(id, new UpdatePlaylist(Name: "  ")));
        Assert.Contains("name", refused.Message);

        await _api.DeletePlaylistAsync(id);
        Assert.Empty(await _api.ListPlaylistsAsync());
        Assert.Null(await _api.GetPlaylistAsync(id));
    }

    [TestLibraryFact]
    public async Task Syncing_sends_changes_made_here_and_brings_those_made_on_the_server()
    {
        await ScanAsync();
        var (a, b) = await TwoTracksAsync();
        var c = (await _api.GetLibraryTracksAsync()).First(t => !t.Local && t.TrackId != a.TrackId && t.TrackId != b.TrackId);
        var server = new FakePlaylistServer();
        var made = await _api.CreatePlaylistAsync(new CreatePlaylist("Moods/Calm", "Quiet ones", [new PlaylistItem(a.TrackId), new PlaylistItem(b.TrackId)]));

        // New here: made there, with the server's IDs for the songs.
        await SyncAsync(server);
        var there = Assert.Single(server.Playlists.Values);
        Assert.Equal("Moods/Calm", there.Name);
        Assert.Equal("Quiet ones", there.Comment);
        Assert.Equal([$"s:{a.Title}", $"s:{b.Title}"], there.Songs.Select(s => s.Id));
        Assert.Equal(PlaylistSyncState.Synced, (await _api.GetPlaylistAsync(made.Playlist.Id))!.Playlist.Sync);

        // Changed there: renamed, and a song added that the library has and one it doesn't.
        server.Change(there.Id, "Moods/Quieter", [.. there.Songs, new ServerTrack("s:c", null, c.Title, c.ArtistCredit, null, null, null),
            new ServerTrack("s:x", null, "Nobody's Song", "Nobody", "Nowhere", TimeSpan.FromSeconds(200), null)]);
        await SyncAsync(server);
        var pulled = (await _api.GetPlaylistAsync(made.Playlist.Id))!;
        Assert.Equal("Moods/Quieter", pulled.Playlist.Name);
        Assert.Equal([a.Title, b.Title, c.Title, "Nobody's Song"], pulled.Songs.Select(s => s.Title));
        Assert.Equal(c.TrackId, pulled.Songs[2].TrackId);
        Assert.Equal(PlaylistSongState.ServerOnly, pulled.Songs[3].State);
        Assert.Equal(PlaylistSyncState.Synced, pulled.Playlist.Sync);

        // Changed here: the server's own song goes back with the rest.
        await _api.ArrangePlaylistAsync(made.Playlist.Id, new ArrangePlaylist([pulled.Songs[3].EntryId, pulled.Songs[0].EntryId]));
        await SyncAsync(server);
        Assert.Equal(["s:x", $"s:{a.Title}"], server.Playlists[there.Id].Songs.Select(s => s.Id));

        // Made there: brought here. Deleted here: deleted there.
        server.Add("Running", [new ServerTrack($"s:{b.Title}", null, b.Title, b.ArtistCredit, null, null, null)]);
        await SyncAsync(server);
        var running = Assert.Single(await _api.ListPlaylistsAsync(), p => p.Name == "Running");
        Assert.Equal(b.TrackId, Assert.Single((await _api.GetPlaylistAsync(running.Id))!.Songs).TrackId);
        await _api.DeletePlaylistAsync(made.Playlist.Id);
        await SyncAsync(server);
        Assert.Equal(["Running"], server.Playlists.Values.Select(p => p.Name));

        // Deleted there, unchanged here: deleted here too.
        server.Playlists.Clear();
        await SyncAsync(server);
        Assert.Empty(await _api.ListPlaylistsAsync());
    }

    private async Task SyncAsync(FakePlaylistServer server)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var user = await db.Users.SingleAsync();
        user.MediaServerUsername = "someone";
        var problem = await scope.ServiceProvider.GetRequiredService<PlaylistSync>()
            .SyncWithAsync(user, new ServerLogin("someone", "secret"), FakePlaylistServer.InstanceId, server, server, default);
        Assert.Null(problem);
    }

    private async Task<(LibraryTrack A, LibraryTrack B)> TwoTracksAsync()
    {
        var tracks = (await _api.GetLibraryTracksAsync()).Where(t => !t.Local).DistinctBy(t => t.TrackId).ToList();
        return (tracks[0], tracks[1]);
    }

    private async Task ScanAsync()
    {
        // The test library is shared, so no lyrics are written into it.
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Melodex.Core.Library.SettingsService>().SaveLibraryAsync(new LibrarySettings { FetchLyrics = false }, default);
        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (task.Active && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, task.Error);
    }

    /// <summary>A media server's playlists, kept in memory. It finds every song, as "s:" and its title.</summary>
    private sealed class FakePlaylistServer : IPlaylistSync, IMediaServerConnector
    {
        public static readonly Guid InstanceId = Guid.NewGuid();

        public sealed record Stored(string Id, string Name, string? Comment, List<ServerTrack> Songs, DateTimeOffset Changed);

        public Dictionary<string, Stored> Playlists { get; } = [];
        private int _next;
        private DateTimeOffset _clock = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        private DateTimeOffset Tick() => _clock = _clock.AddMinutes(1);

        public void Change(string id, string name, IReadOnlyList<ServerTrack> songs) =>
            Playlists[id] = Playlists[id] with { Name = name, Songs = [.. songs], Changed = Tick() };

        public void Add(string name, IReadOnlyList<ServerTrack> songs)
        {
            var id = $"p{++_next}";
            Playlists[id] = new Stored(id, name, null, [.. songs], Tick());
        }

        public bool SyncsPlaylists => true;

        public Task<HealthReport> CheckLoginAsync(ServerLogin login, CancellationToken ct) => Task.FromResult(HealthReport.Healthy());

        public Task<IReadOnlyList<UserPlaylist>> GetPlaylistsAsync(ServerLogin login, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UserPlaylist>>(Playlists.Values.Select(Summary).ToList());

        public Task<UserPlaylistSongs?> GetPlaylistAsync(ServerLogin login, string playlistId, CancellationToken ct) =>
            Task.FromResult(Playlists.TryGetValue(playlistId, out var p) ? new UserPlaylistSongs(Summary(p), p.Songs) : null);

        public Task<string> CreatePlaylistAsync(ServerLogin login, PlaylistContent content, CancellationToken ct)
        {
            var id = $"p{++_next}";
            Playlists[id] = new Stored(id, content.Name, content.Comment, Songs(content), Tick());
            return Task.FromResult(id);
        }

        public Task UpdatePlaylistAsync(ServerLogin login, string playlistId, PlaylistContent content, CancellationToken ct)
        {
            Playlists[playlistId] = new Stored(playlistId, content.Name, content.Comment, Songs(content), Tick());
            return Task.CompletedTask;
        }

        public Task DeletePlaylistAsync(ServerLogin login, string playlistId, CancellationToken ct)
        {
            Playlists.Remove(playlistId);
            return Task.CompletedTask;
        }

        public Task<string?> ResolveTrackAsync(TrackLookup lookup, CancellationToken ct) => Task.FromResult<string?>($"s:{lookup.Title}");

        private static UserPlaylist Summary(Stored p) => new(p.Id, p.Name, p.Comment, p.Songs.Count, p.Changed);

        private static List<ServerTrack> Songs(PlaylistContent content) =>
            content.TrackIds.Select(id => new ServerTrack(id, null, id.StartsWith("s:") ? id[2..] : id, null, null, null, null)).ToList();

        public Task<HealthReport> CheckHealthAsync(CancellationToken ct) => Task.FromResult(HealthReport.Healthy());
        public Task TriggerScanAsync(IReadOnlyCollection<string> paths, CancellationToken ct) => Task.CompletedTask;
        public Task<ScanStatus> GetScanStatusAsync(CancellationToken ct) => Task.FromResult(new ScanStatus(false));
    }
}
