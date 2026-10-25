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
public sealed record LibraryArtist(
    Guid Id,
    string Name,
    string? SortName,
    string? Disambiguation,
    IReadOnlyList<ProviderId> Ids,
    int OwnedAlbums,
    int MissingAlbums,
    int CoveredAlbums,
    int Files);

public sealed record ArtistDetail(
    Guid Id,
    string Name,
    string? SortName,
    string? Disambiguation,
    string? Country,
    IReadOnlyList<ProviderId> Ids,
    bool InLibrary,
    DateTime? DiscographyRefreshedAt,
    IReadOnlyList<AlbumSummary> Albums);

/// <param name="HiddenByDefault">The missing view's filters hide it when it isn't owned.</param>
/// <param name="OwnedTracks">Tracks owned (here or elsewhere) on the owned edition, or the best-covered edition.</param>
/// <param name="TotalTracks">Tracks on that edition; null until the album's editions have been fetched.</param>
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
    IReadOnlyList<ProviderId> Ids);

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
    IReadOnlyList<ProviderId> Ids);

public sealed record MissingAlbum(string ArtistName, AlbumSummary Album);

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
    SuggestedTrack? Suggestion);

public sealed record SuggestedTrack(Guid TrackId, string Title, string ArtistCredit, double? DurationSeconds, Guid? EditionId, Guid? AlbumId, string? AlbumTitle);

public sealed record SearchResults(
    IReadOnlyList<SearchArtist> Artists,
    IReadOnlyList<AlbumSummary> Albums,
    IReadOnlyList<SearchTrack> Tracks);

public sealed record SearchArtist(Guid Id, string Name, string? Disambiguation, bool InLibrary, IReadOnlyList<ProviderId> Ids);

public sealed record SearchTrack(Guid Id, string Title, string ArtistCredit, double? DurationSeconds, bool Owned, IReadOnlyList<ProviderId> Ids);

public enum TaskState
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

public sealed record TaskInfo(Guid Id, string Kind, TaskState State, string? Progress, string? Error, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt);

/// <param name="LastScan">The most recent finished scan, successful or not: its progress says what it found, its error why it failed.</param>
public sealed record LibraryStatus(int Files, int Matched, int Review, int Pending, DateTime? LastScanAt, TaskInfo? CurrentTask, TaskInfo? LastScan = null);

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

/// <param name="Latest">The newest entry's sequence number, even when no entries are returned.</param>
public sealed record LogPage(IReadOnlyList<LogEntry> Entries, long Latest);
