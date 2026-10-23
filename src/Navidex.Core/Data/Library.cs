using Navidex.Contracts.Plugins;

namespace Navidex.Core.Data;

// Library and work entities, filled by the core. Owned and missing are never stored as truth: they are
// computed by comparing catalogue entities with library files through the crosswalk.

/// <summary>How a library file was matched to the catalogue, in the order matching tries them.</summary>
public enum MatchMethod
{
    Unmatched,
    MusicBrainzTags,
    Crosswalk,
    IsrcOrUpc,
    Fuzzy,
    Manual,
}

public class LibraryFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Path relative to the library root.</summary>
    public required string Path { get; set; }

    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string? Format { get; set; }
    public TimeSpan? Duration { get; set; }

    /// <summary>Tags as read from the file, as a JSON object of tag name to value.</summary>
    public string TagsJson { get; set; } = "{}";

    public Guid? TrackId { get; set; }
    public Track? Track { get; set; }
    public Guid? EditionId { get; set; }
    public Edition? Edition { get; set; }
    public MatchMethod MatchMethod { get; set; }

    /// <summary>From 0 to 1; anything under the threshold goes to the review list.</summary>
    public double? MatchConfidence { get; set; }

    public string? MediaServerTrackId { get; set; }
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
}

public enum RequestMode
{
    Permanent,
    ListenNow,
}

public enum RequestStatus
{
    Queued,
    Searching,
    Fetching,
    Verifying,
    Importing,
    Notifying,
    InLibrary,
    NeedsReview,
    Failed,
    Cancelled,
}

/// <summary>Something a user or app asked for.</summary>
public class Request
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Artist, album or track; editions are never requested directly.</summary>
    public EntityKind TargetKind { get; set; }

    public Guid TargetId { get; set; }
    public RequestMode Mode { get; set; }

    /// <summary>The admin or the API key's name.</summary>
    public required string Requester { get; set; }

    public RequestStatus Status { get; set; }

    /// <summary>For artist and album requests, the request that fanned out to this one.</summary>
    public Guid? ParentRequestId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Job> Jobs { get; set; } = [];
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public enum VerificationResult
{
    NotRun,
    Passed,
    DurationMismatch,
    FingerprintMismatch,
}

/// <summary>One attempt to fulfil a request through one retrieval plugin.</summary>
public class Job
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RequestId { get; set; }
    public Request Request { get; set; } = null!;
    public Guid? PluginInstanceId { get; set; }
    public PluginInstance? PluginInstance { get; set; }

    /// <summary>1 for the first plugin in the chain, 2 for the first fallback, and so on.</summary>
    public int Attempt { get; set; } = 1;

    public JobStatus Status { get; set; }

    /// <summary>The chosen retrieval candidate, as JSON.</summary>
    public string? CandidateJson { get; set; }

    public VerificationResult Verification { get; set; }
    public string? VerificationDetail { get; set; }
    public string Log { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>The listen-now lifecycle. Library actions unlock only at <see cref="InLibrary"/>.</summary>
public enum ListenNowState
{
    Requested,
    Fetching,
    Playable,
    Importing,
    InLibrary,
    Failed,
    Expired,
}

/// <summary>A temporary file held by the service, outside every media-server library.</summary>
public class ListenNowItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RequestId { get; set; }
    public Request Request { get; set; } = null!;
    public Guid TrackId { get; set; }
    public Track Track { get; set; } = null!;
    public ListenNowState State { get; set; }

    /// <summary>Path relative to the listen-now store.</summary>
    public string? FilePath { get; set; }

    public string? Codec { get; set; }
    public string? Container { get; set; }
    public int? BitrateKbps { get; set; }
    public DateTime? LastPlayedAt { get; set; }

    /// <summary>One hour after the last play; each play pushes it back.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A configured plugin.</summary>
public class PluginInstance
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The manifest ID, for example <c>navidex.index.musicbrainz</c>.</summary>
    public required string PluginId { get; set; }

    public PluginType Type { get; set; }

    /// <summary>User-facing name, for example <c>YouTube (opus)</c>.</summary>
    public required string Name { get; set; }

    /// <summary>The contract version the plugin declared when it was set up.</summary>
    public required string ContractVersion { get; set; }

    /// <summary>Settings as JSON, validated against the manifest's schema. Includes the output format for retrieval plugins.</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>For retrieval plugins, the base URL of the plugin's container.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Lower runs first: fallback order for retrieval plugins, field priority for index plugins.</summary>
    public int Priority { get; set; }

    public bool Enabled { get; set; } = true;
    public HealthStatus? Health { get; set; }
    public string? HealthMessage { get; set; }
    public DateTime? HealthCheckedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
