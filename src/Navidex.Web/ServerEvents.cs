using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Navidex.Web;

/// <summary>
/// Reads <c>GET /events</c> for the whole app and says what changed, so pages reload instead of polling. Reconnects
/// when the connection drops, for example while the core restarts.
/// </summary>
public sealed class ServerEvents(HttpClient http) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;

    /// <summary>The kind of thing that changed: <c>requests</c>, <c>listen</c>, <c>library</c> or <c>tasks</c>.</summary>
    public event Action<string>? Received;

    /// <summary>Whether events are arriving, so pollers can slow down.</summary>
    public bool Connected { get; private set; }

    public void EnsureStarted() => _running ??= RunAsync(_stop.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/events");
                request.SetBrowserResponseStreamingEnabled(true);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                Connected = true;
                delay = TimeSpan.FromSeconds(1);
                // Missed while disconnected: everyone reloads.
                Received?.Invoke("connected");
                while (await reader.ReadLineAsync(ct) is { } line)
                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                        Received?.Invoke(line["event: ".Length..]);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Dropped or refused: try again, more slowly each time.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Connected = false;
            await Task.Delay(delay, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
