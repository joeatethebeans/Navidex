using Navidex.Client;

namespace Navidex.Core.Diagnostics;

/// <summary>
/// The recent activity the web app's Log page shows: Navidex's own events from Information up, and warnings and
/// errors from everything else. Kept in memory, so it covers the time since the server started.
/// </summary>
public sealed class ActivityLog
{
    public const int Capacity = 5000;

    private readonly LogEntry?[] _entries = new LogEntry?[Capacity];
    private readonly Lock _lock = new();
    private long _latest;

    public long Latest
    {
        get { lock (_lock) return _latest; }
    }

    public void Add(LogEntryLevel level, string source, string message, string? error)
    {
        lock (_lock)
        {
            _latest++;
            _entries[_latest % Capacity] = new LogEntry(_latest, DateTime.UtcNow, level, source, message, error);
        }
    }

    /// <summary>Entries after <paramref name="after"/>, oldest first, at most <paramref name="limit"/> of the newest.</summary>
    public IReadOnlyList<LogEntry> After(long after, int limit)
    {
        lock (_lock)
        {
            var from = Math.Max(Math.Max(after, _latest - Capacity), _latest - limit) + 1;
            var result = new List<LogEntry>((int)Math.Max(0, _latest - from + 1));
            for (var seq = from; seq <= _latest; seq++)
                if (_entries[seq % Capacity] is { } entry)
                    result.Add(entry);
            return result;
        }
    }
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
        private readonly bool _ours = category.StartsWith("Navidex.", StringComparison.Ordinal);

        // "Navidex.Core.Library.LibraryScanner" shows as "LibraryScanner".
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
                _ => LogEntryLevel.Information,
            };
            log.Add(level, _source, formatter(state, exception), exception?.GetBaseException().Message);
        }
    }
}
