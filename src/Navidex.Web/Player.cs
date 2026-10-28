using Microsoft.JSInterop;
using Navidex.Client;

namespace Navidex.Web;

/// <summary>A track in the player's queue: one you have, or one fetched to listen to.</summary>
public sealed class PlayerEntry
{
    public required Guid TrackId { get; init; }
    public Guid? EditionId { get; init; }
    public required string Title { get; init; }
    public required string ArtistCredit { get; init; }
    public Guid? ArtistId { get; init; }
    public Guid? AlbumId { get; init; }
    public string? AlbumTitle { get; init; }
    public string? CoverUrl { get; init; }
    public double? DurationSeconds { get; set; }

    /// <summary>A file of your own that no catalogue knows: it's played as the file, and its album is a local one.</summary>
    public Guid? FileId { get; init; }

    /// <summary>The listen-now track, for a track you don't have.</summary>
    public Guid? ListenId { get; init; }

    public ListenNowState? ListenState { get; set; }

    /// <summary>Why it can't be played, or what's happening to it.</summary>
    public string? Detail { get; set; }

    /// <summary>It failed, expired or the browser can't play it, so the queue skips it.</summary>
    public bool Unplayable { get; set; }

    public bool Waiting => ListenState is ListenNowState.Requested or ListenNowState.Fetching;

    public static PlayerEntry Owned(TrackRow track, AlbumSummary album, Guid? editionId) => new()
    {
        TrackId = track.TrackId, EditionId = editionId, Title = track.Title, ArtistCredit = track.ArtistCredit, ArtistId = album.ArtistId,
        AlbumId = album.Id, AlbumTitle = album.Title, CoverUrl = album.CoverUrl, DurationSeconds = track.DurationSeconds,
    };

    public static PlayerEntry Owned(SearchTrack track) => new()
    {
        TrackId = track.Id, Title = track.Title, ArtistCredit = track.ArtistCredit, ArtistId = track.ArtistId,
        AlbumId = track.AlbumId, AlbumTitle = track.AlbumTitle, CoverUrl = track.CoverUrl, DurationSeconds = track.DurationSeconds,
    };

    public static PlayerEntry Owned(LibraryTrack track) => new()
    {
        TrackId = track.TrackId, Title = track.Title, ArtistCredit = track.ArtistCredit, ArtistId = track.ArtistId,
        AlbumId = track.AlbumId, AlbumTitle = track.AlbumTitle, CoverUrl = track.CoverUrl, DurationSeconds = track.DurationSeconds,
        FileId = track.Local ? track.FileId : null,
    };

    public static PlayerEntry Owned(LocalTrack track, LocalAlbumDetail album) => new()
    {
        TrackId = track.FileId, FileId = track.FileId, Title = track.Title, ArtistCredit = track.Artist, ArtistId = album.ArtistLocal ? null : album.ArtistId,
        AlbumId = album.Id, AlbumTitle = album.Title, CoverUrl = album.CoverUrl, DurationSeconds = track.DurationSeconds,
    };

    /// <summary>Where its album's page is.</summary>
    public string? AlbumHref => AlbumId is { } album ? $"{(FileId is null ? "albums" : "local/albums")}/{album}" : null;

    public static PlayerEntry Listening(ListenNowInfo item) => new()
    {
        TrackId = item.TrackId, EditionId = item.EditionId, Title = item.Title, ArtistCredit = item.ArtistCredit, ArtistId = item.ArtistId,
        AlbumId = item.AlbumId, AlbumTitle = item.AlbumTitle, CoverUrl = item.CoverUrl, DurationSeconds = item.DurationSeconds,
        ListenId = item.Id, ListenState = item.State, Detail = item.Detail,
    };

    internal void Update(ListenNowInfo item)
    {
        ListenState = item.State;
        Detail = item.Detail;
        DurationSeconds ??= item.DurationSeconds;
        if (item.State is ListenNowState.Failed or ListenNowState.Expired)
            Unplayable = true;
    }
}

/// <summary>
/// The queue and the audio element behind the player bar. Tracks you have stream from the music folder; tracks you
/// don't are fetched to listen to, and the queue waits at one until it's ready.
/// </summary>
public sealed class Player : IAsyncDisposable
{
    private readonly NavidexClient _api;
    private readonly IJSRuntime _js;
    private readonly ServerEvents _events;
    private readonly List<PlayerEntry> _queue = [];
    private IJSObjectReference? _audio;
    private DotNetObjectReference<Player>? _self;
    private Timer? _poll;
    private int _starting;

