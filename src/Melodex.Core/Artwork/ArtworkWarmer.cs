using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Artwork;

/// <summary>
/// Fetches pictures for what's in the library before anyone looks: the cover of every edition you have and a picture
/// of every library artist. It runs whenever library data changes, so covers arrive while a scan is still matching
/// files, and the library page finds them already cached.
/// </summary>
public sealed class ArtworkWarmer(IServiceScopeFactory scopes, ArtworkCache cache, LibraryCache library, ILogger<ArtworkWarmer> logger)
    : BackgroundService
{
    /// <summary>Pictures looked for at once. Each plugin keeps to its own request rate regardless.</summary>
    private const int Parallel = 4;

    /// <summary>How long to let a burst of writes settle before looking for new work.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        library.Changed += Wake;
        cache.Reset += Wake;
        try
        {
            Wake();
            while (await _wake.Reader.WaitToReadAsync(stoppingToken))
            {
                _wake.Reader.TryRead(out _);
                await Task.Delay(Settle, stoppingToken);
                try
                {
                    await WarmAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning("Couldn't fetch artwork ahead of time: {Message}", e.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            library.Changed -= Wake;
            cache.Reset -= Wake;
        }
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    /// <summary>Fetches every picture the library lacks, once each per pass: one a plugin couldn't reach is left for the next.</summary>
    private async Task WarmAsync(CancellationToken ct)
    {
        var tried = new HashSet<(EntityKind, Guid)>();
        while (true)
        {
            List<(EntityKind Kind, Guid Id)> batch;
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
                var editions = await db.LibraryFiles.Where(f => f.EditionId != null).Select(f => f.EditionId!.Value).Distinct()
                    .Where(e => !db.Artwork.Any(a => a.EntityKind == EntityKind.Edition && a.EntityId == e)).ToListAsync(ct);
                var artists = await db.Artists.Where(a => a.InLibrary)
                    .Where(a => !db.Artwork.Any(w => w.EntityKind == EntityKind.Artist && w.EntityId == a.Id)).Select(a => a.Id).ToListAsync(ct);
                batch = [.. editions.Select(e => (EntityKind.Edition, e)).Concat(artists.Select(a => (EntityKind.Artist, a))).Where(tried.Add)];
            }
            if (batch.Count == 0)
                return;
            logger.LogDebug("Fetching {Count} pictures ahead of time", batch.Count);
            await System.Threading.Tasks.Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct },
                async (item, c) => await cache.GetAsync(item.Kind, item.Id, c));
        }
    }
}
