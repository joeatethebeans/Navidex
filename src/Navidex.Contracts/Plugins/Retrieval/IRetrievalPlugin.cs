namespace Navidex.Contracts.Plugins.Retrieval;

/// <summary>
/// The retrieval contract as seen from .NET. The core implements it over HTTP against a plugin container;
/// each method maps to one endpoint of docs/contracts/retrieval-v1.openapi.yaml. Settings travel with every
/// call, so implementations hold them per instance.
/// </summary>
public interface IRetrievalPlugin
{
    /// <summary><c>GET /v1/manifest</c>.</summary>
    Task<PluginManifest> GetManifestAsync(CancellationToken cancellationToken);

    /// <summary><c>POST /v1/health</c>.</summary>
    Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary><c>POST /v1/search</c>.</summary>
    Task<IReadOnlyList<Candidate>> SearchAsync(RetrievalTarget target, int limit, CancellationToken cancellationToken);

    /// <summary><c>POST /v1/fetch</c>. Returns as soon as the job is accepted.</summary>
    Task<FetchStatus> StartFetchAsync(string jobId, RetrievalTarget target, Candidate candidate, string stagingDirectory, CancellationToken cancellationToken);

    /// <summary><c>GET /v1/fetch/{jobId}</c>.</summary>
    Task<FetchStatus> GetFetchStatusAsync(string jobId, CancellationToken cancellationToken);

    /// <summary><c>DELETE /v1/fetch/{jobId}</c>. Only offered when the manifest declares <see cref="RetrievalCapabilities.Cancel"/>.</summary>
    Task CancelFetchAsync(string jobId, CancellationToken cancellationToken);
}
