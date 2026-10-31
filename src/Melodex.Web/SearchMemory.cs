namespace Melodex.Web;

/// <summary>
/// The last search, kept for the rest of the session after leaving the Search page, so coming back shows it as it was,
/// scrolled to where it was, rather than asking MusicBrainz all over again.
/// </summary>
public sealed class SearchMemory
{
    /// <summary>A search as it was left.</summary>
    /// <param name="Busy">MusicBrainz hadn't answered everything yet, so it's asked again on coming back.</param>
    public sealed record Kept(string Query, Guid? FileId, string Kind, string Owned, string Sort, bool Descending, Melodex.Client.SearchResults Results,
        bool Busy, double ScrollY);

    private Kept? _kept;

    public void Keep(Kept search) => _kept = search;

    /// <summary>The search left last, to come back to.</summary>
    public Kept? Recall() => _kept;

    public void Forget() => _kept = null;
}
