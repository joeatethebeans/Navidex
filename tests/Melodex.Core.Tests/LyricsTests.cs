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
    public void Edited_lines_are_written_back_as_lrc_or_plain_text()
    {
        Assert.Equal("[00:04.50] Second\n[01:02.05] Third\n",
            LyricsText.Write([new LyricLine(62.05, "Third"), new LyricLine(4.5, "Second ")]));
        Assert.Equal("Verse\n\nChorus\n", LyricsText.Write([new LyricLine(null, "Verse"), new LyricLine(null, ""), new LyricLine(null, "Chorus")]));
        Assert.Equal([(4.5, "Second"), (62.05, "Third")], LyricsText.Lines(LyricsText.Write([new LyricLine(62.05, "Third"), new LyricLine(4.5, "Second")]))
            .Select(l => (Math.Round(l.Seconds!.Value, 2), l.Text)));

        Assert.Equal("1:02.50", LyricsText.Time(62.5));
        Assert.Equal("01:02.50", LyricsText.Time(62.5, lrc: true));
        foreach (var (typed, seconds) in new[] { ("1:02.5", 62.5), ("01:02", 62.0), ("[00:03.25]", 3.25), ("7.5", 7.5) })
        {
            Assert.True(LyricsText.TryParseTime(typed, out var read), typed);
            Assert.Equal(seconds, read, 3);
        }
        Assert.False(LyricsText.TryParseTime("1:75", out _));
        Assert.False(LyricsText.TryParseTime("soon", out _));
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

    [TestLibraryFact]
    public async Task Lyrics_can_be_chosen_edited_deleted_and_excluded_from_a_songs_page()
    {
        var album = (await Api.GetLibraryAlbumsAsync()).First(a => a.Album.Title == "Homework").Album;
        var song = (await Api.GetAlbumAsync(album.Id))!.Tracks.Where(t => t.Status == TrackStatus.Owned).ToList()[2];
        fixture.Lyrics[song.Title] = "[00:01.00] Found line";
        var file = (await Api.GetTrackAsync(song.TrackId))!.Files.Single();
        var lrc = Path.Combine(fixture.Music, Path.ChangeExtension(file.Path, ".lrc"));
        try
        {
            // Searched for, by the song's own title and artist or by what's typed, and chosen.
            var choice = Assert.Single(await Api.SearchTrackLyricsAsync(song.TrackId, null));
            Assert.Equal(("LRCLIB", song.Title, LyricsKind.Synced), (choice.Source, choice.Title, choice.Kind));
            Assert.Single(await Api.SearchTrackLyricsAsync(song.TrackId, $"{song.Title} daft punk"));
            Assert.Empty(await Api.SearchTrackLyricsAsync(song.TrackId, "nothing like it"));
            var chosen = await Api.SaveTrackLyricsAsync(song.TrackId, choice.Text);
            Assert.Equal((LyricsKind.Synced, "sidecar", "[00:01.00] Found line"), (chosen.Kind, chosen.Source, chosen.Text!.Trim()));

            // Edited, they're written as they are now.
            var edited = await Api.SaveTrackLyricsAsync(song.TrackId, "[00:01.00] Found line\n[00:02.50] Added line\n");
            Assert.Equal([(1.0, "Found line"), (2.5, "Added line")], edited.Lines.Select(l => (l.Seconds!.Value, l.Text)));
            Assert.Contains("Added line", await File.ReadAllTextAsync(lrc));

            // Excluded from lyrics matching, deleted, and nothing looks for them again until it's included.
            Assert.True((await Api.ExcludeTrackLyricsAsync(song.TrackId, true)).Excluded);
            var sent = fixture.Importer.Formats.Count;
            var deleted = await Api.SaveTrackLyricsAsync(song.TrackId, null);
            Assert.Equal((LyricsKind.None, true), (deleted.Kind, deleted.Excluded));
            var request = Assert.Single(fixture.Importer.Formats.Skip(sent)).Files.Single();
            Assert.Equal(("", ""), (request.Lrc, request.Tags["lyrics"]));
            Assert.False(File.Exists(lrc));
            Assert.Equal(LyricsKind.None, (await Api.GetTrackAsync(song.TrackId))!.Files.Single().Lyrics);
            Assert.False(await WantedAsync(song.TrackId));
            Assert.False((await Api.ExcludeTrackLyricsAsync(song.TrackId, false)).Excluded);
            Assert.True(await WantedAsync(song.TrackId));
        }
        finally
        {
            fixture.Lyrics.TryRemove(song.Title, out _);
            await Api.ExcludeTrackLyricsAsync(song.TrackId, false);
            File.Delete(lrc);
        }
    }

    private async Task<bool> WantedAsync(Guid trackId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LyricsFinder>().Wanting().AnyAsync(f => f.TrackId == trackId);
    }
}
