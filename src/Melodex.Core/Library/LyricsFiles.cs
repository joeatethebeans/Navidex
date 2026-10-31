using Melodex.Client;

namespace Melodex.Core.Library;

/// <summary>
/// A music file's lyrics: an .lrc file beside it with the same name, or else the lyrics in its tags. Either can be
/// synced (LRC, each line with when it's sung) or plain.
/// </summary>
public static class LyricsFiles
{
    /// <summary>Where a file's .lrc goes: beside it, with the same name.</summary>
    public static string SidecarOf(string path) => Path.ChangeExtension(path, ".lrc");

    /// <summary>The file's lyrics text and where it came from, or null if it has none (or can't be read).</summary>
    public static (string Text, string Source)? Read(string path)
    {
        var sidecar = SidecarOf(path);
        try
        {
            if (File.Exists(sidecar) && File.ReadAllText(sidecar) is { } text && !string.IsNullOrWhiteSpace(text))
                return (text, "sidecar");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable: the tags may still have some.
        }
        try
        {
            using var file = TagLib.File.Create(path);
            return file.Tag.Lyrics is { } embedded && !string.IsNullOrWhiteSpace(embedded) ? (embedded, "embedded") : null;
        }
        catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What lyrics the file has.</summary>
    public static LyricsKind KindOf(string path) => Read(path) is { } lyrics ? KindOfText(lyrics.Text) : LyricsKind.None;

    public static LyricsKind KindOfText(string? text) => LyricsText.KindOf(text);

    /// <summary>The file's lyrics, line by line.</summary>
    public static TrackLyrics Lyrics(string path) => Read(path) is { } lyrics
        ? new TrackLyrics(KindOfText(lyrics.Text), Parse(lyrics.Text), lyrics.Source)
        : new TrackLyrics(LyricsKind.None, [], null);

    /// <inheritdoc cref="LyricsText.Lines"/>
    public static IReadOnlyList<LyricLine> Parse(string text) => LyricsText.Lines(text);
}
