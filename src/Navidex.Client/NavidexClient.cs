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

    public async Task<IReadOnlyList<MissingAlbum>> GetMissingAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<MissingAlbum>>($"{Api}/library/missing", NavidexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<ReviewItem>> GetReviewAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ReviewItem>>($"{Api}/library/review", NavidexJson.Options, ct) ?? [];

    public async Task<TaskInfo> ScanLibraryAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/library/scan", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(NavidexJson.Options, ct))!;
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

    public async Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SearchResults>($"{Api}/catalogue/search?q={Uri.EscapeDataString(query)}", NavidexJson.Options, ct)
        ?? new SearchResults([], [], []);

    public async Task<LibrarySettings> GetLibrarySettingsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LibrarySettings>($"{Api}/settings/library", NavidexJson.Options, ct) ?? new LibrarySettings();

    public async Task SaveLibrarySettingsAsync(LibrarySettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/library", settings, NavidexJson.Options, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Activity log entries newer than <paramref name="after"/>, oldest first.</summary>
    public async Task<LogPage> GetLogAsync(long after = 0, int limit = 1000, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LogPage>($"{Api}/log?after={after}&limit={limit}", NavidexJson.Options, ct) ?? new LogPage([], after);

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(NavidexJson.Options, ct);
    }
}
