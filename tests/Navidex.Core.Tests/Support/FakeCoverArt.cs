using System.Net;
using System.Net.Http.Headers;

namespace Navidex.Core.Tests.Support;

/// <summary>
/// Serves a tiny image for every Cover Art Archive request and passes everything else on, so artwork tests don't
/// depend on the archive having a picture. Counts the requests to show what the core's cache saves.
/// </summary>
public sealed class FakeCoverArt(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public static readonly byte[] Image = Convert.FromBase64String("R0lGODlhAQABAAAAACw=");

    public int Requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != "coverartarchive.org")
            return base.SendAsync(request, cancellationToken);
        Interlocked.Increment(ref Requests);
        var content = new ByteArrayContent(Image);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/gif");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
