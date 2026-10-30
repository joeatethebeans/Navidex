using System.Net;
using System.Net.Http.Json;

namespace Melodex.Client;

/// <summary>Typed client for the Melodex public API. Pass an HttpClient whose BaseAddress is the Melodex server.</summary>
public sealed class MelodexClient(HttpClient http)
{
    private const string Api = "api/v1";

    public Task<LibraryStatus?> GetLibraryStatusAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<LibraryStatus>($"{Api}/library", MelodexJson.Options, ct);

    public Task<IReadOnlyList<LibraryArtist>> GetLibraryArtistsAsync(CancellationToken ct = default) =>
        GetListAsync<LibraryArtist>($"{Api}/library/artists", ct);

    public Task<IReadOnlyList<LibraryAlbum>> GetLibraryAlbumsAsync(CancellationToken ct = default) =>
        GetListAsync<LibraryAlbum>($"{Api}/library/albums", ct);

    public Task<IReadOnlyList<LibraryTrack>> GetLibraryTracksAsync(CancellationToken ct = default) =>
        GetListAsync<LibraryTrack>($"{Api}/library/tracks", ct);

    // The library's big lists as last sent, by their ETags, so asking again for one that hasn't changed is a 304 with
    // nothing to download or read.
    private readonly Dictionary<string, (string ETag, object List)> _lists = [];

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken ct)
    {
        (string? ETag, object? List) known;
        lock (_lists)
            known = _lists.TryGetValue(path, out var held) ? held : default;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (known.ETag is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", known.ETag);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified && known.List is IReadOnlyList<T> same)
            return same;
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<List<T>>(MelodexJson.Options, ct) ?? [];
        if (response.Headers.ETag is { } tag)
            lock (_lists)
                _lists[path] = (tag.ToString(), list);
        return list;
    }

    public async Task<IReadOnlyList<MissingAlbum>> GetMissingAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<MissingAlbum>>($"{Api}/library/missing", MelodexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<ReviewItem>> GetReviewAsync(ReviewList list = ReviewList.Review, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ReviewItem>>($"{Api}/library/review?list={list}", MelodexJson.Options, ct) ?? [];

    /// <summary>Excludes, defers or hides a file, or with <see cref="SetAside.None"/> puts it back to be matched at the next scan.</summary>
    public async Task SetFileAsideAsync(Guid fileId, SetAside setAside, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/library/files/{fileId}/set-aside", new SetFileAside(setAside), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>As <see cref="SetFileAsideAsync"/>, for many files.</summary>
    public async Task<ReviewDone> SetFilesAsideAsync(IReadOnlyList<Guid> fileIds, SetAside setAside, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/review/set-aside", new SetFilesAside(fileIds, setAside), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewDone>(MelodexJson.Options, ct))!;
    }

    /// <summary>Excludes a file from matching, or includes it again.</summary>
    public async Task SetFileExcludedAsync(Guid fileId, bool excluded, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/library/files/{fileId}/excluded", new ExcludeFile(excluded), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<TaskInfo> ScanLibraryAsync(Rematch rematch = Rematch.None, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/scan", new ScanLibrary(rematch), MelodexJson.Options, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(bool? active = null, int? limit = null, CancellationToken ct = default)
    {
        var query = string.Join('&', new[] { active is { } a ? $"active={a.ToString().ToLowerInvariant()}" : null, limit is { } l ? $"limit={l}" : null }.OfType<string>());
        return await http.GetFromJsonAsync<List<TaskInfo>>($"{Api}/tasks{(query.Length > 0 ? "?" + query : "")}", MelodexJson.Options, ct) ?? [];
    }

    public Task<TaskInfo?> GetTaskAsync(Guid id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<TaskInfo>($"{Api}/tasks/{id}", MelodexJson.Options, ct);

    public Task<ArtistDetail?> GetArtistAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<ArtistDetail>($"{Api}/artists/{id}", ct);

    public async Task<IReadOnlyList<AlbumSummary>> GetArtistAlbumsAsync(Guid id, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AlbumSummary>>($"{Api}/artists/{id}/albums", MelodexJson.Options, ct) ?? [];

    public Task<TrackDetail?> GetTrackAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<TrackDetail>($"{Api}/tracks/{id}", ct);

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
    /// <param name="local">Only what Melodex already holds, which answers at once.</param>
    public async Task<SearchResults> SearchAsync(string query, IReadOnlyCollection<string>? kinds = null, bool local = false, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SearchResults>(
            $"{Api}/catalogue/search?q={Uri.EscapeDataString(query)}{(kinds is { Count: > 0 } ? $"&kinds={string.Join(',', kinds)}" : "")}{(local ? "&local=true" : "")}",
            MelodexJson.Options, ct) ?? new SearchResults([], [], []);

    public async Task<LibrarySettings> GetLibrarySettingsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LibrarySettings>($"{Api}/settings/library", MelodexJson.Options, ct) ?? new LibrarySettings();

    public async Task SaveLibrarySettingsAsync(LibrarySettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/library", settings, MelodexJson.Options, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Activity log entries newer than <paramref name="after"/>, oldest first.</summary>
    /// <param name="since">The <see cref="ScanFeedPage.Latest"/> from the last call, to get only what changed; 0 for every file.</param>
    public async Task<ScanFeedPage> GetScanFilesAsync(long since = 0, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ScanFeedPage>($"{Api}/library/scan/files?since={since}", MelodexJson.Options, ct))!;

    public async Task<LogPage> GetLogAsync(long after = 0, int limit = 1000, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LogPage>($"{Api}/log?after={after}&limit={limit}", MelodexJson.Options, ct) ?? new LogPage([], after);

    // ---- Requests ----

    /// <summary>Fetch an artist, album or track. Returns the new request, or the one already queued for it.</summary>
    public async Task<RequestInfo> CreateRequestAsync(CreateRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/requests", request, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<RequestInfo>> ListRequestsAsync(bool activeOnly = false, int limit = 100, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<RequestInfo>>($"{Api}/requests?active={activeOnly.ToString().ToLowerInvariant()}&limit={limit}", MelodexJson.Options, ct) ?? [];

    public Task<RequestInfo?> GetRequestAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<RequestInfo>($"{Api}/requests/{id}", ct);

    // ---- Listen now ----

    /// <summary>Starts listening to a track, or to an album's missing tracks in order.</summary>
    public async Task<IReadOnlyList<ListenNowInfo>> StartListeningAsync(StartListen start, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/listen", start, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<ListenNowInfo>>(MelodexJson.Options, ct))!;
    }

    public async Task<RemovalPreview> GetRemovalAsync(LibraryItemKind kind, Guid id, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<RemovalPreview>($"{Api}/library/removal?kind={kind}&id={id}", MelodexJson.Options, ct))!;

    public async Task<FormatPlan> GetFormatPlanAsync(FormatScope scope, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<FormatPlan>($"{Api}/library/format?kind={scope.Kind}{(scope.Id is { } id ? $"&id={id}" : "")}", MelodexJson.Options, ct))!;

    /// <summary>Starts formatting in the background; the task reports progress like a scan.</summary>
    public async Task<TaskInfo> StartFormatAsync(StartFormat body, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"{Api}/library/format", body, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<RemovalResult> RemoveFromLibraryAsync(RemoveFromLibrary body, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"{Api}/library/remove", body, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RemovalResult>(MelodexJson.Options, ct))!;
    }

    /// <param name="all">Include those discarded, expired or added to the library.</param>
    public async Task<IReadOnlyList<ListenNowInfo>> ListListeningAsync(bool all = false, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ListenNowInfo>>($"{Api}/listen{(all ? "?all=true" : "")}", MelodexJson.Options, ct) ?? [];

    public Task<ListenNowInfo?> GetListeningAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<ListenNowInfo>($"{Api}/listen/{id}", ct);

    /// <summary>Adds a playable listen-now track to the library.</summary>
    public async Task<ListenNowInfo> AddListeningAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/listen/{id}/add", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ListenNowInfo>(MelodexJson.Options, ct))!;
    }

    public async Task DiscardListeningAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/listen/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>How to play a track you have; null if you don't have it.</summary>
    /// <summary>How to play one file, matched or not.</summary>
    public Task<PlayInfo?> GetFilePlayInfoAsync(Guid fileId, CancellationToken ct = default) =>
        GetOrNullAsync<PlayInfo>($"{Api}/library/files/{fileId}/play", ct);

    public Task<LocalArtistDetail?> GetLocalArtistAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<LocalArtistDetail>($"{Api}/library/local/artists/{id}", ct);

    public Task<LocalAlbumDetail?> GetLocalAlbumAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<LocalAlbumDetail>($"{Api}/library/local/albums/{id}", ct);

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
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<RequestInfo> ChooseCandidateAsync(Guid id, ChooseCandidate choice, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/requests/{id}/choose", choice, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(MelodexJson.Options, ct))!;
    }

    /// <summary>Accepts the suggestion for each of these files that has one.</summary>
    public async Task<ReviewDone> ApproveFilesAsync(IReadOnlyList<Guid> fileIds, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/review/approve", new ReviewFiles(fileIds), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewDone>(MelodexJson.Options, ct))!;
    }

    /// <summary>Excludes these files from matching.</summary>
    public async Task<ReviewDone> ExcludeFilesAsync(IReadOnlyList<Guid> fileIds, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/review/exclude", new ReviewFiles(fileIds), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewDone>(MelodexJson.Options, ct))!;
    }

    public async Task MatchFileAsync(Guid fileId, MatchFile match, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/files/{fileId}/match", match, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<LogSettings> GetLogSettingsAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<LogSettings>($"{Api}/settings/log", MelodexJson.Options, ct))!;

    public async Task SaveLogSettingsAsync(LogSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/log", settings, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<FetchSettings> GetFetchSettingsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<FetchSettings>($"{Api}/settings/fetch", MelodexJson.Options, ct) ?? new FetchSettings();

    public async Task SaveFetchSettingsAsync(FetchSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/fetch", settings, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    // ---- Plugins ----

    public async Task<IReadOnlyList<PluginInfo>> ListPluginsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<PluginInfo>>($"{Api}/plugins", MelodexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<AvailablePlugin>> ListAvailablePluginsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AvailablePlugin>>($"{Api}/plugins/available", MelodexJson.Options, ct) ?? [];

    public async Task<PluginInfo> AddPluginAsync(AddPlugin plugin, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/plugins", plugin, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<PluginInfo> UpdatePluginAsync(Guid id, UpdatePlugin update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/plugins/{id}", update, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(MelodexJson.Options, ct))!;
    }

    public async Task OrderPluginsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/plugins/order", new PluginOrder(ids), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<PluginInfo> TestPluginAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/plugins/{id}/test", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(MelodexJson.Options, ct))!;
    }

    public async Task RemovePluginAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/plugins/{id}", ct);
        await EnsureAsync(response, ct);
    }

    // ---- Pausing and ordering ----

    public Task PauseRequestAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/requests/{id}/pause", null, ct);

    public Task ResumeRequestAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/requests/{id}/resume", null, ct);

    /// <summary>Moves a request to just before <paramref name="before"/> in its queue, or to the end.</summary>
    public Task MoveRequestAsync(Guid id, Guid? before, CancellationToken ct = default) => PostAsync($"{Api}/requests/{id}/move", new MoveInQueue(before), ct);

    public Task CancelTaskAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/cancel", null, ct);

    public Task PauseTaskAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/pause", null, ct);

    public Task ResumeTaskAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/resume", null, ct);

    public Task MoveTaskAsync(Guid id, Guid? before, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/move", new MoveInQueue(before), ct);

    private async Task PostAsync(string url, object? body, CancellationToken ct)
    {
        using var response = body is null ? await http.PostAsync(url, null, ct) : await http.PostAsJsonAsync(url, body, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    // ---- Access ----

    public async Task<AuthStatus> GetAuthStatusAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<AuthStatus>($"{Api}/auth/status", MelodexJson.Options, ct))!;

    /// <summary>Makes the admin account, the first time, and signs in with it.</summary>
    public async Task SetUpLoginAsync(SetUpLogin login, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/auth/setup", login, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task SignInAsync(SignIn signIn, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/auth/login", signIn, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/auth/logout", null, ct);
        await EnsureAsync(response, ct);
    }

    public async Task UpdateLoginAsync(UpdateLogin update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/auth/account", update, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<ServerVersion> GetVersionAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ServerVersion>($"{Api}/version", MelodexJson.Options, ct))!;

    public async Task<SetupStatus> GetSetupAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<SetupStatus>($"{Api}/setup", MelodexJson.Options, ct))!;

    /// <summary>Setup is finished or skipped: the web app stops showing its steps.</summary>
    public async Task FinishSetupAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/setup/done", null, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Keeps how the web app looks with your account.</summary>
    public async Task SetAppearanceAsync(Appearance appearance, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/auth/appearance", appearance, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<UserInfo>> ListUsersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<UserInfo>>($"{Api}/users", MelodexJson.Options, ct) ?? [];

    public async Task<UserInfo> CreateUserAsync(CreateUser user, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/users", user, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UserInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<UserInfo> UpdateUserAsync(Guid id, UpdateUser update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/users/{id}", update, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UserInfo>(MelodexJson.Options, ct))!;
    }

    public async Task RemoveUserAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/users/{id}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListApiKeysAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ApiKeyInfo>>($"{Api}/keys", MelodexJson.Options, ct) ?? [];

    /// <summary>Makes a key. Its <see cref="CreatedApiKey.Key"/> can't be had again, so show or keep it now.</summary>
    public async Task<CreatedApiKey> CreateApiKeyAsync(CreateApiKey key, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/keys", key, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CreatedApiKey>(MelodexJson.Options, ct))!;
    }

    public async Task RevokeApiKeyAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/keys/{id}", ct);
        await EnsureAsync(response, ct);
    }

    // ---- Webhooks ----

    public async Task<IReadOnlyList<WebhookInfo>> ListWebhooksAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<WebhookInfo>>($"{Api}/webhooks", MelodexJson.Options, ct) ?? [];

    public async Task<WebhookInfo> CreateWebhookAsync(SaveWebhook webhook, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/webhooks", webhook, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<WebhookInfo> UpdateWebhookAsync(Guid id, SaveWebhook webhook, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/webhooks/{id}", webhook, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<WebhookInfo> RotateWebhookSecretAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/webhooks/{id}/secret", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookInfo>(MelodexJson.Options, ct))!;
    }

    public async Task RemoveWebhookAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/webhooks/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Sends a ping now and says how it went.</summary>
    public async Task<WebhookDeliveryInfo> TestWebhookAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/webhooks/{id}/test", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookDeliveryInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<WebhookDeliveryInfo>> ListWebhookDeliveriesAsync(Guid id, int limit = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<WebhookDeliveryInfo>>($"{Api}/webhooks/{id}/deliveries?limit={limit}", MelodexJson.Options, ct) ?? [];

    /// <summary>Throws <see cref="MelodexApiException"/> with the server's own explanation for anything but success.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? message = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(MelodexJson.Options, ct);
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
        throw new MelodexApiException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : message);
    }

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(MelodexJson.Options, ct);
    }
}

/// <summary>The server refused a request, with its reason.</summary>
public sealed class MelodexApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
