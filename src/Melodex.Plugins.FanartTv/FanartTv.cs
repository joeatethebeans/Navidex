using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Artwork;

namespace Melodex.Plugins.FanartTv;

/// <summary>Entry point for the fanart.tv artwork plugin.</summary>
public sealed class FanartTvFactory : IPluginFactory
{
    public const string PluginId = "melodex.artwork.fanarttv";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "fanart.tv",
        Version = "0.1.0",
        Type = PluginType.Artwork,
        ContractVersion = ContractVersions.Artwork.ToString(),
        Description = "Artist pictures from fanart.tv, by MusicBrainz artist ID. Needs a free personal API key.",
        Homepage = "https://fanart.tv",
        Capabilities = [ArtworkCapabilities.Artists],
        Access = new PluginAccess { Network = ["webservice.fanart.tv", "assets.fanart.tv"] },
        MaxRequestsPerSecond = 2,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["apiKey"],
              "properties": {
                "apiKey": {
                  "type": "string",
                  "title": "API key",
                  "description": "Your personal API key, from your account page on fanart.tv.",
                  "writeOnly": true
                },
                "size": {
                  "type": "string",
                  "enum": ["small", "full"],
                  "default": "small",
                  "title": "Image size",
                  "description": "Small pictures are about 200 pixels wide; full ones 1000, which look sharper and take more space."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new FanartTv(context);
}

/// <summary>Artist thumbnails from fanart.tv, the most liked first.</summary>
public sealed class FanartTv(PluginContext context) : IArtworkPlugin
{
    private static readonly Uri Service = new("https://webservice.fanart.tv/v3/music/");

    // Portishead, who have pictures there, to check the key works.
    private const string KnownArtist = "8f6bd1e4-fbe1-4f50-aa9b-94c450ec0f11";

    private readonly HttpClient _http = context.HttpClient;
    private readonly string _apiKey = context.Settings.TryGetProperty("apiKey", out var key) ? key.GetString() ?? "" : "";
    private readonly bool _small = !(context.Settings.TryGetProperty("size", out var size) && size.GetString() == "full");

    public async Task<IReadOnlyList<ArtworkCandidate>> FindAsync(ArtworkQuery query, CancellationToken cancellationToken)
    {
        if (query.Subject != ArtworkSubject.Artist)
            return [];
        foreach (var id in query.Ids.Where(i => i.Provider == Providers.MusicBrainz))
        {
            if (await GetArtistAsync(id.Value, cancellationToken) is not { } artist || !artist.TryGetProperty("artistthumb", out var thumbs))
                continue;
            return thumbs.EnumerateArray()
                .Select(t => (Url: t.TryGetProperty("url", out var u) ? u.GetString() : null, Likes: t.TryGetProperty("likes", out var l) && int.TryParse(l.GetString(), out var n) ? n : 0))
                .Where(t => t.Url is not null)
                .OrderByDescending(t => t.Likes)
                .Select(t => new ArtworkCandidate(_small ? Preview(t.Url!) : t.Url!, "thumb"))
                .ToList();
        }
        return [];
    }

    /// <summary>fanart.tv serves a smaller copy of every image under <c>/preview/</c> instead of <c>/fanart/</c>.</summary>
    public static string Preview(string url) => url.Replace("assets.fanart.tv/fanart/", "assets.fanart.tv/preview/", StringComparison.Ordinal);

    public async Task<ArtworkImage?> DownloadAsync(ArtworkCandidate candidate, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(candidate.Key, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return new ArtworkImage(await response.Content.ReadAsByteArrayAsync(cancellationToken), response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetArtistAsync(KnownArtist, cancellationToken);
            return HealthReport.Healthy();
        }
        catch (FanartException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, e.Message);
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach fanart.tv: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, "fanart.tv didn't answer in time.");
        }
    }

    /// <summary>Everything fanart.tv has for an artist; null if it doesn't know them.</summary>
    private async Task<JsonElement?> GetArtistAsync(string mbid, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(Service, $"{Uri.EscapeDataString(mbid)}?api_key={Uri.EscapeDataString(_apiKey)}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new FanartException("fanart.tv didn't accept the API key.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}

/// <summary>fanart.tv refused the request.</summary>
public sealed class FanartException(string message) : Exception(message);
