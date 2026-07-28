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
        const collapsed = state.collapsed[name];
        el.style.flex = collapsed ? '0 0 0px' : `0 0 ${state.sizes[name]}px`;
        if (collapsed) el.dataset.collapsed = '1'; else delete el.dataset.collapsed;
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
                applyPane(cfg.pane);
            };
            const onUp = () => {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.userSelect = '';
                document.body.style.cursor = '';
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

    // Restore persisted sizes/collapsed state on attach.
    for (const name of Object.keys(GUTTERS)) {
        applyPane(name);
        updateCaret(name);
    }

    return true;
}
