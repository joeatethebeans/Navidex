namespace Navidex.Contracts.Catalogue;

/// <summary>
/// An identifier in some provider's scheme. The core maps every provider ID onto its own internal ID
/// through the crosswalk, so plugins only ever deal in their own IDs.
/// </summary>
/// <param name="Provider">Scheme name, for example <c>musicbrainz</c>, <c>deezer</c>, <c>isrc</c> or <c>upc</c>. See <see cref="Providers"/>.</param>
/// <param name="Value">The ID within that scheme.</param>
public sealed record ProviderId(string Provider, string Value)
{
    /// <inheritdoc />
    public override string ToString() => $"{Provider}:{Value}";
}

/// <summary>Well-known provider scheme names. Plugins may use others; keep them lowercase.</summary>
public static class Providers
{
    /// <summary>MusicBrainz MBIDs (artist, release group, release or recording, depending on the entity).</summary>
    public const string MusicBrainz = "musicbrainz";

    /// <summary>International Standard Recording Code. Provider-neutral glue for tracks.</summary>
    public const string Isrc = "isrc";

    /// <summary>Universal Product Code / EAN barcode. Provider-neutral glue for editions.</summary>
    public const string Upc = "upc";

    /// <summary>Deezer IDs.</summary>
    public const string Deezer = "deezer";

    /// <summary>Discogs IDs.</summary>
    public const string Discogs = "discogs";

    /// <summary>Spotify IDs.</summary>
    public const string Spotify = "spotify";
}
