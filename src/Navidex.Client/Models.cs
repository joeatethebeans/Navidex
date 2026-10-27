using System.Text.Json;
using System.Text.Json.Serialization;
using Navidex.Contracts.Catalogue;

namespace Navidex.Client;

// Models of the public API, /api/v1. Every catalogue entity carries its internal ID plus every provider ID the core
// knows it by, so apps can match against their own data. Enums are camelCase strings on the wire.

/// <summary>Serializer settings matching the API.</summary>
public static class NavidexJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}

/// <summary>Whether an album is in the library.</summary>
public enum AlbumStatus
{
    /// <summary>An edition is in the library with every track.</summary>
    Complete,

    /// <summary>An edition is in the library, but some of its tracks are missing.</summary>
    Partial,

    /// <summary>
    /// No edition is in the library, but every track of one is, on other albums. Only official editions count when the
    /// album has any, so a one-track promo doesn't hide a B-side. Only when duplicate tracks are
    /// fetched once (<see cref="DuplicateTracks.KeepInAlbum"/> or <see cref="DuplicateTracks.KeepInSingle"/>).
    /// </summary>
    Covered,

    /// <summary>Not in the library.</summary>
    Missing,
}

/// <summary>Whether a track is in the library.</summary>
public enum TrackStatus
{
    /// <summary>A file for it is in this album.</summary>
    Owned,

    /// <summary>The same recording is in the library on another album, and duplicates are fetched once.</summary>
    OwnedElsewhere,

    /// <summary>Not in the library.</summary>
    Missing,

    /// <summary>Not in the library, and ignored: it doesn't count against the album.</summary>
    Ignored,
}

/// <summary>How to treat a song that appears on more than one album, for example on an album and on a single.</summary>
public enum DuplicateTracks
{
    /// <summary>Fetch it for each album and keep a copy in each. Every album is judged on its own files.</summary>
    KeepBoth,

    /// <summary>Fetch it once and keep it in the album proper. Owning it anywhere counts as owning it everywhere.</summary>
    KeepInAlbum,

    /// <summary>Fetch it once and keep it in the single's folder. Owning it anywhere counts as owning it everywhere.</summary>
    KeepInSingle,
}

/// <summary>Library-wide settings for matching and the missing view.</summary>
public sealed record LibrarySettings
{
    public DuplicateTracks DuplicateTracks { get; init; } = DuplicateTracks.KeepInAlbum;

    /// <summary>Fuzzy matches under this confidence, from 0 to 1, go to the review list.</summary>
    public double MatchThreshold { get; init; } = 0.85;

    /// <summary>
    /// How many times a scan tries an album while the catalogue source (MusicBrainz) says it's busy. Each busy album
    /// goes to the back of the queue; after the last attempt it waits for the next scan. From 1 to 10.
    /// </summary>
    public int AttemptsWhenBusy { get; init; } = 3;

    /// <summary>Album types the missing view shows by default.</summary>
    public IReadOnlyList<AlbumType> ShownTypes { get; init; } = [AlbumType.Album, AlbumType.Ep, AlbumType.Single];

    /// <summary>Hide albums with any secondary type: live, compilation, remix, demo, soundtrack, interview, mixtape...</summary>
    public bool HideSecondaryTypes { get; init; } = true;

    /// <summary>Hide albums with no official release, such as bootleg-only albums.</summary>
    public bool HideUnofficial { get; init; } = true;

    /// <summary>Whether owning a recording on any album counts for every album it appears on.</summary>
    [JsonIgnore]
    public bool CountsOwnedAnywhere => DuplicateTracks != DuplicateTracks.KeepBoth;
}

