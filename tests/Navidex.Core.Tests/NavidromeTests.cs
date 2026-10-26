using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Navidex.Contracts.Catalogue;
using Navidex.Contracts.Plugins;
using Navidex.Contracts.Plugins.Artwork;
using Navidex.Contracts.Plugins.Connectors;
using Navidex.Plugins.Navidrome;

namespace Navidex.Core.Tests;

public sealed class NavidromeTests
{
    private static readonly string Songs = """
        [
          { "id": "a", "title": "Glory Box", "artist": "Portishead", "album": "Dummy", "duration": 306, "path": "Portishead/Dummy/01-11 - Glory Box.flac",
            "musicBrainzId": "rec-glory", "isrc": ["GBAQT9400066"] },
          { "id": "b", "title": "Glory Box", "artist": "Portishead", "album": "Roseland NYC Live", "duration": 375, "musicBrainzId": "rec-live" },
          { "id": "c", "title": "Glory Box", "artist": "Portishead", "album": "Dummy", "duration": 305 }
        ]
        """;

    [Fact]
    public async Task A_track_is_found_by_its_musicbrainz_recording_first()
    {
        var (connector, _) = Create();
        Assert.Equal("b", await connector.ResolveTrackAsync(new TrackLookup(Title: "Glory Box", MusicBrainzRecordingId: "rec-live"), default));
        Assert.Equal("a", await connector.ResolveTrackAsync(new TrackLookup(Title: "Glory Box", Isrc: "GB-AQT-94-00066"), default));
    }

    [Fact]
    public async Task Without_ids_the_tags_and_length_must_all_agree()
    {
        var (connector, _) = Create();
        Assert.Equal("c", await connector.ResolveTrackAsync(new TrackLookup(Title: "glory box", Artist: "Portishead", Album: "Dummy",
            Duration: TimeSpan.FromSeconds(304), MusicBrainzRecordingId: "rec-unknown"), default));
        Assert.Null(await connector.ResolveTrackAsync(new TrackLookup(Title: "Glory Box", Artist: "Portishead", Album: "Dummy",
            Duration: TimeSpan.FromSeconds(250)), default));
    }

    [Fact]
    public async Task Calls_use_token_auth_and_never_send_the_password()
    {
        var (connector, server) = Create();
        await connector.TriggerScanAsync(["Portishead/Dummy/11 Glory Box.flac"], default);
        var query = HttpUtility.ParseQueryString(server.Requests.Single().Query);
        Assert.Equal("/rest/startScan", server.Requests.Single().AbsolutePath);
        Assert.Equal("admin", query["u"]);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("secret" + query["s"]))), query["t"]);
        Assert.DoesNotContain("secret", server.Requests.Single().ToString());
    }

    [Fact]
    public async Task Scan_status_and_health_are_read_from_the_server()
    {
        var (connector, server) = Create();
        var status = await connector.GetScanStatusAsync(default);
        Assert.True(status.Scanning);
        Assert.Equal(1234, status.TrackCount);
        var health = await connector.CheckHealthAsync(default);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Contains("navidrome 0.64.2", health.Message);

        server.WrongPassword = true;
        health = await connector.CheckHealthAsync(default);
        Assert.Equal(HealthStatus.Unhealthy, health.Status);
        Assert.Equal("Wrong username or password.", health.Message);
    }

    [Fact]
    public async Task The_whole_library_is_read_page_by_page()
    {
        var (connector, _) = Create();
        var tracks = new List<ServerTrack>();
        await foreach (var track in connector.GetTracksAsync(default))
            tracks.Add(track);
        Assert.Equal(["a", "b", "c"], tracks.Select(t => t.Id));
        Assert.Equal("rec-glory", tracks[0].MusicBrainzRecordingId);
    }

    [Fact]
    public void Cover_art_is_taken_from_the_same_release_or_else_an_album_of_the_same_name()
    {
        var albums = JsonDocument.Parse("""
            [
              { "id": "1", "name": "Dummy", "artist": "Portishead", "musicBrainzId": "rel-japan", "coverArt": "al-1" },
              { "id": "2", "name": "Dummy", "artist": "Portishead", "musicBrainzId": "rel-uk", "coverArt": "al-2" },
              { "id": "3", "name": "Dummy", "artist": "Portishead", "coverArt": "al-3" },
              { "id": "4", "name": "Dummy", "artist": "Someone Else", "coverArt": "al-4" }
            ]
            """).RootElement.EnumerateArray().ToList();
        ArtworkQuery Query(ArtworkSubject subject, params string[] ids) =>
            new(subject, "dummy", "PORTISHEAD", [.. ids.Select(i => new ProviderId(Providers.MusicBrainz, i))], [], []);

        Assert.Equal("2", NavidromeArtwork.Pick(albums, Query(ArtworkSubject.Edition, "rel-uk"))?.GetProperty("id").GetString());
        // Another release isn't this edition however alike the names, but an untagged album of the same name may be.
        Assert.Equal("3", NavidromeArtwork.Pick(albums, Query(ArtworkSubject.Edition, "rel-us"))?.GetProperty("id").GetString());
        Assert.Equal("1", NavidromeArtwork.Pick(albums, Query(ArtworkSubject.Album, "rg-dummy"))?.GetProperty("id").GetString());
        Assert.Null(NavidromeArtwork.Pick(albums[3..], Query(ArtworkSubject.Album)));
    }

    private (NavidromeConnector, FakeSubsonic) Create()
    {
        var server = new FakeSubsonic(Songs);
        var settings = JsonDocument.Parse("""{ "url": "http://navidrome:4533/", "username": "admin", "password": "secret" }""").RootElement;
        var connector = new NavidromeConnector(new PluginContext
        {
            Settings = settings,
            HttpClient = new HttpClient(server),
            LoggerFactory = NullLoggerFactory.Instance,
            DataDirectory = Path.GetTempPath(),
        });
        return (connector, server);
    }

    private sealed class FakeSubsonic(string songs) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public bool WrongPassword { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var body = WrongPassword
                ? """ "status": "failed", "error": { "code": 40, "message": "Wrong username or password" } """
                : """ "status": "ok", """ + (request.RequestUri.AbsolutePath switch
                {
                    "/rest/getScanStatus" => """ "scanStatus": { "scanning": true, "count": 1234, "lastScan": "2026-10-25T10:00:00Z" } """,
                    "/rest/search3" => query["songOffset"] == "0" ? $$""" "searchResult3": { "song": {{songs}} } """ : """ "searchResult3": {} """,
                    _ => """ "x": 0 """,
                });
            var json = $$"""{ "subsonic-response": { "version": "1.16.1", "type": "navidrome", "serverVersion": "0.64.2", "openSubsonic": true, {{body}} } }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
