using Melodex.Client;
using Microsoft.JSInterop;

namespace Melodex.Web;

/// <summary>Shows the web app in someone's theme: see <c>wwwroot/js/theme.js</c>, which keeps it for this browser too.</summary>
public sealed class Theme(IJSRuntime js)
{
    /// <summary>Each colour, with its accent in light and dark (as in <c>app.css</c>) for the swatches that show it.</summary>
    public static readonly IReadOnlyList<(ThemeColor Color, string Name, string Light, string Dark)> Colors =
    [
        (ThemeColor.Pink, "Pink", "#b8296a", "#f27aae"),
        (ThemeColor.Green, "Green", "#1f6f5c", "#4fb89a"),
        (ThemeColor.Blue, "Blue", "#2a5fb0", "#6ea8f5"),
        (ThemeColor.Grey, "Grey", "#2e2e2e", "#e6e6e6"),
        (ThemeColor.Purple, "Purple", "#6a3dbb", "#b294f5"),
        (ThemeColor.Amber, "Amber", "#a64b06", "#f5a54a"),
    ];

    public static readonly IReadOnlyList<(ThemeMode Mode, string Name, string Hint)> Modes =
    [
        (ThemeMode.Auto, "Auto", "Light or dark, as your device is"),
        (ThemeMode.Light, "Light", "Always light"),
        (ThemeMode.Dark, "Dark", "Always dark"),
        (ThemeMode.Black, "Black", "Dark, with pure black behind, for OLED screens"),
    ];

    private const string ReloadedKey = "melodex.reloaded-for-theme";

    public async Task ApplyAsync(Appearance appearance)
    {
        try
        {
            await js.InvokeVoidAsync("melodexTheme.apply", new
            {
                color = appearance.Color.ToString().ToLowerInvariant(),
                mode = appearance.Mode.ToString().ToLowerInvariant(),
                highContrast = appearance.HighContrast,
            });
            await js.InvokeVoidAsync("sessionStorage.removeItem", ReloadedKey);
        }
        catch (JSException)
        {
            // The page is one a browser kept from an older version, without theme.js. Loading it again fetches the
            // current one; once only, so a page that really is broken doesn't reload for ever.
            if (await js.InvokeAsync<string?>("sessionStorage.getItem", ReloadedKey) is null)
            {
                await js.InvokeVoidAsync("sessionStorage.setItem", ReloadedKey, "1");
                await js.InvokeVoidAsync("location.reload");
            }
        }
    }
}