/// <summary>An artist in the library, with how complete it is.</summary>
/// <param name="OwnedAlbums">Albums with an edition in the library.</param>
/// <param name="MissingAlbums">Albums the missing view lists, after filters and ignores.</param>
/// <param name="CoveredAlbums">Albums whose tracks are all in the library on other albums.</param>
/// <param name="ImageUrl">A picture of the artist from the artwork plugins, served by the core; a 404 when there's none.</param>
/// <param name="PartialAlbums">Owned albums with tracks missing; included in <paramref name="OwnedAlbums"/>.</param>
public sealed record LibraryArtist(
    Guid Id,
    string Name,
    string? SortName,
    string? Disambiguation,
    IReadOnlyList<ProviderId> Ids,
    int OwnedAlbums,
    int MissingAlbums,
    int CoveredAlbums,
    int Files,
    string? ImageUrl = null,
    int PartialAlbums = 0);

public sealed record ArtistDetail(
    Guid Id,
    string Name,
    string? SortName,
    string? Disambiguation,
    string? Country,
    IReadOnlyList<ProviderId> Ids,
    bool InLibrary,
    DateTime? DiscographyRefreshedAt,
    IReadOnlyList<AlbumSummary> Albums,
    string? ImageUrl = null);

/// <param name="HiddenByDefault">The missing view's filters hide it when it isn't owned.</param>
/// <param name="OwnedTracks">Tracks owned (here or elsewhere) on the owned edition, or the best-covered edition.</param>
/// <param name="TotalTracks">Tracks on that edition, less ignored ones you don't have; null until the album's editions have been fetched.</param>
/// <param name="Ignored">The album is ignored, or every track you don't have on it is.</param>
/// <param name="CoverUrl">The cover from the artwork plugins, served by the core: the owned edition's, or the album's. A 404 when there's none.</param>
public sealed record AlbumSummary(
    Guid Id,
    Guid ArtistId,
    string Title,
    string ArtistCredit,
    AlbumType Type,
    IReadOnlyList<AlbumSecondaryType> SecondaryTypes,
    string? FirstReleaseDate,
    AlbumStatus Status,
    bool HiddenByDefault,
    bool Ignored,
    int? OwnedTracks,
    int? TotalTracks,
    IReadOnlyList<ProviderId> Ids,
    string? CoverUrl = null);

/// <param name="OwnedEditionId">The edition the library has, if any.</param>
/// <param name="ShownEditionId">The edition whose tracks are listed: the owned one, the requested one, or the best fit.</param>
public sealed record AlbumDetail(
    AlbumSummary Album,
    string ArtistName,
    Guid? OwnedEditionId,
    Guid? ShownEditionId,
    IReadOnlyList<EditionSummary> Editions,
    IReadOnlyList<TrackRow> Tracks);

public sealed record EditionSummary(
    Guid Id,
    string Title,
    string? ReleaseDate,
    string? Country,
    string? Status,
    string? Disambiguation,
    string? Formats,
    int TrackCount,
    int FilesInLibrary,
    IReadOnlyList<ProviderId> Ids);

/// <param name="FilePath">The library file, path relative to the music folder, when owned on this album.</param>
/// <param name="Ignored">The song is ignored. A track you have still shows as owned.</param>
public sealed record TrackRow(
    Guid TrackId,
    int Disc,
    int Position,
    string Number,
    string Title,
    string ArtistCredit,
    double? DurationSeconds,
    TrackStatus Status,
    string? FilePath,
    IReadOnlyList<ProviderId> Ids,
    bool Ignored = false);

public sealed record MissingAlbum(string ArtistName, AlbumSummary Album);

/// <summary>An album you have some or all of.</summary>
public sealed record LibraryAlbum(string ArtistName, AlbumSummary Album);

/// <summary>A song you have: one matched file.</summary>
/// <param name="AlbumId">The album the file is filed under, when it's matched to one.</param>
/// <param name="Number">Its track number on that album, such as <c>4</c> or <c>A2</c>.</param>
/// <param name="Path">The file, relative to the music folder.</param>
public sealed record LibraryTrack(
    Guid FileId,
    Guid TrackId,
    string Title,
    string ArtistCredit,
    Guid? ArtistId,
    Guid? AlbumId,
    string? AlbumTitle,
    int? Disc,
    string? Number,
    double? DurationSeconds,
    string Path,
    string? Format,
    string? CoverUrl);

