using Microsoft.EntityFrameworkCore;
using Navidex.Contracts.Catalogue;
using Navidex.Contracts.Plugins.Index;
using Navidex.Core.Catalogue;
using Navidex.Core.Data;

namespace Navidex.Core.Library;

/// <summary>
/// Matches scanned files to catalogue tracks and editions, in the plan's order: MusicBrainz IDs in the tags, then
/// other crosswalk IDs, then ISRC, then a fuzzy comparison of title, artist and duration. Files that fall under the
/// confidence threshold go to the review list with the best candidate as a suggestion.
/// </summary>
public sealed class LibraryMatcher(
    NavidexDbContext db,
    CatalogueService catalogue,
    SettingsService settings,
    ILogger<LibraryMatcher> logger)
{
    // MusicBrainz's "Various Artists": its discography is enormous and never what a compilation should match against.
    private static readonly ProviderId VariousArtists = new(Providers.MusicBrainz, "89ad4ac3-39f7-470e-963a-56509c546377");

    private readonly Dictionary<string, Artist?> _artistsByName = new(StringComparer.OrdinalIgnoreCase);
    private double _threshold;

    public sealed record Result(int Matched, int Review);

    public async Task<Result> MatchPendingAsync(IProgress<string>? progress, CancellationToken ct)
    {
        _threshold = (await settings.GetLibraryAsync(ct)).MatchThreshold;
        var pending = await db.LibraryFiles.AsNoTracking().Where(f => f.MatchedAt == null).Select(f => new { f.Id, f.Path, f.TagsJson }).ToListAsync(ct);
        var groups = pending
            .Select(f => (f.Id, Tags: FileTags.Parse(f.TagsJson), f.Path))
            .GroupBy(x => x.Tags.Album is null
                ? $"loose|{x.Path}"
                : $"{x.Tags.AlbumArtist ?? x.Tags.Artist}|{x.Tags.Album}|{Path.GetDirectoryName(x.Path)}")
            // Groups with MusicBrainz IDs first: they pin their artists by ID, so later groups by the same artist don't
            // need a name search. Then by key, so the order (and the requests made) never depends on the filesystem.
            .OrderBy(g => g.Any(x => x.Tags.MusicBrainzReleaseGroupId is not null || x.Tags.MusicBrainzReleaseId is not null) ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        int done = 0, matched = 0, review = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            // Each group starts with a clear tracker, so catalogue imports don't pile up and slow every later query.
            db.ChangeTracker.Clear();
            var ids = group.Select(x => x.Id).ToList();
            var tracked = await db.LibraryFiles.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
            var files = group.Where(x => tracked.ContainsKey(x.Id)).Select(x => new Candidate(tracked[x.Id], x.Tags)).ToList();
            try
            {
                await MatchGroupAsync(files, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                // Upstream trouble: leave the files pending so the next scan tries again.
                logger.LogWarning(e, "Couldn't match {Count} files in {Group}; will retry next scan", files.Count, group.Key);
                db.ChangeTracker.Clear();
                continue;
            }

            foreach (var file in files.Select(c => c.File))
            {
                file.MatchedAt = DateTime.UtcNow;
                if (file.TrackId is null) review++; else matched++;
            }
            LogGroup(files);
            await db.SaveChangesAsync(ct);
            done += files.Count;
            progress?.Report($"Matched {done} of {pending.Count} files");
        }
        db.ChangeTracker.Clear();

        await UpdateLibraryArtistsAsync(ct);
        return new Result(matched, review);
    }

    private sealed record Candidate(LibraryFile File, FileTags Tags);

    private void LogGroup(List<Candidate> files)
    {
        var placed = files.Where(f => f.File.TrackId is not null).ToList();
        var where = files.Count == 1 ? files[0].File.Path : Path.GetDirectoryName(files[0].File.Path)?.Replace('\\', '/') + "/";
        if (placed.Count > 0)
        {
            var how = string.Join(", ", placed.GroupBy(f => f.File.MatchMethod).Select(g => $"{g.Count()} by {Describe(g.Key)}"));
            logger.LogInformation("Matched {Matched} of {Total} files in {Where} ({How})", placed.Count, files.Count, where, how);
        }
        foreach (var file in files.Where(f => f.File.TrackId is null))
            logger.LogWarning("To review: {Path}: {Reason}", file.File.Path, file.File.ReviewReason ?? "no confident match");
    }

    private static string Describe(MatchMethod method) => method switch
    {
        MatchMethod.MusicBrainzTags => "MusicBrainz tags",
        MatchMethod.IsrcOrUpc => "ISRC",
        MatchMethod.Fuzzy => "title, artist and length",
        MatchMethod.Crosswalk => "a known ID",
        _ => method.ToString(),
    };

    private async Task MatchGroupAsync(List<Candidate> files, CancellationToken ct)
    {
        var first = files[0].Tags;
        var artist = await ResolveArtistAsync(first, ct);
        foreach (var file in files)
            file.File.ArtistId = artist?.Id;

        var unplaced = files;
        if (first.Album is not null)
        {
            var albums = await CandidateAlbumsAsync(files, artist, ct);
            unplaced = await MatchToAlbumsAsync(files, albums, ct);
        }

        foreach (var file in unplaced)
            await MatchLooseAsync(file, artist, ct);
    }

    // ---- Artist ----

    private async Task<Artist?> ResolveArtistAsync(FileTags tags, CancellationToken ct)
    {
        if ((tags.MusicBrainzAlbumArtistId ?? tags.MusicBrainzArtistId) is { } mbid)
        {
            var artist = await catalogue.GetArtistAsync(new ProviderId(Providers.MusicBrainz, mbid), ct);
            if (artist is not null)
                return artist;
        }

        var name = tags.AlbumArtist ?? tags.Artist;
        if (name is null)
            return null;
        if (!_artistsByName.TryGetValue(name, out var resolved))
            _artistsByName[name] = resolved = await catalogue.ResolveArtistByNameAsync(name, ct);
        return resolved;
    }

    private async Task<bool> IsVariousArtistsAsync(Artist artist, CancellationToken ct) =>
        await catalogue.FindAsync(EntityKind.Artist, VariousArtists, ct) == artist.Id;

    // ---- Album matching ----

    private async Task<List<Album>> CandidateAlbumsAsync(List<Candidate> files, Artist? artist, CancellationToken ct)
    {
        // 1. A release group ID in the tags pins the album.
        var groupId = files.Select(f => f.Tags.MusicBrainzReleaseGroupId).FirstOrDefault(id => id is not null);
        if (groupId is not null && await catalogue.GetAlbumAsync(new ProviderId(Providers.MusicBrainz, groupId), ct) is { } pinned)
            return [pinned];

        var title = TextMatch.NormaliseAlbum(files[0].Tags.Album);
        var albums = new List<Album>();

        // 2. The artist's discography, by title.
        if (artist is not null && !await IsVariousArtistsAsync(artist, ct))
        {
            await catalogue.RefreshDiscographyAsync(artist, false, ct);
            var discography = await db.Albums.Where(a => a.ArtistId == artist.Id).ToListAsync(ct);
            albums = discography.Where(a => TextMatch.NormaliseAlbum(a.Title) == title).ToList();
            if (albums.Count == 0)
                albums = discography
                    .Select(a => (Album: a, Score: TextMatch.Similarity(TextMatch.NormaliseAlbum(a.Title), title)))
                    .Where(x => x.Score >= 0.9).OrderByDescending(x => x.Score).Take(3).Select(x => x.Album).ToList();
        }

        // 3. A catalogue search, for compilations and artists that couldn't be resolved.
        if (albums.Count == 0)
        {
            var artistName = files[0].Tags.AlbumArtist ?? files[0].Tags.Artist ?? "";
            var plugin = await catalogue.PluginAsync(ct);
            var results = await plugin.SearchAsync(new IndexSearchQuery($"{files[0].Tags.Album} {artistName}".Trim(), new HashSet<SearchKind> { SearchKind.Album }, 10), ct);
            foreach (var hit in results.Albums
                         .Where(a => TextMatch.Similarity(TextMatch.NormaliseAlbum(a.Title), title) >= 0.9
                                     && (artistName.Length == 0 || TextMatch.Similarity(TextMatch.NormaliseArtist(a.ArtistCredit), TextMatch.NormaliseArtist(artistName)) >= 0.8))
                         .Take(2))
                if (await catalogue.GetAlbumAsync(hit.Id, ct) is { } album)
                    albums.Add(album);
        }

        foreach (var album in albums)
            await catalogue.RefreshEditionsAsync(album, false, ct);
        return albums;
    }

    private sealed record Listing(Guid EditionId, int Disc, int Position, string Title, TimeSpan? Duration, Guid TrackId,
        string TrackTitle, string ArtistCredit, TimeSpan? TrackDuration, HashSet<string> Recordings, HashSet<string> Isrcs);

    private sealed record Assignment(Candidate File, Listing Listing, double Score, MatchMethod Method);

    /// <summary>Picks the edition that best fits the files and assigns files to its tracks. Returns files left over.</summary>
    private async Task<List<Candidate>> MatchToAlbumsAsync(List<Candidate> files, List<Album> albums, CancellationToken ct)
    {
        if (albums.Count == 0)
            return files;

        var albumIds = albums.Select(a => a.Id).ToList();
        var editions = await db.Editions.Where(e => albumIds.Contains(e.AlbumId)).ToListAsync(ct);
        if (editions.Count == 0)
            return files;
        var listings = await LoadListingsAsync(editions.Select(e => e.Id).ToList(), ct);
        var editionMbids = await db.Crosswalk
            .Where(m => m.EntityKind == EntityKind.Edition && m.Provider == Providers.MusicBrainz && editions.Select(e => e.Id).Contains(m.EntityId))
            .ToDictionaryAsync(m => m.EntityId, m => m.Value, ct);

        var releaseTag = files.Select(f => f.Tags.MusicBrainzReleaseId).FirstOrDefault(id => id is not null);
        var yearTag = files.Select(f => f.Tags.Date?[..Math.Min(4, f.Tags.Date.Length)]).FirstOrDefault(d => d is not null);

        (Edition Edition, List<Assignment> Assignments, double Score)? best = null;
        foreach (var edition in editions)
        {
            var assignments = Assign(files, listings.GetValueOrDefault(edition.Id, []));
            var score = assignments.Sum(a => a.Score)
                        + (releaseTag is not null && editionMbids.GetValueOrDefault(edition.Id) == releaseTag ? 5 : 0)
                        + (yearTag is not null && edition.ReleaseDate?.StartsWith(yearTag) == true ? 0.1 : 0)
                        + (string.Equals(edition.Status, "Official", StringComparison.OrdinalIgnoreCase) ? 0.05 : 0)
                        - 0.01 * Math.Abs(edition.TrackCount - files.Count);
            if (best is null || score > best.Value.Score)
                best = (edition, assignments, score);
        }

        var leftovers = new List<Candidate>(files);
        foreach (var assignment in best!.Value.Assignments)
        {
            var file = assignment.File.File;
            if (assignment.Score >= _threshold)
            {
                file.TrackId = assignment.Listing.TrackId;
                file.EditionId = assignment.Listing.EditionId;
                file.MatchMethod = assignment.Method;
                file.MatchConfidence = Math.Round(Math.Min(1, assignment.Score), 3);
                file.ReviewReason = null;
                file.SuggestedTrackId = file.SuggestedEditionId = null;
                leftovers.Remove(assignment.File);
            }
            else
            {
                // Keep the album's best guess as the suggestion; matching on its own may still do better.
                file.SuggestedTrackId = assignment.Listing.TrackId;
                file.SuggestedEditionId = assignment.Listing.EditionId;
                file.MatchConfidence = Math.Round(assignment.Score, 3);
            }
        }
        return leftovers;
    }

    private async Task<Dictionary<Guid, List<Listing>>> LoadListingsAsync(List<Guid> editionIds, CancellationToken ct)
    {
        var rows = await db.EditionTracks.Where(et => editionIds.Contains(et.EditionId))
            .Select(et => new { et.EditionId, et.Disc, et.Position, et.Title, et.Duration, et.TrackId, TrackTitle = et.Track.Title, et.Track.ArtistCredit, TrackDuration = et.Track.Duration })
            .ToListAsync(ct);
        var trackIds = rows.Select(r => r.TrackId).Distinct().ToList();
        var ids = (await db.Crosswalk
                .Where(m => m.EntityKind == EntityKind.Track && trackIds.Contains(m.EntityId) && (m.Provider == Providers.MusicBrainz || m.Provider == Providers.Isrc))
                .Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct))
            .ToLookup(m => m.EntityId);

        return rows
            .Select(r => new Listing(r.EditionId, r.Disc, r.Position, r.Title, r.Duration, r.TrackId, r.TrackTitle, r.ArtistCredit, r.TrackDuration,
                ids[r.TrackId].Where(m => m.Provider == Providers.MusicBrainz).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase),
                ids[r.TrackId].Where(m => m.Provider == Providers.Isrc).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .GroupBy(l => l.EditionId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Greedy one-to-one assignment of files to listings, best pairs first.</summary>
    private static List<Assignment> Assign(List<Candidate> files, List<Listing> listings)
    {
        var pairs = new List<Assignment>();
        foreach (var file in files)
        foreach (var listing in listings)
        {
            var (score, method) = Score(file, listing);
            if (score > 0.3)
                pairs.Add(new Assignment(file, listing, score, method));
        }

        var usedFiles = new HashSet<Candidate>();
        var usedListings = new HashSet<Listing>();
        var result = new List<Assignment>();
        foreach (var pair in pairs.OrderByDescending(p => p.Score))
        {
            if (usedFiles.Contains(pair.File) || usedListings.Contains(pair.Listing))
                continue;
            usedFiles.Add(pair.File);
            usedListings.Add(pair.Listing);
            result.Add(pair);
        }
        return result;
    }

    private static (double Score, MatchMethod Method) Score(Candidate file, Listing listing)
    {
        if (file.Tags.MusicBrainzRecordingId is { } recording && listing.Recordings.Contains(recording))
            return (PositionBonus(file, listing) + 1, MatchMethod.MusicBrainzTags);
        if (file.Tags.Isrc is { } isrc && listing.Isrcs.Contains(isrc))
            return (PositionBonus(file, listing) + 1, MatchMethod.IsrcOrUpc);
        var fuzzy = Fuzzy(file.Tags, file.File.Duration, listing.Title, listing.TrackTitle, listing.ArtistCredit, listing.Duration ?? listing.TrackDuration);
        return (Math.Min(1, fuzzy + PositionBonus(file, listing)), MatchMethod.Fuzzy);
    }

    // Breaks ties between identical recordings on one edition (for example a track repeated on a bonus disc).
    private static double PositionBonus(Candidate file, Listing listing) =>
        file.Tags.Track == listing.Position && (file.Tags.Disc ?? 1) == listing.Disc ? 0.05 : 0;

    /// <summary>
    /// Fuzzy confidence from 0 to 1: title counts most, then duration, then artist. A title that isn't close, or a
    /// length more than 20 seconds out, caps the score below any sensible threshold.
    /// </summary>
    public static double Fuzzy(FileTags tags, TimeSpan? fileDuration, string listingTitle, string trackTitle, string artistCredit, TimeSpan? expected)
    {
        var fileTitle = TextMatch.NormaliseTitle(tags.Title);
        var title = Math.Max(TextMatch.Similarity(fileTitle, TextMatch.NormaliseTitle(listingTitle)),
                             TextMatch.Similarity(fileTitle, TextMatch.NormaliseTitle(trackTitle)));
        var fileArtist = tags.Artist ?? tags.AlbumArtist;
        var artist = fileArtist is null ? 0.5 : TextMatch.Similarity(TextMatch.NormaliseArtist(fileArtist), TextMatch.NormaliseArtist(artistCredit));

        // An unknown length is weak evidence against, not neutral: title and artist alone stay under the default
        // threshold, so they go to review rather than matching.
        double duration = 0.25;
        double? difference = null;
        if (fileDuration is { } actual && expected is { } wanted)
        {
            difference = Math.Abs((actual - wanted).TotalSeconds);
            duration = difference <= 3 ? 1 : difference >= 20 ? 0 : 1 - (difference.Value - 3) / 17;
        }

        var score = 0.55 * title + 0.15 * artist + 0.30 * duration;
        if (difference > 20)
            score = Math.Min(score, 0.6);
        if (title < 0.6)
            score = Math.Min(score, 0.5);
        return score;
    }

    // ---- Tracks on their own ----

    private async Task MatchLooseAsync(Candidate file, Artist? artist, CancellationToken ct)
    {
        var tags = file.Tags;
        file.File.EditionId = null;

        // IDs first: a recording or ISRC already in the crosswalk.
        foreach (var id in new[] { tags.MusicBrainzRecordingId is { } r ? new ProviderId(Providers.MusicBrainz, r) : null,
                                   tags.Isrc is { } i ? new ProviderId(Providers.Isrc, i) : null }.OfType<ProviderId>())
        {
            if (await catalogue.FindAsync(EntityKind.Track, id, ct) is { } trackId)
            {
                Match(file.File, trackId, id.Provider == Providers.Isrc ? MatchMethod.IsrcOrUpc : MatchMethod.MusicBrainzTags, 1);
                return;
            }
        }

        var plugin = await catalogue.PluginAsync(ct);
        if (tags.Isrc is { } isrc && plugin is IIsrcLookup isrcLookup && (await isrcLookup.LookupIsrcAsync(isrc, ct)).FirstOrDefault() is { } byIsrc)
        {
            var track = await catalogue.ImportTrackAsync(byIsrc, ct);
            Match(file.File, track.Id, MatchMethod.IsrcOrUpc, 1);
            return;
        }

        if (tags.Title is null)
        {
            Review(file.File, "The file has no title tag.");
            return;
        }

        var artistName = tags.Artist ?? tags.AlbumArtist ?? artist?.Name;
        var results = await plugin.SearchAsync(new IndexSearchQuery($"{tags.Title} {artistName}".Trim(), new HashSet<SearchKind> { SearchKind.Track }, 10), ct);
        var best = results.Tracks
            .Select(t => (Track: t, Score: Fuzzy(tags, file.File.Duration, t.Title, t.Title, t.ArtistCredit, t.Duration)))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best.Track is null)
        {
            if (file.File.SuggestedTrackId is null)
                Review(file.File, "Nothing in the catalogue looks like this file.");
            else
                Review(file.File, $"Closest match is only {file.File.MatchConfidence:P0} confident.");
            return;
        }

        var imported = await catalogue.ImportTrackAsync(best.Track, ct);
        if (best.Score >= _threshold)
        {
            Match(file.File, imported.Id, MatchMethod.Fuzzy, best.Score);
        }
        else if (file.File.SuggestedTrackId is null || best.Score > (file.File.MatchConfidence ?? 0))
        {
            file.File.SuggestedTrackId = imported.Id;
            file.File.SuggestedEditionId = null;
            file.File.MatchConfidence = Math.Round(best.Score, 3);
            Review(file.File, $"Closest match, {best.Track.Title} by {best.Track.ArtistCredit}, is only {best.Score:P0} confident.");
        }
        else
        {
            Review(file.File, $"Closest match is only {file.File.MatchConfidence:P0} confident.");
        }
    }

    private static void Match(LibraryFile file, Guid trackId, MatchMethod method, double confidence)
    {
        file.TrackId = trackId;
        file.MatchMethod = method;
        file.MatchConfidence = Math.Round(confidence, 3);
        file.ReviewReason = null;
        file.SuggestedTrackId = file.SuggestedEditionId = null;
    }

    private static void Review(LibraryFile file, string reason)
    {
        file.TrackId = null;
        file.EditionId = null;
        file.MatchMethod = MatchMethod.Unmatched;
        file.ReviewReason = reason;
    }

    private async Task UpdateLibraryArtistsAsync(CancellationToken ct)
    {
        var credited = await db.LibraryFiles.Where(f => f.ArtistId != null).Select(f => f.ArtistId!.Value).Distinct().ToListAsync(ct);
        await db.Artists.Where(a => a.InLibrary && !credited.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.InLibrary, false), ct);
        await db.Artists.Where(a => !a.InLibrary && credited.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.InLibrary, true), ct);
    }
}
