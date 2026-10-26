using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Navidex.Core.Data;
using Navidex.Core.Library;
using System.Runtime.Versioning;
using Navidex.Core.Tests.Support;

namespace Navidex.Core.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class LibraryScannerTests : IDisposable
{
    private readonly string _music = Path.Combine(Path.GetTempPath(), "navidex-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly NavidexDbContext _db;
    private readonly LibraryScanner _scanner;

    public LibraryScannerTests()
    {
        Directory.CreateDirectory(_music);
        _connection.Open();
        _db = new NavidexDbContext(new DbContextOptionsBuilder<NavidexDbContext>().UseSqlite(_connection, NavidexDbContext.Sqlite).Options);
        _db.Database.Migrate();
        _scanner = new LibraryScanner(_db, Options.Create(new NavidexOptions { LibraryPath = _music }), new ScanFeed(), NullLogger<LibraryScanner>.Instance);
    }

    public void Dispose()
    {
        // Unlock the root before listing it, then everything under it, so the folder can be deleted.
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(_music, owner);
        foreach (var dir in Directory.EnumerateDirectories(_music, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            File.SetUnixFileMode(dir, owner);
        Directory.Delete(_music, recursive: true);
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task An_empty_music_folder_fails_and_says_to_check_the_path()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ScanAsync(null, default));
        Assert.Contains("is empty", error.Message);
        Assert.Contains("MUSIC_PATH", error.Message);
    }

    [Fact]
    public async Task A_folder_with_no_audio_fails_and_says_so()
    {
        File.WriteAllText(Path.Combine(_music, "notes.txt"), "");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ScanAsync(null, default));
        Assert.Contains("no audio files", error.Message);
    }

    [NotRootFact]
    public async Task An_unreadable_music_folder_fails_and_says_to_fix_permissions()
    {
        Directory.CreateDirectory(Path.Combine(_music, "Artist"));
        File.SetUnixFileMode(_music, UnixFileMode.UserExecute);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ScanAsync(null, default));
        Assert.Contains("can't read", error.Message);
    }

    [NotRootFact]
    public async Task An_unreadable_file_is_skipped_and_counted_rather_than_failing_the_scan()
    {
        var locked = Path.Combine(_music, "Locked Song.flac");
        File.WriteAllText(locked, "");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var result = await _scanner.ScanAsync(null, default);

        Assert.Equal(1, result.Unreadable);
        Assert.Equal(0, await _db.LibraryFiles.CountAsync());
    }

    [Fact]
    public async Task An_empty_scan_never_removes_known_files()
    {
        _db.LibraryFiles.Add(new LibraryFile { Path = "Artist/Album/01 Song.flac", Format = "flac", TagsJson = "{}" });
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ScanAsync(null, default));
        Assert.Equal(1, await _db.LibraryFiles.CountAsync());
    }

    [TestLibraryFact]
    public async Task Files_in_an_unreadable_folder_are_kept_and_the_folder_is_counted()
    {
        if (Environment.UserName == "root")
            return; // root reads everything, so there's nothing to test.
        var song = Directory.EnumerateFiles(TestLibrary.Music, "*.flac", SearchOption.AllDirectories).First();
        Directory.CreateDirectory(Path.Combine(_music, "Readable"));
        File.Copy(song, Path.Combine(_music, "Readable", "01 Song.flac"));
        _db.LibraryFiles.Add(new LibraryFile { Path = "Locked/01 Song.flac", Format = "flac", TagsJson = "{}" });
        await _db.SaveChangesAsync();
        var locked = Directory.CreateDirectory(Path.Combine(_music, "Locked")).FullName;
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var result = await _scanner.ScanAsync(null, default);

        Assert.Equal(1, result.SkippedFolders);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, await _db.LibraryFiles.CountAsync());
    }
}

/// <summary>A permissions test, skipped as root, which can read anything.</summary>
public sealed class NotRootFactAttribute : FactAttribute
{
    public NotRootFactAttribute()
    {
        if (Environment.UserName == "root")
            Skip = "Running as root, which ignores file permissions.";
    }
}
