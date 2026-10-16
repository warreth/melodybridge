// MelodyBridge client helpers. Plain JS, no dependencies.
window.melody = (() => {
    const THEME_KEY = 'mb-theme';
    const ACCENT_KEY = 'mb-accent';
    // Palettes shipped in app.css; anything else falls back to teal.
    const ACCENTS = ['teal', 'blue', 'violet', 'rose', 'amber'];

    function setTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        try { localStorage.setItem(THEME_KEY, theme); } catch { /* private mode */ }
    }

    // Swap the accent palette by setting data-accent on the html element;
    // every accent-colored rule reads the --accent tokens, so no reload
    // and no per-element restyling is needed.
    function setAccent(accent) {
        const value = ACCENTS.includes(accent) ? accent : 'teal';
        document.documentElement.setAttribute('data-accent', value);
        try { localStorage.setItem(ACCENT_KEY, value); } catch { /* private mode */ }
    }

    function init() {
        let saved = null;
        let savedAccent = null;
        try { saved = localStorage.getItem(THEME_KEY); } catch { /* ignore */ }
        try { savedAccent = localStorage.getItem(ACCENT_KEY); } catch { /* ignore */ }
        setTheme(saved === 'light' ? 'light' : 'dark');
        setAccent(savedAccent || 'teal');
    }

    // The settings page reads this once to mark the active swatch; falls
    // back to teal when the attribute is missing (fresh browser).
    function getAccent() {
        const value = document.documentElement.getAttribute('data-accent');
        return ACCENTS.includes(value) ? value : 'teal';
    }

    // The layout reads this once after the first render so the toggle icon
    // starts in sync with the theme the browser actually shows.
    function getTheme() {
        const attr = document.documentElement.getAttribute('data-theme');
        if (attr === 'light' || attr === 'dark') return attr;
        try { return localStorage.getItem(THEME_KEY) === 'light' ? 'light' : 'dark'; } catch { return 'dark'; }
    }

    // Position the tour spotlight over an element and scroll it into view.
    // Returns true when the element exists; the caller falls back to a
    // centered card when it doesn't. The scroll is instant on purpose:
    // a smooth scroll makes the rect read stale and the box lands in the
    // wrong place until the next render.
    function spotlight(selector) {
        const el = document.querySelector(selector);
        if (!el) return false;
        el.scrollIntoView({ block: 'center' });
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

    // Move keyboard focus to an element by selector (tab strips, arrow-key
    // navigation). Any failure is the caller's to ignore.
    function focusSelector(selector) {
        const el = document.querySelector(selector);
        if (el) el.focus();
    }

    return {
        setTheme,
        setAccent,
        getAccent,
        getTheme,
        init,
        spotlight,
        hideSpotlight,
        dismissErrorUi,
        focusSelector,
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

// Help popovers (question-mark next to a setting): the CSS anchors them
// under the icon, which clips inside scroll containers (wizards) and near
// the viewport edges (mobile). Once the popup is visible, switch it to
// fixed positioning clamped to the viewport; when it hides, the inline
// styles are cleared so the stylesheet owns the placement again.
function clampHelpPopup(tip) {
    const popup = tip.querySelector('.help-tip-popup');
    if (!popup) return;
    requestAnimationFrame(() => {
        if (getComputedStyle(popup).display === 'none') return;
        const r = tip.getBoundingClientRect();
        popup.style.position = 'fixed';
        popup.style.left = '0px';
        popup.style.top = '0px';
        const p = popup.getBoundingClientRect();
        const margin = 12;
        let left = r.left;
        if (left + p.width > window.innerWidth - margin) left = window.innerWidth - margin - p.width;
        if (left < margin) left = margin;
        let top = r.bottom + 8;
        if (top + p.height > window.innerHeight - margin) top = Math.max(margin, r.top - p.height - 8);
        popup.style.left = left + 'px';
        popup.style.top = top + 'px';
    });
}

function resetHelpPopup(tip) {
    const popup = tip.querySelector('.help-tip-popup');
    if (!popup) return;
    popup.style.position = '';
    popup.style.left = '';
    popup.style.top = '';
}

document.addEventListener('mouseover', (e) => {
    const tip = e.target.closest && e.target.closest('.help-tip');
    if (tip) clampHelpPopup(tip);
});
document.addEventListener('mouseout', (e) => {
    const tip = e.target.closest && e.target.closest('.help-tip');
    if (tip && !tip.contains(e.relatedTarget)) resetHelpPopup(tip);
});
document.addEventListener('focusin', (e) => {
    const tip = e.target.closest && e.target.closest('.help-tip');
    if (tip) clampHelpPopup(tip);
});
document.addEventListener('focusout', (e) => {
    const tip = e.target.closest && e.target.closest('.help-tip');
    if (tip) resetHelpPopup(tip);
});

// Modal keyboard behaviour for every wizard overlay and confirm dialog:
// focus moves into the dialog when it opens, Tab stays inside it, and
// Escape closes it through the modal's own close path (X, Cancel, or the
// backdrop handler as the last resort).
function topModal() {
    // Fixed-position overlays have no offsetParent, so visibility is
    // checked with the computed style and the client rects.
    const modals = [...document.querySelectorAll('.wizard-overlay')]
        .filter((m) => m.getClientRects().length > 0
            && getComputedStyle(m).display !== 'none'
            && getComputedStyle(m).visibility !== 'hidden');
    return modals.length > 0 ? modals[modals.length - 1] : null;
}

function focusFirstInModal(modal) {
    const target =
        modal.querySelector('.wizard-body input:not([type="hidden"]):not([disabled]), .wizard-body select:not([disabled]), .wizard-body textarea:not([disabled])') ||
        modal.querySelector('.wizard-footer button.btn-modern.secondary') ||
        modal.querySelector('.wizard-footer button');
    if (target) {
        try { target.focus(); } catch { /* focus is best effort */ }
    } else if (modal.querySelector('.wizard-modal')) {
        modal.querySelector('.wizard-modal').setAttribute('tabindex', '-1');
        try { modal.querySelector('.wizard-modal').focus(); } catch { /* ignore */ }
    }
}

function closeTopModal(overlay) {
    const closeButton = overlay.querySelector('.wizard-header .icon-button');
    if (closeButton) { closeButton.click(); return; }
    const cancel = overlay.querySelector('.wizard-footer button.btn-modern.secondary');
    if (cancel) { cancel.click(); return; }
    overlay.click();
}

const modalObserver = new MutationObserver((mutations) => {
    for (const mutation of mutations) {
        for (const node of mutation.addedNodes) {
            if (node.nodeType !== 1) continue;
            const modal = node.classList && node.classList.contains('wizard-overlay')
                ? node
                : node.querySelector && node.querySelector('.wizard-overlay');
            if (modal) requestAnimationFrame(() => focusFirstInModal(modal));
        }
    }
});
modalObserver.observe(document.documentElement, { childList: true, subtree: true });

document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
        const overlay = topModal();
        if (overlay) {
            closeTopModal(overlay);
            e.preventDefault();
            return;
        }
        // Escape also dismisses the guided tour (it covers the page).
        const tour = document.querySelector('.tour-overlay');
        if (tour) {
            const skip = [...tour.querySelectorAll('button')]
                .find((b) => b.textContent.includes('Skip tour'));
            if (skip) {
                skip.click();
                e.preventDefault();
            }
        }
        return;
    }
    if (e.key !== 'Tab') return;

    const overlay = topModal();
    if (!overlay) return;

    const focusables = [...overlay.querySelectorAll(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])')]
        .filter((el) => el.getClientRects().length > 0);
    if (focusables.length === 0) return;

    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    const active = document.activeElement;
    if (!overlay.contains(active)) {
        e.preventDefault();
        first.focus();
    } else if (e.shiftKey && active === first) {
        e.preventDefault();
        last.focus();
    } else if (!e.shiftKey && active === last) {
        e.preventDefault();
        first.focus();
    }
});

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