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
const DEFAULTS = { tree: 240, list: 300, chat: 340 };

// The detail (index) pane deliberately has NO persisted size — it fits its content (ADR 0550). This is only the
// CAP: how much vertical space it may ever take from the preview, which is the thing the user came to look at.
// A constant, not state: a drag is a peek and must not be able to raise it (see beginDrag / resetIndexSizing),
// so there is nothing that could ever change it. A stale `sizes.index` left in storage by an older build is
// simply never read.
const INDEX_CAP = 210;
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

// A tablet is a COARSE-POINTER device at or above this width. Width alone cannot identify one: an iPad Pro is
// 1024px in portrait and 1366px in landscape, so it would land in the tablet tier one way up and the DESKTOP
// tier the other. The pointer is what actually distinguishes a finger from a trackpad.
//
// Deliberately NOT applied to PHONE_MAX above. A narrow desktop window has a fine pointer, and requiring coarse
// there would hand it the overflowing multi-pane layout the phone tier exists to prevent.
const TABLET_MIN = 768;

// The primary pointer is a finger. `(pointer: coarse)` alone rather than `(hover: none) and (pointer: coarse)`
// (which isTouchOnly uses for a different question): a hybrid laptop with a touchscreen reports a FINE primary
// pointer and stays on the desktop layout either way, and the hover half is what a headless browser's touch
// emulation is least consistent about.
function coarsePointer() {
    return !!(window.matchMedia && window.matchMedia('(pointer: coarse)').matches);
}

// Which tier the viewport is in. Reported to Blazor because two things cannot be answered by a media query:
// the tap-to-navigate branch happens at CLICK time, and the touch top bar is conditionally RENDERED rather
// than hidden (its folder name would collide with the desktop tests' text locators).
//
// The conditions here MUST mirror the media queries in Home.razor's <style> — they are two readings of one
// decision, and a disagreement shows as a layout whose behaviour does not match its shape.

// One shared resize hook re-applies the active workbench's panes when the viewport crosses the breakpoint
// (debounced) and reports phone-ness to Blazor (the phone tap-to-navigate needs it at click time); the module
// loads once, so this listener is registered once.
let activeReapply = null;
let viewportRef = null;
let resizeTimer = 0;
function viewportMode() {
    const coarse = coarsePointer();
    if (window.innerWidth <= PHONE_MAX) return 'phone';
    if (!coarse || window.innerWidth < TABLET_MIN) return 'desktop';
    return window.matchMedia('(orientation: portrait)').matches ? 'tablet-portrait' : 'tablet-landscape';
}

function reportViewport() {
    if (viewportRef) viewportRef.invokeMethodAsync('OnViewportModeChanged', viewportMode());
}
window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => { if (activeReapply) activeReapply(); reportViewport(); }, 150);
});

// Report whether the viewport shows ONE pane to Blazor, now and on every subsequent resize. The signature is
// still a bool on purpose: a stale cached module (ADR 0500) then keeps working against the same C# method
// rather than failing to deserialise, and reports the phone answer it always did.
export function watchViewport(dotNetRef) {
    viewportRef = dotNetRef;
    reportViewport();
}

// A touch-ONLY device: no hover + coarse pointer. True on phones/tablets, FALSE on a hybrid with a mouse. Used to
// gate annotation authoring (#349) — a device capability, so read once (it doesn't change with viewport resize).
// The visitor's desktop OS, for showing the right mount instructions (#461). The browser cannot mount a drive
// itself, so the one useful thing it can do is tell the user how — and that differs per platform.
//
// userAgentData is the modern, un-deprecated source; navigator.platform is the fallback for browsers that lack
// it. Returns "mac" | "windows" | "linux" | "other" — deliberately coarse, because the instructions are.
export function desktopOs() {
    const p = (navigator.userAgentData?.platform || navigator.platform || '').toLowerCase();
    if (p.includes('mac')) return 'mac';
    if (p.includes('win')) return 'windows';
    if (p.includes('linux') || p.includes('x11')) return 'linux';
    return 'other';
}

