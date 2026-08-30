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

    return {
        setTheme,
        init,
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
