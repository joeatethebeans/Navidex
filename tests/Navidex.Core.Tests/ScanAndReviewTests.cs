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
