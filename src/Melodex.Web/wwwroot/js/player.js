// The one audio element behind the player bar. Player.cs drives it and hears back about time, ends and errors.
let audio;
let player;
let second = -1;

export function init(dotnet) {
    player = dotnet;
    if (audio)
        return;
    audio = new Audio();
    audio.preload = "auto";
    audio.addEventListener("timeupdate", () => {
        // Once a second is plenty for a seek bar, and keeps calls into .NET down.
        const now = Math.floor(audio.currentTime);
        if (now !== second) {
            second = now;
            player.invokeMethodAsync("OnTime", audio.currentTime, Number.isFinite(audio.duration) ? audio.duration : 0);
        }
    });
    audio.addEventListener("ended", () => player.invokeMethodAsync("OnEnded"));
    audio.addEventListener("playing", () => player.invokeMethodAsync("OnPlaying", true));
    audio.addEventListener("pause", () => player.invokeMethodAsync("OnPlaying", false));
    audio.addEventListener("error", () => {
        if (audio.getAttribute("src"))
            player.invokeMethodAsync("OnError", audio.error?.message || "The file couldn't be played.");
    });
    if ("mediaSession" in navigator) {
        // Media keys, and the lock screen on a phone.
        navigator.mediaSession.setActionHandler("play", () => audio.play());
        navigator.mediaSession.setActionHandler("pause", () => audio.pause());
        navigator.mediaSession.setActionHandler("previoustrack", () => player.invokeMethodAsync("OnRemote", "previous"));
        navigator.mediaSession.setActionHandler("nexttrack", () => player.invokeMethodAsync("OnRemote", "next"));
    }
}

/** Whether the browser expects to play this type; an empty type is given the benefit of the doubt. */
export function canPlay(type) {
    return !type || (audio ?? new Audio()).canPlayType(type) !== "";
}

export function load(url, meta) {
    second = -1;
    audio.src = url;
    if ("mediaSession" in navigator && "MediaMetadata" in window)
        navigator.mediaSession.metadata = new MediaMetadata({
            title: meta.title, artist: meta.artist, album: meta.album ?? "",
            artwork: meta.cover ? [{ src: new URL(meta.cover, document.baseURI).href }] : [],
        });
    play();
}

export function play() {
    audio.play().catch(e => {
        // Loading another track first isn't a failure; the browser refusing to start without a click is shown as paused.
        if (e.name !== "AbortError")
            player.invokeMethodAsync("OnPlaying", false);
    });
}

export function pause() {
    audio.pause();
}

export function seek(seconds) {
    audio.currentTime = seconds;
}

export function stop() {
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    if ("mediaSession" in navigator)
        navigator.mediaSession.metadata = null;
}
