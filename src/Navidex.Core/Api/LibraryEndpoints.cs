using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Contracts.Catalogue;
using Navidex.Contracts.Plugins.Index;
using Navidex.Core.Artwork;
using Navidex.Core.Catalogue;
using Navidex.Core.Data;
using Navidex.Core.Library;
using Navidex.Core.Requests;
using Navidex.Core.Tasks;

namespace Navidex.Core.Api;

public static class LibraryEndpoints
{
    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        var library = api.MapGroup("/library").WithTags("Library");

        library.MapGet("", async (NavidexDbContext db, LibraryReading reading, CancellationToken ct) =>
            {
                var files = await db.LibraryFiles.CountAsync(ct);
                var matched = await db.LibraryFiles.CountAsync(f => f.TrackId != null, ct);
                var pending = await db.LibraryFiles.CountAsync(f => f.MatchedAt == null && f.ExcludedAt == null, ct);
                var excluded = await db.LibraryFiles.CountAsync(f => f.ExcludedAt != null, ct);
                var lastScan = await db.BackgroundTasks.Where(t => t.Kind == LibraryScanTask.TaskKind && t.Status == BackgroundTaskStatus.Succeeded)
                    .MaxAsync(t => (DateTime?)t.FinishedAt, ct);
                var current = await db.BackgroundTasks.Where(t => t.Status == BackgroundTaskStatus.Running || t.Status == BackgroundTaskStatus.Queued)
                    .OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
                var lastFinished = await db.BackgroundTasks.Where(t => t.Kind == LibraryScanTask.TaskKind && t.FinishedAt != null)
                    .OrderByDescending(t => t.FinishedAt).FirstOrDefaultAsync(ct);
                return new LibraryStatus(files, matched, files - matched - pending - excluded, pending, lastScan, current is null ? null : TaskQueue.ToInfo(current),
                    lastFinished is null ? null : TaskQueue.ToInfo(lastFinished), await reading.IsBusyAsync(db, ct), excluded);
            })
            .WithName("GetLibraryStatus").WithSummary("File counts, the last scan, and any task in progress.");

        library.MapPost("/scan", async (TaskQueue queue, CancellationToken ct) =>
                Results.Accepted(null, TaskQueue.ToInfo(await queue.EnqueueAsync(LibraryScanTask.TaskKind, null, ct))))
            .WithName("ScanLibrary").WithSummary("Scan the music folder, match new files and update the catalogue. Returns the queued task.")
            .Produces<TaskInfo>(StatusCodes.Status202Accepted);

        library.MapGet("/artists", (LibraryCache cache, CancellationToken ct) => cache.GetArtistsAsync(ct))
            .WithName("GetLibraryArtists").WithSummary("Artists in the library, with owned, missing and covered album counts. May lag a scan by a few seconds.");

        library.MapGet("/albums", (LibraryCache cache, CancellationToken ct) => cache.GetAlbumsAsync(ct))
            .WithName("GetLibraryAlbums").WithSummary("Albums you have some or all of, by artist. May lag a scan by a few seconds.");

        library.MapGet("/tracks", (LibraryCache cache, CancellationToken ct) => cache.GetTracksAsync(ct))
            .WithName("GetLibraryTracks").WithSummary("Every matched file, by artist and album. May lag a scan by a few seconds.");

        library.MapGet("/missing", (LibraryView view, CancellationToken ct) => view.GetMissingAsync(ct))
            .WithName("GetMissing").WithSummary("Missing albums across the library, after the default filters and ignores.");

        library.MapGet("/scan/files", (ScanFeed feed, long? since) => feed.Since(since ?? 0))
            .WithName("ScanFiles")
            .WithSummary("The files the running or last scan read or matched, in the order it first saw them: each file's name while it's read, "
                         + "then the song it matched or why it needs review. Pass since (the last response's latest) to get only what changed.");

        library.MapGet("/review", (LibraryView view, bool? excluded, CancellationToken ct) => view.GetReviewAsync(excluded == true, ct))
            .WithName("GetReview").WithSummary("Files that couldn't be matched confidently, with the best suggestion. With excluded=true, the files excluded from matching.");

