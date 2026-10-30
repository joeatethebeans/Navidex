using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;

namespace Melodex.Core.Events;

/// <summary>
/// Sends webhook deliveries: a signed POST of the event's JSON. One that fails is tried again later, a few times over
/// several hours, as the receiver may only be down for a while. Deliveries are rows, so they survive a restart.
/// </summary>
public sealed class WebhookSender(IServiceScopeFactory scopes, IHttpClientFactory clients, ILogger<WebhookSender> log) : BackgroundService
{
    public const string HttpClientName = "webhooks";
    public const string SignatureHeader = "Melodex-Signature";
    public const string EventHeader = "Melodex-Event";
    public const string DeliveryHeader = "Melodex-Delivery";

    /// <summary>How long to wait before each retry. A delivery is tried once, then once after each of these.</summary>
    public static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6),
    ];

    /// <summary>Finished deliveries are kept this long, to show on the Webhooks settings.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    private static readonly string UserAgent =
        $"Melodex/{typeof(WebhookSender).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1"}";

    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>There are new deliveries to send.</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    /// <summary>
    /// The signature a receiver checks: hex HMAC-SHA256, keyed with the webhook's secret, of the timestamp, a dot and
    /// the body exactly as sent. The header is <c>t=timestamp,v1=signature</c>.
    /// </summary>
    public static string Sign(string secret, long timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pruned = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = TimeSpan.FromMinutes(1);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
                var now = DateTime.UtcNow;
                var due = await db.WebhookDeliveries.Include(d => d.Webhook)
                    .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
                    .OrderBy(d => d.NextAttemptAt).Take(20).ToListAsync(stoppingToken);
                foreach (var delivery in due)
                    await DeliverAsync(delivery, stoppingToken);
                await db.SaveChangesAsync(stoppingToken);

                if (due.Count == 20)
                    wait = TimeSpan.Zero;
                else if (await db.WebhookDeliveries.Where(d => d.Status == WebhookDeliveryStatus.Pending)
                             .OrderBy(d => d.NextAttemptAt).Select(d => (DateTime?)d.NextAttemptAt).FirstOrDefaultAsync(stoppingToken) is { } next)
                    wait = TimeSpan.FromTicks(Math.Clamp((next - DateTime.UtcNow).Ticks, 0, wait.Ticks));

                if (now - pruned > TimeSpan.FromHours(1))
                {
                    var before = now - KeepFor;
                    await db.WebhookDeliveries.Where(d => d.Status != WebhookDeliveryStatus.Pending && d.CreatedAt < before)
                        .ExecuteDeleteAsync(stoppingToken);
                    pruned = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogError(e, "Couldn't send webhook deliveries");
            }

            if (wait > TimeSpan.Zero)
                await _wake.WaitAsync(wait, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>Tries one delivery and records how it went, on the tracked row. The caller saves.</summary>
    public async Task DeliverAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        var webhook = delivery.Webhook;
        if (!webhook.Enabled && delivery.Event != EventTypes.Ping)
        {
            Finish(delivery, WebhookDeliveryStatus.Failed, "The webhook was turned off before this was sent.");
            return;
        }

        delivery.Attempts++;
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url);
            request.Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Add(EventHeader, delivery.Event);
            request.Headers.Add(DeliveryHeader, delivery.Id.ToString());
            request.Headers.Add(SignatureHeader, $"t={timestamp},v1={Sign(webhook.Secret, timestamp, delivery.PayloadJson)}");
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, ct);
            delivery.ResponseStatus = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                Finish(delivery, WebhookDeliveryStatus.Delivered, null);
                return;
            }
            Retry(delivery, $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            delivery.ResponseStatus = null;
            Retry(delivery, e is TaskCanceledException ? "No answer in time." : e.Message);
        }
    }

    private void Retry(WebhookDelivery delivery, string error)
    {
        if (delivery.Attempts > Backoff.Length || delivery.Event == EventTypes.Ping)
        {
            Finish(delivery, WebhookDeliveryStatus.Failed, error);
            log.LogWarning("Webhook {Name} didn't take {Event}: {Error}", delivery.Webhook.Name, delivery.Event, error);
            return;
        }
        delivery.Error = error;
        delivery.NextAttemptAt = DateTime.UtcNow + Backoff[delivery.Attempts - 1];
    }

    private static void Finish(WebhookDelivery delivery, WebhookDeliveryStatus status, string? error)
    {
        delivery.Status = status;
        delivery.Error = error;
        delivery.FinishedAt = DateTime.UtcNow;
    }
}
