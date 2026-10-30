// Applies the theme before anything is drawn, so the page doesn't flash another one. Until someone signs in, it's the
// last theme this browser used; then the web app calls melodexTheme.apply with theirs, which is kept for next time.
(() => {
    const key = 'melodex.appearance';
    const dark = matchMedia('(prefers-color-scheme: dark)');
    let current = {};

    function apply(appearance) {
        current = { color: 'pink', mode: 'auto', highContrast: false, ...appearance };
        const root = document.documentElement;
        root.dataset.color = current.color;
        root.dataset.mode = current.mode === 'auto' ? (dark.matches ? 'dark' : 'light') : current.mode;
        if (current.highContrast)
            root.dataset.contrast = 'high';
        else
            delete root.dataset.contrast;
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta)
            meta.content = getComputedStyle(root).getPropertyValue('--surface').trim();
    }

    function load(name) {
        try {
            return localStorage.getItem(name);
        } catch {
            return null;
        }
    }

    // Melodex was Navidex: carry this browser's preferences over, once.
    try {
        for (const old of Object.keys(localStorage).filter(k => k.startsWith('navidex.'))) {
            const now = 'melodex.' + old.slice('navidex.'.length);
            if (localStorage.getItem(now) === null)
                localStorage.setItem(now, localStorage.getItem(old));
            localStorage.removeItem(old);
        }
    } catch { }

    let saved = {};
    try {
        saved = JSON.parse(load(key)) ?? {};
    } catch { }
    apply(saved);
    dark.addEventListener('change', () => apply(current));

    window.melodexTheme = {
        apply(appearance) {
            apply(appearance);
            try {
                localStorage.setItem(key, JSON.stringify(current));
            } catch { }
        },
    };
})();
