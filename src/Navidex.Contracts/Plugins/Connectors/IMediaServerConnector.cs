namespace Navidex.Contracts.Plugins.Connectors;

/// <summary>
/// Media-server connector contract (version 1.0): tells a media server about new music and confirms it's
/// there. Optional features are separate interfaces, each matched by a capability name in the manifest;
/// the core and API only offer actions the active connector supports.
/// </summary>
public interface IMediaServerConnector : IPlugin
{
    /// <summary>
    /// Asks the server to pick up changes. <paramref name="paths"/> are library-relative paths that changed;
    /// servers that can't scan selectively may ignore them and scan everything new.
    /// </summary>
    Task TriggerScanAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken);

    /// <summary>Whether a scan is running, and when the last one finished.</summary>
    Task<ScanStatus> GetScanStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Finds the server's track ID for a file or catalogue ID. Returns <c>null</c> when the server doesn't
    /// have the track (yet); the core polls until it does or gives up.
    /// </summary>
    Task<string?> ResolveTrackAsync(TrackLookup lookup, CancellationToken cancellationToken);
}

/// <summary>State of the server's scanner.</summary>
/// <param name="Scanning">Whether a scan is running.</param>
/// <param name="LastScanFinishedAt">When the last scan finished, if the server reports it.</param>
/// <param name="TrackCount">Tracks in the server's library, if reported.</param>
public sealed record ScanStatus(bool Scanning, DateTimeOffset? LastScanFinishedAt = null, long? TrackCount = null);

/// <summary>
/// What to look a track up by. Connectors should try the most specific fields first: the path, then the
/// MusicBrainz recording ID or ISRC, then title, artist, album and duration.
/// </summary>
/// <param name="Path">Library-relative path of the file.</param>
/// <param name="MusicBrainzRecordingId">MusicBrainz recording ID from the file's tags.</param>
/// <param name="MusicBrainzReleaseId">MusicBrainz release ID from the file's tags.</param>
/// <param name="Title">Track title.</param>
/// <param name="Artist">Track artist.</param>
/// <param name="Album">Album title.</param>
/// <param name="Duration">Length.</param>
/// <param name="Isrc">ISRC from the file's tags.</param>
public sealed record TrackLookup(
    string? Path = null,
    string? MusicBrainzRecordingId = null,
    string? MusicBrainzReleaseId = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    TimeSpan? Duration = null,
    string? Isrc = null);

/// <summary>Optional: full rescan. Capability <see cref="ConnectorCapabilities.FullScan"/>.</summary>
public interface IFullScan
{
    /// <summary>Rescans the whole library, including unchanged files.</summary>
    Task TriggerFullScanAsync(CancellationToken cancellationToken);
}

/// <summary>Optional: playlists. Capability <see cref="ConnectorCapabilities.Playlists"/>.</summary>
public interface IPlaylistManager
{
    /// <summary>Lists playlists.</summary>
    Task<IReadOnlyList<ServerPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken);

    /// <summary>Creates a playlist and returns its server ID.</summary>
    Task<string> CreatePlaylistAsync(string name, IReadOnlyList<string> trackIds, CancellationToken cancellationToken);

    /// <summary>Appends tracks to a playlist.</summary>
    Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken cancellationToken);
}

/// <summary>A playlist on the server.</summary>
/// <param name="Id">Server ID.</param>
/// <param name="Name">Name.</param>
/// <param name="TrackCount">Number of tracks.</param>
public sealed record ServerPlaylist(string Id, string Name, int TrackCount);

/// <summary>Optional: favourites. Capability <see cref="ConnectorCapabilities.Favourites"/>.</summary>
public interface IFavouritesManager
{
    /// <summary>Stars or unstars a track.</summary>
    Task SetFavouriteAsync(string trackId, bool favourite, CancellationToken cancellationToken);
}

/// <summary>Optional: reading the server's library. Capability <see cref="ConnectorCapabilities.LibraryRead"/>.</summary>
public interface ILibraryReader
{
    /// <summary>Streams every track the server knows, for cross-checking with the library scan.</summary>
    IAsyncEnumerable<ServerTrack> GetTracksAsync(CancellationToken cancellationToken);
}

/// <summary>A track as the server sees it.</summary>
/// <param name="Id">Server track ID.</param>
/// <param name="Path">Library-relative path, if the server exposes it.</param>
/// <param name="Title">Title.</param>
/// <param name="Artist">Artist.</param>
/// <param name="Album">Album.</param>
/// <param name="Duration">Length.</param>
/// <param name="MusicBrainzRecordingId">MusicBrainz recording ID, if the server exposes it.</param>
public sealed record ServerTrack(
    string Id,
    string? Path,
    string Title,
    string? Artist,
    string? Album,
    TimeSpan? Duration,
    string? MusicBrainzRecordingId);

/// <summary>Capability names a connector can declare in its manifest.</summary>
public static class ConnectorCapabilities
{
    /// <summary>Implements <see cref="IFullScan"/>.</summary>
    public const string FullScan = "full-scan";

    /// <summary>Implements <see cref="IPlaylistManager"/>.</summary>
    public const string Playlists = "playlists";

    /// <summary>Implements <see cref="IFavouritesManager"/>.</summary>
    public const string Favourites = "favourites";

    /// <summary>Implements <see cref="ILibraryReader"/>.</summary>
    public const string LibraryRead = "library-read";

    /// <summary><see cref="IMediaServerConnector.TriggerScanAsync"/> honours its paths.</summary>
    public const string SelectiveScan = "selective-scan";
}
