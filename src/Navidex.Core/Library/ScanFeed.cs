using Navidex.Client;

namespace Navidex.Core.Library;

/// <summary>
/// The files the current scan has read or matched, newest first, so the Activity page can show a scan as it goes: the
/// file name while it's read, then the song once it's matched. Only the latest <see cref="Kept"/> are held.
/// </summary>
public sealed class ScanFeed
{
    public const int Kept = 300;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ScanFile> _files = new(StringComparer.Ordinal);
    private long _seq;

    /// <summary>A new scan starts with an empty list.</summary>
    public void Clear()
    {
        lock (_lock)
            _files.Clear();
    }

    public void Set(string path, ScanFileState state, string? title = null, string? artist = null, string? detail = null) =>
        Put(new ScanFile(0, path, state, title, artist, null, null, null, null, detail, DateTime.UtcNow));

    public void Matched(string path, string title, string artist, Guid? artistId, string? albumTitle, Guid? albumId, string? coverUrl) =>
        Put(new ScanFile(0, path, ScanFileState.Matched, title, artist, artistId, albumTitle, albumId, coverUrl, null, DateTime.UtcNow));

    private void Put(ScanFile file)
    {
        lock (_lock)
        {
            _files[file.Path] = file with { Seq = ++_seq };
            if (_files.Count > Kept * 2)
                foreach (var old in _files.Values.OrderBy(f => f.Seq).Take(_files.Count - Kept).ToList())
                    _files.Remove(old.Path);
        }
    }

    /// <summary>The latest files, the most recently changed first.</summary>
    public IReadOnlyList<ScanFile> Latest(int limit = Kept)
    {
        lock (_lock)
            return _files.Values.OrderByDescending(f => f.Seq).Take(limit).ToList();
    }
}
