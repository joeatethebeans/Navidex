using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Navidex.Contracts.Plugins;

/// <summary>
/// Entry point of an in-process plugin assembly (index plugins and connectors). The core finds every
/// public, non-abstract implementation with a parameterless constructor in the plugins folder at startup,
/// checks <see cref="PluginManifest.ContractVersion"/>, and refuses plugins built for a contract it doesn't support.
/// </summary>
public interface IPluginFactory
{
    /// <summary>The plugin's manifest.</summary>
    PluginManifest Manifest { get; }

    /// <summary>
    /// Creates a configured instance. Called once per plugin instance the user sets up, and again when
    /// its settings change. Must not do network I/O; use <see cref="IPlugin.CheckHealthAsync"/> for that.
    /// </summary>
    IPlugin Create(PluginContext context);
}

/// <summary>What the core hands a plugin instance.</summary>
public sealed record PluginContext
{
    /// <summary>The instance's settings, already validated against <see cref="PluginManifest.SettingsSchema"/>.</summary>
    public required JsonElement Settings { get; init; }

    /// <summary>An HTTP client owned by the core, with a descriptive user agent set. Rate limiting is applied by the core.</summary>
    public required HttpClient HttpClient { get; init; }

    /// <summary>Logger factory; log lines are attached to the instance in the web app.</summary>
    public required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>A private directory the plugin may write to; it survives restarts.</summary>
    public required string DataDirectory { get; init; }
}

/// <summary>Base of every in-process plugin instance.</summary>
public interface IPlugin
{
    /// <summary>
    /// Checks that the plugin can do its job with its current settings, for example by calling its upstream
    /// service. Backs the Test button in the setup form and the periodic health check.
    /// </summary>
    Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken);
}

/// <summary>Plugin health states.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HealthStatus>))]
public enum HealthStatus
{
    /// <summary>Working.</summary>
    Healthy,

    /// <summary>Working with problems, for example slow or partially failing.</summary>
    Degraded,

    /// <summary>Not working.</summary>
    Unhealthy,
}

/// <summary>Result of a health check.</summary>
/// <param name="Status">Overall state.</param>
/// <param name="Message">Human-readable detail, shown next to the plugin in Settings.</param>
public sealed record HealthReport(HealthStatus Status, string? Message = null)
{
    /// <summary>A healthy report with no message.</summary>
    public static HealthReport Healthy() => new(HealthStatus.Healthy);
}
