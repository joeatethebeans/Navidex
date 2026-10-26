using Navidex.Client;
using Navidex.Web.Components;

namespace Navidex.Web;

/// <summary>
/// Recent requests, shared by every page so fetch buttons can show what's already on its way. Reloads when the server
/// says requests changed, polling only as a fallback, and tells library pages to reload when a request lands in the library.
/// </summary>
public sealed class RequestTracker : IDisposable
{
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    private readonly NavidexClient api;
    private readonly ServerEvents events;

    public RequestTracker(NavidexClient api, ServerEvents events)
    {
        this.api = api;
        this.events = events;
        events.Received += OnEvent;
        events.EnsureStarted();
    }

    private void OnEvent(string type)
    {
        if (type is "requests" or "connected" && _loading is not null)
            _ = RefreshAsync();
    }

    private IReadOnlyList<RequestInfo> _requests = [];
    private Timer? _timer;
    private Task? _loading;

    /// <summary>Raised whenever the list is refreshed.</summary>
    public event Action? Changed;

    /// <summary>Top-level requests, newest first.</summary>
    public IReadOnlyList<RequestInfo> Requests => _requests;

    public Task EnsureLoadedAsync() => _loading ??= RefreshAsync();

    /// <summary>
    /// The newest request to fetch this artist, album or track into the library, unless it finished there or was
    /// cancelled. Listen-now requests don't count: they aren't fetching it for the library.
    /// </summary>
    public RequestInfo? Find(RequestKind kind, Guid id) =>
        _requests.FirstOrDefault(r => r.Kind == kind && r.TargetId == id && !r.ListenNow) is { State: not (RequestState.InLibrary or RequestState.Cancelled) } request
            ? request
            : null;

    public async Task<RequestInfo> CreateAsync(CreateRequest request)
    {
        var created = await api.CreateRequestAsync(request);
        await RefreshAsync();
        return created;
    }

    public async Task RefreshAsync()
    {
        IReadOnlyList<RequestInfo> latest;
        try
        {
            latest = await api.ListRequestsAsync(limit: 200);
        }
        catch (HttpRequestException)
        {
            Schedule(Idle);
            return;
        }
        // Something reaching the library changes what the library pages show.
        var landed = latest.Any(r => _requests.FirstOrDefault(old => old.Id == r.Id) is { } old
                                     && (r.State == RequestState.InLibrary && old.State != RequestState.InLibrary
                                         || (r.Parts?.InLibrary ?? 0) > (old.Parts?.InLibrary ?? 0)));
        _requests = latest;
        Schedule(latest.Any(r => r.Active) && !events.Connected ? Busy : Idle);
        Changed?.Invoke();
        if (landed)
            ScanStatus.RaiseLibraryChanged();
    }

    private void Schedule(TimeSpan delay)
    {
        _timer ??= new Timer(_ => _ = RefreshAsync());
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        events.Received -= OnEvent;
        _timer?.Dispose();
    }
}
