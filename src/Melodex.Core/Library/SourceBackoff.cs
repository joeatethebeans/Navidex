using Melodex.Core.Plugins;

namespace Melodex.Core.Library;

/// <summary>
/// How one scan copes with a catalogue source that keeps saying it's busy. Each busy answer earns a short rest. A run of
/// them earns a long one, and a few long rests in a row with nothing getting through means the source is down, so the
/// scan stops asking and leaves the rest for next time. Any answer that gets through starts the count again.
/// </summary>
public sealed class SourceBackoff(PluginHttpOptions options, ILogger logger)
{
    public const int InARowBeforeLongWait = 5;
    public const int LongWaitsBeforeStopping = 3;

    private int _inARow;

    /// <summary>The source has been unavailable for so long that this scan should stop asking it.</summary>
    public bool GaveUp { get; private set; }

    public void Answered() => _inARow = 0;

    public async Task UnavailableAsync(IProgress<string>? progress, string status, CancellationToken ct)
    {
        _inARow++;
        if (_inARow >= InARowBeforeLongWait * LongWaitsBeforeStopping)
        {
            GaveUp = true;
            logger.LogWarning("The catalogue source has stayed busy through {Count} attempts and {Waits} long waits in a row, so this scan stops asking it. The next scan picks up where this one left off.",
                _inARow, LongWaitsBeforeStopping - 1);
            return;
        }
        if (_inARow % InARowBeforeLongWait == 0)
        {
            var minutes = Math.Max(1, (int)Math.Round(options.LongUnavailablePause.TotalMinutes));
            logger.LogWarning("The catalogue source has been busy {Count} times in a row; waiting {Minutes} before carrying on", _inARow, LibraryMatcher.Plural(minutes, "minute", "minutes"));
            progress?.Report($"{status} (the catalogue source is busy; waiting {LibraryMatcher.Plural(minutes, "minute", "minutes")})");
            await Task.Delay(options.LongUnavailablePause, ct);
            return;
        }
        progress?.Report($"{status} (the catalogue source is busy; waiting)");
        await Task.Delay(options.UnavailablePause, ct);
    }
}
