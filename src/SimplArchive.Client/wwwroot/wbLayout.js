// Resizable + collapsible workbench panes (see ADR "Resizable/collapsible workbench panes").
//
// Pure JS interop — no dependency. Blazor owns the pane DOM; this module only wires drag/collapse
// behaviour and applies sizes via inline `flex` + a `data-collapsed` marker (neither of which Blazor
// manages on these elements, so re-renders of pane *content* don't clobber them). Sizes/collapsed state
// persist per browser in localStorage and are restored on attach.
//
// Panes are identified by `data-pane`; gutters by `data-gutter` (each sits next to the pane it sizes).

const KEY = 'simplarchive.wb-layout';

// Default pane extents (px along the resize axis) and, per gutter, which pane it resizes and how the
// pointer maps to a size: 'left' = pointer.x - pane.left, 'right' = pane.right - pointer.x (pane is
// right-anchored), 'top' = pointer.y - pane.top.
const DEFAULTS = { tree: 240, list: 300, index: 210, chat: 340 };
const MIN = 90;
const GUTTERS = {
    tree: { pane: 'tree', mode: 'left' },
    list: { pane: 'list', mode: 'left' },
    index: { pane: 'index', mode: 'top' },
    chat: { pane: 'chat', mode: 'right' },
};

// Below this viewport width the responsive CSS media queries (Home.razor) govern pane sizing — collapsing the
// lower-priority panes for iPad/phone (ADR "Responsive web workbench"). wbLayout.js then stops applying the
// persisted px widths (which would overflow a narrow screen) but keeps the state for when the viewport widens.
const WIDE_MIN = 1200;

// The phone single-pane drill-down mode kicks in at/below this width (ADR "Responsive phone drill-down").
const PHONE_MAX = 767;

// One shared resize hook re-applies the active workbench's panes when the viewport crosses the breakpoint
// (debounced) and reports phone-ness to Blazor (the phone tap-to-navigate needs it at click time); the module
// loads once, so this listener is registered once.
let activeReapply = null;
let viewportRef = null;
let resizeTimer = 0;
function reportViewport() {
    if (viewportRef) viewportRef.invokeMethodAsync('OnViewportChanged', window.innerWidth <= PHONE_MAX);
}
window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => { if (activeReapply) activeReapply(); reportViewport(); }, 150);
});

// Report whether the viewport is phone-sized to Blazor, now and on every subsequent resize.
export function watchViewport(dotNetRef) {
    viewportRef = dotNetRef;
    reportViewport();
}

// A touch-ONLY device: no hover + coarse pointer. True on phones/tablets, FALSE on a hybrid with a mouse. Used to
// gate annotation authoring (#349) — a device capability, so read once (it doesn't change with viewport resize).
export function isTouchOnly() {
    return !!(window.matchMedia && window.matchMedia('(hover: none) and (pointer: coarse)').matches);
}

function loadState() {
    let s;
    try { s = JSON.parse(localStorage.getItem(KEY)); } catch { /* ignore */ }
    s = s || {};
    s.sizes = { ...DEFAULTS, ...(s.sizes || {}) };
    s.collapsed = { tree: false, list: false, index: false, chat: false, ...(s.collapsed || {}) };
    return s;
}

function saveState(s) {
    try { localStorage.setItem(KEY, JSON.stringify(s)); } catch { /* ignore */ }
}

function caretGlyph(mode, collapsed) {
    switch (mode) {
        case 'left': return collapsed ? '›' : '‹';
        case 'right': return collapsed ? '‹' : '›';
        case 'top': return collapsed ? '▾' : '▴';
        default: return '‹';
    }
}

// Returns true once wired (or already wired), false if `root` isn't a real element yet — the workbench DOM
// lives inside <Authorized>, so on a page reload auth may still be resolving when this is first called.
// Ends a temporary drag of the detail pane, restoring fit-to-content (ADR 0550). Called when the SELECTION
// changes: that is when the fitted height would move anyway, so it is the moment the override stops meaning
// anything. A drag therefore survives while you work with the same document — scrolling its fields, editing
// them — which is when you wanted it.
// KNOWN BROKEN (verified in a browser): after a real drag and a genuine selection change the pane stays at the
// dragged height. Fit-content itself works; only this reset does not. The likely cause is that the exported
// function and the state the gutter mutates are not the same instance in practice — Blazor imports the module
// per call site, so `liveState` may belong to a different module instance than `attach` populated. Do not trust
// this until it is fixed and re-verified in a browser; the fit-content default below is unaffected.
export function resetIndexSizing() {
    // The LIVE state, not loadState(): the dragged flag is deliberately never written to storage, so a reader
    // that reloads would always see it unset and this would silently never reset anything.
    if (!liveState || !liveState.sizes.indexDragged) return;
    delete liveState.sizes.indexDragged;
    const el = document.querySelector('[data-pane="index"]');
    if (el) { el.style.flex = '0 1 auto'; el.style.maxHeight = `${liveState.sizes.index}px`; }
}

