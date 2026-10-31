using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Melodex.Client;

/// <summary>Lyrics as text: synced (LRC, each line with when it's sung) or plain, read line by line and written back.</summary>
public static partial class LyricsText
{
    public static LyricsKind KindOf(string? text) =>
        string.IsNullOrWhiteSpace(text) ? LyricsKind.None : Timestamp().IsMatch(text) ? LyricsKind.Synced : LyricsKind.Plain;

    /// <summary>
    /// The lines of LRC or plain lyrics. Synced lines come in the order they're sung: a line sung more than once (with
    /// several times before it) appears at each, and <c>[offset:]</c> shifts them all. Tags such as <c>[ar:]</c> are
    /// left out. In plain lyrics every line is kept, blank ones too, as they separate verses.
    /// </summary>
    public static IReadOnlyList<LyricLine> Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (KindOf(text) != LyricsKind.Synced)
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
                times.Add(Seconds(m));
                rest = rest[m.Length..];
            }
            // Word timings (<mm:ss.xx>) in enhanced LRC are dropped, leaving the words.
            var words = WordTimestamp().Replace(rest, "").Trim();
            timed.AddRange(times.Select(t => new LyricLine(Math.Max(0, t - offset), words)));
        }
        return timed.OrderBy(l => l.Seconds).ToList();
    }

    /// <summary>
    /// The lines as text: LRC in the order they're sung when every line has a time, else plain lyrics, one line each.
    /// </summary>
    public static string Write(IEnumerable<LyricLine> lines)
    {
        var all = lines.ToList();
        var text = new StringBuilder();
        if (all.Count > 0 && all.All(l => l.Seconds is not null))
        {
            foreach (var line in all.OrderBy(l => l.Seconds))
                text.Append('[').Append(Time(line.Seconds!.Value, lrc: true)).Append("] ").Append(line.Text.Trim()).Append('\n');
        }
        else
        {
            foreach (var line in all)
                text.Append(line.Text.TrimEnd()).Append('\n');
        }
        return text.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>A time as LRC has it (<c>01:02.50</c>), or shorter for people to read and type (<c>1:02.50</c>).</summary>
    public static string Time(double seconds, bool lrc = false)
    {
        var hundredths = (long)Math.Round(Math.Max(0, seconds) * 100);
        var minutes = hundredths / 6000;
        var rest = hundredths % 6000;
        return string.Create(CultureInfo.InvariantCulture, $"{(lrc ? minutes.ToString("00") : minutes.ToString())}:{rest / 100:00}.{rest % 100:00}");
    }

    /// <summary>Reads a time someone typed: <c>1:02.5</c>, <c>01:02</c>, or seconds alone.</summary>
    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        var trimmed = text?.Trim().Trim('[', ']') ?? "";
        if (TypedTime().Match(trimmed) is { Success: true } m)
        {
            seconds = Seconds(m);
            return int.Parse(m.Groups[2].Value) < 60;
        }
        return double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;
    }

    private static double Seconds(Match m)
    {
        var fraction = m.Groups[3].Success ? double.Parse("0." + m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + fraction;
    }

    [GeneratedRegex(@"^\s*\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]", RegexOptions.Multiline)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"^\s*\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LeadingTimestamp();

    [GeneratedRegex(@"^(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?$")]
    private static partial Regex TypedTime();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:\.\d{1,3})?>")]
    private static partial Regex WordTimestamp();

    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();
}