    public Player(NavidexClient api, IJSRuntime js, ServerEvents events)
    {
        _api = api;
        _js = js;
        _events = events;
        events.Received += OnEvent;
        events.EnsureStarted();
    }

    /// <summary>The queue changed, or what's playing did.</summary>
    public event Action? Changed;

    /// <summary>The position moved; separate from <see cref="Changed"/> so only the seek bar redraws every second.</summary>
    public event Action? TimeChanged;

    public IReadOnlyList<PlayerEntry> Queue => _queue;
    public int Current { get; private set; } = -1;
    public PlayerEntry? Now => Current >= 0 && Current < _queue.Count ? _queue[Current] : null;
    public bool Playing { get; private set; }
    public double Position { get; private set; }
    public double Duration { get; private set; }

    /// <summary>Something went wrong starting playback, such as nothing to fetch the tracks from.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether this track is the one playing, or waiting to.</summary>
    public bool IsNow(Guid trackId) => Now?.TrackId == trackId;

    // ---- Starting things ----

    /// <summary>
    /// Plays an album in order from a track: tracks you have from the library, missing ones fetched to listen to.
    /// Playback starts as soon as the first is ready.
    /// </summary>
    public async Task PlayAlbumAsync(AlbumDetail album, Guid? fromTrack = null)
    {
        var (entries, start) = await AlbumEntriesAsync(album, fromTrack);
        await PlayAsync(entries, start);
    }

    /// <summary>Adds an album to the end of the queue, fetching tracks you don't have to listen to.</summary>
    public async Task QueueAlbumAsync(AlbumDetail album) => await AddAsync((await AlbumEntriesAsync(album, null)).Entries);

    /// <summary>Adds an album from a list to the end of the queue, loading its tracks first.</summary>
    public async Task QueueAlbumAsync(Guid albumId)
    {
        if (await _api.GetAlbumAsync(albumId) is { } album)
            await QueueAlbumAsync(album);
        else if (await _api.GetLocalAlbumAsync(albumId) is { } own)
            await AddAsync(own.Tracks.Select(t => PlayerEntry.Owned(t, own)).ToList());
    }

    /// <summary>The album's tracks in order: tracks you have, and the missing ones fetched to listen to.</summary>
    private async Task<(List<PlayerEntry> Entries, int Start)> AlbumEntriesAsync(AlbumDetail album, Guid? fromTrack)
    {
        Error = null;
        var tracks = album.Tracks.Where(t => t.Status is TrackStatus.Owned or TrackStatus.OwnedElsewhere or TrackStatus.Missing).ToList();
        var listening = new Dictionary<Guid, ListenNowInfo>();
        if (tracks.Any(t => t.Status == TrackStatus.Missing))
        {
            try
            {
                foreach (var item in await _api.StartListeningAsync(new StartListen(RequestKind.Album, album.Album.Id, album.ShownEditionId)))
                    listening[item.TrackId] = item;
            }
            catch (NavidexApiException e)
            {
                Error = e.Message;
            }
        }
        var entries = new List<PlayerEntry>();
        var start = 0;
        foreach (var track in tracks)
        {
            if (track.TrackId == fromTrack)
                start = entries.Count;
            if (track.Status != TrackStatus.Missing)
                entries.Add(PlayerEntry.Owned(track, album.Album, album.ShownEditionId));
            else if (listening.TryGetValue(track.TrackId, out var item))
                entries.Add(PlayerEntry.Listening(item));
        }
        return (entries, start);
    }

    /// <summary>Plays an album from a list, loading its tracks first. Albums of your own files are found too.</summary>
    public async Task PlayAlbumAsync(Guid albumId)
    {
        if (await _api.GetAlbumAsync(albumId) is { } album)
            await PlayAlbumAsync(album);
        else if (await _api.GetLocalAlbumAsync(albumId) is { } own)
            await PlayAsync(own.Tracks.Select(t => PlayerEntry.Owned(t, own)).ToList());
    }