        library.MapPut("/files/{id:guid}/excluded", async (Guid id, ExcludeFile body, NavidexDbContext db, CancellationToken ct) =>
            {
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } file)
                    return Results.NotFound();
                if (body.Excluded)
                {
                    file.ExcludedAt ??= DateTime.UtcNow;
                    file.TrackId = file.EditionId = file.SuggestedTrackId = file.SuggestedEditionId = null;
                    file.ArtistId = null;
                    file.MatchMethod = MatchMethod.Unmatched;
                    file.MatchConfidence = null;
                    file.ReviewReason = null;
                    file.MatchedAt ??= DateTime.UtcNow;
                }
                else if (file.ExcludedAt is not null)
                {
                    // Matched again by the next scan.
                    file.ExcludedAt = null;
                    file.MatchedAt = null;
                }
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .WithName("ExcludeFile")
            .WithSummary("Exclude a file from matching, for music no catalogue knows: it's never matched and leaves the review list. "
                         + "Including it again matches it at the next scan.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        library.MapPost("/files/{id:guid}/match", async (Guid id, MatchFile body, NavidexDbContext db, RequestPlanner planner, CancellationToken ct) =>
            {
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } file)
                    return Results.NotFound();
                if (await db.Tracks.FindAsync([body.TrackId], ct) is not { } track)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["trackId"] = ["No such track in the catalogue."] });
                var editionId = body.EditionId
                                ?? (file.SuggestedTrackId == track.Id ? file.SuggestedEditionId : null)
                                ?? await planner.ChooseEditionForTrackAsync(track.Id, ct);
                if (editionId is { } edition && !await db.EditionTracks.AnyAsync(et => et.EditionId == edition && et.TrackId == track.Id, ct))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["editionId"] = ["That track isn't on that edition."] });

                file.TrackId = track.Id;
                file.EditionId = editionId;
                file.ArtistId = track.ArtistId;
                file.MatchMethod = MatchMethod.Manual;
                file.MatchConfidence = 1;
                file.ReviewReason = null;
                file.SuggestedTrackId = null;
                file.SuggestedEditionId = null;
                file.MatchedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .WithName("MatchFile").WithSummary("Say which song a file is: accepting a review suggestion, or one picked by searching. It counts as owned at once.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        api.MapGet("/artwork/{kind}/{id:guid}", async (string kind, Guid id, ArtworkCache artwork, HttpContext http, CancellationToken ct) =>
            {
                EntityKind? of = kind switch { "artist" => EntityKind.Artist, "album" => EntityKind.Album, "edition" => EntityKind.Edition, _ => null };
                if (of is null || await artwork.GetAsync(of.Value, id, ct) is not { } found)
                    return Results.NotFound();
                // Revalidated every time, so a change of artwork plugins shows at once; unchanged pictures are a 304.
                http.Response.Headers.CacheControl = "no-cache";
                return Results.File(found.Path, found.ContentType, lastModified: found.UpdatedAt,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{found.UpdatedAt.Ticks:x}\""));
            })
            .WithTags("Library").WithName("GetArtwork")
            .WithSummary("The picture of an artist, album or edition (kind is artist, album or edition), from the artwork plugins. 404 when there's none.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg").Produces(StatusCodes.Status404NotFound);

        api.MapGet("/tasks", async (NavidexDbContext db, bool? active, int? limit, CancellationToken ct) =>
            {
                var query = db.BackgroundTasks.AsQueryable();
                if (active is { } a)
                    query = a
                        ? query.Where(t => t.Status == BackgroundTaskStatus.Queued || t.Status == BackgroundTaskStatus.Running)
                        : query.Where(t => t.Status != BackgroundTaskStatus.Queued && t.Status != BackgroundTaskStatus.Running);
                var tasks = await query.OrderByDescending(t => t.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);

                // Artist refreshes say whose catalogue they fill in.
                var artistIds = tasks.Where(t => t.Kind == ArtistRefreshTask.TaskKind && t.PayloadJson is not null)
                    .Select(t => JsonSerializer.Deserialize<Guid>(t.PayloadJson!)).Distinct().ToList();
                var names = await db.Artists.Where(x => artistIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
                return tasks.Select(t => TaskQueue.ToInfo(t) with
                {
                    Title = t.Kind switch
                    {
                        LibraryScanTask.TaskKind => "Library scan",
                        ArtistRefreshTask.TaskKind when t.PayloadJson is not null && names.TryGetValue(JsonSerializer.Deserialize<Guid>(t.PayloadJson), out var name)
                            => $"Catalogue update · {name}",
                        ArtistRefreshTask.TaskKind => "Catalogue update",
                        _ => t.Kind,
                    },
                }).ToList();
            })
            .WithTags("Library").WithName("ListTasks").WithSummary("Background work such as library scans, newest first.");

        api.MapGet("/tasks/{id:guid}", async (Guid id, NavidexDbContext db, CancellationToken ct) =>
                await db.BackgroundTasks.FindAsync([id], ct) is { } task ? Results.Ok(TaskQueue.ToInfo(task)) : Results.NotFound())
            .WithTags("Library").WithName("GetTask").WithSummary("Status of a background task.")
            .Produces<TaskInfo>().Produces(StatusCodes.Status404NotFound);

        var settings = api.MapGroup("/settings").WithTags("Settings");
        settings.MapGet("/library", (SettingsService s, CancellationToken ct) => s.GetLibraryAsync(ct))
            .WithName("GetLibrarySettings");
        settings.MapPut("/library", async (LibrarySettings value, SettingsService s, CancellationToken ct) =>
            {
                if (value.MatchThreshold is <= 0 or > 1)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["matchThreshold"] = ["Must be between 0 and 1."] });
                if (value.AttemptsWhenBusy is < 1 or > 10)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["attemptsWhenBusy"] = ["Must be between 1 and 10."] });
                await s.SaveLibraryAsync(value, ct);
                return Results.NoContent();
            })
            .WithName("SaveLibrarySettings").WithSummary("Takes effect at once for the missing view; a new threshold applies from the next scan.");

        return api;
    }

    public static RouteGroupBuilder MapCatalogueEndpoints(this RouteGroupBuilder api)
    {
        var catalogue = api.MapGroup("").WithTags("Catalogue");

        catalogue.MapGet("/catalogue/search", async (string q, CatalogueService service, LibraryView view, NavidexDbContext db, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(q))
                    return Results.Ok(new SearchResults([], [], []));
                var plugin = await service.PluginAsync(ct);
                var results = await plugin.SearchAsync(new IndexSearchQuery(q, null, 10), ct);

                var artists = new List<SearchArtist>();
                foreach (var hit in results.Artists)
                {
                    var artist = await service.ImportArtistAsync(hit, ct);
                    artists.Add(new SearchArtist(artist.Id, artist.Name, artist.Disambiguation, artist.InLibrary, [hit.Id], LibraryView.ArtistImageUrl(artist.Id)));
                }
                var albumIds = new List<Guid>();
                foreach (var hit in results.Albums)
                    albumIds.Add((await service.ImportAlbumAsync(hit, null, ct)).Id);
                var tracks = new List<(Track Track, Album? Album)>();
                foreach (var hit in results.Tracks)
                    tracks.Add((await service.ImportTrackAsync(hit, ct), hit.Album is { } from ? await service.ImportAlbumUnlessKnownAsync(from, ct) : null));
                await db.SaveChangesAsync(ct);

                var trackIds = tracks.Select(t => t.Track.Id).ToList();
                var owned = await db.LibraryFiles.Where(f => f.TrackId != null && trackIds.Contains(f.TrackId.Value)).Select(f => f.TrackId!.Value).ToListAsync(ct);
                var summaries = (await view.GetAlbumSummariesAsync([.. albumIds.Union(tracks.Select(t => t.Album?.Id).OfType<Guid>())], ct)).ToDictionary(a => a.Id);
                return Results.Ok(new SearchResults(artists, [.. albumIds.Where(summaries.ContainsKey).Select(id => summaries[id])],
                    tracks.Select((t, i) => new SearchTrack(t.Track.Id, t.Track.Title, t.Track.ArtistCredit, t.Track.Duration?.TotalSeconds, owned.Contains(t.Track.Id),
                        [results.Tracks[i].Id], t.Track.ArtistId, t.Album?.Id, t.Album?.Title,
                        t.Album is { } album ? summaries.GetValueOrDefault(album.Id)?.CoverUrl ?? LibraryView.CoverUrl(album.Id, null) : null)).ToList()));
            })
            .WithName("SearchCatalogue").WithSummary("Search artists, albums and tracks through the index plugin, with owned status.")
            .Produces<SearchResults>();

        catalogue.MapGet("/artists/{id:guid}", async (Guid id, LibraryView view, NavidexDbContext db, CatalogueService service, TaskQueue queue, CancellationToken ct) =>
            {
                var artist = await db.Artists.FindAsync([id], ct);
                if (artist is null)
                    return Results.NotFound();
                if (artist.AlbumsRefreshedAt is null)
                {
                    // First visit: fetch the album list now, and the editions in the background.
                    await service.RefreshDiscographyAsync(artist, false, ct);
                    await queue.EnqueueAsync(ArtistRefreshTask.TaskKind, ArtistRefreshTask.Payload(id), ct);
                }
                return Results.Ok(await view.GetArtistAsync(id, ct));
            })
            .WithName("GetArtist").WithSummary("An artist's full discography, each album with its owned or missing status.")
            .Produces<ArtistDetail>().Produces(StatusCodes.Status404NotFound);

        catalogue.MapGet("/artists/{id:guid}/albums", async (Guid id, LibraryView view, CancellationToken ct) =>
                await view.GetArtistAlbumsAsync(id, ct) is { } albums ? Results.Ok(albums) : Results.NotFound())
            .WithName("GetArtistAlbums").Produces<IReadOnlyList<AlbumSummary>>().Produces(StatusCodes.Status404NotFound);

        catalogue.MapGet("/albums/{id:guid}", async (Guid id, Guid? edition, LibraryView view, NavidexDbContext db, CatalogueService service, CancellationToken ct) =>
            {
                var album = await db.Albums.FindAsync([id], ct);
                if (album is null)
                    return Results.NotFound();
                await service.RefreshEditionsAsync(album, false, ct);
                return Results.Ok(await view.GetAlbumAsync(id, edition, ct));
            })
            .WithName("GetAlbum").WithSummary("An album's editions, and the tracks of the owned (or requested) edition with their status.")
            .Produces<AlbumDetail>().Produces(StatusCodes.Status404NotFound);

        catalogue.MapPut("/albums/{id:guid}/ignore", (Guid id, NavidexDbContext db, CancellationToken ct) => SetIgnored(db, id, true, ct))
            .WithName("IgnoreAlbum").WithSummary("Never list this album as missing.");
        catalogue.MapDelete("/albums/{id:guid}/ignore", (Guid id, NavidexDbContext db, CancellationToken ct) => SetIgnored(db, id, false, ct))
            .WithName("UnignoreAlbum");
        catalogue.MapPut("/tracks/{id:guid}/ignore", (Guid id, NavidexDbContext db, CancellationToken ct) => SetTrackIgnored(db, id, true, ct))
            .WithName("IgnoreTrack").WithSummary("Stop counting this song, on every album it's on, as missing.");
        catalogue.MapDelete("/tracks/{id:guid}/ignore", (Guid id, NavidexDbContext db, CancellationToken ct) => SetTrackIgnored(db, id, false, ct))
            .WithName("UnignoreTrack");

        return api;
    }

    private static async Task<IResult> SetIgnored(NavidexDbContext db, Guid id, bool ignored, CancellationToken ct) =>
        await db.Albums.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Ignored, ignored), ct) == 0
            ? Results.NotFound()
            : Results.NoContent();

    private static async Task<IResult> SetTrackIgnored(NavidexDbContext db, Guid id, bool ignored, CancellationToken ct) =>
        await db.Tracks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Ignored, ignored), ct) == 0
            ? Results.NotFound()
            : Results.NoContent();
}
