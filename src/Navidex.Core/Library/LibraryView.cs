using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Contracts.Catalogue;
using Navidex.Core.Data;

namespace Navidex.Core.Library;

/// <summary>
/// Owned and missing, computed by comparing catalogue albums with library files. Nothing here is stored as truth:
/// it's all derived from the catalogue, the files' matches and the settings.
/// </summary>
public sealed class LibraryView(NavidexDbContext db, SettingsService settingsService)
{
    private sealed record Listing(Guid EditionId, int Disc, int Position, string Number, string Title, Guid TrackId, string ArtistCredit, TimeSpan? Duration);

    private sealed record OwnedFile(Guid Id, string Path, Guid TrackId, Guid? EditionId);

    /// <summary>What's in the library, loaded once for a set of artists.</summary>
    private sealed class Snapshot
    {
        public required LibrarySettings Settings { get; init; }
        public required List<Album> Albums { get; init; }
        public required ILookup<Guid, Edition> EditionsByAlbum { get; init; }
        public required ILookup<Guid, Listing> ListingsByEdition { get; init; }
        public required ILookup<Guid, OwnedFile> FilesByEdition { get; init; }
        public required ILookup<Guid, OwnedFile> FilesByTrack { get; init; }

        /// <summary>Tracks owned anywhere in the library, including recordings that share an ISRC with an owned one.</summary>
        public required HashSet<Guid> OwnedAnywhere { get; init; }

        public required ILookup<Guid, ProviderId> AlbumIds { get; init; }
    }

