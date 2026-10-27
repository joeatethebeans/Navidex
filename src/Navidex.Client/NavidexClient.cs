using System.Net;
using System.Net.Http.Json;

namespace Navidex.Client;

/// <summary>Typed client for the Navidex public API. Pass an HttpClient whose BaseAddress is the Navidex server.</summary>
public sealed class NavidexClient(HttpClient http)
{
    private const string Api = "api/v1";

    public Task<LibraryStatus?> GetLibraryStatusAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<LibraryStatus>($"{Api}/library", NavidexJson.Options, ct);

    public async Task<IReadOnlyList<LibraryArtist>> GetLibraryArtistsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<LibraryArtist>>($"{Api}/library/artists", NavidexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<LibraryAlbum>> GetLibraryAlbumsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<LibraryAlbum>>($"{Api}/library/albums", NavidexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<LibraryTrack>> GetLibraryTracksAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<LibraryTrack>>($"{Api}/library/tracks", NavidexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<MissingAlbum>> GetMissingAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<MissingAlbum>>($"{Api}/library/missing", NavidexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<ReviewItem>> GetReviewAsync(bool excluded = false, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ReviewItem>>($"{Api}/library/review{(excluded ? "?excluded=true" : "")}", NavidexJson.Options, ct) ?? [];

    /// <summary>Excludes a file from matching, or includes it again.</summary>
    public async Task SetFileExcludedAsync(Guid fileId, bool excluded, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/library/files/{fileId}/excluded", new ExcludeFile(excluded), NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<TaskInfo> ScanLibraryAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/library/scan", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(NavidexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(bool? active = null, int? limit = null, CancellationToken ct = default)
    {
        var query = string.Join('&', new[] { active is { } a ? $"active={a.ToString().ToLowerInvariant()}" : null, limit is { } l ? $"limit={l}" : null }.OfType<string>());
        return await http.GetFromJsonAsync<List<TaskInfo>>($"{Api}/tasks{(query.Length > 0 ? "?" + query : "")}", NavidexJson.Options, ct) ?? [];
    }

    public Task<TaskInfo?> GetTaskAsync(Guid id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<TaskInfo>($"{Api}/tasks/{id}", NavidexJson.Options, ct);

    public Task<ArtistDetail?> GetArtistAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<ArtistDetail>($"{Api}/artists/{id}", ct);

    public async Task<IReadOnlyList<AlbumSummary>> GetArtistAlbumsAsync(Guid id, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AlbumSummary>>($"{Api}/artists/{id}/albums", NavidexJson.Options, ct) ?? [];

    public Task<AlbumDetail?> GetAlbumAsync(Guid id, Guid? editionId = null, CancellationToken ct = default) =>
        GetOrNullAsync<AlbumDetail>(editionId is null ? $"{Api}/albums/{id}" : $"{Api}/albums/{id}?edition={editionId}", ct);

    public async Task SetAlbumIgnoredAsync(Guid id, bool ignored, CancellationToken ct = default)
    {
        using var response = ignored
            ? await http.PutAsync($"{Api}/albums/{id}/ignore", null, ct)
            : await http.DeleteAsync($"{Api}/albums/{id}/ignore", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetTrackIgnoredAsync(Guid id, bool ignored, CancellationToken ct = default)
    {
        using var response = ignored
            ? await http.PutAsync($"{Api}/tracks/{id}/ignore", null, ct)
            : await http.DeleteAsync($"{Api}/tracks/{id}/ignore", ct);
        response.EnsureSuccessStatusCode();
    }

    /// <param name="kinds">Some of <c>artists</c>, <c>albums</c> and <c>tracks</c>; all of them when empty.</param>
    /// <param name="local">Only what Navidex already holds, which answers at once.</param>
    public async Task<SearchResults> SearchAsync(string query, IReadOnlyCollection<string>? kinds = null, bool local = false, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SearchResults>(
            $"{Api}/catalogue/search?q={Uri.EscapeDataString(query)}{(kinds is { Count: > 0 } ? $"&kinds={string.Join(',', kinds)}" : "")}{(local ? "&local=true" : "")}",
            NavidexJson.Options, ct) ?? new SearchResults([], [], []);

    public async Task<LibrarySettings> GetLibrarySettingsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LibrarySettings>($"{Api}/settings/library", NavidexJson.Options, ct) ?? new LibrarySettings();

    public async Task SaveLibrarySettingsAsync(LibrarySettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/library", settings, NavidexJson.Options, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Activity log entries newer than <paramref name="after"/>, oldest first.</summary>
    /// <param name="since">The <see cref="ScanFeedPage.Latest"/> from the last call, to get only what changed; 0 for every file.</param>
    public async Task<ScanFeedPage> GetScanFilesAsync(long since = 0, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ScanFeedPage>($"{Api}/library/scan/files?since={since}", NavidexJson.Options, ct))!;

    public async Task<LogPage> GetLogAsync(long after = 0, int limit = 1000, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LogPage>($"{Api}/log?after={after}&limit={limit}", NavidexJson.Options, ct) ?? new LogPage([], after);

    // ---- Requests ----

    /// <summary>Fetch an artist, album or track. Returns the new request, or the one already queued for it.</summary>
    public async Task<RequestInfo> CreateRequestAsync(CreateRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/requests", request, NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(NavidexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<RequestInfo>> ListRequestsAsync(bool activeOnly = false, int limit = 100, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<RequestInfo>>($"{Api}/requests?active={activeOnly.ToString().ToLowerInvariant()}&limit={limit}", NavidexJson.Options, ct) ?? [];

    public Task<RequestInfo?> GetRequestAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<RequestInfo>($"{Api}/requests/{id}", ct);

    // ---- Listen now ----

    /// <summary>Starts listening to a track, or to an album's missing tracks in order.</summary>
    public async Task<IReadOnlyList<ListenNowInfo>> StartListeningAsync(StartListen start, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/listen", start, NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<ListenNowInfo>>(NavidexJson.Options, ct))!;
    }

    public async Task<RemovalPreview> GetRemovalAsync(LibraryItemKind kind, Guid id, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<RemovalPreview>($"{Api}/library/removal?kind={kind}&id={id}", NavidexJson.Options, ct))!;

    public async Task<RemovalResult> RemoveFromLibraryAsync(RemoveFromLibrary body, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"{Api}/library/remove", body, NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RemovalResult>(NavidexJson.Options, ct))!;
    }

    /// <param name="all">Include those discarded, expired or added to the library.</param>
    public async Task<IReadOnlyList<ListenNowInfo>> ListListeningAsync(bool all = false, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ListenNowInfo>>($"{Api}/listen{(all ? "?all=true" : "")}", NavidexJson.Options, ct) ?? [];

    public Task<ListenNowInfo?> GetListeningAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<ListenNowInfo>($"{Api}/listen/{id}", ct);

    /// <summary>Adds a playable listen-now track to the library.</summary>
    public async Task<ListenNowInfo> AddListeningAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/listen/{id}/add", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ListenNowInfo>(NavidexJson.Options, ct))!;
    }

    public async Task DiscardListeningAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/listen/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>How to play a track you have; null if you don't have it.</summary>
    public Task<PlayInfo?> GetPlayInfoAsync(Guid trackId, Guid? editionId = null, CancellationToken ct = default) =>
        GetOrNullAsync<PlayInfo>($"{Api}/tracks/{trackId}/play{(editionId is { } e ? $"?edition={e}" : "")}", ct);

    public async Task CancelRequestAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/requests/{id}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<RequestInfo> RetryRequestAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/requests/{id}/retry", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(NavidexJson.Options, ct))!;
    }

    public async Task<RequestInfo> ChooseCandidateAsync(Guid id, ChooseCandidate choice, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/requests/{id}/choose", choice, NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(NavidexJson.Options, ct))!;
    }

    /// <summary>Accepts the suggestion for each of these files that has one.</summary>
    public async Task<ReviewDone> ApproveFilesAsync(IReadOnlyList<Guid> fileIds, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/review/approve", new ReviewFiles(fileIds), NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewDone>(NavidexJson.Options, ct))!;
    }

    /// <summary>Excludes these files from matching.</summary>
    public async Task<ReviewDone> ExcludeFilesAsync(IReadOnlyList<Guid> fileIds, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/review/exclude", new ReviewFiles(fileIds), NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewDone>(NavidexJson.Options, ct))!;
    }

    public async Task MatchFileAsync(Guid fileId, MatchFile match, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/files/{fileId}/match", match, NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<LogSettings> GetLogSettingsAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<LogSettings>($"{Api}/settings/log", NavidexJson.Options, ct))!;

    public async Task SaveLogSettingsAsync(LogSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/log", settings, NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<FetchSettings> GetFetchSettingsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<FetchSettings>($"{Api}/settings/fetch", NavidexJson.Options, ct) ?? new FetchSettings();

    public async Task SaveFetchSettingsAsync(FetchSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/fetch", settings, NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    // ---- Plugins ----

    public async Task<IReadOnlyList<PluginInfo>> ListPluginsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<PluginInfo>>($"{Api}/plugins", NavidexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<AvailablePlugin>> ListAvailablePluginsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AvailablePlugin>>($"{Api}/plugins/available", NavidexJson.Options, ct) ?? [];

    public async Task<PluginInfo> AddPluginAsync(AddPlugin plugin, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/plugins", plugin, NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(NavidexJson.Options, ct))!;
    }

    public async Task<PluginInfo> UpdatePluginAsync(Guid id, UpdatePlugin update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/plugins/{id}", update, NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(NavidexJson.Options, ct))!;
    }

    public async Task OrderPluginsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/plugins/order", new PluginOrder(ids), NavidexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<PluginInfo> TestPluginAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/plugins/{id}/test", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(NavidexJson.Options, ct))!;
    }

    public async Task RemovePluginAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/plugins/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Throws <see cref="NavidexApiException"/> with the server's own explanation for anything but success.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? message = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(NavidexJson.Options, ct);
            if (problem.TryGetProperty("errors", out var errors) && errors.ValueKind == System.Text.Json.JsonValueKind.Object)
                message = string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetString())));
            else if (problem.TryGetProperty("detail", out var detail))
                message = detail.GetString();
            else if (problem.TryGetProperty("title", out var title))
                message = title.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
        }
        throw new NavidexApiException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : message);
    }

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(NavidexJson.Options, ct);
    }
}

/// <summary>The server refused a request, with its reason.</summary>
public sealed class NavidexApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
