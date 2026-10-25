using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Contracts.Catalogue;
using Navidex.Contracts.Plugins.Index;
using Navidex.Core.Catalogue;
using Navidex.Core.Data;
using Navidex.Core.Library;
using Navidex.Core.Tasks;

namespace Navidex.Core.Api;

public static class LibraryEndpoints
{
    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        var library = api.MapGroup("/library").WithTags("Library");

        library.MapGet("", async (NavidexDbContext db, CancellationToken ct) =>
            {
                var files = await db.LibraryFiles.CountAsync(ct);
                var matched = await db.LibraryFiles.CountAsync(f => f.TrackId != null, ct);
                var pending = await db.LibraryFiles.CountAsync(f => f.MatchedAt == null, ct);
                var lastScan = await db.BackgroundTasks.Where(t => t.Kind == LibraryScanTask.TaskKind && t.Status == BackgroundTaskStatus.Succeeded)
                    .MaxAsync(t => (DateTime?)t.FinishedAt, ct);
                var current = await db.BackgroundTasks.Where(t => t.Status == BackgroundTaskStatus.Running || t.Status == BackgroundTaskStatus.Queued)
                    .OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
                var lastFinished = await db.BackgroundTasks.Where(t => t.Kind == LibraryScanTask.TaskKind && t.FinishedAt != null)
                    .OrderByDescending(t => t.FinishedAt).FirstOrDefaultAsync(ct);
                return new LibraryStatus(files, matched, files - matched - pending, pending, lastScan, current is null ? null : TaskQueue.ToInfo(current),
                    lastFinished is null ? null : TaskQueue.ToInfo(lastFinished));
            })
            .WithName("GetLibraryStatus").WithSummary("File counts, the last scan, and any task in progress.");

        library.MapPost("/scan", async (TaskQueue queue, CancellationToken ct) =>
                Results.Accepted(null, TaskQueue.ToInfo(await queue.EnqueueAsync(LibraryScanTask.TaskKind, null, ct))))
            .WithName("ScanLibrary").WithSummary("Scan the music folder, match new files and update the catalogue. Returns the queued task.")
            .Produces<TaskInfo>(StatusCodes.Status202Accepted);

        library.MapGet("/artists", (LibraryView view, CancellationToken ct) => view.GetLibraryArtistsAsync(ct))
            .WithName("GetLibraryArtists").WithSummary("Artists in the library, with owned, missing and covered album counts.");

        library.MapGet("/missing", (LibraryView view, CancellationToken ct) => view.GetMissingAsync(ct))
            .WithName("GetMissing").WithSummary("Missing albums across the library, after the default filters and ignores.");

        library.MapGet("/review", (LibraryView view, CancellationToken ct) => view.GetReviewAsync(ct))
            .WithName("GetReview").WithSummary("Files that couldn't be matched confidently, with the best suggestion.");

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
                    artists.Add(new SearchArtist(artist.Id, artist.Name, artist.Disambiguation, artist.InLibrary, [hit.Id]));
                }
                var albumIds = new List<Guid>();
                foreach (var hit in results.Albums)
                    albumIds.Add((await service.ImportAlbumAsync(hit, null, ct)).Id);
                var tracks = new List<Track>();
                foreach (var hit in results.Tracks)
                    tracks.Add(await service.ImportTrackAsync(hit, ct));
                await db.SaveChangesAsync(ct);

                var trackIds = tracks.Select(t => t.Id).ToList();
                var owned = await db.LibraryFiles.Where(f => f.TrackId != null && trackIds.Contains(f.TrackId.Value)).Select(f => f.TrackId!.Value).ToListAsync(ct);
                var albums = await view.GetAlbumSummariesAsync(albumIds, ct);
                return Results.Ok(new SearchResults(artists, albums,
                    tracks.Select((t, i) => new SearchTrack(t.Id, t.Title, t.ArtistCredit, t.Duration?.TotalSeconds, owned.Contains(t.Id), [results.Tracks[i].Id])).ToList()));
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
