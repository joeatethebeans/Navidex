using System.Text.Json.Serialization;

namespace Navidex.Contracts.Catalogue;

/// <summary>Primary type of an album, following MusicBrainz release-group primary types.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlbumType>))]
public enum AlbumType
{
    /// <summary>A full-length album.</summary>
    Album,

    /// <summary>An EP.</summary>
    Ep,

    /// <summary>A single.</summary>
    Single,

    /// <summary>A broadcast, such as a radio session.</summary>
    Broadcast,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>
/// Secondary types of an album. The missing view hides live albums, compilations, remixes, demos and
/// soundtracks by default.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlbumSecondaryType>))]
public enum AlbumSecondaryType
{
    /// <summary>Compilation.</summary>
    Compilation,

    /// <summary>Soundtrack.</summary>
    Soundtrack,

    /// <summary>Spoken word.</summary>
    Spokenword,

    /// <summary>Interview.</summary>
    Interview,

    /// <summary>Audiobook.</summary>
    Audiobook,

    /// <summary>Audio drama.</summary>
    AudioDrama,

    /// <summary>Live recording.</summary>
    Live,

    /// <summary>Remix album.</summary>
    Remix,

    /// <summary>DJ mix.</summary>
    DjMix,

    /// <summary>Mixtape or street album.</summary>
    Mixtape,

    /// <summary>Demo.</summary>
    Demo,

    /// <summary>Field recording.</summary>
    FieldRecording,
}

/// <summary>A performer or group.</summary>
/// <param name="Id">The artist's ID in the plugin's own scheme.</param>
/// <param name="Name">Display name.</param>
/// <param name="SortName">Name for sorting, for example <c>Beatles, The</c>.</param>
/// <param name="Disambiguation">Short text that tells same-named artists apart.</param>
/// <param name="Country">ISO 3166-1 country code, if known.</param>
/// <param name="ExternalIds">This artist's IDs in other providers' schemes, when the plugin knows them.</param>
public sealed record IndexArtist(
    ProviderId Id,
    string Name,
    string? SortName = null,
    string? Disambiguation = null,
    string? Country = null,
    IReadOnlyList<ProviderId>? ExternalIds = null);

/// <summary>A reference to an artist inside another entity.</summary>
/// <param name="Id">The artist's ID in the plugin's own scheme.</param>
/// <param name="Name">The name as credited.</param>
public sealed record ArtistRef(ProviderId Id, string Name);

/// <summary>An album as people think of it, across all its editions (a MusicBrainz release group).</summary>
/// <param name="Id">The album's ID in the plugin's own scheme.</param>
/// <param name="Title">Title.</param>
/// <param name="Artist">Primary credited artist.</param>
/// <param name="ArtistCredit">Full artist credit as displayed, for example <c>A feat. B</c>.</param>
/// <param name="Type">Primary type.</param>
/// <param name="SecondaryTypes">Secondary types.</param>
/// <param name="FirstReleaseDate">Earliest release date, as precise as known: <c>yyyy</c>, <c>yyyy-MM</c> or <c>yyyy-MM-dd</c>.</param>
/// <param name="ExternalIds">This album's IDs in other providers' schemes.</param>
/// <param name="HasOfficialRelease">
/// Whether at least one edition is an official release, as opposed to only bootlegs, promos or pseudo-releases.
/// The missing view hides albums without one by default. <c>null</c> when the plugin doesn't know.
/// </param>
public sealed record IndexAlbum(
    ProviderId Id,
    string Title,
    ArtistRef Artist,
    string ArtistCredit,
    AlbumType Type,
    IReadOnlyList<AlbumSecondaryType> SecondaryTypes,
    string? FirstReleaseDate = null,
    IReadOnlyList<ProviderId>? ExternalIds = null,
    bool? HasOfficialRelease = null);

/// <summary>An album with all of its editions and their track listings.</summary>
/// <param name="Album">The album.</param>
/// <param name="Editions">Every edition the plugin knows of.</param>
public sealed record IndexAlbumDetail(IndexAlbum Album, IReadOnlyList<IndexEdition> Editions);

/// <summary>One specific release of an album: deluxe, remaster, regional pressing.</summary>
/// <param name="Id">The edition's ID in the plugin's own scheme (a MusicBrainz release ID).</param>
/// <param name="Title">Title, which may differ from the album's, for example with <c>(Deluxe)</c>.</param>
/// <param name="ReleaseDate">Release date, as precise as known.</param>
/// <param name="Country">ISO 3166-1 country code or <c>XW</c> for worldwide.</param>
/// <param name="Status">Release status, for example <c>Official</c> or <c>Bootleg</c>.</param>
/// <param name="Disambiguation">Short text that tells editions apart.</param>
/// <param name="Upc">Barcode, if known.</param>
/// <param name="Media">Discs or other media, in order.</param>
/// <param name="ExternalIds">This edition's IDs in other providers' schemes.</param>
public sealed record IndexEdition(
    ProviderId Id,
    string Title,
    string? ReleaseDate,
    string? Country,
    string? Status,
    string? Disambiguation,
    string? Upc,
    IReadOnlyList<IndexMedium> Media,
    IReadOnlyList<ProviderId>? ExternalIds = null);

/// <summary>One disc or other medium of an edition.</summary>
/// <param name="Position">1-based position within the edition.</param>
/// <param name="Format">Format, for example <c>CD</c> or <c>Digital Media</c>.</param>
/// <param name="Tracks">Track listing in order.</param>
public sealed record IndexMedium(int Position, string? Format, IReadOnlyList<IndexTrackListing> Tracks);

/// <summary>A track's appearance on a medium.</summary>
/// <param name="Position">1-based position on the medium.</param>
/// <param name="Number">Track number as printed, for example <c>A1</c>.</param>
/// <param name="Title">Title on this edition, which may differ from the recording's.</param>
/// <param name="Duration">Length on this edition.</param>
/// <param name="Track">The recording.</param>
public sealed record IndexTrackListing(int Position, string Number, string Title, TimeSpan? Duration, IndexTrack Track);

/// <summary>A recording, which can appear on several editions.</summary>
/// <param name="Id">The track's ID in the plugin's own scheme (a MusicBrainz recording ID).</param>
/// <param name="Title">Title.</param>
/// <param name="Artist">Primary credited artist.</param>
/// <param name="ArtistCredit">Full artist credit as displayed.</param>
/// <param name="Duration">Length.</param>
/// <param name="Isrcs">ISRCs assigned to the recording.</param>
/// <param name="ExternalIds">This track's IDs in other providers' schemes.</param>
public sealed record IndexTrack(
    ProviderId Id,
    string Title,
    ArtistRef Artist,
    string ArtistCredit,
    TimeSpan? Duration,
    IReadOnlyList<string> Isrcs,
    IReadOnlyList<ProviderId>? ExternalIds = null);