    private async Task<Snapshot> LoadAsync(IReadOnlyCollection<Guid> artistIds, CancellationToken ct)
    {
        var settings = await settingsService.GetLibraryAsync(ct);
        var albums = await db.Albums.Where(a => artistIds.Contains(a.ArtistId)).ToListAsync(ct);
        var albumIds = albums.Select(a => a.Id).ToList();
        var editions = await db.Editions.Where(e => albumIds.Contains(e.AlbumId)).ToListAsync(ct);
        var editionIds = editions.Select(e => e.Id).ToList();
        var listings = await db.EditionTracks.Where(et => editionIds.Contains(et.EditionId))
            .Select(et => new Listing(et.EditionId, et.Disc, et.Position, et.Number, et.Title, et.TrackId, et.Track.ArtistCredit, et.Duration ?? et.Track.Duration))
            .ToListAsync(ct);

        var files = await db.LibraryFiles.Where(f => f.TrackId != null)
            .Select(f => new OwnedFile(f.Id, f.Path, f.TrackId!.Value, f.EditionId)).ToListAsync(ct);
        var owned = files.Select(f => f.TrackId).ToHashSet();

        // Different recordings sharing an ISRC are the same song as far as owning it goes.
        var ownedIsrcs = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && m.Provider == Providers.Isrc && owned.Contains(m.EntityId))
            .Select(m => m.Value).Distinct().ToListAsync(ct);
        var listingTracks = listings.Select(l => l.TrackId).Distinct().ToList();
        var sharingIsrc = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && m.Provider == Providers.Isrc
                                                       && ownedIsrcs.Contains(m.Value) && listingTracks.Contains(m.EntityId))
            .Select(m => m.EntityId).ToListAsync(ct);
        owned.UnionWith(sharingIsrc);

        var providerIds = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Album && albumIds.Contains(m.EntityId))
            .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct);

        return new Snapshot
        {
            Settings = settings,
            Albums = albums,
            EditionsByAlbum = editions.ToLookup(e => e.AlbumId),
            ListingsByEdition = listings.ToLookup(l => l.EditionId),
            FilesByEdition = files.Where(f => f.EditionId != null).ToLookup(f => f.EditionId!.Value),
            FilesByTrack = files.ToLookup(f => f.TrackId),
            OwnedAnywhere = owned,
            AlbumIds = providerIds.ToLookup(p => p.EntityId, p => new ProviderId(p.Provider, p.Value)),
        };
    }

    private sealed record Evaluation(Album Album, AlbumStatus Status, Edition? OwnedEdition, Edition? ShownEdition, int? OwnedTracks, int? TotalTracks);

    private static Evaluation Evaluate(Snapshot s, Album album)
    {
        var editions = s.EditionsByAlbum[album.Id].ToList();
        var editionIds = editions.Select(e => e.Id).ToHashSet();
        var ownedEdition = editions
            .Select(e => (Edition: e, Files: s.FilesByEdition[e.Id].Count()))
            .Where(x => x.Files > 0)
            .OrderByDescending(x => x.Files).ThenByDescending(x => x.Edition.TrackCount)
            .Select(x => x.Edition).FirstOrDefault();

        if (ownedEdition is not null)
        {
            var listings = s.ListingsByEdition[ownedEdition.Id].ToList();
            var statuses = listings.Select(l => TrackStatusOf(s, l, editionIds)).ToList();
            var missing = statuses.Count(t => t == TrackStatus.Missing);
            return new Evaluation(album, missing == 0 ? AlbumStatus.Complete : AlbumStatus.Partial, ownedEdition, ownedEdition,
                listings.Count - missing, listings.Count);
        }

        if (album.EditionsRefreshedAt is null || editions.Count == 0)
            return new Evaluation(album, AlbumStatus.Missing, null, null, null, null);

        // Not owned: find the edition most covered by recordings owned elsewhere, preferring official editions.
        var official = editions.Where(e => string.Equals(e.Status, "Official", StringComparison.OrdinalIgnoreCase)).ToList();
        var best = (official.Count > 0 ? official : editions)
            .Select(e =>
            {
                var tracks = s.ListingsByEdition[e.Id].ToList();
                return (Edition: e, Total: tracks.Count, Owned: tracks.Count(t => s.OwnedAnywhere.Contains(t.TrackId)));
            })
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Owned == x.Total).ThenByDescending(x => x.Owned).ThenByDescending(x => x.Total)
            .FirstOrDefault();
        if (best.Edition is null)
            return new Evaluation(album, AlbumStatus.Missing, null, null, null, null);

        var covered = s.Settings.CountsOwnedAnywhere && best.Owned == best.Total;
        return new Evaluation(album, covered ? AlbumStatus.Covered : AlbumStatus.Missing, null, best.Edition,
            s.Settings.CountsOwnedAnywhere ? best.Owned : 0, best.Total);
    }

    private static TrackStatus TrackStatusOf(Snapshot s, Listing listing, HashSet<Guid> albumEditions)
    {
        // Owned here if a file for this recording sits on any edition of this album.
        if (s.FilesByTrack[listing.TrackId].Any(f => f.EditionId is { } e && albumEditions.Contains(e)))
            return TrackStatus.Owned;
        return s.Settings.CountsOwnedAnywhere && s.OwnedAnywhere.Contains(listing.TrackId) ? TrackStatus.OwnedElsewhere : TrackStatus.Missing;
    }

    private static AlbumSummary Summarise(Snapshot s, Evaluation e) => new(
        e.Album.Id, e.Album.ArtistId, e.Album.Title, e.Album.ArtistCredit, e.Album.Type, e.Album.SecondaryTypes, e.Album.FirstReleaseDate,
        e.Status, !s.Settings.Shows(e.Album), e.Album.Ignored, e.OwnedTracks, e.TotalTracks, s.AlbumIds[e.Album.Id].ToList());

    private static bool IsListedMissing(Snapshot s, Evaluation e) =>
        e.Status == AlbumStatus.Missing && s.Settings.Shows(e.Album) && !e.Album.Ignored;

    private static IEnumerable<Evaluation> Ordered(IEnumerable<Evaluation> evaluations) =>
        evaluations.OrderBy(e => e.Album.FirstReleaseDate ?? "9999").ThenBy(e => e.Album.Title);

    // ---- Queries ----

    public async Task<IReadOnlyList<LibraryArtist>> GetLibraryArtistsAsync(CancellationToken ct)
    {
        var artists = await db.Artists.Where(a => a.InLibrary).ToListAsync(ct);
        var ids = artists.Select(a => a.Id).ToList();
        var s = await LoadAsync(ids, ct);
        var evaluations = s.Albums.Select(a => Evaluate(s, a)).ToLookup(e => e.Album.ArtistId);
        var files = await db.LibraryFiles.Where(f => f.ArtistId != null).GroupBy(f => f.ArtistId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var artistIds = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Artist && ids.Contains(m.EntityId))
            .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct);
        var idLookup = artistIds.ToLookup(m => m.EntityId, m => new ProviderId(m.Provider, m.Value));

        return artists
            .Select(a =>
            {
                var albums = evaluations[a.Id].ToList();
                return new LibraryArtist(a.Id, a.Name, a.SortName, a.Disambiguation, idLookup[a.Id].ToList(),
                    albums.Count(e => e.Status is AlbumStatus.Complete or AlbumStatus.Partial),
                    albums.Count(e => IsListedMissing(s, e)),
                    albums.Count(e => e.Status == AlbumStatus.Covered && s.Settings.Shows(e.Album)),
                    files.GetValueOrDefault(a.Id));
            })
            .OrderBy(a => a.SortName ?? a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ArtistDetail?> GetArtistAsync(Guid id, CancellationToken ct)
    {
        var artist = await db.Artists.FindAsync([id], ct);
        if (artist is null)
            return null;
        var s = await LoadAsync([id], ct);
        var albums = Ordered(s.Albums.Select(a => Evaluate(s, a))).Select(e => Summarise(s, e)).ToList();
        var ids = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Artist && m.EntityId == id)
            .Select(m => new ProviderId(m.Provider, m.Value)).ToListAsync(ct);
        return new ArtistDetail(artist.Id, artist.Name, artist.SortName, artist.Disambiguation, artist.Country, ids,
            artist.InLibrary, artist.AlbumsRefreshedAt, albums);
    }

    public async Task<AlbumDetail?> GetAlbumAsync(Guid id, Guid? editionId, CancellationToken ct)
    {
        var album = await db.Albums.Include(a => a.Artist).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (album is null)
            return null;
        var s = await LoadAsync([album.ArtistId], ct);
        var evaluation = Evaluate(s, s.Albums.First(a => a.Id == id));
        var editions = s.EditionsByAlbum[id].OrderBy(e => e.ReleaseDate ?? "9999").ThenBy(e => e.Country).ToList();
        var editionIds = editions.Select(e => e.Id).ToHashSet();
        var shown = editions.FirstOrDefault(e => e.Id == editionId) ?? evaluation.ShownEdition ?? editions.FirstOrDefault();

        var editionProviderIds = (await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Edition && editionIds.Contains(m.EntityId))
            .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct)).ToLookup(m => m.EntityId, m => new ProviderId(m.Provider, m.Value));
        var editionSummaries = editions.Select(e => new EditionSummary(e.Id, e.Title, e.ReleaseDate, e.Country, e.Status, e.Disambiguation,
            e.Formats, e.TrackCount, s.FilesByEdition[e.Id].Count(), editionProviderIds[e.Id].ToList())).ToList();

        var tracks = new List<TrackRow>();
        if (shown is not null)
        {
            var listings = s.ListingsByEdition[shown.Id].OrderBy(l => l.Disc).ThenBy(l => l.Position).ToList();
            var trackIds = listings.Select(l => l.TrackId).ToList();
            var trackProviderIds = (await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && trackIds.Contains(m.EntityId))
                .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct)).ToLookup(m => m.EntityId, m => new ProviderId(m.Provider, m.Value));
            foreach (var listing in listings)
            {
                var status = TrackStatusOf(s, listing, editionIds);
                var file = s.FilesByTrack[listing.TrackId].FirstOrDefault(f => f.EditionId is { } e && editionIds.Contains(e));
                tracks.Add(new TrackRow(listing.TrackId, listing.Disc, listing.Position, listing.Number, listing.Title, listing.ArtistCredit,
                    listing.Duration?.TotalSeconds, status, file?.Path, trackProviderIds[listing.TrackId].ToList()));
            }
        }

        return new AlbumDetail(Summarise(s, evaluation), album.Artist.Name, evaluation.OwnedEdition?.Id, shown?.Id, editionSummaries, tracks);
    }

    public async Task<IReadOnlyList<AlbumSummary>?> GetArtistAlbumsAsync(Guid id, CancellationToken ct) =>
        (await GetArtistAsync(id, ct))?.Albums;

    /// <summary>Summaries for any albums, for example search results, in the order given.</summary>
    public async Task<IReadOnlyList<AlbumSummary>> GetAlbumSummariesAsync(IReadOnlyList<Guid> albumIds, CancellationToken ct)
    {
        var artistIds = await db.Albums.Where(a => albumIds.Contains(a.Id)).Select(a => a.ArtistId).Distinct().ToListAsync(ct);
        var s = await LoadAsync(artistIds, ct);
        var byId = s.Albums.ToDictionary(a => a.Id);
        return albumIds.Where(byId.ContainsKey).Select(id => Summarise(s, Evaluate(s, byId[id]))).ToList();
    }

    public async Task<IReadOnlyList<MissingAlbum>> GetMissingAsync(CancellationToken ct)
    {
        var artists = await db.Artists.Where(a => a.InLibrary).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var s = await LoadAsync(artists.Keys, ct);
        return Ordered(s.Albums.Select(a => Evaluate(s, a)).Where(e => IsListedMissing(s, e)))
            .Select(e => new MissingAlbum(artists[e.Album.ArtistId], Summarise(s, e)))
            .OrderBy(m => m.ArtistName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<ReviewItem>> GetReviewAsync(CancellationToken ct)
    {
        var files = await db.LibraryFiles.Where(f => f.TrackId == null && f.MatchedAt != null).OrderBy(f => f.Path).ToListAsync(ct);
        var suggestedIds = files.Select(f => f.SuggestedTrackId).OfType<Guid>().ToList();
        var tracks = await db.Tracks.Where(t => suggestedIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var editionIds = files.Select(f => f.SuggestedEditionId).OfType<Guid>().ToList();
        var editions = await db.Editions.Include(e => e.Album).Where(e => editionIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);

        return files.Select(f =>
        {
            var tags = FileTags.Parse(f.TagsJson);
            SuggestedTrack? suggestion = null;
            if (f.SuggestedTrackId is { } trackId && tracks.TryGetValue(trackId, out var track))
            {
                var edition = f.SuggestedEditionId is { } e ? editions.GetValueOrDefault(e) : null;
                suggestion = new SuggestedTrack(track.Id, track.Title, track.ArtistCredit, track.Duration?.TotalSeconds, edition?.Id, edition?.AlbumId, edition?.Album.Title);
            }
            return new ReviewItem(f.Id, f.Path, f.ReviewReason, f.MatchConfidence, tags.Title, tags.Artist ?? tags.AlbumArtist, tags.Album,
                f.Duration?.TotalSeconds, suggestion);
        }).ToList();
    }
}
