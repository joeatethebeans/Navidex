using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Core.Artwork;
using Melodex.Core.Data;
using Melodex.Core.Import;
using Melodex.Core.Plugins;

namespace Melodex.Core.Library;

/// <summary>Why a cover can't be chosen as asked, by the field that's wrong.</summary>
public sealed class CoverRefusedException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// Choosing an album's or song's cover: from the covers of every release it's on, the files' own, or a picture of
/// your own. The choice is written into its files straight away (and an album's folders get it as their folder image),
/// and Melodex shows it from then on in place of what the artwork plugins found.
/// </summary>
public sealed class CoverChooser(
    MelodexDbContext db,
    LibraryView view,
    IImportWorker worker,
    ArtworkCache artwork,
    ConnectorPlugins connectors,
    IOptions<MelodexOptions> options,
    ILogger<CoverChooser> logger)
{
    /// <summary>The biggest picture that can be uploaded.</summary>
    public const int MaxUpload = 10 * 1024 * 1024;

    public async Task<CoverChoices?> ChoicesAsync(CoverSubject subject, Guid id, CancellationToken ct)
    {
        string title;
        string? current;
        List<Edition> editions;
        if (subject == CoverSubject.Album)
        {
            if (await view.GetAlbumAsync(id, null, ct) is not { } album)
                return null;
            (title, current) = (album.Album.Title, album.Album.CoverUrl);
            editions = await db.Editions.Include(e => e.Album).Where(e => e.AlbumId == id).ToListAsync(ct);
        }
        else
        {
            if (await view.GetTrackAsync(id, ct) is not { } track)
                return null;
            (title, current) = (track.Title, track.CoverUrl);
            editions = await db.Editions.Include(e => e.Album).Where(e => e.Tracks.Any(et => et.TrackId == id)).ToListAsync(ct);
        }

        var choices = new List<CoverOption>();
        var files = await Files(subject, id).OrderBy(f => f.Path).ToListAsync(ct);
        if (files.FirstOrDefault(f => FileArt.Find(FullPath(f)) is not null) is { } pictured)
            choices.Add(Option($"file:{pictured.Id}", "In your library", "Your files' own", null, LocalMusic.CoverUrl(pictured.Id)));

        // Albums by when they came out, each with its usual cover then its editions'.
        foreach (var release in editions.GroupBy(e => e.Album).OrderBy(g => g.Key.FirstReleaseDate ?? "9999").ThenBy(g => g.Key.Title))
        {
            var album = release.Key;
            var kind = string.Join(", ", [album.Type.ToString(), .. album.SecondaryTypes.Select(t => t.ToString())]);
            choices.Add(Option($"album:{album.Id}", album.Title, "Usual cover", Join(kind, Year(album.FirstReleaseDate)), LibraryView.CoverUrl(album.Id, null)));
            foreach (var edition in release.OrderBy(e => e.ReleaseDate ?? "9999").ThenBy(e => e.Country))
                choices.Add(Option($"edition:{edition.Id}", album.Title, edition.Title == album.Title ? "Edition" : edition.Title,
                    Join(edition.ReleaseDate, edition.Country, edition.Formats, edition.Disambiguation), LibraryView.CoverUrl(album.Id, edition.Id)));
        }

        var health = await worker.CheckHealthAsync(ct);
        return new CoverChoices(title, current, choices, files.Count, health.Status == HealthStatus.Healthy,
            health.Status == HealthStatus.Healthy ? null : health.Message);

        CoverOption Option(string source, string group, string name, string? detail, string url) =>
            new(source, group, name, detail, url, url == current);
    }

    /// <summary>Uses the cover, writing it into the files. Null when there's no such album or song.</summary>
    /// <exception cref="CoverRefusedException">The source or upload isn't a picture that can be used.</exception>
    /// <exception cref="HttpRequestException">The beets worker couldn't be reached.</exception>
    public async Task<CoverChosen?> ChooseAsync(CoverSubject subject, Guid id, ChooseCover body, CancellationToken ct)
    {
        var exists = subject == CoverSubject.Album
            ? await db.Albums.AnyAsync(a => a.Id == id, ct)
            : await db.Tracks.AnyAsync(t => t.Id == id, ct);
        if (!exists)
            return null;
        var (data, contentType) = await PictureAsync(body, ct);

        var files = await Files(subject, id).ToListAsync(ct);
        var written = new List<LibraryFile>();
        var failed = new List<string>();
        if (files.Count > 0)
        {
            var request = new FormatRequest(
                files.Select(f => new FormatFile(f.Path, new Dictionary<string, string>(), "cover", false)).ToList(),
                new Dictionary<string, FormatArtwork> { ["cover"] = new(Convert.ToBase64String(data)) },
                null,
                subject == CoverSubject.Album ? "cover" : null);
            var result = await worker.FormatAsync(request, ct);
            var root = Path.GetFullPath(options.Value.LibraryPath);
            foreach (var formatted in result.Files)
            {
                if (files.FirstOrDefault(f => f.Path == formatted.Path) is not { } file)
                    continue;
                if (formatted.NewPath is null)
                {
                    failed.Add($"{formatted.Path}: {formatted.Error}");
                    logger.LogWarning("Couldn't write the cover into {Path}: {Reason}", formatted.Path, formatted.Error);
                    continue;
                }
                FormatTask.Reread(file, root, logger);
                written.Add(file);
            }
            // Saved even if it's cancelled now: the files have been written.
            await db.SaveChangesAsync(CancellationToken.None);
            await FormatTask.TellMediaServerAsync(connectors, written.Select(f => f.Path).ToList(), logger, CancellationToken.None);
        }

        // Shown from now on for the album and the editions its files are on, or for the song.
        (EntityKind, Guid)[] shownFor = subject == CoverSubject.Album
            ? [(EntityKind.Album, id), .. files.Select(f => f.EditionId).OfType<Guid>().Distinct().Select(e => (EntityKind.Edition, e))]
            : [(EntityKind.Track, id)];
        var chosen = await artwork.ChooseAsync(shownFor, data, contentType, CancellationToken.None);
        logger.LogInformation("Chose a cover for {Subject} {Id}, written into {Files}", subject, id, LibraryMatcher.Plural(written.Count, "file", "files"));

        var url = subject == CoverSubject.Album
            ? (await view.GetAlbumAsync(id, null, CancellationToken.None))?.Album.CoverUrl ?? LibraryView.CoverUrl(id, null)
            : (await view.GetTrackAsync(id, CancellationToken.None))?.CoverUrl ?? $"api/v1/artwork/track/{id}";
        // A new address, so a page already showing the old picture shows this one.
        return new CoverChosen(written.Count, failed, $"{url}{(url.Contains('?') ? '&' : '?')}v={chosen.UpdatedAt.Ticks:x}");
    }

    private IQueryable<LibraryFile> Files(CoverSubject subject, Guid id) =>
        FormatTask.Files(db, new FormatScope(subject == CoverSubject.Album ? FormatScopeKind.Album : FormatScopeKind.Track, id))
            .Where(f => f.ExcludedAt == null);

    private string FullPath(LibraryFile file) => Path.Combine(Path.GetFullPath(options.Value.LibraryPath), file.Path);

    // The picture asked for, and what kind of image it is.
    private async Task<(byte[] Data, string ContentType)> PictureAsync(ChooseCover body, CancellationToken ct)
    {
        if (body.Upload is { } upload)
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(upload);
            }
            catch (FormatException)
            {
                throw new CoverRefusedException("upload", "The picture must be base64.");
            }
            if (data.Length > MaxUpload)
                throw new CoverRefusedException("upload", "The picture is bigger than 10 MB.");
            return (data, ImageType(data) ?? throw new CoverRefusedException("upload", "That isn't a JPEG, PNG, WebP or GIF picture."));
        }

        var parts = body.Source?.Split(':', 2);
        if (parts is not [var kind, var raw] || !Guid.TryParse(raw, out var sourceId))
            throw new CoverRefusedException("source", "Say which cover to use, or upload one.");
        if (kind == "file")
        {
            var file = await db.LibraryFiles.FirstOrDefaultAsync(f => f.Id == sourceId, ct)
                       ?? throw new CoverRefusedException("source", "There's no such file in the library.");
            var picture = FileArt.Find(FullPath(file)) ?? throw new CoverRefusedException("source", "That file has no picture of its own.");
            return (picture.Data, picture.ContentType);
        }
        EntityKind? of = kind switch { "album" => EntityKind.Album, "edition" => EntityKind.Edition, _ => null };
        if (of is null)
            throw new CoverRefusedException("source", $"Covers come from an album, edition or file, not {kind}.");
        var found = await artwork.GetAsync(of.Value, sourceId, ct) ?? throw new CoverRefusedException("source", "There's no picture for that.");
        return (await File.ReadAllBytesAsync(found.Path, ct), found.ContentType);
    }

    /// <summary>The kind of image the data is, by its first bytes; null for anything else.</summary>
    public static string? ImageType(byte[] data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null,
    };

    private static string? Year(string? date) => date is { Length: >= 4 } ? date[..4] : null;

    private static string? Join(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } some ? string.Join(" · ", some) : null;
}
