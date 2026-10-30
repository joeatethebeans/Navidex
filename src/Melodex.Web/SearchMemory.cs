using Melodex.Client;

namespace Melodex.Web;

/// <summary>
/// The last search, kept for a while after leaving the Search page (as long as the person's settings say), so coming
/// back shows it as it was, scrolled to where it was, rather than asking MusicBrainz all over again.
/// </summary>
public sealed class SearchMemory(AuthState auth)
{
    /// <summary>A search as it was left.</summary>
    /// <param name="Busy">MusicBrainz hadn't answered everything yet, so it's asked again on coming back.</param>
    public sealed record Kept(string Query, Guid? FileId, string Kind, string Owned, string Sort, bool Descending, SearchResults Results,
        bool Busy, double ScrollY);

    private Kept? _kept;
    private DateTime _leftAt;

    private TimeSpan KeepFor => TimeSpan.FromMinutes(auth.Status?.Appearance?.KeepSearchMinutes ?? new Appearance().KeepSearchMinutes);

    public void Keep(Kept search)
    {
        _kept = search;
        _leftAt = DateTime.UtcNow;
    }

    /// <summary>The search left last, if it's recent enough to come back to.</summary>
    public Kept? Recall()
    {
        if (_kept is not null && DateTime.UtcNow - _leftAt > KeepFor)
            _kept = null;
        return _kept;
    }

    public void Forget() => _kept = null;
}
