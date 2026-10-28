namespace Navidex.Core.Access;

/// <summary>
/// What a caller may do. An API key has any of these; the admin, signed in or on the local network when that's
/// allowed, has them all. Admin covers the rest.
/// </summary>
public static class Scopes
{
    /// <summary>Browse the catalogue, the library, requests, listen-now tracks, tasks and events.</summary>
    public const string Read = "read";

    /// <summary>Ask for music to be fetched into the library, cancel and retry requests, and ignore albums and tracks.</summary>
    public const string Request = "request";

    /// <summary>Listen now: fetch tracks to play without adding them, stream them and your own music, and keep them.</summary>
    public const string Listen = "listen";

    /// <summary>Everything else: settings, plugins, keys, webhooks, scans, review and removing music.</summary>
    public const string Admin = "admin";

    public static readonly IReadOnlyList<string> All = [Read, Request, Listen, Admin];

    public static bool IsKnown(string scope) => All.Contains(scope);
}

/// <summary>The scope an endpoint needs, when it isn't the default: read for GET, admin for anything that changes something.</summary>
public sealed record RequiredScope(string Scope);

/// <summary>An endpoint anyone may call, signed in or not: signing in itself, health checks, and signed stream URLs.</summary>
public sealed class OpenAccess
{
    public static readonly OpenAccess Instance = new();
}

public static class AccessEndpointExtensions
{
    /// <summary>Needs this scope, rather than the default for its method.</summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredScope(scope));

    /// <summary>Open to anyone. Use only where the endpoint checks access itself, or gives nothing away.</summary>
    public static TBuilder AllowAnyone<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(OpenAccess.Instance);

    /// <summary>The scope this endpoint needs: its own, or read for GET and admin for anything else.</summary>
    public static string ScopeFor(Endpoint endpoint) => ScopeFor(endpoint.Metadata);

    /// <inheritdoc cref="ScopeFor(Endpoint)"/>
    public static string ScopeFor(IEnumerable<object> metadata)
    {
        var list = metadata as IReadOnlyList<object> ?? metadata.ToList();
        if (list.OfType<RequiredScope>().LastOrDefault() is { } required)
            return required.Scope;
        var methods = list.OfType<IHttpMethodMetadata>().LastOrDefault()?.HttpMethods ?? [];
        return methods.Count > 0 && methods.All(m => m is "GET" or "HEAD") ? Scopes.Read : Scopes.Admin;
    }
}

public enum CallerKind
{
    /// <summary>Signed in with the admin's username and password.</summary>
    Admin,

    /// <summary>On the local network, which is let in without signing in when that's allowed.</summary>
    Local,

    /// <summary>An app with an API key.</summary>
    Key,
}

/// <summary>Who's calling, and what they may do.</summary>
public sealed record Caller(CallerKind Kind, string Name, IReadOnlySet<string> Scopes, Guid? KeyId = null)
{
    public bool May(string scope) => Scopes.Contains(scope) || Scopes.Contains(Access.Scopes.Admin);

    /// <summary>The name requests are credited to.</summary>
    public string Requester => Kind == CallerKind.Key ? Name : "admin";

    public static Caller Of(HttpContext http) => http.Items[typeof(Caller)] as Caller ?? Unknown;

    private static readonly Caller Unknown = new(CallerKind.Admin, "admin", new HashSet<string>());
}
