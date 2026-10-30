using System.Text.Json;
using Melodex.Client;
using Melodex.Core.Data;

namespace Melodex.Core.Library;

public static class LibrarySettingsExtensions
{
    /// <summary>Whether the missing view shows this album when it isn't owned.</summary>
    public static bool Shows(this LibrarySettings settings, Album album) =>
        settings.ShownTypes.Contains(album.Type)
        && !(settings.HideSecondaryTypes && album.SecondaryTypes.Count > 0)
        && !(settings.HideUnofficial && album.HasOfficialRelease == false);
}

public sealed class SettingsService(MelodexDbContext db)
{
    private const string LibraryKey = "library";
    private const string FetchKey = "fetch";
    private const string LogKey = "log";

    public Task<LibrarySettings> GetLibraryAsync(CancellationToken ct) => GetAsync<LibrarySettings>(LibraryKey, ct);

    public Task SaveLibraryAsync(LibrarySettings value, CancellationToken ct) => SaveAsync(LibraryKey, value, ct);

    public Task<FetchSettings> GetFetchAsync(CancellationToken ct) => GetAsync<FetchSettings>(FetchKey, ct);

    public Task SaveFetchAsync(FetchSettings value, CancellationToken ct) => SaveAsync(FetchKey, value, ct);

    public Task<LogSettings> GetLogAsync(CancellationToken ct) => GetAsync<LogSettings>(LogKey, ct);

    public Task SaveLogAsync(LogSettings value, CancellationToken ct) => SaveAsync(LogKey, value, ct);

    private async Task<T> GetAsync<T>(string key, CancellationToken ct) where T : new()
    {
        var row = await db.Settings.FindAsync([key], ct);
        return row is null ? new T() : JsonSerializer.Deserialize<T>(row.ValueJson, MelodexJson.Options) ?? new T();
    }

    private async Task SaveAsync<T>(string key, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, MelodexJson.Options);
        var row = await db.Settings.FindAsync([key], ct);
        if (row is null)
            db.Settings.Add(new Setting { Key = key, ValueJson = json });
        else
            row.ValueJson = json;
        await db.SaveChangesAsync(ct);
    }
}
