using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Lyrics;
using Melodex.Core.Data;
using Melodex.Core.Import;
using Melodex.Core.Plugins;
using Melodex.Core.Tasks;

namespace Melodex.Core.Library;

/// <summary>
/// Finds lyrics for songs in the library through the lyrics plugins, and has the beets worker write them where the
/// settings say: an .lrc file beside each song, its lyrics tag, or both. A file that already has lyrics, beside it or
/// in its tags, keeps them; one no source has lyrics for isn't asked about again for a while.
/// </summary>
public sealed class LyricsFinder(
    MelodexDbContext db,
    LyricsPlugins plugins,
    IImportWorker worker,
    SettingsService settings,
    ConnectorPlugins connectors,
    IOptions<MelodexOptions> options,
    ILogger<LyricsFinder> logger)
{
    /// <summary>How long "no source has lyrics for it" is believed before asking again.</summary>
    public static readonly TimeSpan NoneFor = TimeSpan.FromDays(30);

    /// <summary>Failures in a row after which a source isn't asked again this time, as it's likely down.</summary>
    public const int FailuresBeforeGivingUp = 3;

    // Failures in a row of each source, over everything this finder is asked to do: a task's whole run.
    private readonly Dictionary<Guid, int> _failures = new();

    /// <param name="Found">Files given lyrics, or found to be instrumental.</param>
    /// <param name="Already">Files that had lyrics already, beside them or in their tags.</param>
    /// <param name="None">Files no source had lyrics for.</param>
    /// <param name="Failed">Files a source or the worker failed on, to try again next time.</param>
    public sealed record Outcome(int Found, int Already, int None, int Failed, string? Problem = null)
    {
        public static Outcome operator +(Outcome a, Outcome b) =>
            new(a.Found + b.Found, a.Already + b.Already, a.None + b.None, a.Failed + b.Failed, a.Problem ?? b.Problem);
    }

    /// <summary>Matched files in the library without lyrics that the sources haven't lately said they have none for.</summary>
    public IQueryable<LibraryFile> Wanting()
    {
        var since = DateTime.UtcNow - NoneFor;
        return db.LibraryFiles.Where(f => f.TrackId != null && f.ExcludedAt == null && f.HiddenAt == null && f.Lyrics == LyricsKind.None
                                          && f.LyricsExcludedAt == null && (f.LyricsCheckedAt == null || f.LyricsCheckedAt < since));
    }

    public Task<bool> HaveSourcesAsync(CancellationToken ct) => db.PluginInstances.AnyAsync(p => p.Type == Contracts.Plugins.PluginType.Lyrics && p.Enabled, ct);

    /// <summary>
    /// Finds and writes lyrics for the files. With <paramref name="again"/>, lyrics they have already are looked for
    /// afresh and replaced.
    /// </summary>
    /// <exception cref="HttpRequestException">The beets worker couldn't be reached.</exception>
    public async Task<Outcome> FindAsync(IReadOnlyCollection<Guid> fileIds, bool again, CancellationToken ct)
    {
        var sources = await plugins.GetAsync(ct);
        var storage = (await settings.GetLibraryAsync(ct)).LyricsStorage;
        var root = Path.GetFullPath(options.Value.LibraryPath);
        var files = await db.LibraryFiles.Include(f => f.Track).Include(f => f.Edition).ThenInclude(e => e!.Album)
            .Where(f => fileIds.Contains(f.Id) && f.Track != null).OrderBy(f => f.Path).ToListAsync(ct);
        var now = DateTime.UtcNow;
        int already = 0, none = 0, failed = 0;
        string? problem = null;

        // One question per song, however many files it has.
        var answers = new Dictionary<Guid, (FoundLyrics? Lyrics, bool Failed)>();
        var writes = new List<(LibraryFile File, FormatFile Request, LyricsKind Kind)>();
        var found = 0;
        foreach (var file in files)
        {
            if (!again && LyricsFiles.KindOf(Path.Combine(root, file.Path)) is not LyricsKind.None and var kind)
            {
                file.Lyrics = kind;
                already++;
                continue;
            }
            if (!answers.TryGetValue(file.TrackId!.Value, out var answer))
                answers[file.TrackId.Value] = answer = await AskAsync(sources, file, ct);
            switch (answer)
            {
                case { Failed: true, Lyrics: null }:
                    failed++;
                    problem ??= "a lyrics source isn't answering";
                    break;
                case { Lyrics: null }:
                    file.LyricsCheckedAt = now;
                    none++;
                    break;
                case { Lyrics.Instrumental: true }:
                    file.Lyrics = LyricsKind.Instrumental;
                    file.LyricsCheckedAt = now;
                    found++;
                    break;
                case { Lyrics: { } lyrics }:
                    var text = lyrics.Synced ?? lyrics.Plain!;
                    writes.Add((file, Write(file, text, storage), LyricsFiles.KindOfText(text)));
                    break;
            }
        }

        var (written, unwritten, why) = await WriteAsync(writes, root, ct);
        // Saved even if it's cancelled now: the files have been written.
        await db.SaveChangesAsync(CancellationToken.None);
        return new Outcome(found + written, already, none, failed + unwritten, problem ?? why);
    }

    /// <summary>
    /// Writes the lyrics to the files where the settings say, replacing any they have; with null, takes their lyrics
    /// away, from beside them and from their tags.
    /// </summary>
    /// <returns>Why some couldn't be written, or null when every one was.</returns>
    /// <exception cref="HttpRequestException">The beets worker couldn't be reached.</exception>
    public async Task<string?> SaveAsync(IReadOnlyCollection<LibraryFile> files, string? text, CancellationToken ct)
    {
        var storage = (await settings.GetLibraryAsync(ct)).LyricsStorage;
        var root = Path.GetFullPath(options.Value.LibraryPath);
        var writes = files.Select(f => text is null
            ? (f, new FormatFile(f.Path, new Dictionary<string, string> { ["lyrics"] = "" }, null, false, ""), LyricsKind.None)
            : (f, Write(f, text, storage), LyricsFiles.KindOfText(text))).ToList();
        var (_, failed, problem) = await WriteAsync(writes, root, ct);
        await db.SaveChangesAsync(CancellationToken.None);
        return failed > 0 ? problem ?? "the beets worker couldn't write them" : null;
    }

    /// <summary>
    /// What the lyrics plugins that can search have that might be the song: for what's typed, or else its title and
    /// artist. Those of the song's length come first.
    /// </summary>
    public async Task<IReadOnlyList<LyricsChoice>> SearchAsync(LibraryFile file, string? text, CancellationToken ct)
    {
        await db.Entry(file).Reference(f => f.Track).LoadAsync(ct);
        await db.Entry(file).Reference(f => f.Edition).LoadAsync(ct);
        if (file.Edition is not null)
            await db.Entry(file.Edition).Reference(e => e.Album).LoadAsync(ct);
        var song = await QueryAsync(file, ct);
        var choices = new List<LyricsChoice>();
        string? problem = null;
        foreach (var (instance, plugin) in await plugins.GetAsync(ct))
        {
            try
            {
                foreach (var result in await plugin.SearchAsync(new LyricsSearch(text ?? "", song), ct))
                {
                    var lyrics = result.Lyrics.Synced ?? result.Lyrics.Plain;
                    if (lyrics is not null)
                        choices.Add(new LyricsChoice(instance.Name, result.Title, result.Artist, result.Album, result.Duration?.TotalSeconds,
                            LyricsFiles.KindOfText(lyrics), lyrics));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("{Plugin} couldn't search for lyrics: {Message}", instance.Name, e.Message);
                problem ??= $"{instance.Name} isn't answering: {e.Message}";
            }
        }
        if (choices.Count == 0 && problem is not null)
            throw new HttpRequestException(problem);
        return choices;
    }

    private static FormatFile Write(LibraryFile file, string text, LyricsStorage storage)
    {
        var tags = storage == LyricsStorage.Sidecar ? new Dictionary<string, string>() : new Dictionary<string, string> { ["lyrics"] = text };
        return new FormatFile(file.Path, tags, null, false, storage == LyricsStorage.Embedded ? null : text);
    }

    // Has the worker write each, and notes what each file has now.
    private async Task<(int Written, int Failed, string? Problem)> WriteAsync(List<(LibraryFile File, FormatFile Request, LyricsKind Kind)> writes, string root,
        CancellationToken ct)
    {
        if (writes.Count == 0)
            return (0, 0, null);
        var result = await worker.FormatAsync(new FormatRequest(writes.Select(w => w.Request).ToList(), new Dictionary<string, FormatArtwork>(), null), ct);
        var written = new List<string>();
        var failed = 0;
        string? problem = null;
        foreach (var formatted in result.Files)
        {
            if (writes.FirstOrDefault(w => w.File.Path == formatted.Path) is not { File: { } file } write)
                continue;
            if (formatted.NewPath is null)
            {
                failed++;
                problem ??= formatted.Error;
                logger.LogWarning("Couldn't write lyrics for {Path}: {Reason}", formatted.Path, formatted.Error);
                continue;
            }
            FormatTask.Reread(file, root, logger);
            file.Lyrics = write.Kind;
            file.LyricsCheckedAt = null;
            written.Add(file.Path);
        }
        await FormatTask.TellMediaServerAsync(connectors, written, logger, CancellationToken.None);
        return (written.Count, failed, problem);
    }

    private async Task<LyricsQuery> QueryAsync(LibraryFile file, CancellationToken ct)
    {
        var track = file.Track!;
        var ids = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Track && m.EntityId == track.Id)
            .Select(m => new ProviderId(m.Provider, m.Value)).ToListAsync(ct);
        return new LyricsQuery(track.Title, track.ArtistCredit, file.Edition?.Album.Title, file.Duration ?? track.Duration, ids);
    }

    // The first lyrics a source has, in the user's order. A failure is only reported if no source had any.
    private async Task<(FoundLyrics? Lyrics, bool Failed)> AskAsync(IReadOnlyList<(PluginInstance Instance, ILyricsPlugin Plugin)> sources,
        LibraryFile file, CancellationToken ct)
    {
        var track = file.Track!;
        var query = await QueryAsync(file, ct);
        var failed = false;
        foreach (var (instance, plugin) in sources)
        {
            if (_failures.GetValueOrDefault(instance.Id) >= FailuresBeforeGivingUp)
            {
                failed = true;
                continue;
            }
            try
            {
                var lyrics = await plugin.FindAsync(query, ct);
                _failures[instance.Id] = 0;
                if (lyrics is not null)
                    return (lyrics, false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed = true;
                if ((_failures[instance.Id] = _failures.GetValueOrDefault(instance.Id) + 1) == FailuresBeforeGivingUp)
                    logger.LogWarning("{Plugin} failed {Count} times in a row, so it isn't asked again until next time: {Message}",
                        instance.Name, FailuresBeforeGivingUp, e.Message);
                else
                    logger.LogWarning("{Plugin} couldn't look for the lyrics of {Title}: {Message}", instance.Name, track.Title, e.Message);
            }
        }
        return (null, failed);
    }
}

/// <summary>
/// Finds lyrics for every song in the library without them, after a scan. It goes a few songs at a time, so pausing
/// or cancelling keeps what's been found, and picking it up again skips them.
/// </summary>
public sealed class LyricsTask(LyricsFinder finder, MelodexDbContext db) : ITaskHandler
{
    public const string TaskKind = "library-lyrics";
    public const string Title = "Finding lyrics";
    private const int BatchSize = 25;

    public string Kind => TaskKind;

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        if (!await finder.HaveSourcesAsync(ct))
        {
            progress.Report("No lyrics sources are set up");
            return;
        }
        var todo = await finder.Wanting().OrderBy(f => f.Path).Select(f => f.Id).ToListAsync(ct);
        var outcome = new LyricsFinder.Outcome(0, 0, 0, 0);
        var done = 0;
        progress.Report($"Finding lyrics: 0 of {LibraryMatcher.Plural(todo.Count, "file", "files")}");
        foreach (var batch in todo.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            outcome += await finder.FindAsync(batch, again: false, ct);
            done += batch.Length;
            progress.Report($"Finding lyrics: {done} of {LibraryMatcher.Plural(todo.Count, "file", "files")}, {outcome.Found} found");
        }

        var notes = new List<string>();
        if (outcome.Already > 0)
            notes.Add($"{outcome.Already} had them already");
        if (outcome.None > 0)
            notes.Add($"{outcome.None} have none at the sources");
        if (outcome.Failed > 0)
            notes.Add($"{outcome.Failed} to try again next scan ({outcome.Problem})");
        progress.Report($"Found lyrics for {LibraryMatcher.Plural(outcome.Found, "file", "files")}" + string.Concat(notes.Select(n => "; " + n)));
    }
}
