using Microsoft.JSInterop;

namespace Navidex.Web;

/// <summary>
/// Runs a change to the page as a view transition (transition.js): the tiles on screen slide to their new places
/// instead of the page jumping. Where the browser can't, the change just happens.
/// </summary>
public sealed class ViewTransitions(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<ViewTransitions>? _self;
    private Func<Task>? _change;

    /// <summary>Loads the script ahead of time: fetching it on the first change would hold that change back.</summary>
    public async Task PreloadAsync()
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/transition.js");
        }
        catch (JSException)
        {
        }
    }

    /// <param name="change">Makes the change and redraws what it affects.</param>
    public async Task RunAsync(Func<Task> change)
    {
        _change = change;
        try
        {
            _self ??= DotNetObjectReference.Create(this);
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/transition.js");
            await _module.InvokeVoidAsync("run", _self, nameof(ApplyAsync));
        }
        catch (JSException)
        {
            await ApplyAsync();
        }
    }

    [JSInvokable]
    public async Task ApplyAsync()
    {
        if (Interlocked.Exchange(ref _change, null) is { } change)
            await change();
    }

    public async ValueTask DisposeAsync()
    {
        _self?.Dispose();
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
