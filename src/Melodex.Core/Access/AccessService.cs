using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;

namespace Melodex.Core.Access;

/// <summary>The admin login from before there were users, kept in the settings table. Read once, to make the admin user.</summary>
public sealed record AdminLogin
{
    public string Username { get; init; } = "";
    public string PasswordHash { get; init; } = "";
    public string Stamp { get; init; } = "";
}

/// <summary>
/// Who may call the API: users signed in with a session cookie, and apps with API keys, each key owned by a user and
/// never able to do more than they may. Users and keys are cached in memory, as they're checked on every call.
/// </summary>
public sealed class AccessService(IServiceScopeFactory scopes, ILogger<AccessService> log)
{
    public const string LoginKey = "auth.login";
    public const string KeyHeader = "X-Api-Key";
    public const string KeyQuery = "apikey";
    public const string StampClaim = "melodex:stamp";

    /// <summary>Keys start with this, so they're easy to spot, in a config file or a leak.</summary>
    public const string KeyPrefix = "mdx_";

    private const int Iterations = 210_000;
    private static readonly TimeSpan LastUsedEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private const int FailuresAllowed = 5;

    private readonly SemaphoreSlim _loading = new(1, 1);
    private Dictionary<Guid, User>? _users;
    private Dictionary<string, ApiKey>? _keys;
    private readonly ConcurrentDictionary<Guid, DateTime> _lastUsed = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _failures = new();

    // ---- Users ----

