using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Navidex.Core.Data;

namespace Navidex.Core.Library;

/// <summary>The tags matching uses, as read from a file.</summary>
public sealed record FileTags
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? Track { get; init; }
    public int? Disc { get; init; }
    public string? Date { get; init; }
    public string? Isrc { get; init; }
    public string? MusicBrainzRecordingId { get; init; }
    public string? MusicBrainzReleaseId { get; init; }
    public string? MusicBrainzReleaseGroupId { get; init; }
    public string? MusicBrainzArtistId { get; init; }
    public string? MusicBrainzAlbumArtistId { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static FileTags Parse(string json) => JsonSerializer.Deserialize<FileTags>(json, Json) ?? new FileTags();

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Walks the music folder and records every audio file and its tags. It never writes to the library.</summary>
public sealed class LibraryScanner(NavidexDbContext db, IOptions<NavidexOptions> options, ILogger<LibraryScanner> logger)
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".oga", ".opus", ".wma", ".wav", ".aif", ".aiff", ".ape", ".wv", ".mpc", ".dsf",
    };

    public sealed record Result(int Files, int Added, int Changed, int Removed, int Unreadable);

    public async Task<Result> ScanAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(options.Value.LibraryPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The music folder {root} doesn't exist.");

        var known = await db.LibraryFiles.ToDictionaryAsync(f => f.Path, ct);
        var seen = new HashSet<string>();
        int added = 0, changed = 0, unreadable = 0;

        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)));
        foreach (var fullPath in files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            seen.Add(path);
            var info = new FileInfo(fullPath);
            var modified = info.LastWriteTimeUtc;

            if (known.TryGetValue(path, out var existing) && existing.Size == info.Length && existing.ModifiedAt == modified)
                continue;

            var read = Read(fullPath);
            if (read is null)
            {
                unreadable++;
                continue;
            }

            if (existing is null)
            {
                existing = new LibraryFile { Path = path };
                db.LibraryFiles.Add(existing);
                added++;
            }
            else
            {
                changed++;
            }
            existing.Size = info.Length;
            existing.ModifiedAt = modified;
            existing.Format = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
            existing.Duration = read.Value.Duration;
            existing.TagsJson = read.Value.Tags.ToJson();
            existing.ScannedAt = DateTime.UtcNow;
            // Changed files are matched again from scratch.
            existing.MatchedAt = null;
            existing.TrackId = existing.EditionId = existing.SuggestedTrackId = existing.SuggestedEditionId = null;
            existing.MatchMethod = MatchMethod.Unmatched;

            if ((added + changed) % 200 == 0)
            {
                progress?.Report($"Read {added + changed} new or changed files");
                await db.SaveChangesAsync(ct);
            }
        }

        var removed = known.Values.Where(f => !seen.Contains(f.Path)).ToList();
        db.LibraryFiles.RemoveRange(removed);
        await db.SaveChangesAsync(ct);

        var result = new Result(seen.Count, added, changed, removed.Count, unreadable);
        logger.LogInformation("Scanned {Root}: {Result}", root, result);
        return result;
    }

    private (FileTags Tags, TimeSpan? Duration)? Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            return (new FileTags
            {
                Title = Blank(tag.Title),
                Artist = Blank(tag.JoinedPerformers),
                AlbumArtist = Blank(tag.JoinedAlbumArtists),
                Album = Blank(tag.Album),
                Track = tag.Track == 0 ? null : (int)tag.Track,
                Disc = tag.Disc == 0 ? null : (int)tag.Disc,
                Date = tag.Year == 0 ? null : tag.Year.ToString(),
                Isrc = Blank(tag.ISRC),
                MusicBrainzRecordingId = Blank(tag.MusicBrainzTrackId),
                MusicBrainzReleaseId = Blank(tag.MusicBrainzReleaseId),
                MusicBrainzReleaseGroupId = Blank(tag.MusicBrainzReleaseGroupId),
                MusicBrainzArtistId = Blank(tag.MusicBrainzArtistId?.Split('/', ';')[0]),
                MusicBrainzAlbumArtistId = Blank(tag.MusicBrainzReleaseArtistId?.Split('/', ';')[0]),
            }, file.Properties?.Duration is { Ticks: > 0 } d ? d : null);
        }
        catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException)
        {
            logger.LogWarning("Can't read {Path}: {Message}", path, e.Message);
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
