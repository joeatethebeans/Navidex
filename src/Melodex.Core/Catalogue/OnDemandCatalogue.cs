using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Plugins;
using Melodex.Core.Tasks;

namespace Melodex.Core.Catalogue;

/// <summary>
/// Fetches an artist's discography or an album's editions for a page that asked for them, in the background, so the
/// page can show what's already known at once and the rest as it arrives. One fetch at a time for each, however many
/// pages ask.
/// </summary>
public sealed class OnDemandCatalogue(IServiceScopeFactory scopes, ILogger<OnDemandCatalogue> logger)
{
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _fetching = [];

    /// <summary>Whether the artist's or album's fetch is under way.</summary>
    public bool IsFetching(Guid id)
    {
        lock (_lock)
            return _fetching.Contains(id);
    }

    /// <summary>Fetches the artist's discography, then queues its editions as a first visit always has.</summary>
    public void FetchDiscography(Guid artistId) => Start(artistId, async (services, ct) =>
    {
        var db = services.GetRequiredService<MelodexDbContext>();
        if (await db.Artists.FindAsync([artistId], ct) is not { } artist)
            return;
        await services.GetRequiredService<CatalogueService>().RefreshDiscographyAsync(artist, false, ct);
        await services.GetRequiredService<TaskQueue>().EnqueueAsync(ArtistRefreshTask.TaskKind, ArtistRefreshTask.Payload(artistId), ct);
    });

    /// <summary>Fetches the album's editions, if they're unknown or old.</summary>
    public void FetchEditions(Guid albumId) => Start(albumId, async (services, ct) =>
    {
        var db = services.GetRequiredService<MelodexDbContext>();
        if (await db.Albums.FindAsync([albumId], ct) is not { } album)
            return;
        await services.GetRequiredService<CatalogueService>().RefreshEditionsAsync(album, false, ct);
    });

    private void Start(Guid id, Func<IServiceProvider, CancellationToken, Task> fetch)
    {
        lock (_lock)
        {
            if (!_fetching.Add(id))
                return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // Someone's looking at the page: it goes ahead of catalogue updates for the source's rate limit.
                PluginHttp.Interactive.Value = true;
                using var scope = scopes.CreateScope();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await fetch(scope.ServiceProvider, timeout.Token);
            }
            catch (Exception e)
            {
                // The page shows what's known, and the next visit tries again.
                logger.LogWarning(e, "Fetching the catalogue for {Id} failed", id);
            }
            finally
            {
                lock (_lock)
                    _fetching.Remove(id);
            }
        });
    }
}
