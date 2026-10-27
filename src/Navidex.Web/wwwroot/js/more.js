// Tells .NET when an element nears the screen, so a long list can draw more of itself as it's scrolled.
export function observe(element, target) {
    const observer = new IntersectionObserver(async entries => {
        if (!entries.some(e => e.isIntersecting))
            return;
        await target.invokeMethodAsync('MoreAsync');
        // Look again: if it's still near the screen after drawing more, draw more again.
        observer.unobserve(element);
        observer.observe(element);
    }, { rootMargin: '1000px 0px' });
    observer.observe(element);
    return { dispose: () => observer.disconnect() };
}
