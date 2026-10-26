using Navidex.Plugins.FanartTv;

namespace Navidex.Core.Tests;

public sealed class FanartTvTests
{
    [Fact]
    public void Small_pictures_come_from_the_preview_folder()
    {
        Assert.Equal("https://assets.fanart.tv/preview/music/abc/artistthumb/x.jpg",
            FanartTv.Preview("https://assets.fanart.tv/fanart/music/abc/artistthumb/x.jpg"));
        Assert.Equal("https://example.com/fanart/x.jpg", FanartTv.Preview("https://example.com/fanart/x.jpg"));
    }
}