/// <summary>A file that couldn't be matched confidently.</summary>
public sealed record ReviewItem(
    Guid FileId,
    string Path,
    string? Reason,
    double? Confidence,
    string? Title,
    string? Artist,
    string? Album,
    double? DurationSeconds,
    SuggestedTrack? Suggestion,
    bool Excluded = false);

/// <summary>Body of <c>PUT /library/files/{id}/excluded</c>.</summary>
public sealed record ExcludeFile(bool Excluded);

/// <summary>Body of <c>POST /library/review/approve</c> and <c>/library/review/exclude</c>: the files to act on.</summary>
public sealed record ReviewFiles(IReadOnlyList<Guid> FileIds);

/// <summary>How many of the files a bulk review action changed.</summary>
public sealed record ReviewDone(int Count);

/// <summary>Body of <c>POST /library/files/{id}/match</c>. Without an edition, the suggestion's or the likeliest one is used.</summary>
public sealed record MatchFile(Guid TrackId, Guid? EditionId = null);

/// <param name="CoverUrl">The suggested edition's cover, served by the core.</param>
public sealed record SuggestedTrack(Guid TrackId, string Title, string ArtistCredit, double? DurationSeconds, Guid? EditionId, Guid? AlbumId, string? AlbumTitle,
    string? CoverUrl = null, Guid? ArtistId = null);

public sealed record SearchResults(
    IReadOnlyList<SearchArtist> Artists,
    IReadOnlyList<AlbumSummary> Albums,
    IReadOnlyList<SearchTrack> Tracks);

/// <param name="ImageUrl">A picture of the artist, served by the core; a 404 when there's none.</param>
public sealed record SearchArtist(Guid Id, string Name, string? Disambiguation, bool InLibrary, IReadOnlyList<ProviderId> Ids, string? ImageUrl = null);

/// <param name="AlbumId">The album the track is best known from, when the catalogue says.</param>
/// <param name="CoverUrl">That album's cover, served by the core.</param>
public sealed record SearchTrack(Guid Id, string Title, string ArtistCredit, double? DurationSeconds, bool Owned, IReadOnlyList<ProviderId> Ids,
    Guid? ArtistId = null, Guid? AlbumId = null, string? AlbumTitle = null, string? CoverUrl = null);

public enum TaskState
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <param name="Title">What it's working on, such as "Library scan"; filled in by <c>GET /tasks</c>.</param>
public sealed record TaskInfo(Guid Id, string Kind, TaskState State, string? Progress, string? Error, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt,
    string? Title = null)
{
    public bool Active => State is TaskState.Queued or TaskState.Running;

    /// <summary>When it last changed: finished, started or queued.</summary>
    public DateTime UpdatedAt => FinishedAt ?? StartedAt ?? CreatedAt;
}

/// <param name="LastScan">The most recent finished scan, successful or not: its progress says what it found, its error why it failed.</param>
/// <param name="Reading">A scan is reading and matching files, or about to: new requests are refused and queued ones wait until it's done.</param>
/// <param name="Excluded">Files excluded from matching; they're counted in <paramref name="Files"/> but not under review.</param>
public sealed record LibraryStatus(int Files, int Matched, int Review, int Pending, DateTime? LastScanAt, TaskInfo? CurrentTask, TaskInfo? LastScan = null,
    bool Reading = false, int Excluded = 0);

public enum LogEntryLevel
{
    Information,
    Warning,
    Error,
    /// <summary>Information about something that finished well, such as a scan.</summary>
    Success,
}

/// <summary>One event in the activity log.</summary>
/// <param name="Seq">Increases by one per entry; pass the latest you have as <c>after</c> to get only newer ones.</param>
/// <param name="Source">The part of Navidex it came from, such as LibraryScanner or LibraryMatcher.</param>
/// <param name="Error">The underlying error's message, if there was one.</param>
public sealed record LogEntry(long Seq, DateTime Time, LogEntryLevel Level, string Source, string Message, string? Error);

