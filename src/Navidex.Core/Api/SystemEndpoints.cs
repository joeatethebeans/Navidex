using System.Reflection;
using Navidex.Contracts.Plugins;
using Navidex.Client;
using Navidex.Core.Access;
using Navidex.Core.Data;
using Navidex.Core.Diagnostics;
using Navidex.Core.Library;

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
            .AllowAnyone()
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
            .AllowAnyone()
            .WithName("GetVersion")
            .WithSummary("Service version and the plugin contract versions it supports.");

        group.MapGet("/log", (ActivityLog log, long after = 0, int limit = 1000) =>
                new LogPage(log.After(after, Math.Clamp(limit, 1, ActivityLog.PageLimit)), log.Latest))
            .RequireScope(Scopes.Settings)
            .WithName("GetLog")
            .WithSummary("Recent activity, as far back as the log settings keep in memory: scans, matches, requests, warnings and errors.");

        var settings = api.MapGroup("/settings").WithTags("Settings").RequireScope(Scopes.Settings);
        settings.MapGet("/log", (SettingsService s, CancellationToken ct) => s.GetLogAsync(ct)).WithName("GetLogSettings");
        settings.MapPut("/log", async (LogSettings value, SettingsService s, IEnumerable<IHostedService> services, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (value.MemoryDays is < 1 or > 90)
                    errors["memoryDays"] = ["Must be between 1 and 90."];
                if (value.FileDays is < 1 or > 3650)
                    errors["fileDays"] = ["Must be between 1 and 3650."];
                else if (value.FileDays < value.MemoryDays)
                    errors["fileDays"] = ["Must be at least as long as the Log page keeps entries."];
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors);
                await s.SaveLogAsync(value, ct);
                if (services.OfType<LogArchive>().FirstOrDefault() is { } archive)
                    await archive.TidyAsync(ct);
                return Results.NoContent();
            })
            .WithName("SaveLogSettings").WithSummary("Applies at once: older entries leave the Log page, and older backup files are deleted.");

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
