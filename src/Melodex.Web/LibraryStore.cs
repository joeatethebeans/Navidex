using Melodex.Client;

namespace Melodex.Web;

/// <summary>
/// The library's lists as last loaded, so the Library page shows them at once when it's opened again or switched
/// between views, and refreshes them behind the scenes.
/// </summary>
public sealed class LibraryStore(MelodexClient api)
{
    public IReadOnlyList<LibraryArtist>? Artists { get; private set; }
    public IReadOnlyList<LibraryAlbum>? Albums { get; private set; }
    public IReadOnlyList<LibraryTrack>? Tracks { get; private set; }

    public async Task<IReadOnlyList<LibraryArtist>> LoadArtistsAsync() => Artists = await api.GetLibraryArtistsAsync();
    public async Task<IReadOnlyList<LibraryAlbum>> LoadAlbumsAsync() => Albums = await api.GetLibraryAlbumsAsync();
    public async Task<IReadOnlyList<LibraryTrack>> LoadTracksAsync() => Tracks = await api.GetLibraryTracksAsync();

    /// <summary>Drops something removed from the library, until the lists are next loaded.</summary>
    public void Forget(LibraryItemKind kind, Guid id)
    {
        switch (kind)
        {
            case LibraryItemKind.Artist:
                Artists = Artists?.Where(a => a.Id != id).ToList();
                Albums = Albums?.Where(a => a.Album.ArtistId != id).ToList();
                Tracks = Tracks?.Where(t => t.ArtistId != id).ToList();
                break;
            case LibraryItemKind.Album:
                Albums = Albums?.Where(a => a.Album.Id != id).ToList();
                Tracks = Tracks?.Where(t => t.AlbumId != id).ToList();
                break;
            default:
                Tracks = Tracks?.Where(t => t.FileId != id).ToList();
                break;
        }
    }
}