    /// <summary>Fetches a track you don't have to listen to, and plays it now or after the queue.</summary>
    public async Task ListenAsync(Guid trackId, Guid? editionId = null, bool next = false)
    {
        Error = null;
        try
        {
            var items = await _api.StartListeningAsync(new StartListen(RequestKind.Track, trackId, editionId));
            var entries = items.Select(PlayerEntry.Listening).ToList();
            await (next ? AddAsync(entries) : PlayAsync(entries));
        }
        catch (NavidexApiException e)
        {
            Error = e.Message;
            Changed?.Invoke();
        }
    }

    /// <summary>Replaces the queue and starts playing it.</summary>
    public Task PlayAsync(IReadOnlyList<PlayerEntry> entries, int start = 0)
    {
        _queue.Clear();
        _queue.AddRange(entries);
        Current = entries.Count == 0 ? -1 : Math.Clamp(start, 0, entries.Count - 1);
        return StartAsync();
    }

    /// <summary>Adds to the end of the queue, starting it if nothing's playing.</summary>
    public Task AddAsync(IReadOnlyList<PlayerEntry> entries)
    {
        _queue.AddRange(entries);
        if (Now is null && entries.Count > 0)
        {
            Current = _queue.Count - entries.Count;
            return StartAsync();
        }
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    // ---- Controls ----

    public async Task ToggleAsync()
    {
        if (_audio is null || Now is null)
            return;
        if (Playing)
            await _audio.InvokeVoidAsync("pause");
        else if (Now.Waiting || Now.Unplayable)
            await StartAsync();
        else
            await _audio.InvokeVoidAsync("play");
    }

    public Task NextAsync() => JumpAsync(Current + 1);

    /// <summary>Back to the start of this track, or to the one before if it's only just started.</summary>
    public async Task PreviousAsync()
    {
        if (Position > 3 && _audio is not null)
            await SeekAsync(0);
        else
            await JumpAsync(Math.Max(Current - 1, 0));
    }

    public async Task JumpAsync(int index)
    {
        if (index >= _queue.Count)
        {
            await StopAsync();
            return;
        }
        Current = index;
        await StartAsync();
    }

    public async Task SeekAsync(double seconds)
    {
        Position = seconds;
        if (_audio is not null)
            await _audio.InvokeVoidAsync("seek", seconds);
        TimeChanged?.Invoke();
    }

    public async Task RemoveAsync(int index)
    {
        if (index < 0 || index >= _queue.Count)
            return;
        _queue.RemoveAt(index);
        if (index < Current)
            Current--;
        else if (index == Current)
        {
            await JumpAsync(Current);
            return;
        }
        Changed?.Invoke();
    }

    /// <summary>Stops and empties the queue, which hides the player bar.</summary>
    public async Task ClearAsync()
    {
        _queue.Clear();
        await StopAsync();
    }

    /// <summary>Adds a listen-now track to the library, from the file it already has.</summary>
    public async Task AddToLibraryAsync(PlayerEntry entry)
    {
        if (entry.ListenId is not { } id)
            return;
        try
        {
            entry.Update(await _api.AddListeningAsync(id));
        }
        catch (NavidexApiException e)
        {
            entry.Detail = e.Message;
        }
        Changed?.Invoke();
    }

    // ---- Playing ----

    /// <summary>Plays <see cref="Current"/>, skipping tracks that can't be played, or waits if it's still being fetched.</summary>
    private async Task StartAsync()
    {
        // A newer start (a click, or the next track) wins over one still waiting on the API.
        var attempt = ++_starting;
        while (Now is { } entry)
        {
            var (url, mediaType) = await SourceAsync(entry);
            if (attempt != _starting)
                return;
            if (url is not null)
            {
                var audio = await AudioAsync();
                if (await audio.InvokeAsync<bool>("canPlay", mediaType))
                {
                    Position = 0;
                    Duration = entry.DurationSeconds ?? 0;
                    await audio.InvokeVoidAsync("load", url, new { title = entry.Title, artist = entry.ArtistCredit, album = entry.AlbumTitle, cover = entry.CoverUrl });
                    break;
                }
                entry.Unplayable = true;
                entry.Detail = $"Your browser can't play this file ({mediaType}).";
            }
            if (entry.Waiting)
            {
                // Paused here until the server says it's ready; polling too, in case events are down.
                if (_audio is not null)
                    await _audio.InvokeVoidAsync("stop");
                Playing = false;
                _poll ??= new Timer(_ => _ = RefreshListeningAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                break;
            }
            if (Current + 1 >= _queue.Count)
            {
                await StopAsync();
                return;
            }
            Current++;
        }
        Changed?.Invoke();
    }

    /// <summary>A fresh stream URL for the entry, or null if it can't be played (yet).</summary>
    private async Task<(string? Url, string? MediaType)> SourceAsync(PlayerEntry entry)
    {
        if (entry.Unplayable)
            return (null, null);
        try
        {
            if (entry.ListenId is { } id)
            {
                if (await _api.GetListeningAsync(id) is not { } item)
                {
                    entry.Unplayable = true;
                    return (null, null);
                }
                entry.Update(item);
                if (item.StreamUrl is not null)
                    return (item.StreamUrl, item.MediaType);
                if (item.State != ListenNowState.InLibrary)
                    return (null, null);
                // Added since: play it from the library.
            }
            if (await (entry.FileId is { } file ? _api.GetFilePlayInfoAsync(file) : _api.GetPlayInfoAsync(entry.TrackId, entry.EditionId)) is { } play)
            {
                entry.DurationSeconds ??= play.DurationSeconds;
                return (play.StreamUrl, play.MediaType);
            }
            entry.Unplayable = true;
            entry.Detail = "It's no longer in your library.";
        }
        catch (HttpRequestException e)
        {
            entry.Unplayable = true;
            entry.Detail = e.Message;
        }
        return (null, null);
    }

    private async Task StopAsync()
    {
        _starting++;
        Current = -1;
        Playing = false;
        Position = Duration = 0;
        StopPolling();
        if (_audio is not null)
            await _audio.InvokeVoidAsync("stop");
        Changed?.Invoke();
    }

    private async Task<IJSObjectReference> AudioAsync()
    {
        if (_audio is null)
        {
            _audio = await _js.InvokeAsync<IJSObjectReference>("import", "./js/player.js");
            _self = DotNetObjectReference.Create(this);
            await _audio.InvokeVoidAsync("init", _self);
        }
        return _audio;
    }

    // ---- Keeping listen-now tracks current ----

    private void OnEvent(string type)
    {
        if (type is "listen" or "connected")
            _ = RefreshListeningAsync();
    }

    private async Task RefreshListeningAsync()
    {
        if (!_queue.Any(e => e.ListenId is not null && e.ListenState is not (ListenNowState.InLibrary or ListenNowState.Expired)))
        {
            StopPolling();
            return;
        }
        IReadOnlyList<ListenNowInfo> items;
        try
        {
            items = await _api.ListListeningAsync(all: true);
        }
        catch (HttpRequestException)
        {
            return;
        }
        var byId = items.ToDictionary(i => i.Id);
        var wasWaiting = Now?.Waiting == true;
        foreach (var entry in _queue)
            if (entry.ListenId is { } id && byId.TryGetValue(id, out var item))
                entry.Update(item);
        if (!_queue.Any(e => e.Waiting))
            StopPolling();
        if (wasWaiting && Now is { Waiting: false })
            await StartAsync();
        else
            Changed?.Invoke();
    }

    private void StopPolling()
    {
        _poll?.Dispose();
        _poll = null;
    }

    // ---- From the audio element ----

    [JSInvokable]
    public void OnTime(double position, double duration)
    {
        Position = position;
        if (duration > 0)
            Duration = duration;
        TimeChanged?.Invoke();
    }

    [JSInvokable]
    public void OnPlaying(bool playing)
    {
        Playing = playing;
        Changed?.Invoke();
    }

    [JSInvokable]
    public Task OnEnded() => NextAsync();

    [JSInvokable]
    public Task OnError(string message)
    {
        if (Now is { } entry)
        {
            entry.Unplayable = true;
            entry.Detail = message;
        }
        return NextAsync();
    }

    [JSInvokable]
    public Task OnRemote(string action) => action == "next" ? NextAsync() : PreviousAsync();

    public async ValueTask DisposeAsync()
    {
        _events.Received -= OnEvent;
        StopPolling();
        if (_audio is not null)
            await _audio.DisposeAsync();
        _self?.Dispose();
    }
}
