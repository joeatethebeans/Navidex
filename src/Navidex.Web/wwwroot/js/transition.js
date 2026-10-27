// Runs a change to the page as a view transition where the browser has them: the tiles on screen that stay slide to
// their new places, new ones fade in and the rest of the page changes at once. Only tiles on screen are named, as
// the browser takes a picture of each named element, and the page itself isn't, so nothing else flickers.
// A transition waits for a frame to be drawn before it changes anything, so if none comes soon (a tab in the
// background) it's skipped, which makes the change at once.
const attribute = 'data-vt';

function nameVisible(named) {
    const height = window.innerHeight;
    for (const element of document.querySelectorAll(`[${attribute}]`)) {
        const key = element.getAttribute(attribute);
        if (named.has(key))
            continue;
        const box = element.getBoundingClientRect();
        if (box.bottom < 0 || box.top > height)
            continue;
        element.style.viewTransitionName = `vt-${key}`;
        named.set(key, element);
    }
}

function clear(named) {
    for (const element of named.values())
        element.style.viewTransitionName = '';
}

export function run(target, method) {
    const apply = () => target.invokeMethodAsync(method);
    if (!document.startViewTransition || matchMedia('(prefers-reduced-motion: reduce)').matches)
        return apply();
    const named = new Map();
    nameVisible(named);
    let started = false;
    const transition = document.startViewTransition(async () => {
        started = true;
        await apply();
        // A tile that stayed keeps its name; name the ones that came onto the screen.
        for (const [key, element] of named)
            if (!element.isConnected)
                named.delete(key);
        nameVisible(named);
    });
    transition.finished.finally(() => clear(named));
    setTimeout(() => { if (!started) transition.skipTransition(); }, 250);
    return transition.updateCallbackDone;
}
