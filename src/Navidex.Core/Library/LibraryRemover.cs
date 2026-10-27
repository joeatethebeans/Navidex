using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Contracts.Plugins;
using Navidex.Core.Data;
using Navidex.Core.Import;
using Navidex.Core.Plugins;

namespace Navidex.Core.Library;

/// <summary>
/// Removes an artist, album or file from the library: has the import worker (the only thing that writes the music
/// folder) delete the files and tells the media server, or keeps them and excludes them from matching so Navidex
/// stops counting them.
/// </summary>
public sealed class LibraryRemover(NavidexDbContext db, IImportWorker worker, ConnectorPlugins connectors, ILogger<LibraryRemover> logger)
{
    public async Task<RemovalPreview> PreviewAsync(LibraryItemKind kind, Guid id, CancellationToken ct)
    {
        var files = await FilesAsync(kind, id, ct);
        var folders = files.Select(f => Path.GetDirectoryName(f.Path)?.Replace('\\', '/') ?? "").Distinct().Order(StringComparer.Ordinal).Take(20).ToList();
        var health = await worker.CheckHealthAsync(ct);
        return new RemovalPreview(files.Count, files.Sum(f => f.Size), folders, health.Status == HealthStatus.Healthy,
            health.Status == HealthStatus.Healthy ? null : health.Message);
    }

    public async Task<RemovalResult> RemoveAsync(RemoveFromLibrary body, CancellationToken ct)
    {
        var files = await FilesAsync(body.Kind, body.Id, ct);
        var failed = new List<string>();
        var removed = new List<LibraryFile>();
        if (body.DeleteFiles)
        {
            if (files.Count > 0)
            {
                var result = await worker.DeleteAsync(new DeleteRequest(files.Select(f => f.Path).ToList()), ct);
                var deleted = result.Deleted.ToHashSet(StringComparer.Ordinal);
                removed.AddRange(files.Where(f => deleted.Contains(f.Path)));
                failed.AddRange(result.Failed.Select(f => $"{f.Path}: {f.Error}"));
            }
            db.LibraryFiles.RemoveRange(removed);
            await db.SaveChangesAsync(ct);
            await TellMediaServerAsync(removed.Select(f => f.Path).ToList(), ct);
            logger.LogInformation("Deleted {Count} files from the library{Failed}", removed.Count,
                failed.Count == 0 ? "" : $"; couldn't delete {failed.Count}: {string.Join("; ", failed.Take(5))}");
        }
        else
        {
            var now = DateTime.UtcNow;
            foreach (var file in files)
            {
                file.ExcludedAt ??= now;
                file.TrackId = file.EditionId = file.SuggestedTrackId = file.SuggestedEditionId = null;
                file.ArtistId = null;
                file.MatchMethod = MatchMethod.Unmatched;
                file.MatchConfidence = null;
                file.ReviewReason = null;
                file.MatchedAt ??= now;
                removed.Add(file);
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Removed {Count} files from the library, keeping them in the music folder as excluded files", removed.Count);
        }
        await LibraryMatcher.UpdateLibraryArtistsAsync(db, ct);
        return new RemovalResult(removed.Count, failed);
    }

    private async Task<List<LibraryFile>> FilesAsync(LibraryItemKind kind, Guid id, CancellationToken ct) => kind switch
    {
        LibraryItemKind.File => await db.LibraryFiles.Where(f => f.Id == id).ToListAsync(ct),
        LibraryItemKind.Album => await db.LibraryFiles.Where(f => f.EditionId != null && f.Edition!.AlbumId == id).ToListAsync(ct),
        _ => await db.LibraryFiles.Where(f => f.ArtistId == id || f.EditionId != null && f.Edition!.Album.ArtistId == id).ToListAsync(ct),
    };

    private async Task TellMediaServerAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0 || await connectors.GetActiveAsync(ct) is not { } active)
            return;
        try
        {
            await active.Connector.TriggerScanAsync(paths, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Couldn't ask {Server} to pick up the deleted files: {Reason}", active.Instance.Name, e.Message);
        }
    }
}
