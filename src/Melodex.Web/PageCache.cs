namespace Melodex.Web;

/// <summary>
/// The artist, album and song pages seen this session, so going back or forward to one (or opening it again) shows it
/// at once, while the page asks for it afresh behind. Only the latest few are kept.
/// </summary>
public sealed class PageCache
{
    private const int Most = 60;

    private readonly Dictionary<string, object> _pages = [];
    private readonly LinkedList<string> _order = [];

    public T? Get<T>(string key) where T : class => _pages.GetValueOrDefault(key) as T;

    /// <summary>Keeps the page as it is now (or forgets it, when it's gone), and hands it back.</summary>
    public T? Keep<T>(string key, T? page) where T : class
    {
        _order.Remove(key);
        if (page is null)
        {
            _pages.Remove(key);
            return null;
        }
        _pages[key] = page;
        _order.AddFirst(key);
        while (_order.Count > Most)
        {
            _pages.Remove(_order.Last!.Value);
            _order.RemoveLast();
        }
        return page;
    }
}
