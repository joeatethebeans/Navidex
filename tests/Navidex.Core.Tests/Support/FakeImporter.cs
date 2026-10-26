using System.Collections.Concurrent;
using Navidex.Contracts.Plugins;
using Navidex.Core.Import;

namespace Navidex.Core.Tests.Support;

/// <summary>
/// Stands in for the beets worker: moves staged files into the library under the destination folder, or
/// <c>Artist/Album</c>, named <c>disc-position title</c>. Records every request so tests can check what beets was told.
/// </summary>
public sealed class FakeImporter(string stagingRoot, string libraryRoot) : IImportWorker
{
    public ConcurrentQueue<ImportRequest> Requests { get; } = new();

    public Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        var folder = request.DestinationDirectory ?? $"{Safe(request.Release.Artist)}/{Safe(request.Release.Title)}";
        var files = new List<ImportedFile>();
        foreach (var file in request.Files)
        {
            var path = $"{folder}/{file.Disc}-{file.Position:00} {Safe(file.Title)}{Path.GetExtension(file.Path)}";
            var target = Path.Combine(libraryRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(Path.Combine(stagingRoot, request.StagingDirectory, file.Path), target, overwrite: true);
            files.Add(new ImportedFile(file.Path, path, null, "wav", new Dictionary<string, string>
            {
                ["mb_trackid"] = file.RecordingId,
                ["mb_albumid"] = request.Release.MusicBrainzId,
                ["title"] = file.Title,
            }));
        }
        return Task.FromResult(new ImportResult(files));
    }

    public Task<HealthReport> CheckHealthAsync(CancellationToken ct) => Task.FromResult(HealthReport.Healthy());

    private static string Safe(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars()));
}
