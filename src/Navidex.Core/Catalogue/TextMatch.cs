using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Navidex.Core.Catalogue;

/// <summary>Normalisation and similarity for fuzzy matching of titles and names.</summary>
public static partial class TextMatch
{
    // Qualifiers that don't change which recording a title refers to. "Live", "remix", "edit" and the like do, so
    // they're kept and count against a match.
    [GeneratedRegex(@"[\(\[](?:[^\)\]]*\b(?:album version|original (?:mix|version)|remaster(?:ed)?|explicit|clean|bonus track|lp version)\b[^\)\]]*)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TrackQualifier();

    // Qualifiers that mark an edition of an album rather than a different album.
    [GeneratedRegex(@"[\(\[](?:[^\)\]]*\b(?:deluxe|expanded|anniversary|remaster(?:ed)?|special|edition|bonus|version|reissue|flac|mp3|aac|m4a|ogg|opus|320|v0|web|cd|vinyl|\d{4})\b[^\)\]]*)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex AlbumQualifier();

    [GeneratedRegex(@"\s*[\(\[]?\b(?:feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"^the\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingThe();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphanumeric();

    /// <summary>Lower case, no accents or punctuation, single spaces.</summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var s = text.ToLowerInvariant()
            .Replace("œ", "oe").Replace("æ", "ae").Replace("ß", "ss").Replace("ø", "o").Replace("ł", "l").Replace("đ", "d")
            .Replace("&", " and ").Replace("’", "'").Replace("'", "");
        var decomposed = s.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return NonAlphanumeric().Replace(builder.ToString(), " ").Trim();
    }

    public static string NormaliseTitle(string? title) => Normalise(TrackQualifier().Replace(title ?? "", " "));

    public static string NormaliseAlbum(string? album) => Normalise(AlbumQualifier().Replace(album ?? "", " "));

    public static string NormaliseArtist(string? artist) =>
        Normalise(LeadingThe().Replace(Featuring().Replace(artist ?? "", ""), ""));

    /// <summary>From 0 to 1: the better of character-level similarity and word-order-insensitive similarity.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
            return 1;
        if (a.Length == 0 || b.Length == 0)
            return 0;
        if (a == b)
            return 1;
        var sorted = Ratio(SortWords(a), SortWords(b));
        return Math.Max(Ratio(a, b), sorted);
    }

    private static string SortWords(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    private static double Ratio(string a, string b) => 1 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
