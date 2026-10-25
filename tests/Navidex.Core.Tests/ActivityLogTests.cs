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
    public void Only_the_newest_entries_are_kept()
    {
        var log = new ActivityLog();
        for (var i = 1; i <= ActivityLog.Capacity + 10; i++)
            log.Add(LogEntryLevel.Information, "Test", $"event {i}", null);

        var all = log.After(0, ActivityLog.Capacity);
        Assert.Equal(ActivityLog.Capacity, all.Count);
        Assert.Equal("event 11", all[0].Message);
        Assert.Equal($"event {ActivityLog.Capacity + 10}", all[^1].Message);
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
