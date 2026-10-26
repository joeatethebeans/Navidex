using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Core.Data;
using Navidex.Core.Events;
using Navidex.Core.Library;
using Navidex.Core.Listening;

namespace Navidex.Core.Api;

public static class ListenEndpoints
{
    public static RouteGroupBuilder MapListenEndpoints(this RouteGroupBuilder api)
    {
        var listen = api.MapGroup("/listen").WithTags("Listen now");

        listen.MapPost("", async (StartListen body, ListenNowService service, CancellationToken ct) =>
            {
                try
                {
                    var items = await service.StartAsync(body, ct);
                    return items is null ? Results.NotFound() : Results.Accepted(null, await service.ToInfoAsync(items, ct));
                }
                catch (ListenNowException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "Nothing to listen to");
                }
            })
            .WithName("StartListening")
            .WithSummary("Fetch a track to listen to without adding it to the library, or an album's missing tracks in order. "
                         + "A track already on its way or playable is returned as it is. Refused (409) for tracks you have.")
            .Produces<IReadOnlyList<ListenNowInfo>>(StatusCodes.Status202Accepted).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        listen.MapGet("", (ListenNowService service, CancellationToken ct) => service.ListAsync(ct))
            .WithName("ListListening").WithSummary("Listen-now tracks that haven't expired, and those added in the last day, newest first.");

        listen.MapGet("/{id:guid}", async (Guid id, NavidexDbContext db, ListenNowService service, CancellationToken ct) =>
                await db.ListenNowItems.FindAsync([id], ct) is { } item ? Results.Ok((await service.ToInfoAsync([item], ct))[0]) : Results.NotFound())
            .WithName("GetListening").WithSummary("One listen-now track, with a fresh stream URL while it's playable.")
            .Produces<ListenNowInfo>().Produces(StatusCodes.Status404NotFound);

