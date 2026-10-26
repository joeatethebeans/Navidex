using Navidex.Client;
using Navidex.Core.Data;
using Navidex.Core.Library;
using Navidex.Core.Requests;

namespace Navidex.Core.Api;

public static class RequestEndpoints
{
    public static RouteGroupBuilder MapRequestEndpoints(this RouteGroupBuilder api)
    {
        var requests = api.MapGroup("/requests").WithTags("Requests");

        requests.MapPost("", async (CreateRequest body, RequestService service, CancellationToken ct) =>
            {
                try
                {
                    var (request, created) = await service.CreateAsync(body, "admin", ct);
                    if (request is null)
                        return Results.NotFound();
                    var info = await service.GetAsync(request.Id, ct);
                    return created ? Results.Accepted($"/api/v1/requests/{request.Id}", info) : Results.Ok(info);
                }
                catch (ArgumentException e)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["editionId"] = [e.Message] });
                }
                catch (LibraryBusyException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "The library is being scanned");
                }
            })
            .WithName("CreateRequest")
            .WithSummary("Fetch an artist, album or track into the library. Asking for something already queued returns that request (200) instead of a new one (202). Refused (409) while a scan is reading the library.")
            .Produces<RequestInfo>(StatusCodes.Status202Accepted).Produces<RequestInfo>().Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        requests.MapGet("", (RequestService service, bool? active, int? limit, CancellationToken ct) =>
                service.ListAsync(active ?? false, Math.Clamp(limit ?? 100, 1, 500), ct))
            .WithName("ListRequests").WithSummary("Requests asked for directly, newest first; parts of artist and album requests are under each one.");

        requests.MapGet("/{id:guid}", async (Guid id, RequestService service, CancellationToken ct) =>
                await service.GetAsync(id, ct) is { } info ? Results.Ok(info) : Results.NotFound())
            .WithName("GetRequest").WithSummary("A request with its parts and its fetch attempts.")
            .Produces<RequestInfo>().Produces(StatusCodes.Status404NotFound);

        requests.MapDelete("/{id:guid}", async (Guid id, NavidexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                await service.CancelAsync(request, ct);
                return Results.NoContent();
            })
            .WithName("CancelRequest").WithSummary("Cancel a request and its unfinished parts. Files already in the library stay.");

        requests.MapPost("/{id:guid}/retry", async (Guid id, NavidexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                return await service.RetryAsync(request, ct) ? Results.Ok(await service.GetAsync(id, ct)) : Results.Conflict();
            })
            .WithName("RetryRequest").WithSummary("Queue a failed or reviewed request again, with the parts of it that aren't in the library.")
            .Produces<RequestInfo>().Produces(StatusCodes.Status409Conflict);

        requests.MapPost("/{id:guid}/choose", async (Guid id, ChooseCandidate body, NavidexDbContext db, RequestService service, CancellationToken ct) =>
            {
                if (await db.Requests.FindAsync([id], ct) is not { } request)
                    return Results.NotFound();
                return await service.ChooseAsync(request, body.JobId, body.CandidateId, ct) ? Results.Ok(await service.GetAsync(id, ct)) : Results.Conflict();
            })
            .WithName("ChooseCandidate").WithSummary("Fetch one of the candidates an earlier search found, instead of searching again. For finished track or whole-album requests.")
            .Produces<RequestInfo>().Produces(StatusCodes.Status409Conflict);

        var settings = api.MapGroup("/settings").WithTags("Settings");
        settings.MapGet("/fetch", (SettingsService s, CancellationToken ct) => s.GetFetchAsync(ct)).WithName("GetFetchSettings");
        settings.MapPut("/fetch", async (FetchSettings value, SettingsService s, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (string.IsNullOrWhiteSpace(value.PathTemplate))
                    errors["pathTemplate"] = ["Can't be empty."];
                if (value.ParallelFetches is < 1 or > 8)
                    errors["parallelFetches"] = ["Must be between 1 and 8."];
                if (value.DurationToleranceSeconds is < 0 or > 120)
                    errors["durationToleranceSeconds"] = ["Must be between 0 and 120."];
                if (value.DurationTolerancePercent is < 0 or > 50)
                    errors["durationTolerancePercent"] = ["Must be between 0 and 50."];
                if (value.CandidatesPerPlugin is < 1 or > 5)
                    errors["candidatesPerPlugin"] = ["Must be between 1 and 5."];
                if (value.ListenNowExpiryMinutes is < 5 or > 7 * 24 * 60)
                    errors["listenNowExpiryMinutes"] = ["Must be between 5 minutes and a week."];
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors);
                await s.SaveFetchAsync(value, ct);
                return Results.NoContent();
            })
            .WithName("SaveFetchSettings").WithSummary("Applies to requests from their next step.");

        return api;
    }
}
