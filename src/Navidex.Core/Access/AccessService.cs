using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Core.Data;

namespace Navidex.Core.Access;

/// <summary>The admin login, kept in the settings table. Never sent to clients.</summary>
public sealed record AdminLogin
{
    public string Username { get; init; } = "";

    /// <summary>PBKDF2 with SHA-256: <c>pbkdf2-sha256$iterations$salt$hash</c>, salt and hash in base64.</summary>
    public string PasswordHash { get; init; } = "";

    /// <summary>Let the local network in without signing in.</summary>
    public bool SkipOnLocalNetwork { get; init; }

    /// <summary>Changes with the password, which signs out every other session.</summary>
    public string Stamp { get; init; } = "";
}

/// <summary>
/// Who may call the API: the admin, signed in with a session cookie or on the local network when that's allowed, and
/// apps with API keys. The login and keys are cached in memory, as they're checked on every call.
/// </summary>
public sealed class AccessService(IServiceScopeFactory scopes, ILogger<AccessService> log)
{
    public const string LoginKey = "auth.login";
    public const string KeyHeader = "X-Api-Key";
    public const string KeyQuery = "apikey";
    public const string StampClaim = "navidex:stamp";

    /// <summary>Keys start with this, so they're easy to spot, in a config file or a leak.</summary>
    public const string KeyPrefix = "ndx_";

    private const int Iterations = 210_000;
    private static readonly TimeSpan LastUsedEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private const int FailuresAllowed = 5;

    private readonly SemaphoreSlim _loading = new(1, 1);
    private AdminLogin? _login;
    private bool _loginLoaded;
    private Dictionary<string, ApiKey>? _keys;
    private readonly ConcurrentDictionary<Guid, DateTime> _lastUsed = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _failures = new();

    // ---- The admin login ----

    public async Task<AdminLogin?> GetLoginAsync(CancellationToken ct)
    {
        if (_loginLoaded)
            return _login;
        await _loading.WaitAsync(ct);
        try
        {
            if (!_loginLoaded)
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
                var row = await db.Settings.FindAsync([LoginKey], ct);
                _login = row is null ? null : JsonSerializer.Deserialize<AdminLogin>(row.ValueJson, NavidexJson.Options);
                if (_login is { PasswordHash.Length: 0 })
                    _login = null;
                _loginLoaded = true;
            }
            return _login;
        }
        finally
        {
            _loading.Release();
        }
    }

    public async Task SaveLoginAsync(AdminLogin? login, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
        var row = await db.Settings.FindAsync([LoginKey], ct);
        if (login is null)
        {
            if (row is not null)
                db.Settings.Remove(row);
        }
        else
        {
            var json = JsonSerializer.Serialize(login, NavidexJson.Options);
            if (row is null)
                db.Settings.Add(new Setting { Key = LoginKey, ValueJson = json });
            else
                row.ValueJson = json;
        }
        await db.SaveChangesAsync(ct);
        _login = login;
        _loginLoaded = true;
    }

    public static AdminLogin NewLogin(string username, string password, bool skipOnLocalNetwork) =>
        new() { Username = username.Trim(), PasswordHash = HashPassword(password), SkipOnLocalNetwork = skipOnLocalNetwork, Stamp = NewStamp() };

    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>What a session cookie carries: the username, and the stamp it was made under.</summary>
    public static ClaimsPrincipal Principal(AdminLogin login) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, login.Username), new Claim(StampClaim, login.Stamp)], "cookie"));

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

    /// <summary>A new key: the full key, shown once, and the record kept of it.</summary>
    public static (string Key, ApiKey Record) NewKey(string name, IEnumerable<string> scopes)
    {
        var key = KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (key, new ApiKey
        {
            Name = name.Trim(),
            Prefix = key[..(KeyPrefix.Length + 6)],
            Hash = HashKey(key),
            Scopes = string.Join(",", scopes.Distinct().Where(Scopes.IsKnown).OrderBy(s => Scopes.All.ToList().IndexOf(s))),
        });
    }

    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Keys changed: they're read again on the next call.</summary>
    public void KeysChanged() => _keys = null;

    private async Task<Dictionary<string, ApiKey>> KeysAsync(CancellationToken ct)
    {
        if (_keys is { } keys)
            return keys;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
        keys = await db.ApiKeys.AsNoTracking().ToDictionaryAsync(k => k.Hash, ct);
        _keys = keys;
        return keys;
    }

    private async Task<Caller?> FromKeyAsync(string key, CancellationToken ct)
    {
        if (!(await KeysAsync(ct)).TryGetValue(HashKey(key), out var record))
            return null;
        NoteUsed(record.Id);
        return new Caller(CallerKind.Key, record.Name, record.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(), record.Id);
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
                var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
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

        /// <summary>No admin login yet: the web app asks for one.</summary>
        SetupRequired,
        SignInRequired,
        BadKey,
    }

    /// <summary>Who's calling: an app by its key, the admin by their session, or the local network if that's let in.</summary>
    public async Task<(Caller? Caller, Refusal Refusal)> IdentifyAsync(HttpContext http, CancellationToken ct)
    {
        var key = http.Request.Headers[KeyHeader].FirstOrDefault() ?? http.Request.Query[KeyQuery].FirstOrDefault();
        if (!string.IsNullOrEmpty(key))
            return await FromKeyAsync(key, ct) is { } app ? (app, Refusal.None) : (null, Refusal.BadKey);

        var login = await GetLoginAsync(ct);
        if (http.User.Identity?.IsAuthenticated == true && login is not null)
            return (new Caller(CallerKind.Admin, login.Username, AllScopes), Refusal.None);
        if (login is null)
            return (null, Refusal.SetupRequired);
        if (login.SkipOnLocalNetwork && IsLocal(http))
            return (new Caller(CallerKind.Local, login.Username, AllScopes), Refusal.None);
        return (null, Refusal.SignInRequired);
    }

    private static readonly IReadOnlySet<string> AllScopes = Scopes.All.ToHashSet();

    /// <summary>
    /// From this machine or the local network, as best the core can tell. Anything that came through a reverse proxy
    /// isn't, as the proxy may be passing on calls from anywhere.
    /// </summary>
    public static bool IsLocal(HttpContext http)
    {
        var headers = http.Request.Headers;
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("Forwarded") || headers.ContainsKey("X-Real-IP"))
            return false;
        return http.Connection.RemoteIpAddress is { } address && IsLocal(address);
    }

    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
        }
        // Unique local (fc00::/7) and link-local addresses.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6LinkLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
