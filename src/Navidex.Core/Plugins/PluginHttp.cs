using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace Navidex.Core.Plugins;

/// <summary>Settings for the HTTP clients the core hands to plugins.</summary>
public class PluginHttpOptions
{
    /// <summary>Apply each plugin's <c>maxRequestsPerSecond</c>. Only tests replaying recorded responses turn this off.</summary>
    public bool RateLimit { get; set; } = true;

    /// <summary>Creates the innermost handler. Tests swap in a handler that replays recorded responses.</summary>
    public Func<HttpMessageHandler> PrimaryHandler { get; set; } = () => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
}

public static class PluginHttp
{
    public static readonly ProductInfoHeaderValue[] UserAgent =
    [
        new("Navidex", typeof(PluginHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0"),
        new("(https://github.com/joeatethebeans/Navidex)"),
    ];

    /// <summary>A client with the core's user agent, retries on 503 and 429, and the plugin's rate limit.</summary>
    public static HttpClient CreateClient(PluginHttpOptions options, double? maxRequestsPerSecond)
    {
        HttpMessageHandler handler = new RetryHandler { InnerHandler = options.PrimaryHandler() };
        if (options.RateLimit && maxRequestsPerSecond is > 0)
            handler = new RateLimitHandler(TimeSpan.FromSeconds(1 / maxRequestsPerSecond.Value)) { InnerHandler = handler };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        foreach (var product in UserAgent)
            client.DefaultRequestHeaders.UserAgent.Add(product);
        return client;
    }

    /// <summary>Spaces requests at least <c>interval</c> apart, across every caller of the client.</summary>
    private sealed class RateLimitHandler(TimeSpan interval) : DelegatingHandler
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTime _next = DateTime.MinValue;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var wait = _next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellationToken);
                _next = DateTime.UtcNow + interval;
            }
            finally
            {
                _gate.Release();
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Retries when the upstream says it's overloaded or rate-limiting, honouring Retry-After, and retries GETs whose
    /// connection failed.
    /// </summary>
    private sealed class RetryHandler : DelegatingHandler
    {
        private const int Attempts = 6;
        private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(request, cancellationToken);
                }
                catch (HttpRequestException) when (request.Method == HttpMethod.Get && attempt < Attempts)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken);
                    continue;
                }
                if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests) || attempt == Attempts)
                    return response;
                var delay = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                response.Dispose();
                await Task.Delay(delay > MaxDelay ? MaxDelay : delay, cancellationToken);
            }
        }

        private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), MaxDelay.TotalSeconds));
    }
}
