// Runs a change to the page as a view transition where the browser has them, so what stays slides into place and
// the rest fades, instead of the page jumping. A transition waits for a frame to be drawn before it changes anything,
// so if none comes soon (a tab in the background) it's skipped, which makes the change at once.
export function run(target, method, arg) {
    const apply = () => target.invokeMethodAsync(method, arg);
    if (!document.startViewTransition || matchMedia('(prefers-reduced-motion: reduce)').matches)
        return apply();
    let started = false;
    const transition = document.startViewTransition(() => {
        started = true;
        return apply();
    });
    setTimeout(() => { if (!started) transition.skipTransition(); }, 250);
    return transition.updateCallbackDone;
}
