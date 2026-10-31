using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Artwork;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Choosing an album's or song's cover, written into its files by the fake beets.</summary>
[Collection(RequestCollection.Name)]
public sealed class CoverTests(RequestFixture fixture)
{
    // The smallest PNG there is: one transparent pixel.
    private static readonly byte[] Pixel = Convert.FromHexString(
        "89504e470d0a1a0a0000000d4948445200000001000000010806000000" + "1f15c4890000000d49444154789c6300010000050001" + "0d0a2db40000000049454e44ae426082");

    private MelodexClient Api => fixture.Api;

    private async Task<AlbumDetail> OwnedAlbumAsync(string title)
    {
        var album = (await Api.GetLibraryAlbumsAsync()).First(a => a.Album.Title == title).Album;
        return (await Api.GetAlbumAsync(album.Id))!;
    }

    [TestLibraryFact]
    public async Task An_albums_chosen_cover_goes_into_its_files_and_folder_and_is_shown_from_then_on()
    {
        var album = await OwnedAlbumAsync("Discovery");
        var choices = await Api.GetCoverChoicesAsync(CoverSubject.Album, album.Album.Id);
        Assert.Equal("Discovery", choices.Title);
        Assert.True(choices.Files > 0);
        // One for the album, not one for each of its editions.
        Assert.Single(choices.Options, o => o.Source == $"album:{album.Album.Id}" && o.Title == "Album");
        Assert.DoesNotContain(choices.Options, o => o.Source.StartsWith("edition:"));

        var chosen = await Api.ChooseCoverAsync(CoverSubject.Album, album.Album.Id, new ChooseCover(Upload: Convert.ToBase64String(Pixel)));
        Assert.Equal(choices.Files, chosen.Written);
        Assert.Empty(chosen.Failed);
        Assert.Contains("v=", chosen.CoverUrl);

        // Embedded in every file, and the folder's image, with no tags touched.
        var sent = fixture.Importer.Formats.Last();
        Assert.Equal("cover", sent.FolderArtwork);
        Assert.Null(sent.PathTemplate);
        Assert.All(sent.Files, f => Assert.Equal(("cover", 0), (f.Artwork, f.Tags.Count)));
        var folder = Path.GetDirectoryName(Path.Combine(fixture.Music, sent.Files[0].Path))!;
        Assert.Equal(Pixel, await File.ReadAllBytesAsync(Path.Combine(folder, "cover.jpg")));

        // Shown for the album and the edition you have, and kept when the artwork is fetched afresh.
        using (var scope = fixture.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArtworkCache>().ResetAsync(default);
        var http = fixture.Factory.CreateSignedInClient();
        Assert.Equal(Pixel, await http.GetByteArrayAsync($"api/v1/artwork/album/{album.Album.Id}"));
        var edition = (await OwnedAlbumAsync("Discovery")).OwnedEditionId;
        Assert.Equal(Pixel, await http.GetByteArrayAsync($"api/v1/artwork/edition/{edition}"));
    }

    [TestLibraryFact]
    public async Task A_songs_chosen_cover_is_its_own()
    {
        var album = await OwnedAlbumAsync("Homework");
        var owned = album.Tracks.First(t => t.Status == TrackStatus.Owned);
        var choices = await Api.GetCoverChoicesAsync(CoverSubject.Track, owned.TrackId);
        Assert.Contains(choices.Options, o => o.Source == $"album:{album.Album.Id}");

        var chosen = await Api.ChooseCoverAsync(CoverSubject.Track, owned.TrackId, new ChooseCover(Upload: Convert.ToBase64String(Pixel)));
        Assert.True(chosen.Written > 0);
        Assert.Null(fixture.Importer.Formats.Last().FolderArtwork);
        Assert.Equal($"api/v1/artwork/track/{owned.TrackId}", (await Api.GetTrackAsync(owned.TrackId))!.CoverUrl);
        Assert.StartsWith($"api/v1/artwork/track/{owned.TrackId}?v=", chosen.CoverUrl);
    }

    [TestLibraryFact]
    public async Task Pictures_that_arent_and_sources_that_dont_exist_are_refused()
    {
        var album = await OwnedAlbumAsync("Discovery");
        foreach (var bad in new[] { new ChooseCover(Upload: Convert.ToBase64String("not a picture"u8.ToArray())), new ChooseCover(Upload: "!!"),
                     new ChooseCover("artist:" + album.Album.ArtistId), new ChooseCover("file:" + Guid.NewGuid()), new ChooseCover() })
        {
            var refused = await Assert.ThrowsAsync<MelodexApiException>(() => Api.ChooseCoverAsync(CoverSubject.Album, album.Album.Id, bad));
            Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        }
        var missing = await Assert.ThrowsAsync<MelodexApiException>(() =>
            Api.ChooseCoverAsync(CoverSubject.Track, Guid.NewGuid(), new ChooseCover(Upload: Convert.ToBase64String(Pixel))));
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
    }
}
