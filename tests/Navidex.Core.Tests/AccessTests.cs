using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Navidex.Client;
using Navidex.Core.Access;
using Navidex.Core.Data;
using Navidex.Core.Events;
using Navidex.Core.Library;
using Navidex.Core.Tests.Support;

namespace Navidex.Core.Tests;

/// <summary>Signing in, the local network, API keys and their scopes, and webhooks.</summary>
public sealed class AccessTests : IDisposable
{
    /// <summary>Stands in for the caller's address, which the test server doesn't have.</summary>
    private const string RemoteHeader = "X-Test-Remote";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "navidex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Receiver _receiver = new();

    public AccessTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Navidex:DataPath", _dataPath)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new RemoteAddress());
                services.AddHttpClient(WebhookSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _receiver);
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [Fact]
    public async Task Before_setup_only_open_endpoints_answer()
    {
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/v1/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/v1/version")).StatusCode);
        var status = await http.GetFromJsonAsync<AuthStatus>("api/v1/auth/status", NavidexJson.Options);
        Assert.False(status!.SetUp);
        Assert.False(status.LetIn);

        var refused = await http.GetAsync("api/v1/library");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("setup-required", (await Problem(refused)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Setup_happens_once_and_then_the_admin_signs_in()
    {
        var admin = _factory.CreateSignedInClient();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("api/v1/library")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("api/v1/auth/setup", new SetUpLogin("someone", "another password"))).StatusCode);

        var other = _factory.CreateClient();
        var refused = await other.GetAsync("api/v1/library");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("sign-in-required", (await Problem(refused)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await other.PostAsJsonAsync("api/v1/auth/login", new SignIn(SignedIn.Username, "wrong password"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await other.PostAsJsonAsync("api/v1/auth/login", new SignIn("ADMIN", SignedIn.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("api/v1/library")).StatusCode);

        await other.PostAsync("api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("api/v1/library")).StatusCode);
    }

    [Fact]
    public async Task Too_many_wrong_passwords_have_to_wait()
    {
        _factory.CreateSignedInClient();
        var http = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await http.PostAsJsonAsync("api/v1/auth/login", new SignIn(SignedIn.Username, "wrong password"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await http.PostAsJsonAsync("api/v1/auth/login", new SignIn(SignedIn.Username, SignedIn.Password))).StatusCode);
    }

    [Fact]
    public async Task A_new_password_signs_out_other_sessions_but_not_this_one()
    {
        var first = new NavidexClient(_factory.CreateSignedInClient());
        var secondHttp = _factory.CreateSignedInClient();

        await first.UpdateLoginAsync(new UpdateLogin(SignedIn.Password, NewPassword: "a brand new password"));

        Assert.NotNull(await first.GetLibraryStatusAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await secondHttp.GetAsync("api/v1/library")).StatusCode);
        var wrong = await Assert.ThrowsAsync<NavidexApiException>(() => first.UpdateLoginAsync(new UpdateLogin("not it", "renamed")));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
    }

    [Fact]
    public async Task Keys_do_what_their_scopes_allow()
    {
        var admin = new NavidexClient(_factory.CreateSignedInClient());
        var reader = await admin.CreateApiKeyAsync(new CreateApiKey("Reader app", [Scopes.Read]));
        var requester = await admin.CreateApiKeyAsync(new CreateApiKey("Requester app", [Scopes.Read, Scopes.Request]));
        Assert.StartsWith("ndx_", reader.Key);
        Assert.StartsWith(reader.Info.Prefix, reader.Key);

        var app = _factory.CreateClient();
        app.DefaultRequestHeaders.Add(AccessService.KeyHeader, reader.Key);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("api/v1/library")).StatusCode);

        var refused = await app.PostAsJsonAsync("api/v1/requests", new CreateRequest(RequestKind.Track, Guid.NewGuid()), NavidexJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await Problem(refused);
        Assert.Equal("scope-required", problem.GetProperty("code").GetString());
        Assert.Equal("request", problem.GetProperty("scope").GetString());

        // Settings, plugins and keys are for the admin, even to read.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("api/v1/settings/fetch")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("api/v1/plugins")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("api/v1/keys")).StatusCode);

        // Past the scope check: there's no such track.
        using var other = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PostAsJsonAsync($"api/v1/requests?apikey={requester.Key}", new CreateRequest(RequestKind.Track, Guid.NewGuid()), NavidexJson.Options))
            .StatusCode);

        await admin.RevokeApiKeyAsync(reader.Info.Id);
        var revoked = await app.GetAsync("api/v1/library");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("invalid-key", (await Problem(revoked)).GetProperty("code").GetString());
        Assert.Equal(["Requester app"], (await admin.ListApiKeysAsync()).Select(k => k.Name));
    }

    [Fact]
    public async Task Bad_key_requests_are_refused()
    {
        var admin = new NavidexClient(_factory.CreateSignedInClient());
        var error = await Assert.ThrowsAsync<NavidexApiException>(() => admin.CreateApiKeyAsync(new CreateApiKey("App", ["everything"])));
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        error = await Assert.ThrowsAsync<NavidexApiException>(() => admin.CreateApiKeyAsync(new CreateApiKey(" ", [Scopes.Read])));
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
    }

    [Fact]
    public async Task The_local_network_is_let_in_only_when_allowed_and_not_through_a_proxy()
    {
        var admin = new NavidexClient(_factory.CreateSignedInClient());

        async Task<HttpStatusCode> FromAsync(string address, bool proxied = false)
        {
            using var http = _factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/library");
            request.Headers.Add(RemoteHeader, address);
            if (proxied)
                request.Headers.Add("X-Forwarded-For", "203.0.113.9");
            return (await http.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await FromAsync("192.168.1.20"));

        await admin.SaveAccessSettingsAsync(new AccessSettings(SkipOnLocalNetwork: true));
        Assert.Equal(HttpStatusCode.OK, await FromAsync("192.168.1.20"));
        Assert.Equal(HttpStatusCode.OK, await FromAsync("10.0.0.4"));
        Assert.Equal(HttpStatusCode.OK, await FromAsync("::1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await FromAsync("192.168.1.20", proxied: true));
        Assert.Equal(HttpStatusCode.Unauthorized, await FromAsync("8.8.8.8"));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.200", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("::ffff:192.168.0.5", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", false)]
    public void Local_addresses(string address, bool local) => Assert.Equal(local, AccessService.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public async Task Webhooks_get_signed_events_they_asked_for()
    {
        var admin = new NavidexClient(_factory.CreateSignedInClient());
        var webhook = await admin.CreateWebhookAsync(new SaveWebhook("Scans", "http://receiver.test/hook", [EventTypes.ScanCompleted]));
        Assert.StartsWith("whsec_", webhook.Secret);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
            db.BackgroundTasks.Add(new BackgroundTask { Kind = LibraryScanTask.TaskKind, Status = BackgroundTaskStatus.Succeeded, FinishedAt = DateTime.UtcNow });
            db.Requests.Add(new Request
            {
                TargetKind = EntityKind.Track, TargetId = Guid.NewGuid(), Title = "Glory Box · Portishead", Requester = "admin", Status = RequestStatus.InLibrary,
            });
            await db.SaveChangesAsync();
        }

        var sent = await _receiver.WaitAsync(1);
        // The request finished too, but this webhook only wants scans.
        await Task.Delay(500);
        Assert.Single(_receiver.Received);
        var (headers, body) = sent[0];
        Assert.Equal(EventTypes.ScanCompleted, headers[WebhookSender.EventHeader]);
        var @event = JsonSerializer.Deserialize<NavidexEvent>(body, NavidexJson.Options)!;
        Assert.Equal(EventTypes.ScanCompleted, @event.Type);
        Assert.Equal(LibraryScanTask.TaskKind, @event.DataAs<ScanEventData>()!.Task.Kind);

        var signature = headers[WebhookSender.SignatureHeader].Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(WebhookSender.Sign(webhook.Secret, long.Parse(signature["t"]), body), signature["v1"]);

        var deliveries = await admin.ListWebhookDeliveriesAsync(webhook.Id);
        Assert.Equal(WebhookDeliveryState.Delivered, Assert.Single(deliveries).State);
    }

    [Fact]
    public async Task A_failed_delivery_is_tried_again_later()
    {
        var admin = new NavidexClient(_factory.CreateSignedInClient());
        _receiver.Status = HttpStatusCode.InternalServerError;
        var webhook = await admin.CreateWebhookAsync(new SaveWebhook("Everything", "http://receiver.test/hook"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
            db.Requests.Add(new Request
            {
                TargetKind = EntityKind.Track, TargetId = Guid.NewGuid(), Title = "Roads · Portishead", Requester = "Some app", Status = RequestStatus.Failed,
            });
            await db.SaveChangesAsync();
        }

        var sent = await _receiver.WaitAsync(1);
        var @event = JsonSerializer.Deserialize<NavidexEvent>(sent[0].Body, NavidexJson.Options)!;
        Assert.Equal(EventTypes.RequestFailed, @event.Type);
        Assert.Equal("Some app", @event.DataAs<RequestEventData>()!.Requester);

        WebhookDeliveryInfo delivery;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            delivery = Assert.Single(await admin.ListWebhookDeliveriesAsync(webhook.Id));
        } while (delivery.Attempts == 0 && DateTime.UtcNow < deadline && await Delay());
        Assert.Equal(WebhookDeliveryState.Pending, delivery.State);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal(500, delivery.ResponseStatus);
        Assert.True(delivery.NextAttemptAt > DateTime.UtcNow.AddSeconds(20));
    }

    [Fact]
    public async Task Testing_a_webhook_sends_a_ping_now()
    {
        var admin = new NavidexClient(_factory.CreateSignedInClient());
        var webhook = await admin.CreateWebhookAsync(new SaveWebhook("Test", "http://receiver.test/hook"));
        var delivery = await admin.TestWebhookAsync(webhook.Id);
        Assert.Equal(WebhookDeliveryState.Delivered, delivery.State);
        Assert.Equal(EventTypes.Ping, Assert.Single(_receiver.Received).Headers[WebhookSender.EventHeader]);

        var invalid = await Assert.ThrowsAsync<NavidexApiException>(() => admin.CreateWebhookAsync(new SaveWebhook("Bad", "not a url", ["nothing"])));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
    }

    [Fact]
    public async Task The_api_reference_is_open_and_says_what_each_operation_needs()
    {
        var http = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/docs")).StatusCode);
        var spec = await http.GetStringAsync("openapi/v1.json");
        Assert.Contains("\"X-Api-Key\"", spec);
        Assert.Contains("Needs the `request` scope.", spec);
        Assert.Contains("Open to anyone: no key needed.", spec);
    }

    private static async Task<bool> Delay()
    {
        await Task.Delay(100);
        return true;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private sealed class RemoteAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, then) =>
            {
                if (http.Request.Headers.TryGetValue(RemoteHeader, out var address))
                {
                    http.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                    http.Request.Headers.Remove(RemoteHeader);
                }
                return then(http);
            });
            next(app);
        };
    }

    /// <summary>Stands in for the app a webhook posts to.</summary>
    private sealed class Receiver : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(Dictionary<string, string> Headers, string Body)> _received = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public IReadOnlyList<(Dictionary<string, string> Headers, string Body)> Received => _received.ToList();

        public async Task<IReadOnlyList<(Dictionary<string, string> Headers, string Body)>> WaitAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (_received.Count < count && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Assert.True(_received.Count >= count, $"Expected {count} deliveries, got {_received.Count}");
            return Received;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            _received.Enqueue((headers, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Status);
        }
    }
}
