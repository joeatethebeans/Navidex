using System.Threading.Channels;
using Melodex.Client;

namespace Melodex.Core.Diagnostics;

/// <summary>
/// The recent activity the web app's Log page shows: Melodex's own events from Information up, and warnings and
/// errors from everything else. Every entry is also written to the day's file by <see cref="LogArchive"/>; memory
/// holds the last week or so (see <see cref="LogSettings"/>), reloaded from the files when the server starts.
/// </summary>
public sealed class ActivityLog
{
    /// <summary>The most a single read returns.</summary>
    public const int PageLimit = 5000;

    /// <summary>A ceiling on what's kept in memory however long the retention, in case something logs in a loop.</summary>
    public const int MaxEntries = 100_000;

    // Sequence numbers are contiguous: _entries[i].Seq == _first + i.
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _lock = new();
    private readonly Channel<LogEntry> _unwritten = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions { SingleReader = true });
    private long _first = 1;

    public long Latest
    {
        get { lock (_lock) return _first + _entries.Count - 1; }
    }

    /// <summary>Entries waiting to be written to the archive.</summary>
    public ChannelReader<LogEntry> Unwritten => _unwritten.Reader;

    /// <summary>
    /// The most of a message or error that's kept. A failed database command logs its whole SQL, which can run to
    /// megabytes; the start says what went wrong.
    /// </summary>
    public const int MaxLength = 4000;

    public void Add(LogEntryLevel level, string source, string message, string? error)
    {
        message = Shorten(message)!;
        error = Shorten(error);
        LogEntry entry;
        lock (_lock)
        {
            entry = new LogEntry(_first + _entries.Count, DateTime.UtcNow, level, source, message, error);
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                Drop(_entries.Count - MaxEntries);
        }
        _unwritten.Writer.TryWrite(entry);
    }

    private static string? Shorten(string? text) =>
        text is { Length: > MaxLength } ? $"{text[..MaxLength]}… ({text.Length - MaxLength:N0} more characters)" : text;

    /// <summary>Entries after <paramref name="after"/>, oldest first, at most <paramref name="limit"/> of the newest.</summary>
    public IReadOnlyList<LogEntry> After(long after, int limit)
    {
        lock (_lock)
        {
            var latest = _first + _entries.Count - 1;
            var from = Math.Max(Math.Max(after + 1, _first), latest - limit + 1);
            return from > latest ? [] : _entries.GetRange((int)(from - _first), (int)(latest - from + 1));
        }
    }

    /// <summary>Forgets entries older than the cutoff; they stay in the archive.</summary>
    public void Trim(DateTime olderThan)
    {
        lock (_lock)
        {
            var count = 0;
            while (count < _entries.Count && _entries[count].Time < olderThan)
                count++;
            Drop(count);
        }
    }

    /// <summary>
    /// Puts entries from before a restart back in front of what's been logged since, oldest first. Everything is
    /// numbered again from 1, which the web app takes as a restart anyway.
    /// </summary>
    public void Restore(IReadOnlyList<LogEntry> earlier)
    {
        lock (_lock)
        {
            var all = earlier.Concat(_entries).Select((e, i) => e with { Seq = i + 1 }).ToList();
            _entries.Clear();
            _entries.AddRange(all.Count > MaxEntries ? all[^MaxEntries..] : all);
            _first = _entries.Count > 0 ? _entries[0].Seq : 1;
        }
    }

    private void Drop(int count)
    {
        _entries.RemoveRange(0, count);
        _first += count;
    }
}

/// <summary>Event IDs the activity log shows differently.</summary>
public static class LogEvents
{
    /// <summary>Logged at Information, shown as a success: a scan or other task that finished well.</summary>
    public static readonly EventId Success = new(1, nameof(Success));
}

/// <summary>Feeds <see cref="ActivityLog"/> from the app's logging.</summary>
public sealed class ActivityLogProvider(ActivityLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(log, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(ActivityLog log, string category) : ILogger
    {
        private readonly bool _ours = category.StartsWith("Melodex.", StringComparison.Ordinal);

        // "Melodex.Core.Library.LibraryScanner" shows as "LibraryScanner".
        private readonly string _source = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= (_ours ? LogLevel.Information : LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var level = logLevel switch
            {
                >= LogLevel.Error => LogEntryLevel.Error,
                LogLevel.Warning => LogEntryLevel.Warning,
                _ when eventId.Name == LogEvents.Success.Name => LogEntryLevel.Success,
                _ => LogEntryLevel.Information,
            };
            log.Add(level, _source, formatter(state, exception), exception?.GetBaseException().Message);
        }
    }
}
