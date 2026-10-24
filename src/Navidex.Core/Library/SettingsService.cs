using System.Text.Json;
using Navidex.Client;
using Navidex.Core.Data;

namespace Navidex.Core.Library;

public static class LibrarySettingsExtensions
{
    /// <summary>Whether the missing view shows this album when it isn't owned.</summary>
    public static bool Shows(this LibrarySettings settings, Album album) =>
        settings.ShownTypes.Contains(album.Type)
        && !(settings.HideSecondaryTypes && album.SecondaryTypes.Count > 0)
        && !(settings.HideUnofficial && album.HasOfficialRelease == false);
}

public sealed class SettingsService(NavidexDbContext db)
{
    private const string LibraryKey = "library";

    public async Task<LibrarySettings> GetLibraryAsync(CancellationToken ct)
    {
        var row = await db.Settings.FindAsync([LibraryKey], ct);
        return row is null ? new LibrarySettings() : JsonSerializer.Deserialize<LibrarySettings>(row.ValueJson, NavidexJson.Options) ?? new LibrarySettings();
    }

    public async Task SaveLibraryAsync(LibrarySettings value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, NavidexJson.Options);
        var row = await db.Settings.FindAsync([LibraryKey], ct);
        if (row is null)
            db.Settings.Add(new Setting { Key = LibraryKey, ValueJson = json });
        else
            row.ValueJson = json;
        await db.SaveChangesAsync(ct);
    }
}
