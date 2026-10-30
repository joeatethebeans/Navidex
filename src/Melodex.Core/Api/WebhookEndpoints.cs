using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Events;

namespace Melodex.Core.Api;

public static class WebhookEndpoints
{
    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder api)
    {
        // Webhooks carry their secrets, so even listing them is for the admin.
        var webhooks = api.MapGroup("/webhooks").WithTags("Webhooks").RequireScope(Scopes.Keys);

        webhooks.MapGet("", async (MelodexDbContext db, CancellationToken ct) =>
            {
                var all = await db.Webhooks.AsNoTracking().OrderBy(w => w.CreatedAt).ToListAsync(ct);
                var ids = all.Select(w => w.Id).ToList();
                var last = await db.WebhookDeliveries.AsNoTracking().Where(d => ids.Contains(d.WebhookId))
                    .GroupBy(d => d.WebhookId).Select(g => g.OrderByDescending(d => d.CreatedAt).First()).ToListAsync(ct);
                return all.Select(w => Info(w, last.FirstOrDefault(d => d.WebhookId == w.Id))).ToList();
            })
            .WithName("ListWebhooks").WithSummary("Addresses Melodex sends events to, with their secrets and how the last delivery went.");

        webhooks.MapGet("/events", () => EventTypes.All)
            .WithName("ListEventTypes").WithSummary("The event types a webhook can choose from.");

        webhooks.MapPost("", async (SaveWebhook body, MelodexDbContext db, CancellationToken ct) =>
            {
                if (Invalid(body) is { } invalid)
                    return invalid;
                var webhook = new Webhook { Name = body.Name.Trim(), Url = body.Url.Trim(), Secret = NewSecret(), Events = Events(body), Enabled = body.Enabled };
                db.Webhooks.Add(webhook);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/v1/webhooks/{webhook.Id}", Info(webhook, null));
            })
            .WithName("CreateWebhook").WithSummary("Send events to an address. Melodex makes the secret that signs them.")
            .Produces<WebhookInfo>(StatusCodes.Status201Created).ProducesValidationProblem();

        webhooks.MapPut("/{id:guid}", async (Guid id, SaveWebhook body, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await db.Webhooks.FindAsync([id], ct) is not { } webhook)
                    return Results.NotFound();
                if (Invalid(body) is { } invalid)
                    return invalid;
                webhook.Name = body.Name.Trim();
                webhook.Url = body.Url.Trim();
                webhook.Events = Events(body);
                webhook.Enabled = body.Enabled;
                await db.SaveChangesAsync(ct);
                return Results.Ok(Info(webhook, null));
            })
            .WithName("UpdateWebhook").WithSummary("Change where a webhook sends, which events, or turn it off.")
            .Produces<WebhookInfo>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        webhooks.MapPost("/{id:guid}/secret", async (Guid id, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await db.Webhooks.FindAsync([id], ct) is not { } webhook)
                    return Results.NotFound();
                webhook.Secret = NewSecret();
                await db.SaveChangesAsync(ct);
                return Results.Ok(Info(webhook, null));
            })
            .WithName("RotateWebhookSecret").WithSummary("Make a new secret. Deliveries from now on, retries included, are signed with it.")
            .Produces<WebhookInfo>().Produces(StatusCodes.Status404NotFound);

        webhooks.MapDelete("/{id:guid}", async (Guid id, MelodexDbContext db, CancellationToken ct) =>
                await db.Webhooks.Where(w => w.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent())
            .WithName("RemoveWebhook").WithSummary("Stop sending events there. Deliveries not yet sent are dropped.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        webhooks.MapPost("/{id:guid}/test", async (Guid id, MelodexDbContext db, WebhookSender sender, CancellationToken ct) =>
            {
                if (await db.Webhooks.FindAsync([id], ct) is not { } webhook)
                    return Results.NotFound();
                var delivery = EventAnnouncer.Delivery(webhook,
                    EventAnnouncer.New(EventTypes.Ping, DateTime.UtcNow, new PingEventData("Melodex can send events here.")));
                db.WebhookDeliveries.Add(delivery);
                await sender.DeliverAsync(delivery, ct);
                await db.SaveChangesAsync(ct);
                return Results.Ok(Info(delivery));
            })
            .WithName("TestWebhook").WithSummary("Send a ping event now, once, and say how it went.")
            .Produces<WebhookDeliveryInfo>().Produces(StatusCodes.Status404NotFound);

        webhooks.MapGet("/{id:guid}/deliveries", async (Guid id, MelodexDbContext db, int? limit, CancellationToken ct) =>
                (await db.WebhookDeliveries.AsNoTracking().Where(d => d.WebhookId == id).OrderByDescending(d => d.CreatedAt)
                    .Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct)).Select(Info).ToList())
            .WithName("ListWebhookDeliveries").WithSummary("Recent deliveries to a webhook, newest first. Finished ones are kept for a week.");

        return api;
    }

    private static WebhookInfo Info(Webhook w, WebhookDelivery? last) =>
        new(w.Id, w.Name, w.Url, w.Secret, w.Events.Split(',', StringSplitOptions.RemoveEmptyEntries), w.Enabled, w.CreatedAt, last is null ? null : Info(last));

    private static WebhookDeliveryInfo Info(WebhookDelivery d) =>
        new(d.Id, d.Event, (WebhookDeliveryState)(int)d.Status, d.Attempts, d.ResponseStatus, d.Error, d.CreatedAt, d.FinishedAt,
            d.Status == WebhookDeliveryStatus.Pending ? d.NextAttemptAt : null);

    private static string Events(SaveWebhook body) => string.Join(",", (body.Events ?? []).Distinct());

    private static string NewSecret() =>
        "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');

    private static IResult? Invalid(SaveWebhook body)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(body.Name))
            errors["name"] = ["Give the webhook a name."];
        if (!Uri.TryCreate(body.Url?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            errors["url"] = ["Use a full http:// or https:// address."];
        if (body.Events?.FirstOrDefault(e => !EventTypes.All.Contains(e)) is { } unknown)
            errors["events"] = [$"There's no {unknown} event. Choose from {string.Join(", ", EventTypes.All)}."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
