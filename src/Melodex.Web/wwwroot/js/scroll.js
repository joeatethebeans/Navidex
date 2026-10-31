// Where the page is scrolled to, so a page can be shown again as it was left. The page may still be filling in (covers,
// sections drawn after the first), so going back keeps trying for a moment until it's long enough to get there.
window.melodexScroll = {
    get: () => window.scrollY,
    to: y => {
        let tries = 0;
        const go = () => {
            window.scrollTo({ top: y, behavior: 'instant' });
            if (Math.abs(window.scrollY - y) > 2 && ++tries < 20)
                setTimeout(go, 50);
        };
        go();
    },
};

// Back and forward put each page where it was scrolled to this session. Where a page was is noted as it's left, by a
// link or by back and forward; the browser's own attempt comes before the page is drawn, so it's left to this.
(() => {
    const positions = new Map();
    let here = location.href;
    const leave = () => positions.set(here, window.scrollY);
    history.scrollRestoration = 'manual';

    const push = history.pushState.bind(history);
    history.pushState = (...args) => {
        leave();
        push(...args);
        here = location.href;
    };
    const replace = history.replaceState.bind(history);
    history.replaceState = (...args) => {
        replace(...args);
        here = location.href;
    };
    addEventListener('popstate', () => {
        leave();
        here = location.href;
        const y = positions.get(here) ?? 0;
        setTimeout(() => window.melodexScroll.to(y), 0);
    });
})();
