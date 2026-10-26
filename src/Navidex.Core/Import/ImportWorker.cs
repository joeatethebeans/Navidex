using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Navidex.Contracts.Plugins;
using Navidex.Contracts.Plugins.Retrieval;

namespace Navidex.Core.Import;

// The beets worker: the only thing that writes the music library. It runs in its own container behind a small HTTP
// API (docs/import-worker.md). The core says exactly which MusicBrainz release and recordings the files are, so beets
// confirms a known target instead of guessing, then tags and files them.

/// <summary>Body of <c>POST /v1/import</c>.</summary>
/// <param name="StagingDirectory">Where the files are, relative to the staging volume.</param>
/// <param name="Release">The edition the files belong to.</param>
/// <param name="Files">Each file and the recording it is.</param>
/// <param name="DestinationDirectory">
/// Library-relative folder to put the files in instead of the one the path template gives, so a missing track joins
/// the album you already have wherever it lives. Null to follow the template.
/// </param>
/// <param name="PathTemplate">Beets path format for new albums, for example <c>$albumartist/$album%aunique{}/$track $title</c>.</param>
/// <param name="MusicBrainzServer">The MusicBrainz server beets should ask, so a mirror set for the core applies too.</param>
public sealed record ImportRequest(
    string StagingDirectory,
    ImportRelease Release,
    IReadOnlyList<ImportFile> Files,
    string? DestinationDirectory,
    string PathTemplate,
    string? MusicBrainzServer);

/// <param name="MusicBrainzId">The release's MusicBrainz ID, which pins beets' match.</param>
public sealed record ImportRelease(string MusicBrainzId, string Title, string Artist, string? Date, int TrackCount);

/// <param name="Path">Relative to the staging directory.</param>
/// <param name="RecordingId">The MusicBrainz recording the file is.</param>
/// <param name="TrackId">The MusicBrainz track (the recording's place on this release), when known.</param>
public sealed record ImportFile(string Path, string RecordingId, string? TrackId, string Title, string Artist, int Disc, int Position);

public sealed record ImportResult(IReadOnlyList<ImportedFile> Files);

/// <param name="Source">The staged file's path, as sent.</param>
/// <param name="Path">Where it is now, relative to the library root.</param>
/// <param name="Tags">The tags beets wrote, by beets field name.</param>
public sealed record ImportedFile(string Source, string Path, double? DurationSeconds, string? Format, IReadOnlyDictionary<string, string> Tags);

/// <summary>Imports staged files into the library.</summary>
public interface IImportWorker
{
    Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct);

    Task<HealthReport> CheckHealthAsync(CancellationToken ct);
}

public sealed class ImportWorkerOptions
{
    /// <summary>The beets worker's base URL.</summary>
    public string Url { get; set; } = "http://beets-worker:8080";

    /// <summary>A beets import asks MusicBrainz for the release, and moves files; albums take a while.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>The beets worker over HTTP.</summary>
public sealed class HttpImportWorker(HttpClient http) : IImportWorker
{
    public const string HttpClientName = "import";

    public static void Configure(IServiceProvider services, HttpClient http)
    {
        var options = services.GetRequiredService<IOptions<ImportWorkerOptions>>().Value;
        http.BaseAddress = new Uri(options.Url.EndsWith('/') ? options.Url : options.Url + "/");
        http.Timeout = options.Timeout;
    }

    public async Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v1/import", request, RetrievalJson.Options, ct);
        if (!response.IsSuccessStatusCode)
            throw new ImportException(await ErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<ImportResult>(RetrievalJson.Options, ct))!;
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("v1/health", ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<HealthReport>(RetrievalJson.Options, ct))!
                : new HealthReport(HealthStatus.Unhealthy, await ErrorAsync(response, ct));
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach the beets worker at {http.BaseAddress}: {e.Message}");
        }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (await response.Content.ReadFromJsonAsync<RetrievalError>(RetrievalJson.Options, ct) is { } error)
                return error.Error;
        }
        catch (JsonException)
        {
        }
        return $"The beets worker answered {(int)response.StatusCode} {response.ReasonPhrase}";
    }
}

/// <summary>Beets couldn't import the files, with its reason.</summary>
public sealed class ImportException(string message) : Exception(message);
