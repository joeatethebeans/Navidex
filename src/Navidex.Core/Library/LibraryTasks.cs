using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Navidex.Core.Catalogue;
using Navidex.Core.Data;
using Navidex.Core.Tasks;

namespace Navidex.Core.Library;

/// <summary>
/// Scans the music folder, matches new and changed files, then fills in the catalogue for every library artist:
/// their discography and the editions of each album the missing view might show, which reveals bootleg-only albums
/// and albums covered by tracks owned elsewhere.
/// </summary>
public sealed class LibraryScanTask(
    LibraryScanner scanner,
    LibraryMatcher matcher,
    CatalogueRefresher refresher,
    NavidexDbContext db) : ITaskHandler
{
    public const string TaskKind = "library-scan";

    public string Kind => TaskKind;

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Reading files");
        var scan = await scanner.ScanAsync(progress, ct);
        progress.Report($"Found {scan.Files} files ({scan.Added} new, {scan.Changed} changed, {scan.Removed} removed); matching");
        var match = await matcher.MatchPendingAsync(progress, ct);

        var artists = await db.Artists.Where(a => a.InLibrary).Select(a => a.Id).ToListAsync(ct);
        var failures = await refresher.RefreshArtistsAsync(artists, progress, ct);
        var notes = new List<string>();
        if (scan.SkippedFolders > 0)
            notes.Add($"{scan.SkippedFolders} folders couldn't be read");
        if (scan.Unreadable > 0)
            notes.Add($"{scan.Unreadable} files couldn't be read");
        if (failures > 0)
            notes.Add($"{failures} catalogue updates failed and will be retried next scan");
        progress.Report($"{scan.Files} files: {match.Matched} newly matched, {match.Review} to review" + string.Concat(notes.Select(n => "; " + n)));
    }
}

/// <summary>Fills in one artist's catalogue after their page is opened, so non-library artists get full statuses.</summary>
public sealed class ArtistRefreshTask(CatalogueRefresher refresher) : ITaskHandler
{
    public const string TaskKind = "artist-refresh";

    public string Kind => TaskKind;

    public static string Payload(Guid artistId) => JsonSerializer.Serialize(artistId);

    public Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct) =>
        refresher.RefreshArtistsAsync([JsonSerializer.Deserialize<Guid>(task.PayloadJson!)], progress, ct);
}

/// <summary>
/// Fills in the catalogue for library artists: discographies, then editions of the albums the missing view shows (editions
/// say whether an album has an official release). A provider failure skips that artist or album and leaves it stale, so the
/// next scan tries it again; it doesn't fail the scan.
/// </summary>
public sealed class CatalogueRefresher(NavidexDbContext db, CatalogueService catalogue, SettingsService settings, ILogger<CatalogueRefresher> logger)
{
    public async Task<int> RefreshArtistsAsync(IReadOnlyList<Guid> artistIds, IProgress<string> progress, CancellationToken ct)
    {
        var library = await settings.GetLibraryAsync(ct);
        var failures = 0;
        for (var i = 0; i < artistIds.Count; i++)
        {
            var artist = await db.Artists.FindAsync([artistIds[i]], ct);
            if (artist is null)
                continue;
            progress.Report($"Updating catalogue: {artist.Name} ({i + 1} of {artistIds.Count})");
            if (!await TryAsync(() => catalogue.RefreshDiscographyAsync(artist, false, ct), $"the discography of {artist.Name}", ct))
            {
                failures++;
                continue;
            }

            var stale = DateTime.UtcNow - CatalogueService.EditionsTtl;
            var albums = await db.Albums.Where(a => a.ArtistId == artist.Id && (a.EditionsRefreshedAt == null || a.EditionsRefreshedAt < stale)).ToListAsync(ct);
            var wanted = albums.Where(library.Shows).Select(a => a.Id).ToList();
            for (var j = 0; j < wanted.Count; j++)
            {
                progress.Report($"Updating catalogue: {artist.Name}, album {j + 1} of {wanted.Count} ({i + 1} of {artistIds.Count} artists)");
                // Loaded one at a time with a clear tracker: a big discography tracks thousands of entities otherwise,
                // and every query and save would scan them all.
                db.ChangeTracker.Clear();
                if (await db.Albums.FindAsync([wanted[j]], ct) is not { } album)
                    continue;
                if (!await TryAsync(() => catalogue.RefreshEditionsAsync(album, false, ct), $"the editions of {album.Title}", ct))
                    failures++;
            }
            db.ChangeTracker.Clear();
        }
        return failures;
    }

    private async Task<bool> TryAsync(Func<Task> refresh, string what, CancellationToken ct)
    {
        try
        {
            await refresh();
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Couldn't update {What}; it will be tried again on the next scan", what);
            // Drop whatever the failed refresh left half-done, so the next save doesn't write it.
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
