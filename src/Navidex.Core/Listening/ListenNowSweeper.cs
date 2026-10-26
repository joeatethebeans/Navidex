namespace Navidex.Core.Listening;

/// <summary>Runs <see cref="ListenNowService.SweepAsync"/> every minute, so expired listen-now files don't linger.</summary>
public sealed class ListenNowSweeper(IServiceScopeFactory scopes, ILogger<ListenNowSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ListenNowService>().SweepAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't tidy the listen-now store: {Message}", e.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
