using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Navidex.Client;
using Navidex.Core.Library;

namespace Navidex.Core.Events;

/// <summary>
/// What changed, for <c>GET /events</c>: requests, listen-now tracks, the library or background tasks. Events say only
/// what kind of thing changed, and clients reload what they show. Like <see cref="LibraryCache"/>, it watches database
/// writes as an interceptor, so no write path can forget to announce itself.
/// </summary>
/// <remarks>Each kind is sent at most once per <see cref="Coalesce"/>, however many writes there were.</remarks>
public sealed class EventFeed : DbCommandInterceptor
{
    public const string Requests = "requests";
    public const string Listen = "listen";
    public const string Library = "library";
    public const string Tasks = "tasks";

    /// <summary>How long changes of one kind are gathered into one event.</summary>
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(250);

    // A listen-now track's state follows its request, so request changes are listen-now changes too.
    private static readonly (string Table, string[] Kinds)[] Tables =
    [
        ("\"Requests\"", [Requests, Listen]),
        ("\"Jobs\"", [Requests]),
        ("\"ListenNowItems\"", [Listen]),
        ("\"BackgroundTasks\"", [Tasks]),
    ];

    private readonly ConcurrentDictionary<Channel<ServerEvent>, byte> _subscribers = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();

    public EventFeed(LibraryCache library)
    {
        library.Changed += () => Raise(Library);
    }

    /// <summary>Events from now on, until the subscription is disposed. A slow reader loses the oldest.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<ServerEvent>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[channel] = 0;
        return new Subscription(channel.Reader, () =>
        {
            _subscribers.TryRemove(channel, out _);
            channel.Writer.TryComplete();
        });
    }

    /// <summary>Announces a change of this kind, once the burst it's part of is over.</summary>
    public void Raise(string kind)
    {
        if (!_pending.TryAdd(kind, 0))
            return;
        _ = Task.Delay(Coalesce).ContinueWith(_ =>
        {
            _pending.TryRemove(kind, out byte _);
            var @event = new ServerEvent(kind, DateTime.UtcNow);
            foreach (var subscriber in _subscribers.Keys)
                subscriber.Writer.TryWrite(@event);
        }, TaskScheduler.Default);
    }

    public sealed class Subscription(ChannelReader<ServerEvent> reader, Action dispose) : IDisposable
    {
        public ChannelReader<ServerEvent> Reader { get; } = reader;

        public void Dispose() => dispose();
    }

    // ---- Watching for writes ----

    private void Saw(DbCommand command)
    {
        var sql = command.CommandText;
        if (!(sql.Contains("INSERT INTO", StringComparison.Ordinal) || sql.Contains("UPDATE ", StringComparison.Ordinal)
              || sql.Contains("DELETE FROM", StringComparison.Ordinal)))
            return;
        foreach (var (table, kinds) in Tables)
            if (sql.Contains(table, StringComparison.Ordinal))
                foreach (var kind in kinds)
                    Raise(kind);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Saw(command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
    {
        Saw(command);
        return ValueTask.FromResult(result);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Saw(command);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken ct = default)
    {
        Saw(command);
        return ValueTask.FromResult(result);
    }
}