/// <summary>How long the activity log is kept.</summary>
public sealed record LogSettings
{
    /// <summary>Days of entries the Log page shows, from 1 to 90.</summary>
    public int MemoryDays { get; init; } = 7;

    /// <summary>Days the backup files under <c>data/logs</c> are kept, from 1 to 3650; at least <see cref="MemoryDays"/>.</summary>
    public int FileDays { get; init; } = 30;
}

/// <param name="Latest">The newest entry's sequence number, even when no entries are returned.</param>
public sealed record LogPage(IReadOnlyList<LogEntry> Entries, long Latest);

/// <summary>Where a file is in the current scan.</summary>
public enum ScanFileState
{
    /// <summary>Its tags are being read.</summary>
    Reading,

    /// <summary>Being matched.</summary>
    Matching,

    /// <summary>Matched to a song; the song's details are filled in.</summary>
    Matched,

    /// <summary>No confident match; it's on the Review page.</summary>
    Review,

    /// <summary>The catalogue source was busy; it's tried again later in the scan, or next scan.</summary>
    Waiting,

    /// <summary>Read, and waiting its turn to be matched.</summary>
    Queued,
}

/// <summary>A file the running (or last) scan has read or matched, from <c>GET /library/scan/files</c>.</summary>
/// <param name="Seq">When it last changed: pass the highest seen as <c>since</c> to get only later changes.</param>
/// <param name="Order">Its place in the list, which it keeps as it's read and matched.</param>
/// <param name="Path">Relative to the music folder.</param>
/// <param name="Title">The song once matched; before that, the file's title tag if it has one.</param>
/// <param name="Detail">Why it needs review, or what it's waiting for.</param>
public sealed record ScanFile(
    long Seq,
    long Order,
    string Path,
    ScanFileState State,
    string? Title,
    string? ArtistCredit,
    Guid? ArtistId,
    string? AlbumTitle,
    Guid? AlbumId,
    string? CoverUrl,
    string? Detail,
    DateTime At);

/// <summary>The scan's files that changed since a sequence number, from <c>GET /library/scan/files</c>.</summary>
/// <param name="Scan">Which scan they belong to. When it changes, a new scan has started: start the list again.</param>
/// <param name="Latest">The newest change's sequence number, to pass as <c>since</c> next time.</param>
/// <param name="Files">In list order.</param>
public sealed record ScanFeedPage(Guid Scan, long Latest, IReadOnlyList<ScanFile> Files);

/// <summary>What to remove from the library.</summary>
public enum LibraryItemKind
{
    /// <summary>One file: a row of the library's track list.</summary>
    File,

    /// <summary>Every file of an album, whichever edition.</summary>
    Album,

    /// <summary>Every file credited to an artist or filed under one of their albums.</summary>
    Artist,
}

/// <summary>What removing something from the library would affect, from <c>GET /library/removal</c>.</summary>
/// <param name="Files">How many files.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="Folders">The folders they're in, relative to the music folder; up to 20.</param>
/// <param name="CanDelete">Whether the files can be deleted: the import worker, which writes the music folder, is working.</param>
/// <param name="CantDeleteReason">Why not, when they can't.</param>
public sealed record RemovalPreview(int Files, long Bytes, IReadOnlyList<string> Folders, bool CanDelete, string? CantDeleteReason = null);

/// <summary>Removes an artist, album or file from the library, for <c>POST /library/remove</c>.</summary>
/// <param name="DeleteFiles">
/// Delete the files from the music folder, and any folder left with nothing but artwork and the like. Otherwise the
/// files stay where they are, excluded from matching, so Navidex no longer counts them; they can be included again
/// from the Review page.
/// </param>
public sealed record RemoveFromLibrary(LibraryItemKind Kind, Guid Id, bool DeleteFiles);

/// <param name="Removed">Files deleted, or excluded when keeping them.</param>
/// <param name="Failed">Files that couldn't be deleted, with why; they stay in the library.</param>
public sealed record RemovalResult(int Removed, IReadOnlyList<string> Failed);
