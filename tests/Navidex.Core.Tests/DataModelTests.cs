using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Navidex.Contracts.Catalogue;
using Navidex.Core.Data;

namespace Navidex.Core.Tests;

public sealed class DataModelTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly NavidexDbContext _db;

    public DataModelTests()
    {
        _connection.Open();
        _db = new NavidexDbContext(new DbContextOptionsBuilder<NavidexDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Model_has_no_pending_changes_against_the_migrations()
    {
        Assert.False(_db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task A_recording_can_appear_on_several_editions()
    {
        var artist = new Artist { Name = "Artist" };
        var album = new Album { Artist = artist, Title = "Album", ArtistCredit = "Artist", Type = AlbumType.Album };
        var track = new Track { Artist = artist, Title = "Song", ArtistCredit = "Artist" };
        var standard = new Edition { Album = album, Title = "Album" };
        var deluxe = new Edition { Album = album, Title = "Album (Deluxe)" };
        standard.Tracks.Add(new EditionTrack { Track = track, Disc = 1, Position = 1, Number = "1", Title = "Song" });
        deluxe.Tracks.Add(new EditionTrack { Track = track, Disc = 1, Position = 3, Number = "3", Title = "Song" });
        _db.AddRange(standard, deluxe);
        await _db.SaveChangesAsync();

        _db.ChangeTracker.Clear();
        var loaded = await _db.Tracks.Include(t => t.Appearances).SingleAsync();
        Assert.Equal(2, loaded.Appearances.Count);
    }

    [Fact]
    public async Task A_provider_id_maps_to_one_entity_per_kind()
    {
        const string mbid = "b5d7d380-f43a-4c1f-a5de-694150b093ac";
        _db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = Guid.CreateVersion7(), Provider = Providers.MusicBrainz, Value = mbid });
        // The same value may identify an entity of another kind...
        _db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Edition, EntityId = Guid.CreateVersion7(), Provider = Providers.MusicBrainz, Value = mbid });
        await _db.SaveChangesAsync();

        // ...but not a second entity of the same kind.
        _db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = Guid.CreateVersion7(), Provider = Providers.MusicBrainz, Value = mbid });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task An_isrc_can_identify_several_tracks()
    {
        // One ISRC is often shared by distinct MusicBrainz recordings of the same master, such as a single and album version.
        _db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = Guid.CreateVersion7(), Provider = Providers.Isrc, Value = "GBAYE0601498" });
        _db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = Guid.CreateVersion7(), Provider = Providers.Isrc, Value = "GBAYE0601498" });
        await _db.SaveChangesAsync();

        Assert.Equal(2, await _db.Crosswalk.CountAsync(c => c.Provider == Providers.Isrc));
    }

    [Fact]
    public async Task Enums_are_stored_by_name()
    {
        _db.Requests.Add(new Request { TargetKind = EntityKind.Album, TargetId = Guid.CreateVersion7(), Mode = RequestMode.ListenNow, Requester = "admin" });
        await _db.SaveChangesAsync();

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Mode FROM Requests";
        Assert.Equal("ListenNow", command.ExecuteScalar());
    }
}
