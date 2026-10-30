using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Lyrics;

namespace Melodex.Plugins.Lrclib;

/// <summary>Entry point for the LRCLIB lyrics plugin.</summary>
public sealed class LrclibFactory : IPluginFactory
{
    public const string PluginId = "melodex.lyrics.lrclib";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "LRCLIB",
        Version = "0.1.0",
        Type = PluginType.Lyrics,
        ContractVersion = ContractVersions.Lyrics.ToString(),
        Description = "Time-synced and plain lyrics from LRCLIB, an open, community-made lyrics database. No account needed.",
        Homepage = "https://lrclib.net",
        Capabilities = [LyricsCapabilities.Synced, LyricsCapabilities.Plain],
        Access = new PluginAccess { Network = ["lrclib.net"] },
        MaxRequestsPerSecond = 5,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "server": {
                  "type": "string",
                  "format": "uri",
                  "default": "https://lrclib.net",
                  "title": "Server",
                  "description": "LRCLIB's address. Change it only if you run your own copy."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new Lrclib(context);
}

/// <summary>
/// Lyrics from LRCLIB: first by the song's title, artist, album and length, which LRCLIB matches to within a couple
/// of seconds; then, failing that, by searching for the title and artist and taking the closest in length.
/// </summary>
public sealed class Lrclib(PluginContext context) : ILyricsPlugin
{
    /// <summary>How far a version's length can be from the file's and still be the same recording.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http = context.HttpClient;
    private readonly Uri _server = new(((context.Settings.TryGetProperty("server", out var s) ? s.GetString() : null) is { Length: > 0 } server
        ? server : "https://lrclib.net").TrimEnd('/') + "/");

    public async Task<FoundLyrics?> FindAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        if (query.Album is not null && query.Duration is { } duration)
        {
            var exact = await GetAsync<JsonElement?>(
                $"api/get?track_name={Escape(query.Title)}&artist_name={Escape(query.Artist)}&album_name={Escape(query.Album)}&duration={(int)Math.Round(duration.TotalSeconds)}",
                cancellationToken);
            if (exact is { } found && Read(found) is { } lyrics)
                return lyrics;
        }
        var results = await GetAsync<JsonElement?>($"api/search?track_name={Escape(query.Title)}&artist_name={Escape(query.Artist)}", cancellationToken);
        return results is { ValueKind: JsonValueKind.Array } list ? Pick(list, query.Duration) : null;
    }

    /// <summary>
    /// The best of a search's results: those close enough in length (when it's known), synced before plain, then the
    /// closest in length. Null when none has lyrics or says it's instrumental.
    /// </summary>
    public static FoundLyrics? Pick(JsonElement results, TimeSpan? duration) => results.EnumerateArray()
        .Select(r => (Lyrics: Read(r), Off: duration is { } d && r.TryGetProperty("duration", out var length) && length.ValueKind == JsonValueKind.Number
            ? Math.Abs(length.GetDouble() - d.TotalSeconds) : (double?)null))
        .Where(r => r.Lyrics is not null && (duration is null || r.Off is { } off && off <= Tolerance.TotalSeconds))
        .OrderBy(r => r.Lyrics!.Synced is null ? 1 : 0).ThenBy(r => r.Lyrics!.Instrumental ? 1 : 0).ThenBy(r => r.Off ?? 0)
        .Select(r => r.Lyrics)
        .FirstOrDefault();

    /// <summary>One of LRCLIB's records as lyrics; null when it has neither words nor says it's instrumental.</summary>
    public static FoundLyrics? Read(JsonElement record)
    {
        string? Text(string name) => record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        var synced = Text("syncedLyrics");
        var plain = Text("plainLyrics");
        if (synced is not null || plain is not null)
            return new FoundLyrics(synced, plain);
        return record.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True
            ? new FoundLyrics(null, null, Instrumental: true)
            : null;
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var results = await GetAsync<JsonElement?>("api/search?track_name=Glory%20Box&artist_name=Portishead", cancellationToken);
            return results is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0
                ? HealthReport.Healthy()
                : new HealthReport(HealthStatus.Degraded, "LRCLIB answered, but didn't find a song it should know.");
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach LRCLIB: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, "LRCLIB didn't answer in time.");
        }
    }

    // Null for a 404, which is how LRCLIB says it has no such song.
    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(_server, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
