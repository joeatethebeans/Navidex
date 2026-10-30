using System.Net;
using System.Text.Json;
using System.Web;

namespace Melodex.Core.Tests.Support;

/// <summary>
/// Stands in for LRCLIB: songs in <paramref name="songs"/> have the lyrics given, by title, and every other song has
/// none. Everything else goes on to the next handler.
/// </summary>
public sealed class FakeLrclib(HttpMessageHandler inner, IReadOnlyDictionary<string, string> songs) : DelegatingHandler(inner)
{
    private IReadOnlyDictionary<string, string> Songs => songs;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != "lrclib.net")
            return base.SendAsync(request, cancellationToken);
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        var title = query["track_name"] ?? "";
        object? record = Songs.TryGetValue(title, out var lyrics)
            ? new { trackName = title, artistName = query["artist_name"], duration = 0, instrumental = false, syncedLyrics = lyrics, plainLyrics = (string?)null }
            : null;
        if (request.RequestUri.AbsolutePath == "/api/get")
            return Task.FromResult(record is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"code":404,"name":"TrackNotFound"}""") }
                : Json(record));
        // Searches find it whatever its length, as LRCLIB's do: the plugin checks.
        return Task.FromResult(Json(record is null ? Array.Empty<object>() : new[] { record }));
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
}
