// A picture that didn't load: the server answers 503 while it's still fetching one, and 404 when there's none. An
// <img> can't tell which, so ask: keep the shimmer and try again for one on its way, show the placeholder otherwise.
window.navidexCoverFailed = async function (img) {
    const cover = img.parentElement;
    const tries = Number(img.dataset.tries || 0);
    let status = 0;
    try {
        status = (await fetch(img.src, { cache: 'no-store' })).status;
    } catch {
    }
    if (!img.isConnected)
        return;
    if (status === 200 || (status === 503 && tries < 30)) {
        img.dataset.tries = tries + 1;
        const url = new URL(img.src);
        url.searchParams.set('try', tries + 1);
        setTimeout(() => { if (img.isConnected) img.src = url.toString(); }, status === 200 ? 0 : 2000);
        return;
    }
    cover.classList.replace('pending', 'blank');
};
