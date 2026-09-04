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

    // Hide the framework error banner without a full reload. The banner
    // itself is toggled by Blazor; this only wires the dismiss button.
    function dismissErrorUi() {
        const banner = document.getElementById('blazor-error-ui');
        if (banner) banner.style.display = 'none';
    }

    return {
        setTheme,
        init,
        spotlight,
        hideSpotlight,
        dismissErrorUi,
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

document.addEventListener('DOMContentLoaded', () => {
    window.melody.init();
    const dismiss = document.querySelector('#blazor-error-ui .dismiss');
    if (dismiss) dismiss.addEventListener('click', () => window.melody.dismissErrorUi());
    wireReconnectDialog();
});

// Blazor reconnect dialog: markup lives in _Host.cshtml, Blazor toggles
// its classes; we own the dialog open/close calls and retry wiring.
function wireReconnectDialog() {
    const modal = document.getElementById('components-reconnect-modal');
    if (!modal) return;
    const retryButton = document.getElementById('components-reconnect-button');
    const resumeButton = document.getElementById('components-resume-button');
    if (!retryButton || !resumeButton) return;

    modal.addEventListener('components-reconnect-state-changed', (event) => {
        const state = event.detail.state;
        if (state === 'show') modal.showModal();
        else if (state === 'hide') modal.close();
        else if (state === 'failed') {
            document.addEventListener('visibilitychange', retryWhenVisible);
        } else if (state === 'rejected') {
            location.reload();
        }
    });
    retryButton.addEventListener('click', retryReconnect);
    resumeButton.addEventListener('click', resumeCircuit);
}

async function retryReconnect() {
    document.removeEventListener('visibilitychange', retryWhenVisible);
    try {
        const ok = await Blazor.reconnect();
        if (!ok) {
            const resumed = await Blazor.resumeCircuit();
            if (resumed) {
                document.getElementById('components-reconnect-modal').close();
            } else {
                location.reload();
            }
        }
    } catch {
        // Server unreachable: keep listening for a tab becoming visible.
        document.addEventListener('visibilitychange', retryWhenVisible);
    }
}

async function resumeCircuit() {
    try {
        const ok = await Blazor.resumeCircuit();
        if (!ok) location.reload();
    } catch {
        location.reload();
    }
}

async function retryWhenVisible() {
    if (document.visibilityState === 'visible') await retryReconnect();
}