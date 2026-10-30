using Microsoft.JSInterop;

namespace Melodex.Web;

/// <summary>
/// Reads <c>GET /events</c> for the whole app (events.js) and says what changed, so pages reload instead of polling.
/// Reconnects when the connection drops, for example while the core restarts, and as soon as the app signs in.
/// </summary>
public sealed class ServerEvents(IJSRuntime js, AuthState auth) : IAsyncDisposable
{
    private Task? _starting;
    private DotNetObjectReference<ServerEvents>? _self;
    private IJSObjectReference? _module;
    private IJSObjectReference? _source;

    /// <summary>The kind of thing that changed: <c>requests</c>, <c>listen</c>, <c>library</c> or <c>tasks</c>.</summary>
    public event Action<string>? Received;

    /// <summary>Whether events are arriving, so pollers can slow down.</summary>
    public bool Connected { get; private set; }

    public void EnsureStarted() => _starting ??= StartAsync();

    private async Task StartAsync()
    {
        try
        {
            _self = DotNetObjectReference.Create(this);
            _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/events.js");
            _source = await _module.InvokeAsync<IJSObjectReference>("listen", _self);
            auth.Changed += OnAuthChanged;
        }
        catch (JSException)
        {
            // No events: pages fall back to polling.
        }
    }

    // Refused before signing in, it would otherwise wait out its back-off.
    private void OnAuthChanged()
    {
        if (auth.Status is { LetIn: true } && !Connected && _source is not null)
            _ = _source.InvokeVoidAsync("reconnect").AsTask();
    }

    [JSInvokable]
    public void Opened()
    {
        Connected = true;
        // Missed while disconnected: everyone reloads.
        Received?.Invoke("connected");
    }

    [JSInvokable]
    public void Closed() => Connected = false;

    [JSInvokable]
    public void Changed(string kind) => Received?.Invoke(kind);

    public async ValueTask DisposeAsync()
    {
        auth.Changed -= OnAuthChanged;
        try
        {
            if (_source is not null)
            {
                await _source.InvokeVoidAsync("dispose");
                await _source.DisposeAsync();
            }
            if (_module is not null)
                await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
        _self?.Dispose();
    }
}
