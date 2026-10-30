using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;

namespace Melodex.Plugins.Navidrome;

/// <summary>
/// Navidrome over the Subsonic API (with its OpenSubsonic extensions). Navidrome scans the whole music folder, so a
/// scan can't be limited to the new files, and its song paths are made up from tags unless configured otherwise, so
/// tracks are found by MusicBrainz recording ID, then ISRC, then title, artist, album and length.
/// </summary>
public sealed class NavidromeConnector : IMediaServerConnector, IFullScan, IPlaylistManager, IFavouritesManager, ILibraryReader
{
    private readonly SubsonicClient _client;

    public NavidromeConnector(PluginContext context) => _client = new SubsonicClient(context);

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var (report, response) = await _client.PingAsync(cancellationToken);
        if (response is { } reply && (!reply.TryGetProperty("openSubsonic", out var open) || !open.GetBoolean()))
            return new HealthReport(HealthStatus.Degraded, report.Message + ", but it doesn't support OpenSubsonic, so tracks can't be found by MusicBrainz ID.");
        return report;
    }

    private Task<JsonElement> CallAsync(string endpoint, IEnumerable<(string Name, string Value)> parameters, CancellationToken ct) =>
        _client.CallAsync(endpoint, parameters, ct);

    public Task TriggerScanAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken) =>
        CallAsync("startScan", [], cancellationToken);

    public Task TriggerFullScanAsync(CancellationToken cancellationToken) =>
        CallAsync("startScan", [("fullScan", "true")], cancellationToken);

    public async Task<ScanStatus> GetScanStatusAsync(CancellationToken cancellationToken)
    {
        var status = (await CallAsync("getScanStatus", [], cancellationToken)).GetProperty("scanStatus");
        DateTimeOffset? last = status.TryGetProperty("lastScan", out var l) && DateTimeOffset.TryParse(l.GetString(), CultureInfo.InvariantCulture, out var at) ? at : null;
        long? count = status.TryGetProperty("count", out var c) && c.TryGetInt64(out var n) ? n : null;
        return new ScanStatus(status.GetProperty("scanning").GetBoolean(), last, count);
    }

    public async Task<string?> ResolveTrackAsync(TrackLookup lookup, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lookup.Title))
            return null;
        var songs = await SearchAsync(lookup.Title, 0, 50, cancellationToken);
        return Pick(songs, lookup)?.Id;
    }

    /// <summary>The best of the songs for the lookup, most certain evidence first; null if none fits.</summary>
    public static Song? Pick(IReadOnlyList<Song> songs, TrackLookup lookup)
    {
        if (lookup.MusicBrainzRecordingId is { Length: > 0 } recording
            && songs.FirstOrDefault(s => string.Equals(s.MusicBrainzId, recording, StringComparison.OrdinalIgnoreCase)) is { } byRecording)
            return byRecording;
        if (lookup.Isrc is { Length: > 0 } isrc && songs.FirstOrDefault(s => s.Isrcs.Any(i => SameIsrc(i, isrc))) is { } byIsrc)
            return byIsrc;
        if (lookup.Path is { Length: > 0 } path && songs.FirstOrDefault(s => s.Path is not null && SamePath(s.Path, path)) is { } byPath)
            return byPath;

        // A song with its own MusicBrainz ID that isn't this recording is a different song, however alike the tags.
        return songs
            .Where(s => lookup.MusicBrainzRecordingId is null || string.IsNullOrEmpty(s.MusicBrainzId))
            .Where(s => Same(s.Title, lookup.Title))
            .Where(s => lookup.Artist is null || Same(s.Artist, lookup.Artist))
            .Where(s => lookup.Album is null || Same(s.Album, lookup.Album))
            .Where(s => lookup.Duration is null || s.Duration is null || Math.Abs((s.Duration.Value - lookup.Duration.Value).TotalSeconds) <= 3)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<ServerPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken)
    {
        var response = await CallAsync("getPlaylists", [], cancellationToken);
        return Array(response.GetProperty("playlists"), "playlist")
            .Select(p => new ServerPlaylist(Text(p, "id")!, Text(p, "name") ?? "", p.TryGetProperty("songCount", out var c) ? c.GetInt32() : 0))
            .ToList();
    }

    public async Task<string> CreatePlaylistAsync(string name, IReadOnlyList<string> trackIds, CancellationToken cancellationToken)
    {
        var response = await CallAsync("createPlaylist", [("name", name), .. trackIds.Select(id => ("songId", id))], cancellationToken);
        return Text(response.GetProperty("playlist"), "id")!;
    }

    public Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken cancellationToken) =>
        CallAsync("updatePlaylist", [("playlistId", playlistId), .. trackIds.Select(id => ("songIdToAdd", id))], cancellationToken);

    public Task SetFavouriteAsync(string trackId, bool favourite, CancellationToken cancellationToken) =>
        CallAsync(favourite ? "star" : "unstar", [("id", trackId)], cancellationToken);

    public async IAsyncEnumerable<ServerTrack> GetTracksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int page = 500;
        for (var offset = 0; ; offset += page)
        {
            // Navidrome lists every song for an empty query.
            var songs = await SearchAsync("", offset, page, cancellationToken);
            foreach (var song in songs)
                yield return new ServerTrack(song.Id, song.Path, song.Title, song.Artist, song.Album, song.Duration, song.MusicBrainzId);
            if (songs.Count < page)
                yield break;
        }
    }

    private async Task<IReadOnlyList<Song>> SearchAsync(string query, int offset, int count, CancellationToken ct)
    {
        var response = await CallAsync("search3", [("query", query), ("songCount", count.ToString(CultureInfo.InvariantCulture)),
            ("songOffset", offset.ToString(CultureInfo.InvariantCulture)), ("artistCount", "0"), ("albumCount", "0")], ct);
        return response.TryGetProperty("searchResult3", out var result) ? Array(result, "song").Select(Song.From).ToList() : [];
    }

    internal static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    internal static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool SameIsrc(string a, string b) =>
        string.Equals(a.Replace("-", ""), b.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string server, string ours) =>
        server.Replace('\\', '/').TrimStart('/').EndsWith(ours.Replace('\\', '/').TrimStart('/'), StringComparison.Ordinal);

    internal static bool Same(string? a, string? b) => a is not null && b is not null && Simplify(a) == Simplify(b);

    private static string Simplify(string text)
    {
        var normalised = text.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalised.Length);
        foreach (var c in normalised)
            if (char.IsLetterOrDigit(c))
                result.Append(char.ToLowerInvariant(c));
        return result.ToString();
    }

    /// <summary>A song from a Subsonic response.</summary>
    public sealed record Song(string Id, string Title, string? Artist, string? Album, TimeSpan? Duration, string? Path, string? MusicBrainzId, IReadOnlyList<string> Isrcs)
    {
        public static Song From(JsonElement s) => new(
            Text(s, "id")!,
            Text(s, "title") ?? "",
            Text(s, "artist"),
            Text(s, "album"),
            s.TryGetProperty("duration", out var d) && d.TryGetInt32(out var seconds) ? TimeSpan.FromSeconds(seconds) : null,
            Text(s, "path"),
            Text(s, "musicBrainzId") is { Length: > 0 } mbid ? mbid : null,
            s.TryGetProperty("isrc", out var isrc) && isrc.ValueKind == JsonValueKind.Array
                ? isrc.EnumerateArray().Select(i => i.GetString()).OfType<string>().ToList()
                : []);
    }
}

/// <summary>The Subsonic server answered with an error.</summary>
/// <param name="Code">The Subsonic error code, such as 70 for not found.</param>
public sealed class SubsonicException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}
