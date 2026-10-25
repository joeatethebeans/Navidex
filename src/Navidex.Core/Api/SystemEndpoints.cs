using System.Reflection;
using Navidex.Contracts.Plugins;
using Navidex.Client;
using Navidex.Core.Data;
using Navidex.Core.Diagnostics;

namespace Navidex.Core.Api;

public static class SystemEndpoints
{
    public static RouteGroupBuilder MapSystemEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").WithTags("System");

        group.MapGet("/health", async (NavidexDbContext db, CancellationToken ct) =>
            {
                var database = await db.Database.CanConnectAsync(ct);
                var response = new HealthResponse(database ? "healthy" : "unhealthy", new Dictionary<string, string>
                {
                    ["database"] = database ? "healthy" : "unhealthy",
                });
                return database ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("GetHealth")
            .WithSummary("Service health, for monitoring.")
            .Produces<HealthResponse>()
            .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/version", () => new VersionResponse(
                Version: typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                ApiVersion: "v1",
                Contracts: new Dictionary<string, string>
                {
                    ["index"] = ContractVersions.Index.ToString(),
                    ["retrieval"] = ContractVersions.Retrieval.ToString(),
                    ["connector"] = ContractVersions.Connector.ToString(),
                }))
            .WithName("GetVersion")
            .WithSummary("Service version and the plugin contract versions it supports.");

        group.MapGet("/log", (ActivityLog log, long after = 0, int limit = 1000) =>
                new LogPage(log.After(after, Math.Clamp(limit, 1, ActivityLog.Capacity)), log.Latest))
            .WithName("GetLog")
            .WithSummary("Recent activity since the server started: scans, matches, catalogue updates, warnings and errors.");

        return api;
    }
}

/// <param name="Status"><c>healthy</c> or <c>unhealthy</c>.</param>
/// <param name="Checks">Each component's status.</param>
public record HealthResponse(string Status, IReadOnlyDictionary<string, string> Checks);

/// <param name="Version">Service version.</param>
/// <param name="ApiVersion">The API version these endpoints belong to.</param>
/// <param name="Contracts">Supported contract version per plugin type.</param>
public record VersionResponse(string Version, string ApiVersion, IReadOnlyDictionary<string, string> Contracts);
