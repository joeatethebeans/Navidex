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

    /// <summary>A request's state in a word or two, with progress for requests made of parts.</summary>
    public static string RequestLabel(RequestInfo r) => r.State switch
    {
        Client.RequestState.Queued => "Queued",
        Client.RequestState.InProgress when r.Parts is { } p => $"Fetching {p.InLibrary} of {p.Total}",
        Client.RequestState.InProgress => "In progress",
        Client.RequestState.Searching => "Searching",
        Client.RequestState.Fetching => "Fetching",
        Client.RequestState.Verifying => "Checking",
        Client.RequestState.Importing or Client.RequestState.Notifying => "Filing",
        Client.RequestState.InLibrary => "In library",
        Client.RequestState.NeedsReview => "Needs review",
        Client.RequestState.Failed => "Failed",
        _ => "Cancelled",
    };

    /// <summary>The badge colour for a request state.</summary>
    public static string RequestBadge(Client.RequestState state) => state switch
    {
        Client.RequestState.InLibrary => "owned",
        Client.RequestState.NeedsReview => "partial",
        Client.RequestState.Failed => "missing",
        Client.RequestState.Cancelled => "neutral",
        _ => "active",
    };

    public static string TaskLabel(TaskState state) => state switch
    {
        TaskState.Queued => "Waiting",
        TaskState.Running => "Running",
        TaskState.Succeeded => "Done",
        _ => "Failed",
    };

    public static string TaskBadge(TaskState state) => state switch
    {
        TaskState.Succeeded => "owned",
        TaskState.Failed => "missing",
        _ => "active",
    };

    /// <summary>How long ago, roughly: "just now", "5 min ago", "3 h ago", or the date.</summary>
    public static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : utc.ToLocalTime().ToString("d MMM");
    }
}
