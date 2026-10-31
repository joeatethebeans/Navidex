using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Melodex.Contracts.Plugins;

namespace Melodex.Plugins.Navidrome;

/// <summary>Calls a Subsonic server with token authentication, from the connector's settings (URL, username, password).</summary>
internal sealed class SubsonicClient
{
    private const string ApiVersion = "1.16.1";
    private const string ClientName = "melodex";

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

    /// <summary>The same server, as another user.</summary>
    private SubsonicClient(HttpClient http, Uri server, string user, string password)
    {
        _http = http;
        Server = server;
        User = user;
        _password = password;
    }

    public SubsonicClient As(string user, string password) => new(_http, Server, user, password);

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

    // ---- Navidrome's own API, for what Subsonic can't do, such as playlists' pictures ----

    // Its sign-in tokens, by server and user, kept while they work.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Tokens = new();

    /// <summary>Calls Navidrome's own API as this user, signing in when there's no token yet or it's run out.</summary>
    public async Task<HttpResponseMessage> NativeAsync(HttpMethod method, string path, Func<HttpContent?> content, CancellationToken ct)
    {
        var key = $"{Server}|{User}|{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_password)))}";
        for (var attempt = 0; ; attempt++)
        {
            if (!Tokens.TryGetValue(key, out var token))
                Tokens[key] = token = await SignInAsync(ct);
            using var request = new HttpRequestMessage(method, new Uri(Server, path)) { Content = content() };
            request.Headers.Add("X-ND-Authorization", $"Bearer {token}");
            var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                Tokens.TryRemove(key, out _);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new HttpRequestException($"{Server} answered {(int)response.StatusCode} {response.ReasonPhrase} to {method} {path}", null, response.StatusCode);
            }
            return response;
        }
    }

    private async Task<string> SignInAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(Server, "auth/login"), new { username = User, password = _password }, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new SubsonicException("Wrong username or password.", 40);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{Server} answered {(int)response.StatusCode} {response.ReasonPhrase} to signing in", null, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.TryGetProperty("token", out var token) && token.GetString() is { } value
            ? value
            : throw new SubsonicException($"{Server} didn't give a token on signing in: it may not be Navidrome.");
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
