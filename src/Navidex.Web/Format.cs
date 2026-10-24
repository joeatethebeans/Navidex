using Navidex.Client;
using Navidex.Contracts.Catalogue;

namespace Navidex.Web;

public static class Format
{
    public static string Duration(double? seconds) =>
        seconds is { } s ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "";

    public static string Year(string? date) => date is { Length: >= 4 } ? date[..4] : "";

    public static string TypeName(AlbumType type) => type switch
    {
        AlbumType.Album => "Albums",
        AlbumType.Ep => "EPs",
        AlbumType.Single => "Singles",
        AlbumType.Broadcast => "Broadcasts",
        _ => "Other",
    };

    public static string TypeLabel(AlbumType type) => type switch
    {
        AlbumType.Ep => "EP",
        _ => type.ToString(),
    };

    public static string Secondary(IReadOnlyList<AlbumSecondaryType> types) =>
        string.Join(", ", types.Select(t => t switch
        {
            AlbumSecondaryType.DjMix => "DJ mix",
            AlbumSecondaryType.AudioDrama => "Audio drama",
            AlbumSecondaryType.FieldRecording => "Field recording",
            AlbumSecondaryType.Spokenword => "Spoken word",
            _ => t.ToString(),
        }));

    public static string Tracks(AlbumSummary album) =>
        album.TotalTracks is { } total ? $"{album.OwnedTracks ?? 0} of {total}" : "";
}
