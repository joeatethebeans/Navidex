using System.Net;
using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Artwork;

namespace Melodex.Plugins.MusicBrainz;

/// <summary>Entry point for the Cover Art Archive artwork plugin.</summary>
public sealed class CoverArtArchiveFactory : IPluginFactory
{
    public const string PluginId = "melodex.artwork.coverartarchive";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Cover Art Archive",
        Version = "0.1.0",
        Type = PluginType.Artwork,
        ContractVersion = ContractVersions.Artwork.ToString(),
        Description = "Album covers MusicBrainz users have uploaded, run with the Internet Archive.",
        Homepage = "https://coverartarchive.org",
        Capabilities = [ArtworkCapabilities.Albums, ArtworkCapabilities.Editions],
        Access = new PluginAccess { Network = ["coverartarchive.org", "archive.org"] },
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "size": {
                  "type": "integer",
                  "enum": [250, 500, 1200],
                  "default": 500,
                  "title": "Image size",
                  "description": "Width in pixels of the covers kept. Bigger looks sharper on large screens and takes more space."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new CoverArtArchive(context);
}

/// <summary>
/// Front covers from the Cover Art Archive, by MusicBrainz ID: an edition's own (its release), then its album's
/// (the release group's chosen cover).
/// </summary>
public sealed class CoverArtArchive(PluginContext context) : IArtworkPlugin
{
    private static readonly Uri Server = new("https://coverartarchive.org/");

    // Dummy (1994), which has covers, to check the archive answers.
    private const string KnownRelease = "76df3287-6cda-33eb-8e9a-044b5e15ffdd";

    private readonly HttpClient _http = context.HttpClient;
    private readonly int _size = context.Settings.TryGetProperty("size", out var size) && size.TryGetInt32(out var s) ? s : 500;

    public Task<IReadOnlyList<ArtworkCandidate>> FindAsync(ArtworkQuery query, CancellationToken cancellationToken)
    {
        static IEnumerable<string> MusicBrainzIds(IEnumerable<ProviderId> ids) =>
            ids.Where(i => i.Provider == Providers.MusicBrainz).Select(i => i.Value);

        var releases = query.Subject == ArtworkSubject.Edition ? MusicBrainzIds(query.Ids) : [];
        var groups = MusicBrainzIds(query.Subject == ArtworkSubject.Edition ? query.AlbumIds : query.Subject == ArtworkSubject.Album ? query.Ids : []);
        IReadOnlyList<ArtworkCandidate> candidates =
        [
            .. releases.Select(id => new ArtworkCandidate($"release/{id}/front-{_size}")),
            .. groups.Select(id => new ArtworkCandidate($"release-group/{id}/front-{_size}")),
        ];
        return Task.FromResult(candidates);
    }

    public async Task<ArtworkImage?> DownloadAsync(ArtworkCandidate candidate, CancellationToken cancellationToken)
    {
        // The archive redirects to the image on archive.org, which the client follows.
        using var response = await _http.GetAsync(new Uri(Server, candidate.Key), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return new ArtworkImage(await response.Content.ReadAsByteArrayAsync(cancellationToken), response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(Server, $"release/{KnownRelease}"), cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthReport.Healthy()
                : new HealthReport(HealthStatus.Unhealthy, $"The Cover Art Archive answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach the Cover Art Archive: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, "The Cover Art Archive didn't answer in time.");
        }
    }
}