// Brings the tree's current node into view after a reveal (#692). Called from Blazor AFTER the render that
// applies the mark, because the element does not exist until then.
//
// The arithmetic is done here rather than by scrollIntoView, which was tried first and moved nothing: the
// element is small, below the pane, and its nearest scrollable ancestor IS the pane — and the pane's scrollTop
// stayed at 0. Rather than keep guessing at which box the browser considered "nearest", this measures the two
// rectangles and scrolls the pane itself, which is the element we know scrolls.
//
// It implements all three decisions directly and visibly:
//   * only when OUT OF VIEW — an in-view node returns without touching scrollTop, so the pane never lurches
//     without cause, which is what makes a large movement acceptable in response to a small act;
//   * to the NEAREST EDGE — the delta is exactly the overhang, so a node just past the fold moves just past it;
//   * SMOOTHLY, so the eye follows the tree moving rather than finding it changed.
export function scrollTreeCurrentIntoView() {
    const pane = document.querySelector('[data-pane="tree"]');
    const node = pane?.querySelector('.wb-tree-current');
    if (!pane || !node) return false;

    const p = pane.getBoundingClientRect();
    const n = node.getBoundingClientRect();

    // A small tolerance: a node flush with the edge is in view, and rounding must not start a scroll.
    const margin = 2;
    let delta = 0;
    if (n.bottom > p.bottom - margin) {
        delta = n.bottom - p.bottom + margin;
    } else if (n.top < p.top + margin) {
        delta = n.top - p.top - margin;
    } else {
        return false; // already visible — nothing moves
    }

    pane.scrollTo({ top: pane.scrollTop + delta, behavior: 'smooth' });
    return true;
}

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
// A drag of the detail pane is a PEEK (ADR 0550): a transient height that ends at the next SELECTION change —
// which is when the fitted height would move anyway, so it is the moment the override stops meaning anything.
// The peek therefore survives while you work with the same document (scrolling its fields, editing them), which
// is when you wanted it, and leaves NO trace afterwards: it is never written to storage and — the part that was
// wrong before — it never raises INDEX_CAP either. A drag that permanently changed the pane's ceiling would be
// a lasting preference the user never asked to set (issue #413).
let indexPeek = null;

// Set by attach so the exported reset can reach the live workbench's applyPane. (An earlier version kept a
// `liveState` reference here and was suspected of module-instance splitting; that was never the problem —
// Blazor imports `./wbLayout.js` once and shares it — but routing through applyPane keeps the sizing rules in
// exactly one place regardless.)
let activeApplyPane = null;

export function resetIndexSizing() {
    if (indexPeek === null) return;
    indexPeek = null;
    if (activeApplyPane) activeApplyPane('index');
}

export function attach(root) {
    if (!root || typeof root.querySelector !== 'function') return false;
    // Idempotent per element instance: Blazor recreates the container when leaving/returning to the tab,
    // so a fresh element re-wires and re-applies persisted state; the same element no-ops.
    if (root.__wbLayout) return true;
    root.__wbLayout = true;

    const state = loadState();
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
        // A drag still overrides it (see the peek above), but only until the selection changes — that is exactly
        // when the fitted height would move anyway. Capped, because pure fit-content lets a long mask push the
        // preview down, which is the thing this rule exists to prevent.
        if (name === 'index') {
            if (indexPeek !== null) {
                el.style.maxHeight = ''; // a peek is allowed past the cap — that is the point of asking for it
                el.style.flex = `0 0 ${indexPeek}px`;
                return;
            }

            // `0 0 auto`, NOT `0 1 auto`: flex-shrink let the preview squeeze this pane below its own content,
            // producing a scrollbar while the cap still had room (measured: 253px of content shown in 225px,
            // cap 380px). A scrollbar here means the user must scroll to discover there was nothing more to
            // see, which is precisely what ADR 0550 forbids. Growth is bounded by max-height, so refusing to
            // shrink cannot cost the preview more than the cap.
            el.style.flex = '0 0 auto';
            el.style.maxHeight = `${INDEX_CAP}px`;
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
                const raw = cfg.mode === 'left' ? ev.clientX - rect.left
                    : cfg.mode === 'right' ? rect.right - ev.clientX
                        : ev.clientY - rect.top;
                const size = Math.round(Math.max(MIN, Math.min(raw, limit)));
                if (cfg.pane === 'index') {
                    // A peek, held apart from `state` so it can reach neither storage nor the cap (ADR 0550).
                    indexPeek = size;
                } else {
                    state.sizes[cfg.pane] = size;
                }
                applyPane(cfg.pane);
            };
            const onUp = () => {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.userSelect = '';
                document.body.style.cursor = '';
                // Safe to save wholesale: an index drag never touches `state`, so a peek cannot be persisted
                // here — which is what makes a fresh load, and the next selection, start fitted again.
                saveState(state);
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
    // A fresh workbench (a tab switch recreates the DOM) starts with no peek outstanding.
    indexPeek = null;
    activeApplyPane = applyPane;
    activeReapply = () => {
        for (const name of Object.keys(GUTTERS)) {
            applyPane(name);
            updateCaret(name);
        }
    };
    activeReapply();

    return true;
}
