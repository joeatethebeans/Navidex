using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Tests.Support;
using Melodex.Plugins.Lrclib;

namespace Melodex.Core.Tests;

/// <summary>Reading lyrics files and choosing among LRCLIB's answers.</summary>
public sealed class LyricsTests
{
    [Fact]
    public void Synced_lyrics_come_in_the_order_theyre_sung()
    {
        var lines = LyricsFiles.Parse("""
            [ar:Portishead]
            [offset:+500]
            [00:12.50]Oh, can't anybody see
            [00:05.00][00:30.00]We've got a war to fight
            [01:02]<01:02.00>Never <01:03.10>found
            """);
        Assert.Equal([(4.5, "We've got a war to fight"), (12.0, "Oh, can't anybody see"), (29.5, "We've got a war to fight"), (61.5, "Never found")],
            lines.Select(l => (Math.Round(l.Seconds!.Value, 2), l.Text)));
        Assert.Equal(LyricsKind.Synced, LyricsFiles.KindOfText("[00:01.00] words"));
        Assert.Equal(LyricsKind.Plain, LyricsFiles.KindOfText("Words\n\nMore words"));
        Assert.Equal(LyricsKind.None, LyricsFiles.KindOfText("  \n"));

        // Plain lyrics keep their blank lines, which separate verses.
        Assert.Equal(["Verse", "", "Chorus"], LyricsFiles.Parse("\nVerse\n\nChorus").Select(l => l.Text));
        Assert.All(LyricsFiles.Parse("Verse"), l => Assert.Null(l.Seconds));
    }

    [Fact]
    public void Lrclib_takes_synced_lyrics_of_the_same_length()
    {
        var results = JsonDocument.Parse("""
            [
              { "duration": 300, "syncedLyrics": "[00:01.00] Too long", "plainLyrics": "Too long" },
              { "duration": 241, "syncedLyrics": null, "plainLyrics": "Plain" },
              { "duration": 242, "syncedLyrics": "[00:01.00] Synced", "plainLyrics": "Synced" },
              { "duration": 240, "instrumental": true }
            ]
            """).RootElement;
        Assert.Equal("[00:01.00] Synced", Lrclib.Pick(results, TimeSpan.FromSeconds(240))!.Synced);
        Assert.Null(Lrclib.Pick(results, TimeSpan.FromSeconds(100)));
        Assert.True(Lrclib.Pick(JsonDocument.Parse("""[{ "duration": 240, "instrumental": true }]""").RootElement, TimeSpan.FromSeconds(240))!.Instrumental);
        Assert.Null(Lrclib.Read(JsonDocument.Parse("""{ "syncedLyrics": " ", "plainLyrics": null }""").RootElement));
    }
}

/// <summary>Finding lyrics for the library through LRCLIB, faked, and writing them with the fake beets.</summary>
[Collection(RequestCollection.Name)]
public sealed class LibraryLyricsTests(RequestFixture fixture)
{
    private MelodexClient Api => fixture.Api;

    private async Task<TaskInfo> WaitForLyricsAsync()
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            var tasks = await Api.ListTasksAsync();
            if (!tasks.Any(t => t.Active))
                return tasks.First(t => t.Title == LyricsTask.Title);
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    private async Task SaveAsync(LibrarySettings settings)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SettingsService>().SaveLibraryAsync(settings, default);
    }

    [TestLibraryFact]
    public async Task A_scan_finds_lyrics_for_songs_without_them_and_writes_them_beside_each()
    {
        var album = (await Api.GetLibraryAlbumsAsync()).First(a => a.Album.Title == "Homework").Album;
        var tracks = (await Api.GetAlbumAsync(album.Id))!.Tracks.Where(t => t.Status == TrackStatus.Owned).ToList();
        var (sung, unsung) = (tracks[0], tracks[1]);
        fixture.Lyrics[sung.Title] = "[00:02.00] First line\n[00:04.50] Second line";
        try
        {
            await SaveAsync(new LibrarySettings { FetchLyrics = true });
            await Api.ScanLibraryAsync();
            var task = await WaitForLyricsAsync();
            Assert.Equal(TaskState.Succeeded, task.State);
            Assert.StartsWith("Found lyrics for", task.Progress);

            var file = (await Api.GetTrackAsync(sung.TrackId))!.Files.Single();
            Assert.Equal(LyricsKind.Synced, file.Lyrics);
            Assert.True(File.Exists(Path.Combine(fixture.Music, Path.ChangeExtension(file.Path, ".lrc"))));
            var lyrics = await Api.GetTrackLyricsAsync(sung.TrackId);
            Assert.Equal((LyricsKind.Synced, "sidecar"), (lyrics.Kind, lyrics.Source));
            Assert.Equal([(2.0, "First line"), (4.5, "Second line")], lyrics.Lines.Select(l => (l.Seconds!.Value, l.Text)));

            // None anywhere for the other song: remembered, so the next scan doesn't ask again.
            Assert.Equal(LyricsKind.None, (await Api.GetTrackAsync(unsung.TrackId))!.Files.Single().Lyrics);
            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
                Assert.NotNull(await db.LibraryFiles.Where(f => f.TrackId == unsung.TrackId).Select(f => f.LyricsCheckedAt).SingleAsync());
                Assert.False(await scope.ServiceProvider.GetRequiredService<LyricsFinder>().Wanting().AnyAsync(f => f.TrackId == unsung.TrackId));
            }

            // Looked for again from the song's page, they go into the file's tags when the settings say so.
            await SaveAsync(new LibrarySettings { FetchLyrics = false, LyricsStorage = LyricsStorage.Embedded });
            fixture.Lyrics[sung.Title] = "[00:03.00] New first line";
            var sent = fixture.Importer.Formats.Count;
            await Api.FindTrackLyricsAsync(sung.TrackId);
            var request = Assert.Single(fixture.Importer.Formats.Skip(sent)).Files.Single();
            Assert.Equal("[00:03.00] New first line", request.Tags["lyrics"]);
            Assert.Null(request.Lrc);
        }
        finally
        {
            fixture.Lyrics.TryRemove(sung.Title, out _);
            await SaveAsync(new LibrarySettings { FetchLyrics = false });
        }
    }
}
