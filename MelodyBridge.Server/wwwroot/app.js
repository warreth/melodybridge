// MelodyBridge client helpers. Plain JS, no dependencies.
window.melody = (() => {
    const THEME_KEY = 'mb-theme';

    function setTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        try { localStorage.setItem(THEME_KEY, theme); } catch { /* private mode */ }
    }

    function init() {
        let saved = null;
        try { saved = localStorage.getItem(THEME_KEY); } catch { /* ignore */ }
        setTheme(saved === 'light' ? 'light' : 'dark');
    }

    // Position the tour spotlight over an element and scroll it into view.
    // Returns true when the element exists; the caller falls back to a
    // centered card when it doesn't.
    function spotlight(selector) {
        const el = document.querySelector(selector);
        if (!el) return false;
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
        const r = el.getBoundingClientRect();
        const box = document.getElementById('tour-spotlight');
        if (box) {
            box.style.left = (r.left - 8) + 'px';
            box.style.top = (r.top - 8) + 'px';
            box.style.width = (r.width + 16) + 'px';
            box.style.height = (r.height + 16) + 'px';
            box.style.display = 'block';
        }
        return true;
    }

    function hideSpotlight() {
        const box = document.getElementById('tour-spotlight');
        if (box) box.style.display = 'none';
    }

    return {
        setTheme,
        init,
        spotlight,
        hideSpotlight,
        // Trigger a browser download for exported data (playlists, logs, backups).
        downloadFile(fileName, contentType, byteArray) {
            const buffer = new Uint8Array(byteArray);
            const blob = new Blob([buffer], { type: contentType });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            a.remove();
            URL.revokeObjectURL(url);
        }
    };
})();

document.addEventListener('DOMContentLoaded', () => window.melody.init());