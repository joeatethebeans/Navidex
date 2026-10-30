using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Artwork;

namespace Melodex.Plugins.Wikimedia;

/// <summary>Entry point for the Wikimedia Commons artwork plugin.</summary>
public sealed class WikimediaCommonsFactory : IPluginFactory
{
    public const string PluginId = "melodex.artwork.wikimedia";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Wikimedia Commons",
        Version = "0.1.0",
        Type = PluginType.Artwork,
        ContractVersion = ContractVersions.Artwork.ToString(),
        Description = "Freely licensed photos of artists from Wikimedia Commons, found through Wikidata by MusicBrainz artist ID. No account needed.",
        Homepage = "https://commons.wikimedia.org",
        Capabilities = [ArtworkCapabilities.Artists],
        Access = new PluginAccess { Network = ["www.wikidata.org", "commons.wikimedia.org", "thumb.wikimedia.org", "upload.wikimedia.org"] },
        MaxRequestsPerSecond = 5,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "size": {
                  "type": "integer",
                  "enum": [250, 500, 1000],
                  "default": 500,
                  "title": "Image size",
                  "description": "Width in pixels of the pictures kept. Bigger looks sharper on large screens and takes more space."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new WikimediaCommons(context);
}

/// <summary>
/// The image Wikidata gives for an artist (its "image" statement, P18), found by the artist's MusicBrainz ID (P434),
/// so it's never someone else of the same name. Mostly well-known artists have one.
/// </summary>
public sealed class WikimediaCommons(PluginContext context) : IArtworkPlugin
{
    private static readonly Uri Wikidata = new("https://www.wikidata.org/w/api.php");

    // Portishead, who have a picture there, to check the service answers.
    private const string KnownArtist = "8f6bd1e4-fbe1-4f50-aa9b-94c450ec0f11";

    private readonly HttpClient _http = context.HttpClient;
    private readonly int _size = context.Settings.TryGetProperty("size", out var size) && size.TryGetInt32(out var s) ? s : 500;

    public async Task<IReadOnlyList<ArtworkCandidate>> FindAsync(ArtworkQuery query, CancellationToken cancellationToken)
    {
        if (query.Subject != ArtworkSubject.Artist)
            return [];
        foreach (var id in query.Ids.Where(i => i.Provider == Providers.MusicBrainz))
        {
            if (await FindItemAsync(id.Value, cancellationToken) is not { } item)
                continue;
            var files = await ImagesAsync(item, cancellationToken);
            if (files.Count > 0)
                return [.. files.Select(f => new ArtworkCandidate(ImageUrl(f, _size), "photo"))];
        }
        return [];
    }

    /// <summary>A scaled copy of a Commons file; Commons redirects to the thumbnail.</summary>
    public static string ImageUrl(string file, int width) =>
        $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(file.Replace(' ', '_'))}?width={width}";

    public async Task<ArtworkImage?> DownloadAsync(ArtworkCandidate candidate, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(candidate.Key, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return type.StartsWith("image/", StringComparison.Ordinal)
            ? new ArtworkImage(await response.Content.ReadAsByteArrayAsync(cancellationToken), type)
            : null;
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await FindItemAsync(KnownArtist, cancellationToken) is null
                ? new HealthReport(HealthStatus.Degraded, "Wikidata answered, but didn't find an artist it should know.")
                : HealthReport.Healthy();
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach Wikidata: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, "Wikidata didn't answer in time.");
        }
    }

    /// <summary>The Wikidata item with this MusicBrainz artist ID, such as <c>Q191352</c>.</summary>
    private async Task<string?> FindItemAsync(string mbid, CancellationToken ct)
    {
        var search = await GetAsync($"action=query&list=search&srnamespace=0&srlimit=1&srsearch={Uri.EscapeDataString($"haswbstatement:P434={mbid}")}", ct);
        return search.TryGetProperty("query", out var q) && q.TryGetProperty("search", out var hits) && hits.GetArrayLength() > 0
            ? hits[0].GetProperty("title").GetString()
            : null;
    }

    /// <summary>The item's images, preferred ones first.</summary>
    private async Task<IReadOnlyList<string>> ImagesAsync(string item, CancellationToken ct)
    {
        var claims = await GetAsync($"action=wbgetclaims&property=P18&entity={Uri.EscapeDataString(item)}", ct);
        return Files(claims);
    }

    /// <summary>The file names in a <c>wbgetclaims</c> answer for P18, preferred rank first and deprecated ones left out.</summary>
    public static IReadOnlyList<string> Files(JsonElement claims)
    {
        if (!claims.TryGetProperty("claims", out var all) || !all.TryGetProperty("P18", out var images))
            return [];
        return images.EnumerateArray()
            .Select(c => (Rank: c.TryGetProperty("rank", out var r) ? r.GetString() : null,
                File: c.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var value) && value.TryGetProperty("value", out var v)
                    ? v.GetString() : null))
            .Where(c => c.File is { Length: > 0 } && c.Rank != "deprecated")
            .OrderBy(c => c.Rank == "preferred" ? 0 : 1)
            .Select(c => c.File!)
            .ToList();
    }

    private async Task<JsonElement> GetAsync(string query, CancellationToken ct) =>
        await _http.GetFromJsonAsync<JsonElement>(new Uri(Wikidata, $"?format=json&{query}"), ct);
}
