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
    /// <summary>How long a request for a picture waits for it to be fetched before answering 503.</summary>
    private static readonly TimeSpan ArtworkPatience = TimeSpan.FromMilliseconds(100);

    /// <summary>How long the artwork status waits for pictures it was asked about before answering.</summary>
    private static readonly TimeSpan ArtworkStatusPatience = TimeSpan.FromMilliseconds(250);

    private static readonly System.Text.RegularExpressions.Regex ArtworkPath =
        new(@"artwork/(artist|album|edition)/([0-9a-fA-F-]{36})", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static EntityKind? ArtworkKind(string kind) =>
        kind switch { "artist" => EntityKind.Artist, "album" => EntityKind.Album, "edition" => EntityKind.Edition, _ => null };

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

        library.MapGet("/removal", (LibraryItemKind kind, Guid id, LibraryRemover remover, CancellationToken ct) => remover.PreviewAsync(kind, id, ct))
            .WithName("GetRemoval").WithSummary("What removing an artist, album or file from the library would affect: how many files, their size and folders.");

        library.MapPost("/remove", async (RemoveFromLibrary body, LibraryRemover remover, CancellationToken ct) =>
            {
                var result = await remover.RemoveAsync(body, ct);
                return result.Removed == 0 && result.Failed.Count == 0 ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("RemoveFromLibrary")
            .WithSummary("Remove an artist, album or file from the library: delete its files from the music folder and tell the media server, "
                         + "or keep them and exclude them from matching. Files that couldn't be deleted are listed and stay in the library.")
            .Produces<RemovalResult>().Produces(StatusCodes.Status404NotFound);

        library.MapGet("/review", (LibraryView view, bool? excluded, CancellationToken ct) => view.GetReviewAsync(excluded == true, ct))
            .WithName("GetReview").WithSummary("Files that couldn't be matched confidently, with the best suggestion. With excluded=true, the files excluded from matching.");

        library.MapPut("/files/{id:guid}/excluded", async (Guid id, ExcludeFile body, NavidexDbContext db, CancellationToken ct) =>
            {
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } file)
                    return Results.NotFound();
                if (body.Excluded)
                    Exclude(file);
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

                Match(file, track, editionId);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .WithName("MatchFile").WithSummary("Say which song a file is: accepting a review suggestion, or one picked by searching. It counts as owned at once.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        library.MapPost("/review/approve", async (ReviewFiles body, NavidexDbContext db, RequestPlanner planner, CancellationToken ct) =>
            {
                var ids = body.FileIds.Distinct().ToList();
                var files = await db.LibraryFiles.Where(f => ids.Contains(f.Id) && f.SuggestedTrackId != null && f.ExcludedAt == null).ToListAsync(ct);
                var trackIds = files.Select(f => f.SuggestedTrackId!.Value).Distinct().ToList();
                var tracks = await db.Tracks.Where(t => trackIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
                var count = 0;
                foreach (var file in files)
                {
                    if (!tracks.TryGetValue(file.SuggestedTrackId!.Value, out var track))
                        continue;
                    Match(file, track, file.SuggestedEditionId ?? await planner.ChooseEditionForTrackAsync(track.Id, ct));
                    count++;
                }
                await db.SaveChangesAsync(ct);
                return new ReviewDone(count);
            })
            .WithName("ApproveFiles").WithSummary("Accept the suggestion for each of these files that has one, as approving each would.");

        library.MapPost("/review/exclude", async (ReviewFiles body, NavidexDbContext db, CancellationToken ct) =>
            {
                var ids = body.FileIds.Distinct().ToList();
                var files = await db.LibraryFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
                foreach (var file in files)
                    Exclude(file);
                await db.SaveChangesAsync(ct);
                return new ReviewDone(files.Count);
            })
            .WithName("ExcludeFiles").WithSummary("Exclude each of these files from matching, as excluding each would.");

        api.MapGet("/artwork/{kind}/{id:guid}", async (string kind, Guid id, ArtworkCache artwork, HttpContext http, CancellationToken ct) =>
            {
                var of = ArtworkKind(kind);
                if (of is null)
                    return Results.NotFound();
                // A picture not fetched yet can take a while, as artwork sources are rate-limited. Browsers make only a
                // few requests at once to a server, so a page of pictures still on their way would hold up everything
                // else: answer 503 at once instead, and the search goes on. The page asks after them all together, below.
                var finding = artwork.GetAsync(of.Value, id, ct);
                if (await Task.WhenAny(finding, Task.Delay(ArtworkPatience, ct)) != finding)
                {
                    http.Response.Headers.RetryAfter = "2";
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                if (await finding is not { } found)
                    return Results.NotFound();
                // Revalidated every time, so a change of artwork plugins shows at once; unchanged pictures are a 304.
                http.Response.Headers.CacheControl = "no-cache";
                return Results.File(found.Path, found.ContentType, lastModified: found.UpdatedAt,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{found.UpdatedAt.Ticks:x}\""));
            })
            .WithTags("Library").WithName("GetArtwork")
            .WithSummary("The picture of an artist, album or edition (kind is artist, album or edition), from the artwork plugins. 404 when there's none; "
                         + "503 with Retry-After while it's still being fetched.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg").Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status503ServiceUnavailable);

        api.MapPost("/artwork/status", async (string[] urls, ArtworkCache artwork) =>
            {
                // Each is looked for at once, and those still on their way after a moment are pending.
                var asked = urls.Distinct().Take(500).Select(url => (Url: url, Match: ArtworkPath.Match(url))).ToList();
                var finding = asked.Where(a => a.Match.Success).ToDictionary(a => a.Url,
                    a => artwork.GetAsync(ArtworkKind(a.Match.Groups[1].Value)!.Value, Guid.Parse(a.Match.Groups[2].Value), CancellationToken.None));
                await Task.WhenAny(Task.WhenAll(finding.Values), Task.Delay(ArtworkStatusPatience));
                return asked.ToDictionary(a => a.Url, a => !finding.TryGetValue(a.Url, out var found) ? "none"
                    : !found.IsCompleted ? "pending"
                    : found.IsCompletedSuccessfully && found.Result is not null ? "ready" : "none");
            })
            .WithTags("Library").WithName("GetArtworkStatus")
            .WithSummary("For pictures that answered 503, which are ready now, which there are none of, and which are still pending, by URL. "
                         + "One request for a page of them, rather than one each.");

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

        catalogue.MapGet("/catalogue/search", async (string q, string? kinds, bool? local, CatalogueSearch search, CancellationToken ct) =>
            {
                var wanted = kinds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(k => Enum.TryParse<SearchKind>(k.TrimEnd('s'), ignoreCase: true, out var kind) ? kind : (SearchKind?)null)
                    .OfType<SearchKind>().ToHashSet();
                return Results.Ok(await search.SearchAsync(q, wanted, local == true, ct));
            })
            .WithName("SearchCatalogue")
            .WithSummary("Search artists, albums and tracks through the index plugin, with owned status. kinds limits it to some of artists, albums "
                         + "and tracks, which is quicker; local=true searches only what Navidex already holds, which answers at once.")
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

    /// <summary>Never matched again, until it's included: music no catalogue knows.</summary>
    private static void Exclude(LibraryFile file)
    {
        file.ExcludedAt ??= DateTime.UtcNow;
        file.TrackId = file.EditionId = file.SuggestedTrackId = file.SuggestedEditionId = null;
        file.ArtistId = null;
        file.MatchMethod = MatchMethod.Unmatched;
        file.MatchConfidence = null;
        file.ReviewReason = null;
        file.MatchedAt ??= DateTime.UtcNow;
    }

    /// <summary>Settled by hand: this file is this track, so it counts as owned.</summary>
    private static void Match(LibraryFile file, Track track, Guid? editionId)
    {
        file.TrackId = track.Id;
        file.EditionId = editionId;
        file.ArtistId = track.ArtistId;
        file.MatchMethod = MatchMethod.Manual;
        file.MatchConfidence = 1;
        file.ReviewReason = null;
        file.SuggestedTrackId = null;
        file.SuggestedEditionId = null;
        file.MatchedAt = DateTime.UtcNow;
    }
}