// Set by attach so the exported reset can reach the same state object the gutters mutate.
let liveState = null;

export function attach(root) {
    if (!root || typeof root.querySelector !== 'function') return false;
    // Idempotent per element instance: Blazor recreates the container when leaving/returning to the tab,
    // so a fresh element re-wires and re-applies persisted state; the same element no-ops.
    if (root.__wbLayout) return true;
    root.__wbLayout = true;

    const state = loadState();
    liveState = state;
    const pane = name => root.querySelector(`[data-pane="${name}"]`);

    function applyPane(name) {
        const el = pane(name);
        if (!el) return;
        if (window.innerWidth < WIDE_MIN) {
            // Responsive tiers govern (CSS media queries) — clear the JS inline sizing so it doesn't override
            // them; the persisted state is untouched and re-applies once the viewport widens again.
            el.style.flex = '';
            delete el.dataset.collapsed;
            return;
        }
        const collapsed = state.collapsed[name];
        if (collapsed) {
            el.style.flex = '0 0 0px';
            el.dataset.collapsed = '1';
            return;
        }
        delete el.dataset.collapsed;

        // The index (detail) pane FITS ITS CONTENT rather than a remembered height (ADR 0550). Its correct height
        // is decided by what is selected — four rows for a folder, many for a long mask — so a height dragged for
        // one document is wrong for the next one clicked, and persisting it stores noise. The other panes are not
        // like that: a tree or list WIDTH does not depend on the selection, so those stay persisted.
        //
        // A drag still overrides it (see beginDrag), but only until the selection changes — that is exactly when
        // the fitted height would move anyway. Capped, because pure fit-content lets a long mask push the preview
        // down, which is the thing this rule exists to prevent.
        if (name === 'index' && !state.sizes.indexDragged) {
            el.style.flex = '0 1 auto';
            el.style.maxHeight = `${state.sizes.index}px`;
            return;
        }

        el.style.maxHeight = '';
        el.style.flex = `0 0 ${state.sizes[name]}px`;
    }

    function updateCaret(name) {
        const g = root.querySelector(`[data-gutter="${name}"]`);
        const btn = g && g.querySelector('.wb-gutter-toggle');
        if (btn) btn.textContent = caretGlyph(GUTTERS[name].mode, state.collapsed[name]);
    }

    for (const [name, cfg] of Object.entries(GUTTERS)) {
        const g = root.querySelector(`[data-gutter="${name}"]`);
        if (!g) continue;
        const btn = g.querySelector('.wb-gutter-toggle');

        // Drag to resize — but not when the grab starts on the toggle button, and not on a collapsed pane.
        g.addEventListener('mousedown', e => {
            if (btn && (e.target === btn || btn.contains(e.target))) return;
            if (state.collapsed[cfg.pane]) return;
            const el = pane(cfg.pane);
            if (!el) return;
            e.preventDefault();
            const rect = el.getBoundingClientRect();
            const vertical = cfg.mode === 'top';
            const limit = vertical ? window.innerHeight * 0.7 : window.innerWidth * 0.7;

            const onMove = ev => {
                let size = cfg.mode === 'left' ? ev.clientX - rect.left
                    : cfg.mode === 'right' ? rect.right - ev.clientX
                        : ev.clientY - rect.top;
                state.sizes[cfg.pane] = Math.round(Math.max(MIN, Math.min(size, limit)));
                // Dragging the detail pane overrides its fit-to-content height — but TEMPORARILY, and this flag
                // is deliberately not saved: see applyPane and resetIndexSizing (ADR 0550).
                if (cfg.pane === 'index') state.sizes.indexDragged = true;
                applyPane(cfg.pane);
            };
            const onUp = () => {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.userSelect = '';
                document.body.style.cursor = '';
                // The dragged flag never reaches storage: a fresh load starts fitted again, which is the point.
                const { indexDragged, ...persisted } = state.sizes;
                saveState({ ...state, sizes: persisted });
            };
            document.body.style.userSelect = 'none';
            document.body.style.cursor = vertical ? 'row-resize' : 'col-resize';
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });

        if (btn) {
            btn.addEventListener('click', e => {
                e.stopPropagation();
                state.collapsed[cfg.pane] = !state.collapsed[cfg.pane];
                applyPane(cfg.pane);
                updateCaret(cfg.pane);
                saveState(state);
            });
        }
    }

    // Restore persisted sizes/collapsed state on attach, and register this workbench as the resize target.
    activeReapply = () => {
        for (const name of Object.keys(GUTTERS)) {
            applyPane(name);
            updateCaret(name);
        }
    };
    activeReapply();

    return true;
}
