using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Navidex.Client;
using Navidex.Core.Data;
using Navidex.Core.Library;
using Navidex.Core.Plugins;
using Navidex.Core.Tests.Support;

namespace Navidex.Core.Tests;

/// <summary>When MusicBrainz is overloaded, a scan finishes anyway and leaves what it couldn't match for the next one.</summary>
public sealed class BusySourceTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "navidex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;
    private readonly NavidexClient _api;
    private int _requests;

    public BusySourceTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Navidex:DataPath", _dataPath)
            .UseSetting("Navidex:LibraryPath", TestLibrary.Music)
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new BusyHandler(() => Interlocked.Increment(ref _requests));
                http.RateLimit = false;
                http.RetryDelay = TimeSpan.FromMilliseconds(1);
                http.UnavailablePause = TimeSpan.FromMilliseconds(1);
            })));
        _http = _factory.CreateClient();
        _api = new NavidexClient(_http);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [TestLibraryFact]
    public async Task A_scan_while_musicbrainz_is_busy_finishes_and_leaves_files_for_the_next_scan()
    {
        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (task.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            task = (await _api.GetTaskAsync(task.Id))!;
        }

        Assert.True(task.State == TaskState.Succeeded, task.Error);
        Assert.Contains("left to match next scan", task.Progress);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
        Assert.Equal(await db.LibraryFiles.CountAsync(), await db.LibraryFiles.CountAsync(f => f.MatchedAt == null));

        // It stopped asking after a few folders rather than retrying every one of them.
        var log = (await _api.GetLogAsync()).Entries;
        Assert.Contains(log, e => e.Message.Contains("matching stops here"));
        Assert.Equal(LibraryMatcher.UnavailableLimit, log.Count(e => e.Message.Contains("for the next scan:")));
        Assert.True(_requests < 100, $"{_requests} requests");
    }

    [Fact]
    public async Task A_request_that_needs_musicbrainz_while_its_busy_gets_503()
    {
        var response = await _http.GetAsync("/api/v1/catalogue/search?q=Portishead");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    private sealed class BusyHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
                Content = new StringContent("""{"error":"The MusicBrainz web server is currently busy. Please try again later."}"""),
            });
        }
    }
}
