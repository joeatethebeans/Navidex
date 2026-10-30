using Melodex.Client;

namespace Melodex.Core.Library;

/// <summary>
/// The files the current scan has read or matched, so the Activity page can show a scan as it goes: the file name
/// while it's read, then the song once it's matched. Each file keeps the place it first took, and every change gets a
/// sequence number, so a page can ask for only what changed since it last looked.
/// </summary>
public sealed class ScanFeed
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ScanFile> _files = new(StringComparer.Ordinal);
    private long _seq;
    private long _order;

    /// <summary>Which scan the files belong to; it changes when a new scan clears the list.</summary>
    public Guid Scan { get; private set; } = Guid.NewGuid();

    /// <summary>A new scan starts with an empty list.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _files.Clear();
            _order = 0;
            Scan = Guid.NewGuid();
        }
    }

    public void Set(string path, ScanFileState state, string? title = null, string? artist = null, string? detail = null) =>
        Put(new ScanFile(0, 0, path, state, title, artist, null, null, null, null, detail, DateTime.UtcNow));

    public void Matched(string path, string title, string artist, Guid? artistId, string? albumTitle, Guid? albumId, string? coverUrl) =>
        Put(new ScanFile(0, 0, path, ScanFileState.Matched, title, artist, artistId, albumTitle, albumId, coverUrl, null, DateTime.UtcNow));

    private void Put(ScanFile file)
    {
        lock (_lock)
        {
            var order = _files.TryGetValue(file.Path, out var old) ? old.Order : ++_order;
            _files[file.Path] = file with { Seq = ++_seq, Order = order };
        }
    }

    /// <summary>The files changed after <paramref name="since"/> (all of them for 0), in the order they were first seen.</summary>
    public ScanFeedPage Since(long since)
    {
        lock (_lock)
            return new ScanFeedPage(Scan, _seq, _files.Values.Where(f => f.Seq > since).OrderBy(f => f.Order).ToList());
    }
}
