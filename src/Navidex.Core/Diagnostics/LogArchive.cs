using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Navidex.Client;
using Navidex.Core.Library;

namespace Navidex.Core.Diagnostics;

/// <summary>
/// Keeps the activity log on disk, one JSON Lines file per day under <c>data/logs</c>. Every entry is written as it
/// happens, so nothing is lost on a restart. Memory keeps the newest entries for the Log page; the files are kept
/// longer and deleted once older than the backup retention. Both ages are in <see cref="LogSettings"/>.
/// </summary>
public sealed class LogArchive(ActivityLog log, IServiceScopeFactory scopes, IOptions<NavidexOptions> options, ILogger<LogArchive> logger)
    : BackgroundService
{
    private static readonly TimeSpan TidyEvery = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = NavidexJson.Options;

    private string Directory => Path.Combine(options.Value.DataPath, "logs");

    public static string FileFor(string directory, DateTime day) =>
        Path.Combine(directory, $"activity-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.jsonl");

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var settings = await GetSettingsAsync(cancellationToken);
        log.Restore(Read(Directory, DateTime.UtcNow - TimeSpan.FromDays(settings.MemoryDays)));
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var writing = WriteAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TidyAsync(stoppingToken);
                await Task.Delay(TidyEvery, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Couldn't tidy the log files");
                await Task.Delay(TidyEvery, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
        await writing;
    }

    /// <summary>Applies the retention settings now: drops old entries from memory and deletes old files.</summary>
    public async Task TidyAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        log.Trim(DateTime.UtcNow - TimeSpan.FromDays(settings.MemoryDays));
        var oldest = DateTime.UtcNow.Date - TimeSpan.FromDays(settings.FileDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "activity-*.jsonl"))
            if (DayOf(file) is { } day && day < oldest)
                File.Delete(file);
    }

    private async Task WriteAsync(CancellationToken ct)
    {
        StreamWriter? writer = null;
        DateTime day = default;
        try
        {
            while (await log.Unwritten.WaitToReadAsync(ct))
            {
                while (log.Unwritten.TryRead(out var entry))
                {
                    if (writer is null || entry.Time.Date != day)
                    {
                        if (writer is not null)
                            await writer.DisposeAsync();
                        day = entry.Time.Date;
                        writer = new StreamWriter(new FileStream(FileFor(Directory, day), FileMode.Append, FileAccess.Write, FileShare.Read));
                    }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new Stored(entry.Time, entry.Level, entry.Source, entry.Message, entry.Error), Json));
                }
                await writer!.FlushAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Whatever was logged while shutting down still goes to the file.
            while (writer is not null && log.Unwritten.TryRead(out var entry) && entry.Time.Date == day)
                await writer.WriteLineAsync(JsonSerializer.Serialize(new Stored(entry.Time, entry.Level, entry.Source, entry.Message, entry.Error), Json));
        }
        catch (IOException e)
        {
            // The log mustn't take the server down; entries stay in memory.
            Console.Error.WriteLine($"Navidex: can't write the log files in {Directory}: {e.Message}");
        }
        finally
        {
            if (writer is not null)
                await writer.DisposeAsync();
        }
    }

    /// <summary>Entries in the files since the cutoff, oldest first. Unreadable lines are skipped.</summary>
    public static List<LogEntry> Read(string directory, DateTime since)
    {
        var entries = new List<LogEntry>();
        if (!System.IO.Directory.Exists(directory))
            return entries;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "activity-*.jsonl").Where(f => DayOf(f) >= since.Date).OrderBy(f => f, StringComparer.Ordinal))
        foreach (var line in File.ReadLines(file))
        {
            Stored? stored;
            try
            {
                stored = JsonSerializer.Deserialize<Stored>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (stored is not null && stored.Time >= since)
                entries.Add(new LogEntry(0, stored.Time, stored.Level, stored.Source, stored.Message, stored.Error));
        }
        return entries;
    }

    private static DateTime? DayOf(string file) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file)["activity-".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) ? day : null;

    private async Task<LogSettings> GetSettingsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsService>().GetLogAsync(ct);
    }

    private sealed record Stored(DateTime Time, LogEntryLevel Level, string Source, string Message, string? Error);
}
