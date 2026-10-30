using Melodex.Client;
using Melodex.Contracts.Plugins.Retrieval;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Requests;

/// <summary>
/// Checks fetched files against the catalogue before anything reaches the library: the right number of tracks, each
/// about as long as the catalogue says. Lengths are read from the files themselves, not taken from the plugin.
/// </summary>
public static class Verifier
{
    /// <summary>A staged file and the track it's taken to be.</summary>
    /// <param name="Path">Relative to the job's staging directory.</param>
    public sealed record Matched(string Path, WantedTrack Track, TimeSpan? Duration);

    public sealed record Outcome(VerificationResult Result, string Detail, IReadOnlyList<Matched> Files);

    /// <summary>How far a track's length may be from the catalogue's: a few seconds, or a few percent of a long one.</summary>
    public static double Tolerance(double expectedSeconds, FetchSettings fetch) =>
        Math.Max(fetch.DurationToleranceSeconds, expectedSeconds * fetch.DurationTolerancePercent / 100.0);

    public static Outcome Check(string directory, IReadOnlyList<FetchedFile> reported, IReadOnlyList<WantedTrack> wanted, FetchSettings fetch)
    {
        var files = Files(directory, reported);
        if (files.Count == 0)
            return new Outcome(VerificationResult.DurationMismatch, "the plugin wrote no audio files", []);

        List<Matched> matched;
        if (wanted.Count == 1)
        {
            // One track: a plugin may leave extras (a video's other audio track, say); take the closest in length.
            var expected = wanted[0].Duration;
            var best = files.OrderBy(f => expected is null || f.Duration is null ? double.MaxValue : Math.Abs((f.Duration.Value - expected.Value).TotalSeconds)).First();
            matched = [new Matched(best.Path, wanted[0], best.Duration)];
        }
        else
        {
            if (files.Count < wanted.Count)
                return new Outcome(VerificationResult.DurationMismatch, $"got {files.Count} files for {wanted.Count} tracks", []);
            // By disc and position where the plugin says, otherwise in file name order.
            var ordered = files.All(f => f.Disc is not null && f.Position is not null)
                ? files.OrderBy(f => f.Disc).ThenBy(f => f.Position).ToList()
                : files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            matched = wanted.Select((t, i) => new Matched(ordered[i].Path, t, ordered[i].Duration)).ToList();
        }

        var problems = new List<string>();
        foreach (var m in matched)
        {
            if (m.Track.Duration is not { } expected || m.Duration is not { } actual)
                continue;
            var off = Math.Abs((actual - expected).TotalSeconds);
            if (off > Tolerance(expected.TotalSeconds, fetch))
                problems.Add($"{m.Track.Title} is {Length(actual)}, expected {Length(expected)}");
        }
        if (problems.Count > 0)
            return new Outcome(VerificationResult.DurationMismatch, string.Join("; ", problems), matched);

        var detail = matched.Count == 1
            ? matched[0] is { Duration: { } a, Track.Duration: { } e } ? $"{Length(a)} against {Length(e)} expected" : "length unknown, so not checked"
            : $"{matched.Count} tracks, each within {fetch.DurationToleranceSeconds}s or {fetch.DurationTolerancePercent}% of the catalogue";
        return new Outcome(VerificationResult.Passed, detail, matched);
    }

    private sealed record Staged(string Path, TimeSpan? Duration, int? Disc, int? Position);

    private static List<Staged> Files(string directory, IReadOnlyList<FetchedFile> reported)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var listed = reported
            .Select(f => (File: f, Full: Path.GetFullPath(Path.Combine(directory, f.Path))))
            // Nothing outside the job's own folder, whatever the plugin says.
            .Where(f => f.Full.StartsWith(root, StringComparison.Ordinal) && File.Exists(f.Full))
            .Select(f => new Staged(f.File.Path, Read(f.Full) ?? Seconds(f.File.DurationSeconds), f.File.Disc, f.File.Position))
            .ToList();
        if (listed.Count > 0)
            return listed;
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => LibraryScanner.AudioExtensions.Contains(Path.GetExtension(f)))
                .Select(f => new Staged(Path.GetRelativePath(directory, f).Replace('\\', '/'), Read(f), null, null))
                .ToList()
            : [];
    }

    private static TimeSpan? Read(string path)
    {
        try
        {
            return LibraryScanner.ReadTags(path).Duration;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static TimeSpan? Seconds(double? seconds) => seconds is { } s ? TimeSpan.FromSeconds(s) : null;

    private static string Length(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
