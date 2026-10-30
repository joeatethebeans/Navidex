using Melodex.Core.Listening;

namespace Melodex.Core.Tests;

public sealed class StreamTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_signed_url_works_for_its_own_path_until_it_expires()
    {
        var signer = new StreamSigner();
        var url = signer.Sign("api/v1/listen/abc/stream", Now);
        var query = url.Split('?')[1].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var exp = long.Parse(query["exp"]);

        Assert.True(signer.Verify("api/v1/listen/abc/stream", exp, query["sig"], Now));
        Assert.True(signer.Verify("api/v1/listen/abc/stream", exp, query["sig"], Now + StreamSigner.Lifetime));
        Assert.False(signer.Verify("api/v1/listen/abc/stream", exp, query["sig"], Now + StreamSigner.Lifetime + TimeSpan.FromSeconds(1)));
        Assert.False(signer.Verify("api/v1/listen/abd/stream", exp, query["sig"], Now));
        Assert.False(signer.Verify("api/v1/listen/abc/stream", exp + 3600, query["sig"], Now));
        Assert.False(signer.Verify("api/v1/listen/abc/stream", exp, query["sig"][..^1] + "x", Now));
        Assert.False(signer.Verify("api/v1/listen/abc/stream", exp, null, Now));
    }

    [Theory]
    [InlineData("a.opus", null, "audio/ogg; codecs=opus")]
    [InlineData("a.ogg", null, "audio/ogg; codecs=vorbis")]
    [InlineData("a.webm", "opus", "audio/webm; codecs=opus")]
    [InlineData("a.m4a", null, "audio/mp4; codecs=mp4a.40.2")]
    [InlineData("a.m4a", "alac", "audio/mp4; codecs=alac")]
    [InlineData("a.MP3", null, "audio/mpeg")]
    [InlineData("a.flac", null, "audio/flac")]
    [InlineData("a.wav", null, "audio/wav")]
    [InlineData("a.xyz", null, "application/octet-stream")]
    public void Files_are_served_as_a_type_browsers_can_check(string path, string? codec, string expected)
    {
        Assert.Equal(expected, MediaTypes.For(path, codec));
        Assert.Equal(expected.Split(';')[0], MediaTypes.Bare(expected));
    }
}
