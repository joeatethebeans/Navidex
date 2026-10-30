using Microsoft.EntityFrameworkCore;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Retrieval;
using Melodex.Core.Data;

namespace Melodex.Core.Requests;

/// <summary>One track to fetch: its place on the edition, and what the catalogue knows about it.</summary>
public sealed record WantedTrack(
    Guid TrackId,
    int Disc,
    int Position,
    string Title,
    string ArtistCredit,
    Guid ArtistId,
    TimeSpan? Duration,
    string? RecordingId,
    IReadOnlyList<ProviderId> Ids);

/// <summary>
/// Everything a fetch needs to know about its target, loaded once: the edition and its release ID, the tracks wanted,
/// and where in the library they should go.
/// </summary>
/// <param name="Whole">The whole album is fetched in one job, rather than one track.</param>
/// <param name="Tracks">The tracks to fetch; one for a track request.</param>
/// <param name="EditionTrackCount">How many tracks the edition has, for tagging.</param>
/// <param name="DestinationDirectory">The folder of the album's files you already have, if any, so new ones join them.</param>
public sealed record FetchTarget(
    Request Request,
    bool Whole,
    Edition Edition,
    Album Album,
    string? ReleaseId,
    IReadOnlyList<ProviderId> EditionIds,
    IReadOnlyList<WantedTrack> Tracks,
    int EditionTrackCount,
    string? DestinationDirectory)
{
    public RetrievalTarget ToRetrieval()
    {
        if (!Whole)
        {
            var track = Tracks[0];
            return new RetrievalTarget(RetrievalKind.Track, track.Title, track.ArtistCredit, Album.Title, track.Duration?.TotalSeconds, null, track.Ids);
        }
        var total = Tracks.All(t => t.Duration is not null) ? Tracks.Sum(t => t.Duration!.Value.TotalSeconds) : (double?)null;
        return new RetrievalTarget(RetrievalKind.Album, Album.Title, Album.ArtistCredit, null, total,
            Tracks.Select(t => new RetrievalTargetTrack(t.Disc, t.Position, t.Title, t.Duration?.TotalSeconds, t.Ids)).ToList(),
            EditionIds);
    }

    public static async Task<FetchTarget> LoadAsync(MelodexDbContext db, Request request, bool whole, CancellationToken ct)
    {
        var edition = await db.Editions.Include(e => e.Album).FirstAsync(e => e.Id == request.EditionId, ct);
        var listings = await db.EditionTracks.Where(et => et.EditionId == edition.Id)
            .OrderBy(et => et.Disc).ThenBy(et => et.Position)
            .Select(et => new { et.TrackId, et.Disc, et.Position, et.Title, et.Track.ArtistCredit, et.Track.ArtistId, Duration = et.Duration ?? et.Track.Duration })
            .ToListAsync(ct);
        var wanted = whole ? listings : listings.Where(l => l.TrackId == request.TargetId).Take(1).ToList();

        var trackIds = wanted.Select(w => w.TrackId).ToList();
        var ids = (await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && trackIds.Contains(m.EntityId))
            .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct)).ToLookup(m => m.EntityId, m => new ProviderId(m.Provider, m.Value));
        var editionIds = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Edition && m.EntityId == edition.Id)
            .Select(m => new ProviderId(m.Provider, m.Value)).ToListAsync(ct);

        var tracks = wanted.Select(w => new WantedTrack(w.TrackId, w.Disc, w.Position, w.Title, w.ArtistCredit, w.ArtistId, w.Duration,
            ids[w.TrackId].FirstOrDefault(i => i.Provider == Providers.MusicBrainz)?.Value, ids[w.TrackId].ToList())).ToList();

        return new FetchTarget(request, whole, edition, edition.Album, editionIds.FirstOrDefault(i => i.Provider == Providers.MusicBrainz)?.Value,
            editionIds, tracks, listings.Count, await DestinationAsync(db, edition, tracks, ct));
    }

    /// <summary>
    /// The folder of files you have from this album: from the same disc if there are any (multi-disc albums are often
    /// split into disc folders), otherwise the album's most used folder.
    /// </summary>
    private static async Task<string?> DestinationAsync(MelodexDbContext db, Edition edition, IReadOnlyList<WantedTrack> tracks, CancellationToken ct)
    {
        var editionIds = await db.Editions.Where(e => e.AlbumId == edition.AlbumId).Select(e => e.Id).ToListAsync(ct);
        var owned = await db.LibraryFiles.Where(f => f.EditionId != null && editionIds.Contains(f.EditionId.Value)).Select(f => f.Path).ToListAsync(ct);
        if (owned.Count == 0)
            return null;
        var disc = tracks[0].Disc;
        var discs = await db.EditionTracks.Where(et => et.EditionId == edition.Id && et.Disc == disc).Select(et => et.TrackId).ToListAsync(ct);
        var sameDisc = await db.LibraryFiles.Where(f => f.EditionId == edition.Id && f.TrackId != null && discs.Contains(f.TrackId.Value)).Select(f => f.Path).ToListAsync(ct);
        return (sameDisc.Count > 0 ? sameDisc : owned)
            .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "")
            .GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key;
    }
}