        listen.MapGet("/{id:guid}/stream", async (Guid id, long? exp, string? sig, HttpContext http, NavidexDbContext db, ListenNowService service,
                StreamSigner signer, CancellationToken ct) =>
            {
                if (!signer.Verify(ListenNowService.StreamPath(id), exp ?? 0, sig))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (await db.ListenNowItems.FindAsync([id], ct) is not { } item || service.FullPath(item) is not { } file || !File.Exists(file))
                    return Results.NotFound();
                await service.TouchAsync(item, ct);
                return Results.File(file, MediaTypes.Bare(MediaTypes.For(file, item.Codec)), enableRangeProcessing: true);
            })
            .WithName("StreamListening")
            .WithSummary("The file, with range requests for seeking. Needs the signature from its stream URL. Each play keeps it for the expiry setting from then.")
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status206PartialContent).Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

        listen.MapPost("/{id:guid}/add", async (Guid id, NavidexDbContext db, ListenNowService service, CancellationToken ct) =>
            {
                if (await db.ListenNowItems.FindAsync([id], ct) is not { } item)
                    return Results.NotFound();
                try
                {
                    await service.AddAsync(item, ct);
                    return Results.Accepted(null, (await service.ToInfoAsync([item], ct))[0]);
                }
                catch (LibraryBusyException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "The library is being scanned");
                }
                catch (ListenNowException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "Can't add it");
                }
            })
            .WithName("AddListening")
            .WithSummary("Add a playable track to the library: it's filed by beets and announced to the media server, without fetching it again. "
                         + "Follow it until it's in the library, then use its media server track ID.")
            .Produces<ListenNowInfo>(StatusCodes.Status202Accepted).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        listen.MapDelete("/{id:guid}", async (Guid id, NavidexDbContext db, ListenNowService service, CancellationToken ct) =>
            {
                if (await db.ListenNowItems.FindAsync([id], ct) is not { } item)
                    return Results.NotFound();
                try
                {
                    await service.DiscardAsync(item, ct);
                    return Results.NoContent();
                }
                catch (ListenNowException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "Can't discard it");
                }
            })
            .WithName("DiscardListening").WithSummary("Stop fetching a track, or delete its file now instead of when it expires.");

        // ---- Tracks you have ----

        api.MapGet("/tracks/{id:guid}/play", async (Guid id, Guid? edition, NavidexDbContext db, StreamSigner signer, CancellationToken ct) =>
            {
                var file = await db.LibraryFiles.Where(f => f.TrackId == id)
                    .OrderByDescending(f => edition != null && f.EditionId == edition).ThenBy(f => f.Path)
                    .Select(f => new
                    {
                        f.Id, f.Path, f.Duration, f.EditionId, f.Track!.Title, f.Track.ArtistCredit, f.Track.ArtistId,
                        AlbumId = (Guid?)f.Edition!.AlbumId, AlbumTitle = f.Edition.Album.Title,
                    })
                    .FirstOrDefaultAsync(ct);
                if (file is null)
                    return Results.NotFound();
                var codec = MediaTypes.CodecFor(file.Path);
                return Results.Ok(new PlayInfo(id, file.Id, file.Title, file.ArtistCredit, file.ArtistId, file.AlbumId, file.AlbumTitle,
                    file.AlbumId is { } album ? LibraryView.CoverUrl(album, file.EditionId) : null, file.Duration?.TotalSeconds,
                    codec, MediaTypes.For(file.Path, codec), signer.Sign(LibraryStreamPath(file.Id))));
            })
            .WithTags("Library")
            .WithName("PlayTrack").WithSummary("How to play a track you have: a signed stream URL for its file in your music folder. 404 if you don't have it.")
            .Produces<PlayInfo>().Produces(StatusCodes.Status404NotFound);

        api.MapGet("/library/files/{id:guid}/stream", async (Guid id, long? exp, string? sig, NavidexDbContext db, StreamSigner signer,
                Microsoft.Extensions.Options.IOptions<NavidexOptions> options, CancellationToken ct) =>
            {
                if (!signer.Verify(LibraryStreamPath(id), exp ?? 0, sig))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } row)
                    return Results.NotFound();
                var root = Path.GetFullPath(options.Value.LibraryPath);
                var file = Path.GetFullPath(Path.Combine(root, row.Path));
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
                    return Results.NotFound();
                return Results.File(file, MediaTypes.Bare(MediaTypes.For(file)), enableRangeProcessing: true);
            })
            .WithTags("Library")
            .WithName("StreamLibraryFile").WithSummary("A file from your music folder, with range requests for seeking. Needs the signature from its stream URL.")
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status206PartialContent).Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

        // ---- Events ----

        api.MapGet("/events", async (HttpContext http, EventFeed feed, CancellationToken ct) =>
            {
                http.Response.Headers.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache";
                // Tells nginx and similar proxies not to hold events back.
                http.Response.Headers["X-Accel-Buffering"] = "no";
                using var subscription = feed.Subscribe();
                try
                {
                    await http.Response.WriteAsync(": connected\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    while (!ct.IsCancellationRequested)
                    {
                        using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        beat.CancelAfter(KeepAlive);
                        try
                        {
                            await subscription.Reader.WaitToReadAsync(beat.Token);
                            while (subscription.Reader.TryRead(out var @event))
                                await http.Response.WriteAsync($"event: {@event.Type}\ndata: {JsonSerializer.Serialize(@event, NavidexJson.Options)}\n\n", ct);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Nothing for a while: a comment keeps proxies from closing the connection.
                            await http.Response.WriteAsync(": keep-alive\n\n", ct);
                        }
                        await http.Response.Body.FlushAsync(ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The client went away.
                }
            })
            .WithTags("System")
            .WithName("Events")
            .WithSummary("Server-sent events saying what changed: requests, listen (listen-now tracks), library or tasks. Reload what you show when one arrives.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        return api;
    }

    /// <summary>How often an idle event stream sends a comment, so proxies keep it open.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static string LibraryStreamPath(Guid fileId) => $"api/v1/library/files/{fileId}/stream";
}
