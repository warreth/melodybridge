// MelodyBridge client helpers. Plain JS, no dependencies.
window.melody = (() => {
    const THEME_KEY = 'mb-theme';
    const ACCENT_KEY = 'mb-accent';
    // Palettes shipped in app.css; anything else falls back to teal.
    const ACCENTS = ['teal', 'blue', 'violet', 'rose', 'amber'];
    const THEME_MODES = ['auto', 'light', 'dark'];

    // The browser's own preference. Light stays the fallback when the
    // media query is unavailable.
    function preferredTheme() {
        try {
            return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
        } catch { return 'light'; }
    }

    // Auto follows the OS; light and dark are explicit choices.
    function resolveTheme(mode) {
        return mode === 'auto' ? preferredTheme() : mode;
    }

    function storedTheme() {
        try {
            const value = localStorage.getItem(THEME_KEY);
            return THEME_MODES.includes(value) ? value : 'auto';
        } catch { return 'auto'; }
    }

    function applyTheme(mode) {
        const preference = THEME_MODES.includes(mode) ? mode : 'auto';
        document.documentElement.setAttribute('data-theme-pref', preference);
        document.documentElement.setAttribute('data-theme', resolveTheme(preference));
    }

    function announceTheme(preference) {
        try {
            document.dispatchEvent(new CustomEvent('mb-theme', {
                detail: { preference, effective: resolveTheme(preference) }
            }));
        } catch { /* ignore */ }
    }

    function setTheme(theme) {
        const preference = THEME_MODES.includes(theme) ? theme : 'auto';
        applyTheme(preference);
        try { localStorage.setItem(THEME_KEY, preference); } catch { /* private mode */ }
        announceTheme(preference);
        return resolveTheme(preference);
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
        let savedAccent = null;
        try { savedAccent = localStorage.getItem(ACCENT_KEY); } catch { /* ignore */ }
        // No stored choice means Auto: keep following the browser until
        // the visitor picks light or dark explicitly.
        applyTheme(storedTheme());
        setAccent(savedAccent || 'teal');

        try {
            const media = window.matchMedia('(prefers-color-scheme: light)');
            const sync = () => {
                if (storedTheme() !== 'auto') return;
                applyTheme('auto');
                announceTheme('auto');
            };
            if (media.addEventListener) media.addEventListener('change', sync);
            else if (media.addListener) media.addListener(sync);
        } catch { /* media query unavailable */ }
    }

    // The settings page reads this once to mark the active swatch; falls
    // back to teal when the attribute is missing (fresh browser).
    function getAccent() {
        const value = document.documentElement.getAttribute('data-accent');
        return ACCENTS.includes(value) ? value : 'teal';
    }

    // The effective theme the browser actually shows.
    function getTheme() {
        const attr = document.documentElement.getAttribute('data-theme');
        if (attr === 'light' || attr === 'dark') return attr;
        return resolveTheme(storedTheme());
    }

    // The stored choice, including Auto.
    function getThemePreference() {
        const attr = document.documentElement.getAttribute('data-theme-pref');
        if (THEME_MODES.includes(attr)) return attr;
        return storedTheme();
    }

    // The shell keeps its three-state theme icon in sync when the theme
    // changes from Settings or from the OS while Auto is active.
    function watchTheme(dotNetRef) {
        if (window.__mbThemeWatcher) {
            document.removeEventListener('mb-theme', window.__mbThemeWatcher);
        }
        window.__mbThemeWatcher = (event) => {
            const detail = event.detail || {};
            dotNetRef.invokeMethodAsync(
                'OnThemeChanged',
                detail.preference || getThemePreference(),
                detail.effective || getTheme());
        };
        document.addEventListener('mb-theme', window.__mbThemeWatcher);
    }

    // Position the tour spotlight over an element and scroll it into view.
    // Returns true when the element exists; the caller falls back to a
    // centered card when it doesn't. The scroll is instant on purpose:
    // a smooth scroll makes the rect read stale and the box lands in the
    // wrong place until the next render.
    let lastSpotlightSelector = null;

    function positionSpotlight() {
        const el = lastSpotlightSelector && document.querySelector(lastSpotlightSelector);
        const box = document.getElementById('tour-spotlight');
        if (!el || !box) return false;
        const r = el.getBoundingClientRect();
        box.style.left = (r.left - 8) + 'px';
        box.style.top = (r.top - 8) + 'px';
        box.style.width = (r.width + 16) + 'px';
        box.style.height = (r.height + 16) + 'px';
        box.style.display = 'block';
        return true;
    }

    function spotlight(selector) {
        const el = document.querySelector(selector);
        if (!el) return false;
        lastSpotlightSelector = selector;
        el.scrollIntoView({ block: 'center' });
        return positionSpotlight();
    }

    function hideSpotlight() {
        lastSpotlightSelector = null;
        const box = document.getElementById('tour-spotlight');
        if (box) box.style.display = 'none';
    }

    // A resized window moves the spotlit element: keep the box glued to it
    // instead of leaving it at the pre-resize geometry.
    window.addEventListener('resize', () => {
        if (!document.querySelector('.tour-overlay')) return;
        requestAnimationFrame(positionSpotlight);
    });

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

    // Scroll a plugin card into view and flash it, retrying until Blazor
    // has rendered the card after a deep link or a toast action.
    function revealPlugin(pluginId) {
        let tries = 0;
        const tick = () => {
            const el = document.getElementById('plugin-' + pluginId);
            if (el) {
                el.scrollIntoView({ block: 'center', behavior: 'smooth' });
                el.classList.add('highlight');
                setTimeout(() => el.classList.remove('highlight'), 1800);
                return;
            }
            if (tries++ < 60) requestAnimationFrame(tick);
        };
        tick();
    }

    // Remember where a scroll container was when a dialog closed.
    function saveScroll(key, selector) {
        const el = document.querySelector(selector);
        if (!el) return;
        try { sessionStorage.setItem('mb-scroll-' + key, String(el.scrollTop)); } catch { /* ignore */ }
    }

    function restoreScroll(key, selector) {
        let tries = 0;
        const tick = () => {
            const el = document.querySelector(selector);
            if (el) {
                let value = 0;
                try { value = Number(sessionStorage.getItem('mb-scroll-' + key) || '0'); } catch { /* ignore */ }
                el.scrollTop = Number.isFinite(value) ? value : 0;
                return;
            }
            if (tries++ < 60) requestAnimationFrame(tick);
        };
        tick();
    }

    return {
        setTheme,
        setAccent,
        getAccent,
        getTheme,
        getThemePreference,
        watchTheme,
        revealPlugin,
        saveScroll,
        restoreScroll,
        init,
        spotlight,
        hideSpotlight,
        dismissErrorUi,
        focusSelector,
        // Copy text to the system clipboard and report whether it worked.
        // navigator.clipboard only exists in secure contexts (https or
        // localhost), and plain-http hosts are common for self-hosted
        // instances, so the old selection trick is the fallback instead of
        // a failed copy.
        copyText(text) {
            const fallbackCopy = () => {
                const area = document.createElement('textarea');
                area.value = text;
                area.setAttribute('readonly', '');
                area.style.position = 'fixed';
                area.style.left = '-9999px';
                document.body.appendChild(area);
                const selection = document.getSelection();
                const previous = selection && selection.rangeCount > 0 ? selection.getRangeAt(0) : null;
                area.select();
                let copied = false;
                try {
                    copied = document.execCommand('copy');
                } catch (err) {
                    copied = false;
                }
                area.remove();
                if (previous && selection) {
                    selection.removeAllRanges();
                    selection.addRange(previous);
                }
                return copied;
            };

            if (navigator.clipboard && navigator.clipboard.writeText) {
                return navigator.clipboard.writeText(text)
                    .then(() => true)
                    .catch(() => fallbackCopy());
            }
            return Promise.resolve(fallbackCopy());
        },
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

// The control that opened the current dialog, so focus can return to it.
let modalTrigger = null;

const modalObserver = new MutationObserver((mutations) => {
    for (const mutation of mutations) {
        for (const node of mutation.addedNodes) {
            if (node.nodeType !== 1) continue;
            const modal = node.classList && node.classList.contains('wizard-overlay')
                ? node
                : node.querySelector && node.querySelector('.wizard-overlay');
            if (modal) {
                if (!modalTrigger) modalTrigger = document.activeElement;
                requestAnimationFrame(() => focusFirstInModal(modal));
            }
        }
        for (const node of mutation.removedNodes) {
            if (node.nodeType !== 1) continue;
            const removedModal = node.classList && node.classList.contains('wizard-overlay')
                ? node
                : node.querySelector && node.querySelector('.wizard-overlay');
            if (removedModal && !document.querySelector('.wizard-overlay')) {
                // Focus returns to the control that opened the dialog.
                if (modalTrigger && document.contains(modalTrigger)) {
                    try { modalTrigger.focus(); } catch { /* best effort */ }
                }
                modalTrigger = null;
            }
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