    /// <summary>Every user, by ID. The first call makes the admin user from a login kept from before there were users.</summary>
    public async Task<IReadOnlyDictionary<Guid, User>> UsersAsync(CancellationToken ct)
    {
        if (_users is { } users)
            return users;
        await _loading.WaitAsync(ct);
        try
        {
            if (_users is not null)
                return _users;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            await AdoptLegacyLoginAsync(db, ct);
            _users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
            return _users;
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>Users changed: they're read again on the next call.</summary>
    public void UsersChanged()
    {
        _users = null;
        _keys = null;
    }

    /// <summary>The admin, if setup has made them.</summary>
    public async Task<User?> AdminAsync(CancellationToken ct) => (await UsersAsync(ct)).Values.FirstOrDefault(u => u.IsAdmin);

    /// <summary>Set up: the admin has a password. A login reset clears it, and the web app asks for a new one.</summary>
    public async Task<bool> IsSetUpAsync(CancellationToken ct) => await AdminAsync(ct) is { PasswordHash.Length: > 0 };

    public async Task<User?> FindAsync(string username, CancellationToken ct) =>
        (await UsersAsync(ct)).Values.FirstOrDefault(u => u.NormalizedUsername == Normalize(username));

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    /// <summary>What a user may do: every permission and admin for the admin, else theirs.</summary>
    public static IReadOnlySet<string> PermissionsOf(User user) =>
        user.IsAdmin ? Scopes.All.ToHashSet() : Scopes.Parse(user.Permissions).Where(Scopes.IsPermission).ToHashSet();

    public static User NewUser(string username, string password, IEnumerable<string> permissions, bool isAdmin = false) => new()
    {
        Username = username.Trim(),
        NormalizedUsername = Normalize(username),
        PasswordHash = HashPassword(password),
        Permissions = isAdmin ? "" : Scopes.Join(permissions.Where(Scopes.IsPermission)),
        IsAdmin = isAdmin,
        Stamp = NewStamp(),
    };

    // From before there were users: the single admin login, and keys nobody owned. Done once, then the old login goes.
    private async Task AdoptLegacyLoginAsync(MelodexDbContext db, CancellationToken ct)
    {
        var row = await db.Settings.FindAsync([LoginKey], ct);
        var admin = await db.Users.FirstOrDefaultAsync(u => u.IsAdmin, ct);
        if (admin is null && row is not null && JsonSerializer.Deserialize<AdminLogin>(row.ValueJson, MelodexJson.Options) is { PasswordHash.Length: > 0 } login)
        {
            admin = new User
            {
                Username = login.Username, NormalizedUsername = Normalize(login.Username), PasswordHash = login.PasswordHash,
                IsAdmin = true, Stamp = login.Stamp,
            };
            db.Users.Add(admin);
            log.LogInformation("Made the admin user {Username} from the login kept before there were users", admin.Username);
        }
        if (row is not null)
            db.Settings.Remove(row);
        if (admin is not null)
            await db.ApiKeys.Where(k => k.UserId == null).ExecuteUpdateAsync(u => u.SetProperty(k => k.UserId, admin.Id), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The way back in when the admin's password is lost: it's cleared, and the web app asks for a new one.</summary>
    public async Task ResetAdminAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        await AdoptLegacyLoginAsync(db, ct);
        await db.Users.Where(u => u.IsAdmin).ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, "").SetProperty(x => x.Stamp, NewStamp()), ct);
        UsersChanged();
    }

    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>What a session cookie carries: the user, and the stamp it was made under.</summary>
    public static ClaimsPrincipal Principal(User user) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(StampClaim, user.Stamp)], "cookie"));

    /// <summary>The user a session belongs to, while it's still good: they exist, and their password hasn't changed since.</summary>
    public async Task<User?> SessionUserAsync(ClaimsPrincipal principal, CancellationToken ct) =>
        Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) && (await UsersAsync(ct)).GetValueOrDefault(id) is { } user
        && user.PasswordHash.Length > 0 && principal.FindFirst(StampClaim)?.Value == user.Stamp
            ? user
            : null;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool CheckPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts is not ["pbkdf2-sha256", var iterations, var salt, var hash] || !int.TryParse(iterations, out var count))
            return false;
        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), count, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>A wrong password from this address counts against it; too many in a while and it has to wait.</summary>
    public bool TooManyFailures(string address)
    {
        if (!_failures.TryGetValue(address, out var times))
            return false;
        while (times.TryPeek(out var oldest) && DateTime.UtcNow - oldest > FailureWindow)
            times.TryDequeue(out _);
        return times.Count >= FailuresAllowed;
    }

    public void Failed(string address) => _failures.GetOrAdd(address, _ => new ConcurrentQueue<DateTime>()).Enqueue(DateTime.UtcNow);

    public void Succeeded(string address) => _failures.TryRemove(address, out _);

    // ---- API keys ----

    /// <summary>A new key for a user: the full key, shown once, and the record kept of it.</summary>
    public static (string Key, ApiKey Record) NewKey(string name, IEnumerable<string> scopes, Guid userId)
    {
        var key = KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (key, new ApiKey
        {
            Name = name.Trim(),
            Prefix = key[..(KeyPrefix.Length + 6)],
            Hash = HashKey(key),
            Scopes = Scopes.Join(scopes),
            UserId = userId,
        });
    }

    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Keys changed: they're read again on the next call.</summary>
    public void KeysChanged() => _keys = null;

    private async Task<Dictionary<string, ApiKey>> KeysAsync(CancellationToken ct)
    {
        if (_keys is { } keys)
            return keys;
        await UsersAsync(ct);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        keys = await db.ApiKeys.AsNoTracking().ToDictionaryAsync(k => k.Hash, ct);
        _keys = keys;
        return keys;
    }

    /// <summary>
    /// What a key may do: its scopes, as far as its owner may do them. Admin on a key means all its owner may do. A key
    /// whose owner has gone can do nothing.
    /// </summary>
    public static IReadOnlySet<string> ScopesOf(ApiKey key, User? owner)
    {
        if (owner is null)
            return new HashSet<string>();
        var allowed = PermissionsOf(owner);
        var asked = Scopes.Parse(key.Scopes).ToHashSet();
        return asked.Contains(Scopes.Admin) ? allowed : asked.Where(allowed.Contains).ToHashSet();
    }

    private async Task<Caller?> FromKeyAsync(string key, CancellationToken ct)
    {
        if (!(await KeysAsync(ct)).TryGetValue(HashKey(key), out var record))
            return null;
        NoteUsed(record.Id);
        var owner = record.UserId is { } id ? (await UsersAsync(ct)).GetValueOrDefault(id) : null;
        return new Caller(CallerKind.Key, record.Name, ScopesOf(record, owner), owner?.Id, record.Id, owner?.IsAdmin == true);
    }

    // When a key was last used is worth knowing roughly, not worth a write on every call.
    private void NoteUsed(Guid id)
    {
        var now = DateTime.UtcNow;
        if (_lastUsed.TryGetValue(id, out var last) && now - last < LastUsedEvery)
            return;
        _lastUsed[id] = now;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
                await db.ApiKeys.Where(k => k.Id == id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now));
            }
            catch (Exception e)
            {
                log.LogDebug(e, "Couldn't note when an API key was used");
            }
        });
    }

    // ---- Who's calling ----

    public enum Refusal
    {
        None,

        /// <summary>No admin yet, or the admin's login was reset: the web app asks for one.</summary>
        SetupRequired,
        SignInRequired,
        BadKey,
    }

    /// <summary>Who's calling: an app by its key, or someone by their session.</summary>
    public async Task<(Caller? Caller, Refusal Refusal)> IdentifyAsync(HttpContext http, CancellationToken ct)
    {
        var key = http.Request.Headers[KeyHeader].FirstOrDefault() ?? http.Request.Query[KeyQuery].FirstOrDefault();
        if (!string.IsNullOrEmpty(key))
            return await FromKeyAsync(key, ct) is { } app ? (app, Refusal.None) : (null, Refusal.BadKey);

        if (http.User.Identity?.IsAuthenticated == true && await SessionUserAsync(http.User, ct) is { } user)
            return (new Caller(CallerKind.User, user.Username, PermissionsOf(user), user.Id, IsAdmin: user.IsAdmin), Refusal.None);
        return (null, await IsSetUpAsync(ct) ? Refusal.SignInRequired : Refusal.SetupRequired);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
