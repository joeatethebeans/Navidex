using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Navidex.Contracts.Plugins;

namespace Navidex.Plugins.Navidrome;

/// <summary>Calls a Subsonic server with token authentication, from the connector's settings (URL, username, password).</summary>
internal sealed class SubsonicClient
{
    private const string ApiVersion = "1.16.1";
    private const string ClientName = "navidex";

    private readonly HttpClient _http;
    private readonly string _password;

    public SubsonicClient(PluginContext context)
    {
        _http = context.HttpClient;
        var settings = context.Settings;
        Server = new Uri(settings.GetProperty("url").GetString()!.TrimEnd('/') + "/");
        User = settings.GetProperty("username").GetString()!;
        _password = settings.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
    }

    public Uri Server { get; }

    public string User { get; }

    /// <summary>Checks the server answers and the credentials work, with the ping reply when it does.</summary>
    public async Task<(HealthReport Report, JsonElement? Reply)> PingAsync(CancellationToken ct)
    {
        try
        {
            var reply = await CallAsync("ping", [], ct);
            var server = reply.TryGetProperty("type", out var type) ? type.GetString() : "Subsonic server";
            var version = reply.TryGetProperty("serverVersion", out var v) ? $" {v.GetString()}" : "";
            return (new HealthReport(HealthStatus.Healthy, $"Connected to {server}{version} as {User}"), reply);
        }
        catch (SubsonicException e)
        {
            return (new HealthReport(HealthStatus.Unhealthy, e.Message), null);
        }
        catch (HttpRequestException e)
        {
            return (new HealthReport(HealthStatus.Unhealthy, $"Can't reach {Server}: {e.Message}"), null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (new HealthReport(HealthStatus.Unhealthy, $"{Server} didn't answer in time."), null);
        }
    }

    /// <summary>Calls an endpoint and returns the <c>subsonic-response</c> body.</summary>
    public async Task<JsonElement> CallAsync(string endpoint, IEnumerable<(string Name, string Value)> parameters, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, [.. parameters, ("f", "json")], ct);
        return Reply(endpoint, await response.Content.ReadFromJsonAsync<JsonElement>(ct));
    }

    /// <summary>Downloads binary content, such as cover art. Errors come back as a Subsonic response instead.</summary>
    public async Task<(byte[] Data, string ContentType)> DownloadAsync(string endpoint, IEnumerable<(string Name, string Value)> parameters, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, [.. parameters, ("f", "json")], ct);
        var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        if (type is "application/json" or "text/json" or "application/xml" or "text/xml")
        {
            Reply(endpoint, await response.Content.ReadFromJsonAsync<JsonElement>(ct));
            throw new SubsonicException($"{endpoint} answered without content.");
        }
        return (await response.Content.ReadAsByteArrayAsync(ct), type);
    }

    private async Task<HttpResponseMessage> SendAsync(string endpoint, IEnumerable<(string Name, string Value)> parameters, CancellationToken ct)
    {
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var token = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(_password + salt)));
        var query = new StringBuilder($"rest/{endpoint}?u={Uri.EscapeDataString(User)}&t={token}&s={salt}&v={ApiVersion}&c={ClientName}");
        foreach (var (name, value) in parameters)
            query.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));

        var response = await _http.GetAsync(new Uri(Server, query.ToString()), ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"{Server} answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        }
        return response;
    }

    private JsonElement Reply(string endpoint, JsonElement body)
    {
        if (!body.TryGetProperty("subsonic-response", out var reply))
            throw new SubsonicException($"{Server} isn't a Subsonic server.");
        if (reply.GetProperty("status").GetString() == "ok")
            return reply;
        var error = reply.TryGetProperty("error", out var e) ? e : default;
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
        var message = error.ValueKind == JsonValueKind.Object ? NavidromeConnector.Text(error, "message") : null;
        throw new SubsonicException(code switch
        {
            40 => "Wrong username or password.",
            50 => $"{User} isn't allowed to do that{(endpoint == "startScan" ? "; scanning needs an admin user" : "")}.",
            _ => message ?? $"{endpoint} failed with Subsonic error {code}.",
        }, code);
    }
}
