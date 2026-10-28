namespace Navidex.Client;

/// <summary>Answer to <c>GET /auth/status</c>: whether there's a login yet, and whether this caller is let in.</summary>
/// <param name="SetUp">An admin login has been made. Until then the web app asks for one, and the API refuses everything else.</param>
/// <param name="SignedIn">This browser has a session.</param>
/// <param name="LetIn">This caller may use the API: signed in, or on the local network when that's allowed.</param>
/// <param name="Username">The admin's username, for callers that are let in.</param>
/// <param name="SkipOnLocalNetwork">The local network is let in without signing in. Only told to callers that are let in.</param>
/// <param name="Local">This call came from the local network, as far as Navidex can tell.</param>
public sealed record AuthStatus(bool SetUp, bool SignedIn, bool LetIn, string? Username, bool? SkipOnLocalNetwork, bool Local);

/// <summary>Body of <c>POST /auth/setup</c>: the first admin login.</summary>
public sealed record SetUpLogin(string Username, string Password, bool SkipOnLocalNetwork = false);

/// <summary>Body of <c>POST /auth/login</c>.</summary>
public sealed record SignIn(string Username, string Password);

/// <summary>Body of <c>PUT /auth/account</c>: a new username or password, or both. The current password is always needed.</summary>
public sealed record UpdateLogin(string CurrentPassword, string? Username = null, string? NewPassword = null);

/// <summary>Body of <c>PUT /auth/settings</c>.</summary>
public sealed record AccessSettings(bool SkipOnLocalNetwork);

/// <summary>An app's API key, as listed: never the key itself, which is shown only when it's made.</summary>
/// <param name="Prefix">The start of the key, to tell keys apart.</param>
/// <param name="Scopes">Any of read, request, listen and admin.</param>
public sealed record ApiKeyInfo(Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>Body of <c>POST /keys</c>.</summary>
public sealed record CreateApiKey(string Name, IReadOnlyList<string> Scopes);

/// <summary>A key just made. <paramref name="Key"/> is shown this once: Navidex keeps only a hash of it.</summary>
public sealed record CreatedApiKey(ApiKeyInfo Info, string Key);
