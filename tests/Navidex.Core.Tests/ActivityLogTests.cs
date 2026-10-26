using Microsoft.Extensions.Logging;
using Navidex.Client;
using Navidex.Core.Diagnostics;

namespace Navidex.Core.Tests;

public class ActivityLogTests
{
    [Fact]
    public void Entries_come_back_oldest_first_after_a_sequence_number()
    {
        var log = new ActivityLog();
        for (var i = 1; i <= 5; i++)
            log.Add(LogEntryLevel.Information, "Test", $"event {i}", null);

        Assert.Equal(["event 4", "event 5"], log.After(3, 100).Select(e => e.Message));
        Assert.Empty(log.After(5, 100));
        Assert.Equal(5, log.Latest);
    }

    [Fact]
    public void A_limit_returns_the_newest_entries()
    {
        var log = new ActivityLog();
        for (var i = 1; i <= 10; i++)
            log.Add(LogEntryLevel.Information, "Test", $"event {i}", null);

        Assert.Equal(["event 9", "event 10"], log.After(0, 2).Select(e => e.Message));
    }

    [Fact]
    public void Memory_never_holds_more_than_the_ceiling()
    {
        var log = new ActivityLog();
        for (var i = 1; i <= ActivityLog.MaxEntries + 10; i++)
            log.Add(LogEntryLevel.Information, "Test", $"event {i}", null);

        var newest = log.After(0, ActivityLog.PageLimit);
        Assert.Equal(ActivityLog.PageLimit, newest.Count);
        Assert.Equal($"event {ActivityLog.MaxEntries + 10}", newest[^1].Message);
        Assert.Equal("event 11", log.After(0, ActivityLog.MaxEntries)[0].Message);
    }

    [Fact]
    public void Trimming_forgets_old_entries_but_keeps_the_numbering()
    {
        var log = new ActivityLog();
        for (var i = 1; i <= 3; i++)
            log.Add(LogEntryLevel.Information, "Test", $"event {i}", null);
        log.Trim(DateTime.UtcNow.AddSeconds(1));
        Assert.Empty(log.After(0, 100));
        Assert.Equal(3, log.Latest);

        log.Add(LogEntryLevel.Information, "Test", "event 4", null);
        Assert.Equal([4L], log.After(0, 100).Select(e => e.Seq));
    }

    [Fact]
    public void Entries_from_before_a_restart_come_back_first()
    {
        var log = new ActivityLog();
        log.Add(LogEntryLevel.Information, "Test", "since the restart", null);
        var day = DateTime.UtcNow.AddDays(-2);
        log.Restore([new LogEntry(0, day, LogEntryLevel.Warning, "Test", "before", null)]);

        Assert.Equal(["before", "since the restart"], log.After(0, 10).Select(e => e.Message));
        Assert.Equal([1L, 2L], log.After(0, 10).Select(e => e.Seq));
    }

    [Fact]
    public void The_archive_reads_back_entries_since_a_cutoff_and_skips_broken_lines()
    {
        var directory = Directory.CreateTempSubdirectory("navidex-log").FullName;
        try
        {
            var today = DateTime.UtcNow;
            File.WriteAllLines(LogArchive.FileFor(directory, today.AddDays(-10)),
                [$$"""{"time":"{{today.AddDays(-10):O}}","level":"information","source":"Old","message":"too old"}"""]);
            File.WriteAllLines(LogArchive.FileFor(directory, today),
                [$$"""{"time":"{{today:O}}","level":"error","source":"New","message":"kept","error":"boom"}""", "{ not json"]);

            var entries = LogArchive.Read(directory, today.AddDays(-7));
            var entry = Assert.Single(entries);
            Assert.Equal("kept", entry.Message);
            Assert.Equal(LogEntryLevel.Error, entry.Level);
            Assert.Equal("boom", entry.Error);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Our_information_is_kept_but_only_warnings_from_elsewhere()
    {
        var log = new ActivityLog();
        var provider = new ActivityLogProvider(log);
        var ours = provider.CreateLogger("Navidex.Core.Library.LibraryScanner");
        var theirs = provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        ours.LogInformation("Found {Count} files", 3);
        theirs.LogInformation("Executed DbCommand");
        theirs.LogWarning("Something odd");

        var entries = log.After(0, 10);
        Assert.Equal(["Found 3 files", "Something odd"], entries.Select(e => e.Message));
        Assert.Equal("LibraryScanner", entries[0].Source);
        Assert.Equal(LogEntryLevel.Warning, entries[1].Level);
    }
}
