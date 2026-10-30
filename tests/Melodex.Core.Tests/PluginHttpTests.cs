using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Melodex.Core.Plugins;

namespace Melodex.Core.Tests;

/// <summary>The rate limit and retries the core puts between plugins and the services they call.</summary>
public sealed class PluginHttpTests
{
    [Fact]
    public async Task A_busy_answer_pauses_every_request_and_the_retry_waits_its_turn()
    {
        var source = new Source { BusyFirst = 1 };
        using var http = PluginHttp.CreateClient(Options(source, retryDelay: TimeSpan.FromMilliseconds(400)), maxRequestsPerSecond: 20);

        var first = http.GetAsync("http://source/a");
        await Task.Delay(100);
        var second = http.GetAsync("http://source/b");
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);

        // The second request was held back by the pause the first one's busy answer started, though it never got one.
        var sent = source.Sent.ToList();
        Assert.Equal(3, sent.Count);
        Assert.True(sent[1].At - sent[0].At >= TimeSpan.FromMilliseconds(350), $"sent {sent[1].At - sent[0].At} after the busy answer");
    }

    [Fact]
    public async Task Someone_searching_goes_ahead_of_background_work_and_gives_up_sooner()
    {
        var source = new Source { BusyFirst = int.MaxValue };
        using var http = PluginHttp.CreateClient(Options(source, retryDelay: TimeSpan.FromMilliseconds(10)), maxRequestsPerSecond: 50);

        var response = await Task.Run(async () =>
        {
            PluginHttp.Interactive.Value = true;
            return await http.GetAsync("http://source/search");
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, source.Sent.Count);
    }

    private static PluginHttpOptions Options(Source source, TimeSpan retryDelay) =>
        new() { PrimaryHandler = () => source, RetryDelay = retryDelay };

    private sealed class Source : HttpMessageHandler
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _count;

        public int BusyFirst { get; init; }

        public ConcurrentQueue<(TimeSpan At, string Path)> Sent { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Enqueue((_clock.Elapsed, request.RequestUri!.AbsolutePath));
            var busy = Interlocked.Increment(ref _count) <= BusyFirst;
            return Task.FromResult(new HttpResponseMessage(busy ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }
}
