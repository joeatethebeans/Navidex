using System.Text.Json;

namespace Navidex.Client;

/// <summary>
/// The events Navidex announces, to webhooks and as named events on <c>GET /events</c>. Each carries a
/// <see cref="NavidexEvent"/> whose <see cref="NavidexEvent.Data"/> is the record named here.
/// </summary>
public static class EventTypes
{
    /// <summary>A request is in the library. Data: <see cref="RequestEventData"/>.</summary>
    public const string RequestCompleted = "request.completed";

    /// <summary>A request failed: nothing could be fetched, or importing it failed. Data: <see cref="RequestEventData"/>.</summary>
    public const string RequestFailed = "request.failed";

    /// <summary>Every retrieval plugin was tried and none gave a file that passed checks. Data: <see cref="RequestEventData"/>.</summary>
    public const string RequestNeedsReview = "request.needs_review";

    /// <summary>A listen-now track is ready to play. Data: <see cref="ListenEventData"/>.</summary>
    public const string ListenReady = "listen.ready";

    /// <summary>A library scan finished. Data: <see cref="ScanEventData"/>.</summary>
    public const string ScanCompleted = "scan.completed";

    /// <summary>A library scan failed. Data: <see cref="ScanEventData"/>.</summary>
    public const string ScanFailed = "scan.failed";

    /// <summary>Sent by <c>POST /webhooks/{id}/test</c> only. Data: <see cref="PingEventData"/>.</summary>
    public const string Ping = "ping";

    public static IReadOnlyList<string> All { get; } = [RequestCompleted, RequestFailed, RequestNeedsReview, ListenReady, ScanCompleted, ScanFailed];
}

/// <summary>Something that happened, as sent to webhooks and on the event stream.</summary>
/// <param name="Id">Unique to this event. A webhook retried after a failure has the same ID, so receivers can ignore repeats.</param>
/// <param name="Type">One of <see cref="EventTypes"/>.</param>
/// <param name="Data">Depends on the type; see <see cref="EventTypes"/>.</param>
public sealed record NavidexEvent(Guid Id, string Type, DateTime OccurredAt, JsonElement Data)
{
    public T? DataAs<T>() => Data.Deserialize<T>(NavidexJson.Options);
}

/// <summary>A request that finished, failed or needs review. Only requests asked for directly are announced, not their parts.</summary>
/// <param name="Requester">Who asked: <c>admin</c>, or the name of the API key.</param>
public sealed record RequestEventData(RequestInfo Request, string Requester);

/// <summary>A listen-now track that's ready, with its stream URL.</summary>
public sealed record ListenEventData(ListenNowInfo Listen, string Requester);

public sealed record ScanEventData(TaskInfo Task);

public sealed record PingEventData(string Message);

// ---- Webhooks ----

/// <summary>An address Navidex sends events to.</summary>
/// <param name="Secret">Signs each delivery; see the client guide for checking the <c>Navidex-Signature</c> header.</param>
/// <param name="Events">The event types it gets; empty for all of them.</param>
/// <param name="LastDelivery">The most recent delivery, to show whether it's working.</param>
public sealed record WebhookInfo(
    Guid Id,
    string Name,
    string Url,
    string Secret,
    IReadOnlyList<string> Events,
    bool Enabled,
    DateTime CreatedAt,
    WebhookDeliveryInfo? LastDelivery);

/// <summary>Body of <c>POST /webhooks</c> and <c>PUT /webhooks/{id}</c>.</summary>
/// <param name="Events">Event types to send; empty or null for all of them, including ones added later.</param>
public sealed record SaveWebhook(string Name, string Url, IReadOnlyList<string>? Events = null, bool Enabled = true);

public enum WebhookDeliveryState
{
    /// <summary>Not sent yet, or failed with tries left; see <see cref="WebhookDeliveryInfo.NextAttemptAt"/>.</summary>
    Pending,
    Delivered,

    /// <summary>Every try failed.</summary>
    Failed,
}

/// <param name="ResponseStatus">The HTTP status the last try got, if it got one.</param>
public sealed record WebhookDeliveryInfo(
    Guid Id,
    string Event,
    WebhookDeliveryState State,
    int Attempts,
    int? ResponseStatus,
    string? Error,
    DateTime CreatedAt,
    DateTime? FinishedAt,
    DateTime? NextAttemptAt);
