using Navidex.Client;
using Navidex.Core.Tests.Support;

namespace Navidex.Core.Tests;

/// <summary>What a scan shows as it goes, and files excluded from matching.</summary>
[Collection(RequestCollection.Name)]
public sealed class ScanAndReviewTests(RequestFixture fixture)
{
    private NavidexClient Api => fixture.Api;

    [TestLibraryFact]
    public async Task A_scan_shows_each_file_and_the_song_it_matched()
    {
        // A scan only reads files that changed, so change one that matches and one that doesn't.
        var dummy = Directory.GetFiles(Path.Combine(fixture.Music, "Portishead"), "*.*", SearchOption.AllDirectories).First(f => f.Contains("Dummy"));
        var review = (await Api.GetReviewAsync()).Last();
        foreach (var file in new[] { dummy, Path.Combine(fixture.Music, review.Path) })
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        await ScanAsync();
        var page = await Api.GetScanFilesAsync();
        var files = page.Files;

        var matched = files.First(f => f.Path.StartsWith("Portishead/Dummy", StringComparison.Ordinal) && f.State == ScanFileState.Matched);
        Assert.Equal("Dummy", matched.AlbumTitle);
        Assert.Equal("Portishead", matched.ArtistCredit);
        Assert.NotNull(matched.AlbumId);
        Assert.NotNull(matched.CoverUrl);
        Assert.Contains(files, f => f.Path == review.Path && f.State == ScanFileState.Review && f.Detail is not null);
        Assert.DoesNotContain(files, f => f.State is ScanFileState.Reading or ScanFileState.Matching or ScanFileState.Queued);
        // Each file keeps the place it was first read in, which is path order.
        Assert.Equal(files.Select(f => f.Order).Order(), files.Select(f => f.Order));
        Assert.Equal(files.Select(f => f.Path).Order(StringComparer.Ordinal), files.Select(f => f.Path));
        // Nothing has changed since, so asking for later changes gets none.
        var later = await Api.GetScanFilesAsync(page.Latest);
        Assert.Equal(page.Scan, later.Scan);
        Assert.Empty(later.Files);
    }

    [TestLibraryFact]
    public async Task An_excluded_file_leaves_review_and_isnt_matched_until_its_included()
    {
        var item = (await Api.GetReviewAsync()).First(i => i.Path.StartsWith("Unknown/", StringComparison.Ordinal));
        var before = (await Api.GetLibraryStatusAsync())!;

        await Api.SetFileExcludedAsync(item.FileId, true);
        Assert.DoesNotContain(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
        Assert.Contains(await Api.GetReviewAsync(excluded: true), i => i.FileId == item.FileId && i.Excluded);
        var excluded = (await Api.GetLibraryStatusAsync())!;
        Assert.Equal(before.Review - 1, excluded.Review);
        Assert.Equal(before.Excluded + 1, excluded.Excluded);

        // A scan leaves it alone.
        await ScanAsync();
        Assert.DoesNotContain(await Api.GetReviewAsync(), i => i.FileId == item.FileId);

        // Included again, the next scan matches it, and it's back for review.
        await Api.SetFileExcludedAsync(item.FileId, false);
        Assert.Equal(before.Excluded, (await Api.GetLibraryStatusAsync())!.Excluded);
        await ScanAsync();
        Assert.Contains(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
    }

    [TestLibraryFact]
    public async Task Removing_a_file_deletes_it_and_its_emptied_folder()
    {
        var path = await CopyAsync("Removal Test/Copy", withArtwork: true);
        var file = await FileAsync(path);

        var preview = await Api.GetRemovalAsync(LibraryItemKind.File, file);
        Assert.Equal(1, preview.Files);
        Assert.True(preview.Bytes > 0);
        Assert.Equal(["Removal Test/Copy"], preview.Folders);
        Assert.True(preview.CanDelete);

        var result = await Api.RemoveFromLibraryAsync(new RemoveFromLibrary(LibraryItemKind.File, file, DeleteFiles: true));
        Assert.Equal(1, result.Removed);
        Assert.Empty(result.Failed);
        // The folder held only artwork after that, so it went too, and so did its parent.
        Assert.False(Directory.Exists(Path.Combine(fixture.Music, "Removal Test")));
        Assert.Equal(0, (await Api.GetRemovalAsync(LibraryItemKind.File, file)).Files);
    }

    [TestLibraryFact]
    public async Task Removing_a_file_but_keeping_it_excludes_it()
    {
        var path = await CopyAsync("Removal Keep/Copy", withArtwork: false);
        var file = await FileAsync(path);

        var result = await Api.RemoveFromLibraryAsync(new RemoveFromLibrary(LibraryItemKind.File, file, DeleteFiles: false));
        Assert.Equal(1, result.Removed);
        Assert.True(File.Exists(Path.Combine(fixture.Music, path)));
        Assert.Contains(await Api.GetReviewAsync(excluded: true), i => i.FileId == file);

        // Tidy up: without the file, the next scan forgets it.
        Directory.Delete(Path.Combine(fixture.Music, "Removal Keep"), recursive: true);
        await ScanAsync();
    }

    [TestLibraryFact]
    public async Task Removing_an_album_or_artist_covers_all_its_files()
    {
        var album = await fixture.AlbumAsync("Portishead", "Dummy");
        var albumFiles = Directory.GetFiles(fixture.Music, "*.*", SearchOption.AllDirectories).Count(f => f.Contains("Dummy") && !f.EndsWith(".jpg"));
        Assert.Equal(albumFiles, (await Api.GetRemovalAsync(LibraryItemKind.Album, album.Id)).Files);
        Assert.True((await Api.GetRemovalAsync(LibraryItemKind.Artist, album.ArtistId)).Files > albumFiles);
    }

    // Copies one of Portishead's Dummy files into a new folder and scans it in; returns its path.
    private async Task<string> CopyAsync(string folder, bool withArtwork)
    {
        var source = Directory.GetFiles(Path.Combine(fixture.Music, "Portishead"), "*.*", SearchOption.AllDirectories).First(f => f.Contains("Dummy") && !f.EndsWith(".jpg"));
        var target = Path.Combine(fixture.Music, folder);
        Directory.CreateDirectory(target);
        File.Copy(source, Path.Combine(target, Path.GetFileName(source)));
        if (withArtwork)
            await File.WriteAllBytesAsync(Path.Combine(target, "cover.jpg"), [1, 2, 3]);
        await ScanAsync();
        return $"{folder}/{Path.GetFileName(source)}";
    }

    private async Task<Guid> FileAsync(string path)
    {
        for (var i = 0; i < 50; i++)
        {
            if ((await Api.GetLibraryTracksAsync()).FirstOrDefault(t => t.Path == path) is { } track)
                return track.FileId;
            if ((await Api.GetReviewAsync()).FirstOrDefault(r => r.Path == path) is { } review)
                return review.FileId;
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"{path} isn't in the library.");
    }

    private async Task ScanAsync()
    {
        var task = await Api.ScanLibraryAsync();
        while (task.Active)
        {
            await Task.Delay(100);
            task = (await Api.GetTaskAsync(task.Id))!;
        }
        Assert.Equal(TaskState.Succeeded, task.State);
    }
}
