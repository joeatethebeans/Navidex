using System.Text.Json;
using Microsoft.JSInterop;

namespace Melodex.Web;

/// <summary>
/// Per-browser view preferences, such as which artist-page sections are open. Kept in local storage, so they survive
/// reloads but aren't shared between devices. Losing them only resets the view.
/// </summary>
public sealed class Preferences(IJSRuntime js)
{
    private const string Prefix = "melodex.";

    /// <summary>What the library and artist pages show: only what you have, everything, or only what's missing.</summary>
    public OwnershipView Ownership { get; private set; } = OwnershipView.All;

    private bool _loaded;

    public async Task LoadAsync()
    {
        if (_loaded)
            return;
        // Before there were three, there was a tick box for only missing.
        Ownership = await GetAsync("ownership", await GetAsync("onlyMissing", false) ? OwnershipView.Missing : OwnershipView.All);
        _loaded = true;
    }

    public Task SetOwnershipAsync(OwnershipView value)
    {
        Ownership = value;
        return SetAsync("ownership", value);
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

/// <summary>The library's three-way filter.</summary>
public enum OwnershipView
{
    /// <summary>Only what you have all of.</summary>
    Owned,

    All,

    /// <summary>Only what has something missing.</summary>
    Missing,
}
