using System.Globalization;
using System.Text.RegularExpressions;
using Melodex.Client;

namespace Melodex.Core.Library;

/// <summary>
/// A music file's lyrics: an .lrc file beside it with the same name, or else the lyrics in its tags. Either can be
/// synced (LRC, each line with when it's sung) or plain.
/// </summary>
public static partial class LyricsFiles
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

    public static LyricsKind KindOfText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? LyricsKind.None : Timestamp().IsMatch(text) ? LyricsKind.Synced : LyricsKind.Plain;

    /// <summary>The file's lyrics, line by line.</summary>
    public static TrackLyrics Lyrics(string path) => Read(path) is { } lyrics
        ? new TrackLyrics(KindOfText(lyrics.Text), Parse(lyrics.Text), lyrics.Source)
        : new TrackLyrics(LyricsKind.None, [], null);

    /// <summary>
    /// The lines of LRC or plain lyrics. Synced lines come in the order they're sung: a line sung more than once (with
    /// several times before it) appears at each, and <c>[offset:]</c> shifts them all. Tags such as <c>[ar:]</c> are
    /// left out. In plain lyrics every line is kept, blank ones too, as they separate verses.
    /// </summary>
    public static IReadOnlyList<LyricLine> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (KindOfText(text) != LyricsKind.Synced)
            return lines.Select(l => new LyricLine(null, l.TrimEnd())).SkipWhile(l => l.Text.Length == 0).ToList();

        var offset = 0.0;
        var timed = new List<LyricLine>();
        foreach (var line in lines)
        {
            if (Offset().Match(line) is { Success: true } o && double.TryParse(o.Groups[1].Value, CultureInfo.InvariantCulture, out var ms))
            {
                offset = ms / 1000;
                continue;
            }
            var times = new List<double>();
            var rest = line;
            while (LeadingTimestamp().Match(rest) is { Success: true } m)
            {
                var fraction = m.Groups[3].Success ? double.Parse("0." + m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                times.Add(int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + fraction);
                rest = rest[m.Length..];
            }
            // Word timings (<mm:ss.xx>) in enhanced LRC are dropped, leaving the words.
            var words = WordTimestamp().Replace(rest, "").Trim();
            timed.AddRange(times.Select(t => new LyricLine(Math.Max(0, t - offset), words)));
        }
        return timed.OrderBy(l => l.Seconds).ToList();
    }

    [GeneratedRegex(@"^\s*\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]", RegexOptions.Multiline)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"^\s*\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LeadingTimestamp();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:\.\d{1,3})?>")]
    private static partial Regex WordTimestamp();

    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();
}
