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
