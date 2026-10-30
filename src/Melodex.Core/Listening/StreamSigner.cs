using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Melodex.Core.Data;

namespace Melodex.Core.Listening;

/// <summary>
/// Short-lived signed stream URLs, so a player (an <c>audio</c> element, or another app's player) can fetch a file
/// without sending credentials, and a URL that leaks stops working within hours.
/// </summary>
public sealed class StreamSigner
{
    public const string KeySetting = "stream.key";

    /// <summary>How long a stream URL works; long enough for a queue played through an evening.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private byte[] _key = [];

    /// <summary>Loads the signing key, creating one the first time.</summary>
    public async Task InitialiseAsync(MelodexDbContext db)
    {
        var row = await db.Settings.FindAsync(KeySetting);
        if (row is null)
        {
            row = new Setting { Key = KeySetting, ValueJson = $"\"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}\"" };
            db.Settings.Add(row);
            await db.SaveChangesAsync();
        }
        _key = Convert.FromBase64String(row.ValueJson.Trim('"'));
    }

    /// <summary>The URL with <c>exp</c> and <c>sig</c> added, relative to the app's base like other API links.</summary>
    public string Sign(string path, DateTimeOffset? now = null)
    {
        var expires = ((now ?? DateTimeOffset.UtcNow) + Lifetime).ToUnixTimeSeconds();
        return $"{path}?exp={expires}&sig={Signature(path, expires)}";
    }

    /// <summary>Whether a signature is for this path and hasn't expired.</summary>
    public bool Verify(string path, long expires, string? signature, DateTimeOffset? now = null)
    {
        if (signature is null || expires < (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds())
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Signature(path, expires)), Encoding.ASCII.GetBytes(signature));
    }

    private string Signature(string path, long expires) =>
        Base64Url(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{path}\n{expires}")));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
