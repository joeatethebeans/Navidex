using System.Text.Json;
using Microsoft.JSInterop;

namespace Navidex.Web;

/// <summary>
/// Per-browser view preferences, such as which artist-page sections are open. Kept in local storage, so they survive
/// reloads but aren't shared between devices. Losing them only resets the view.
/// </summary>
public sealed class Preferences(IJSRuntime js)
{
    private const string Prefix = "navidex.";

    /// <summary>Show only what's missing: artists with missing albums, and on an artist's page only those albums.</summary>
    public bool OnlyMissing { get; private set; }

    private bool _loaded;

    public async Task LoadAsync()
    {
        if (_loaded)
            return;
        OnlyMissing = await GetAsync("onlyMissing", false);
        _loaded = true;
    }

    public Task SetOnlyMissingAsync(bool value)
    {
        OnlyMissing = value;
        return SetAsync("onlyMissing", value);
    }

    public async Task<T> GetAsync<T>(string key, T fallback)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", Prefix + key);
            return json is null ? fallback : JsonSerializer.Deserialize<T>(json) ?? fallback;
        }
        catch (Exception e) when (e is JSException or JsonException)
        {
            return fallback;
        }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", Prefix + key, JsonSerializer.Serialize(value));
        }
        catch (JSException)
        {
        }
    }
}
