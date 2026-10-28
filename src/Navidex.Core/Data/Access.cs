namespace Navidex.Core.Data;

/// <summary>
/// A key an app uses to call the API. Only a hash of the key is kept: it's shown once, when it's made, and can't be
/// recovered, only revoked and replaced.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>What the key is for, usually the app's name. Requests made with it are credited to this.</summary>
    public required string Name { get; set; }

    /// <summary>The start of the key, to tell keys apart in a list without showing them.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the whole key, in hex. Keys are long and random, so a plain hash is enough.</summary>
    public required string Hash { get; set; }

    /// <summary>What it may do: read, request, listen and admin, comma-separated.</summary>
    public required string Scopes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Roughly when it was last used; updated at most once a minute.</summary>
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>An address Navidex tells, with a signed POST, when something it cares about happens.</summary>
public class Webhook
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string Url { get; set; }

    /// <summary>Signs each delivery, so the receiver can tell it came from this Navidex.</summary>
    public required string Secret { get; set; }

    /// <summary>The events it gets, comma-separated; empty for all of them.</summary>
    public string Events { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum WebhookDeliveryStatus
{
    /// <summary>Not sent yet, or sent and failed with tries left.</summary>
    Pending,
    Delivered,

    /// <summary>Every try failed.</summary>
    Failed,
}

/// <summary>One event sent, or to be sent, to one webhook. Kept in the database so deliveries survive a restart.</summary>
public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WebhookId { get; set; }
    public Webhook Webhook { get; set; } = null!;

    public required string Event { get; set; }

    /// <summary>The body, exactly as sent, so retries send the same bytes and the same signature check works.</summary>
    public required string PayloadJson { get; set; }

    public WebhookDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    /// <summary>The HTTP status of the last try, if it got one.</summary>
    public int? ResponseStatus { get; set; }

    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}
