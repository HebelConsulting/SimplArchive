// Preview renderer with a search hit-overlay (ADRs 0268/0269/0282, web parity). Renders images (as <img>) and
// PDFs (rasterized page-by-page with pdf.js) into a host element, draws per-page word/hit boxes from the
// server text-layout (normalized 0..1), supports find (highlight + count + prev/next) and click-to-copy a
// word to the clipboard. The host div is entirely JS-owned so Blazor re-renders never clobber it.
import * as pdfjsLib from './lib/pdfjs/pdf.min.js';
pdfjsLib.GlobalWorkerOptions.workerSrc = './lib/pdfjs/pdf.worker.min.js';

let state = null; // { host, pages:[{overlay, words, hits:[]}], dotNetRef, active }

const el = (tag, cls) => { const e = document.createElement(tag); if (cls) e.className = cls; return e; };

// ── Zoom (#357) ─────────────────────────────────────────────────────────────────────────────────────────────
// Zoom is a single CSS var on the host: .wb-pv-page { max-width: calc(100% * var(--wb-pv-zoom)) }. At 1 it's the
// fit-width default; above 1 the pages grow past the host and the existing overflow:auto scrolls. The hit/annotation
// overlays are percentage-based, so they scale with the page for free — no overlay math changes. Clamped 1..4 (the
// canvas is rasterized at 2×, so ~2× is the crisp ceiling; 4 allows a closer, softer look). State lives on the host
// dataset so each preview host (detail / fullscreen / recycle-bin) zooms independently.
// A touch-ONLY device (no hover, coarse pointer) — true on phones/tablets, FALSE on a hybrid with a mouse (its
// primary pointer is fine). Annotation authoring (draw / move / resize / marquee) is gated off on such devices
// (#349): the precise drag + hover affordances don't work by finger, so existing annotations stay read-only-
// visible but can't be created/edited/moved. The toolbar's authoring buttons are hidden in Blazor by the same test.
const TOUCH_ONLY = typeof window !== 'undefined' && window.matchMedia
    && window.matchMedia('(hover: none) and (pointer: coarse)').matches;

// 1 is fit-WIDTH, not the smallest useful zoom: for a portrait page in a pane wider than it is tall, fitting the
// width pushes the bottom of the page out of view — exactly when the user wants to see it AS A PAGE (#480). So the
// floor is the fit-PAGE scale, computed per host from the rendered page, and is normally below 1. Until a page has
// been measured the floor stays at 1, which is the behaviour that shipped.
const ZOOM_MAX = 4, ZOOM_FLOOR_MIN = 0.1;

const zoomFloor = host => parseFloat(host.dataset.zoomFloor || '1');

function applyZoom(host, z) {
    z = Math.min(ZOOM_MAX, Math.max(zoomFloor(host), z));
    host.dataset.zoom = z;
    host.style.setProperty('--wb-pv-zoom', z);
    host.classList.toggle('wb-pv-zoomed', z > 1.001);
}

export function zoomBy(host, mult) { applyZoom(host, parseFloat(host.dataset.zoom || '1') * mult); }
export function zoomReset(host) { applyZoom(host, 1); }

// Fit the WHOLE page in view (#480). Measures the first page as currently rendered and scales so its height fits
// the host, which for a portrait page is below 1 — hence the floor below.
//
// "Fit entire document" deliberately means fit the CURRENT page, not all of them: a PDF is rendered as N stacked
// pages, so fitting the lot would zoom a 40-page document to nothing.
export function fitPage(host) {
    const scale = fitPageScale(host);
    if (scale) {
        host.dataset.zoomFloor = scale;   // zooming out now walks down to whole-page and stops there
        applyZoom(host, scale);
    }
}

// The zoom at which one page's full height fits the host, or null when nothing is rendered yet. Derived from the
// page as currently drawn, so it is correct at any starting zoom rather than assuming the default.
function fitPageScale(host) {
    const page = host.querySelector('.wb-pv-page');
    if (!page || !host.clientHeight) return null;

    const drawn = page.getBoundingClientRect().height;
    if (!drawn) return null;

    const z = parseFloat(host.dataset.zoom || '1');
    return Math.max(ZOOM_FLOOR_MIN, Math.min(1, z * host.clientHeight / drawn));
}

// Wire two-finger pinch + Ctrl/⌘-wheel zoom once per host (the host element persists across loads; only its
// children are replaced, so guard against re-wiring). Single-finger touch still pans via native overflow scroll —
// touch-action:pan-x pan-y (CSS) keeps that while suppressing the browser's own pinch-zoom so ours wins.
function wireZoomGestures(host) {
    if (host.dataset.zoomWired) return;
    host.dataset.zoomWired = '1';
    const dist = t => Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);
    let pinch = null;
    host.addEventListener('touchstart', e => { if (e.touches.length === 2) pinch = { d: dist(e.touches), z: parseFloat(host.dataset.zoom || '1') }; }, { passive: true });
    host.addEventListener('touchmove', e => { if (pinch && e.touches.length === 2) { e.preventDefault(); applyZoom(host, pinch.z * dist(e.touches) / pinch.d); } }, { passive: false });
    host.addEventListener('touchend', e => { if (e.touches.length < 2) pinch = null; });
    host.addEventListener('wheel', e => { if (e.ctrlKey || e.metaKey) { e.preventDefault(); applyZoom(host, parseFloat(host.dataset.zoom || '1') * (e.deltaY < 0 ? 1.1 : 0.9)); } }, { passive: false });
}

// Renders the preview at `url` into `host`; `layout` is the per-page word boxes (or null). Returns the kind
// ('image'|'pdf'|'text'|'unsupported'|'error') plus the text for a text preview.
export async function load(host, url, layout, dotNetRef) {
    host.innerHTML = '';
    state = { host, pages: [], dotNetRef, active: -1 };
    delete host.dataset.zoomFloor;  // the floor belongs to the PAGE, not the host — a new document recomputes it
    applyZoom(host, 1);        // each new document opens at fit-width
    wireZoomGestures(host);    // idempotent — wires pinch/⌘-wheel once per host
    if (!url) return { kind: 'unsupported', text: '' };

    try {
        // Sniff by magic bytes rather than the Content-Type header — stored objects are often
        // application/octet-stream, which would misclassify a real PDF/image.
        const buf = await fetch(url).then(r => r.arrayBuffer());
        const b = new Uint8Array(buf.slice(0, 8));
        const magic = a => b.length >= a.length && a.every((v, i) => b[i] === v);

        if (magic([0x25, 0x50, 0x44, 0x46])) { // "%PDF"
            const pdf = await pdfjsLib.getDocument({ data: buf }).promise;
            for (let i = 1; i <= pdf.numPages; i++) {
                const page = await pdf.getPage(i);
                const viewport = page.getViewport({ scale: 2 });
                const canvas = el('canvas', 'wb-pv-media');
                canvas.width = viewport.width;
                canvas.height = viewport.height;
                await page.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;
                addPage(host, canvas, layout?.[i - 1]?.words || []);
            }
            return { kind: 'pdf', text: '' };
        }

        if (magic([0x89, 0x50, 0x4E, 0x47]) || magic([0xFF, 0xD8]) || magic([0x47, 0x49, 0x46])) { // PNG / JPEG / GIF
            const img = el('img', 'wb-pv-media');
            img.src = URL.createObjectURL(new Blob([buf]));
            addPage(host, img, layout?.[0]?.words || []);
            return { kind: 'image', text: '' };
        }

        // Otherwise treat as text (txt/json/xml).
        return { kind: 'text', text: new TextDecoder().decode(buf) };
    } catch {
        return { kind: 'error', text: '' };
    }
}

function addPage(host, media, words) {
    const page = el('div', 'wb-pv-page');
    const overlay = el('div', 'wb-pv-overlay');
    page.appendChild(media);
    page.appendChild(overlay);
    host.appendChild(page);

    const p = { index: state.pages.length, overlay, words: words.map(w => ({ text: w.text, x: w.x, y: w.y, w: w.width, h: w.height })), hits: [], notes: [] };
    overlay.addEventListener('click', e => onClick(e, p));
    attachDraw(overlay, p);
    attachMarquee(overlay, p);
    state.pages.push(p);
}

async function onClick(e, p) {
    const rect = e.currentTarget.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return;
    const nx = (e.clientX - rect.left) / rect.width;
    const ny = (e.clientY - rect.top) / rect.height;

    // Sticky-note placement mode (ADR "Document annotations") wins over word-copy: a click drops a note here.
    if (state.addMode) {
        state.addMode = false;
        state.host.classList.remove('wb-pv-adding');
        state?.dotNetRef?.invokeMethodAsync('OnAnnotationPlaced', p.index, nx, ny);
        return;
    }

    const hit = p.words.find(w => nx >= w.x && nx <= w.x + w.w && ny >= w.y && ny <= w.y + w.h);
    if (!hit) return;

    try {
        let text = hit.text;
        if (e.shiftKey) {
            let current = '';
            try { current = await navigator.clipboard.readText(); } catch { /* read may be denied */ }
            text = current ? `${current} ${hit.text}` : hit.text;
        }
        await navigator.clipboard.writeText(text);
        state?.dotNetRef?.invokeMethodAsync('OnPreviewWordCopied', hit.text, e.shiftKey);
    } catch { /* clipboard best-effort */ }
}

// Highlights every word containing any query term; returns the match count and jumps to the first match.
export function setFind(query) {
    if (!state) return 0;
    const terms = (query || '').split(/\s+/).map(t => t.trim().toLowerCase()).filter(t => t.length);
    const matches = [];

    for (const p of state.pages) {
        p.hits.forEach(h => h.remove());
        p.hits = [];
        if (!terms.length) continue;
        for (const w of p.words) {
            if (terms.some(t => w.text.toLowerCase().includes(t))) {
                const box = el('div', 'wb-pv-hit');
                box.style.left = `${w.x * 100}%`;
                box.style.top = `${w.y * 100}%`;
                box.style.width = `${w.w * 100}%`;
                box.style.height = `${w.h * 100}%`;
                p.overlay.appendChild(box);
                p.hits.push(box);
                matches.push(box);
            }
        }
    }

    state.matches = matches;
    state.active = matches.length ? 0 : -1;
    applyActive();
    return matches.length;
}

function applyActive() {
    if (!state.matches) return;
    state.matches.forEach((m, i) => m.classList.toggle('wb-pv-hit-active', i === state.active));
    if (state.active >= 0) state.matches[state.active].scrollIntoView({ block: 'center', behavior: 'smooth' });
}

export function next() {
    if (!state?.matches?.length) return 0;
    state.active = (state.active + 1) % state.matches.length;
    applyActive();
    return state.active + 1;
}

export function prev() {
    if (!state?.matches?.length) return 0;
    state.active = (state.active - 1 + state.matches.length) % state.matches.length;
    applyActive();
    return state.active + 1;
}

// Sticky notes / positional annotations (ADR "Document annotations"). Renders a coloured marker per note on
// its page; the rich view/edit UI lives in Blazor (a marker click calls back with the note id). Markers are a
// separate array from find-hits so setFind doesn't clear them.
export function setAnnotations(annos) {
    if (!state) return;
    for (const p of state.pages) {
        (p.notes || []).forEach(n => n.remove());
        p.notes = [];
    }
    // The selection count drives group-drag (a press on a selected item when >1 are selected moves them all).
    state.selectedCount = (annos || []).filter(a => a.selected).length;
    for (const a of (annos || [])) {
        const p = state.pages[a.pageIndex];
        if (!p) continue;

        // Markup shapes (highlight / rectangle / arrow / stamp / strikethrough / text-box / freehand, ADR 0525)
        // vs the original sticky note.
        if (a.kind && a.kind > 0) {
            const s = a.kind === 7 ? freehandEl(a.points, a.color) : shapeEl(a.kind);
            if (a.kind !== 7) layoutShape(s, a.kind, a.x, a.y, a.w || 0, a.h || 0, a.color);
            s.style.pointerEvents = 'auto';
            // Stamp + text-box show their caption/content as a centered label.
            if (a.kind === 4 || a.kind === 6) s.textContent = a.text || '';
            s.title = a.text || '';
            if (a.selected) s.classList.add('wb-pv-selected');
            attachInteract(s, p, a, false);
            // Box shapes get a corner resize grip; arrows (3) + freehand (7) are move-only.
            if (a.canEdit && (a.kind === 1 || a.kind === 2 || a.kind === 4 || a.kind === 5 || a.kind === 6)) {
                const grip = el('div', 'wb-pv-shape-grip');
                makeShapeResizable(grip, s, p, a);
                s.appendChild(grip);
            }
            p.overlay.appendChild(s);
            p.notes.push(s);
            continue;
        }

        // A sticky-note box showing its text, always visible (ADR "Post-it note boxes" web parity): top-left at
        // (x,y), width from the persisted extent (min via CSS), height auto-fitting the text.
        const m = el('div', 'wb-pv-note');
        m.style.left = `${a.x * 100}%`;
        m.style.top = `${a.y * 100}%`;
        if (a.w) m.style.width = `${a.w * 100}%`;
        // Honor the persisted height as a minimum, so the box shows its true (resized) height and can be made
        // taller than one line; the text still grows the box beyond this if it needs more room.
        if (a.h) m.style.minHeight = `${a.h * 100}%`;
        m.style.background = a.color;
        m.textContent = a.text || '';
        m.title = a.text || '';
        if (a.selected) m.classList.add('wb-pv-selected');
        // A single click selects; Ctrl/Cmd-click toggles; a double-click opens the edit dialog; a drag moves it
        // (or the whole selection). An author's note is also resizable via a corner grip (ADR "Annotation
        // multi-select" web parity).
        attachInteract(m, p, a, true);
        if (a.canEdit) {
            const grip = el('div', 'wb-pv-note-grip');
            makeResizable(grip, m, p, a.id);
            m.appendChild(grip);
        }
        p.overlay.appendChild(m);
        p.notes.push(m);
    }
}

// Applies the selection outline to the existing annotation elements WITHOUT rebuilding them (ADR "Annotation
// multi-select" web parity) — a full setAnnotations rebuild between the two clicks of a double-click would drop
// the element and break double-click-to-edit, so selection changes go through this lightweight path.
export function setSelection(ids) {
    if (!state) return;
    const set = new Set(ids || []);
    state.selectedCount = 0;
    for (const p of state.pages) {
        for (const elm of (p.notes || [])) {
            const sel = set.has(elm._annoId);
            elm.classList.toggle('wb-pv-selected', sel);
            if (sel) state.selectedCount++;
        }
    }
}

// Unified pointer interaction for a note box or a markup shape (ADRs "Annotation multi-select" / "Highlighting
// redesign"): single-click selects (Ctrl toggles), a drag moves the item (a note OR a shape) or the whole
// selection (any selected item when >1 are selected). Only a NOTE opens the edit dialog on double-click — a
// shape has no dialog (recolour via the toolbar palette, delete via select+delete). Blazor owns the selection.
function attachInteract(elm, p, a, isNote) {
    elm._annoId = a.id;
    let press = null;
    // Block the click from reaching the overlay (which would try to copy a word under the box).
    elm.addEventListener('click', e => e.stopPropagation());
    elm.addEventListener('pointerdown', e => {
        if (TOUCH_ONLY || e.button !== 0) return;
        e.stopPropagation();
        const box = elm.getBoundingClientRect();
        const group = a.selected && (state.selectedCount || 0) > 1;
        press = { x: e.clientX, y: e.clientY, offX: e.clientX - box.left, offY: e.clientY - box.top, ctrl: e.ctrlKey || e.metaKey, group, moved: false };
        if (a.canEdit) { try { elm.setPointerCapture(e.pointerId); } catch { /* best-effort */ } }
    });
    elm.addEventListener('pointermove', e => {
        if (!press || press.ctrl) return;
        if (!press.moved && (Math.abs(e.clientX - press.x) > 3 || Math.abs(e.clientY - press.y) > 3)) press.moved = true;
        if (!press.moved || !a.canEdit) return;
        const rect = p.overlay.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        // Live-move the pressed item; the rest of a group follows on release + reload. A note moves via its
        // top-left (grab offset); a shape re-lays-out at its start + the drag delta (keeps its extent).
        if (isNote) {
            elm.style.left = `${clamp01((e.clientX - press.offX - rect.left) / rect.width) * 100}%`;
            elm.style.top = `${clamp01((e.clientY - press.offY - rect.top) / rect.height) * 100}%`;
        } else {
            layoutShape(elm, a.kind, a.x + (e.clientX - press.x) / rect.width, a.y + (e.clientY - press.y) / rect.height, a.w || 0, a.h || 0, a.color);
        }
    });
    elm.addEventListener('pointerup', e => {
        if (!press) return;
        try { elm.releasePointerCapture(e.pointerId); } catch { /* may not be captured */ }
        const wasMoved = press.moved && (Math.abs(e.clientX - press.x) > 3 || Math.abs(e.clientY - press.y) > 3);
        const pr = press; press = null;
        if (wasMoved && a.canEdit && !pr.ctrl) {
            const rect = p.overlay.getBoundingClientRect();
            if (pr.group) {
                // The whole selection moves by the pure cursor displacement (geometry-independent).
                state?.dotNetRef?.invokeMethodAsync('OnAnnotationGroupMove', p.index, (e.clientX - pr.x) / rect.width, (e.clientY - pr.y) / rect.height);
            } else if (isNote) {
                const nx = clamp01((e.clientX - pr.offX - rect.left) / rect.width);
                const ny = clamp01((e.clientY - pr.offY - rect.top) / rect.height);
                state?.dotNetRef?.invokeMethodAsync('OnAnnotationMoved', a.id, p.index, nx, ny);
            } else {
                // A shape's start point moves by the drag delta (extent preserved).
                state?.dotNetRef?.invokeMethodAsync('OnAnnotationMoved', a.id, p.index, clamp01(a.x + (e.clientX - pr.x) / rect.width), clamp01(a.y + (e.clientY - pr.y) / rect.height));
            }
        } else {
            state?.dotNetRef?.invokeMethodAsync('OnAnnotationSelect', a.id, pr.ctrl);
        }
    });
    if (isNote) {
        elm.addEventListener('dblclick', e => { e.stopPropagation(); state?.dotNetRef?.invokeMethodAsync('OnAnnotationClicked', a.id); });
    }
}

// Drag a box shape's (highlight/rectangle) corner grip to resize its extent (ADR "Highlighting redesign"); the
// start point (x,y) is kept, the width/height follow the cursor. Persists via OnAnnotationResized.
function makeShapeResizable(grip, elm, p, a) {
    let active = false;
    grip.addEventListener('pointerdown', e => {
        if (TOUCH_ONLY || e.button !== 0) return;
        e.stopPropagation();
        e.preventDefault();
        active = true;
        try { grip.setPointerCapture(e.pointerId); } catch { /* best-effort */ }
    });
    grip.addEventListener('pointermove', e => {
        if (!active) return;
        const rect = p.overlay.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        const w = Math.max(0.01, (e.clientX - rect.left) / rect.width - a.x);
        const h = Math.max(0.01, (e.clientY - rect.top) / rect.height - a.y);
        layoutShape(elm, a.kind, a.x, a.y, w, h, a.color);
    });
    grip.addEventListener('pointerup', e => {
        if (!active) return;
        try { grip.releasePointerCapture(e.pointerId); } catch { /* may not be captured */ }
        active = false;
        const rect = p.overlay.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        const w = Math.max(0.01, (e.clientX - rect.left) / rect.width - a.x);
        const h = Math.max(0.01, (e.clientY - rect.top) / rect.height - a.y);
        state?.dotNetRef?.invokeMethodAsync('OnAnnotationResized', a.id, p.index, w, h);
    });
}

// A shape element for a markup kind (1 highlight, 2 rectangle, 3 arrow). Arrows are an SVG overlay spanning the
// page (0..100 viewBox, non-uniform); box shapes are positioned divs.
function shapeEl(kind) {
    if (kind === 3) {
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('class', 'wb-pv-shape wb-pv-arrow');
        svg.setAttribute('viewBox', '0 0 100 100');
        svg.setAttribute('preserveAspectRatio', 'none');
        svg.appendChild(document.createElementNS('http://www.w3.org/2000/svg', 'line'));
        svg.appendChild(document.createElementNS('http://www.w3.org/2000/svg', 'polygon'));
        return svg;
    }
    const cls = kind === 1 ? 'wb-pv-hl' : kind === 4 ? 'wb-pv-stamp' : kind === 5 ? 'wb-pv-strike' : kind === 6 ? 'wb-pv-textbox' : 'wb-pv-rect';
    return el('div', 'wb-pv-shape ' + cls);
}

// A freehand stroke as a full-page SVG polyline built from normalized "x,y x,y …" points (ADR 0525). Move-only.
function freehandEl(points, color) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'wb-pv-shape wb-pv-freehand');
    svg.setAttribute('viewBox', '0 0 100 100');
    svg.setAttribute('preserveAspectRatio', 'none');
    svg.style.left = '0'; svg.style.top = '0'; svg.style.width = '100%'; svg.style.height = '100%';
    const line = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
    const pts = (points || '').split(' ').map(pr => { const [px, py] = pr.split(','); return `${(+px) * 100},${(+py) * 100}`; }).join(' ');
    line.setAttribute('points', pts);
    if (color) line.style.stroke = color;
    svg.appendChild(line);
    return svg;
}

// Positions/sizes a shape from normalized geometry (x,y = start/top-left; w,h = signed extent).
function layoutShape(elm, kind, x, y, w, h, color) {
    if (kind === 3) {
        elm.style.left = '0'; elm.style.top = '0'; elm.style.width = '100%'; elm.style.height = '100%';
        const x1 = x * 100, y1 = y * 100, x2 = (x + w) * 100, y2 = (y + h) * 100;
        const line = elm.querySelector('line');
        line.setAttribute('x1', x1); line.setAttribute('y1', y1); line.setAttribute('x2', x2); line.setAttribute('y2', y2);
        // Arrowhead: a small triangle at the end, oriented along the line (viewBox units; minor skew on non-square pages).
        const ang = Math.atan2(y2 - y1, x2 - x1), hd = 3.5, sp = 0.5;
        const p1 = `${x2},${y2}`;
        const p2 = `${x2 - hd * Math.cos(ang - sp)},${y2 - hd * Math.sin(ang - sp)}`;
        const p3 = `${x2 - hd * Math.cos(ang + sp)},${y2 - hd * Math.sin(ang + sp)}`;
        elm.querySelector('polygon').setAttribute('points', `${p1} ${p2} ${p3}`);
        if (color) { line.style.stroke = color; elm.querySelector('polygon').style.fill = color; }
        return;
    }
    const left = Math.min(x, x + w), top = Math.min(y, y + h);
    elm.style.left = `${left * 100}%`; elm.style.top = `${top * 100}%`;
    elm.style.width = `${Math.abs(w) * 100}%`; elm.style.height = `${Math.abs(h) * 100}%`;
    if (color) {
        if (kind === 1) { elm.style.background = color; }         // highlight fill
        else if (kind === 5) { elm.style.color = color; }         // strikethrough: the CSS mid-line uses currentColor
        else { elm.style.borderColor = color; }                   // rectangle / stamp / text-box border
    }
}

// Draw a shape by dragging on a page overlay (ADR "Annotation markup"). On release, calls back with the
// normalized geometry (signed for arrows). A near-zero drag is ignored so a stray click doesn't draw.
function attachDraw(overlay, p) {
    let start = null, preview = null;
    overlay.addEventListener('pointerdown', e => {
        if (TOUCH_ONLY || e.button !== 0 || !state || !state.drawKind) return;
        const rect = overlay.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        e.stopPropagation(); e.preventDefault();
        start = { x: (e.clientX - rect.left) / rect.width, y: (e.clientY - rect.top) / rect.height, rect, kind: state.drawKind };
        try { overlay.setPointerCapture(e.pointerId); } catch { /* best-effort */ }
        preview = shapeEl(start.kind);
        preview.classList.add('wb-pv-shape-preview');
        overlay.appendChild(preview);
    });
    overlay.addEventListener('pointermove', e => {
        if (!start) return;
        const cx = clamp01((e.clientX - start.rect.left) / start.rect.width);
        const cy = clamp01((e.clientY - start.rect.top) / start.rect.height);
        layoutShape(preview, start.kind, start.x, start.y, cx - start.x, cy - start.y, null);
    });
    overlay.addEventListener('pointerup', e => {
        if (!start) return;
        try { overlay.releasePointerCapture(e.pointerId); } catch { /* may not be captured */ }
        const cx = clamp01((e.clientX - start.rect.left) / start.rect.width);
        const cy = clamp01((e.clientY - start.rect.top) / start.rect.height);
        const w = cx - start.x, h = cy - start.y, kind = start.kind, sx = start.x, sy = start.y;
        if (preview) { preview.remove(); preview = null; }
        start = null;
        if (Math.abs(w) < 0.01 && Math.abs(h) < 0.01) return; // too small — treat as a mis-click
        state?.dotNetRef?.invokeMethodAsync('OnShapeDrawn', p.index, kind, sx, sy, w, h);
    });
}

// Selects the active markup tool: 0 = none (word-copy), 1/2/3 = highlight/rectangle/arrow (drag to draw).
export function setDrawMode(kind) {
    if (!state) return;
    state.drawKind = kind | 0;
    state.host.classList.toggle('wb-pv-drawing', state.drawKind > 0);
}

const clamp01 = v => Math.max(0, Math.min(1, v));

// Marquee rubber-band select over empty page area (ADR "Annotation multi-select" web parity): a drag draws a
// box and selects the annotations it encloses (Ctrl adds to the current selection); a plain click clears it.
// Only active when no markup tool / add-note mode is on (notes/shapes stopPropagation, so this fires on empty).
function attachMarquee(overlay, p) {
    let start = null, boxEl = null, moved = false;
    overlay.addEventListener('pointerdown', e => {
        if (TOUCH_ONLY || e.button !== 0 || !state || state.drawKind || state.addMode) return;
        const rect = overlay.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        start = { x: e.clientX, y: e.clientY, rect };
        moved = false;
        try { overlay.setPointerCapture(e.pointerId); } catch { /* best-effort */ }
    });
    overlay.addEventListener('pointermove', e => {
        if (!start) return;
        if (!moved && (Math.abs(e.clientX - start.x) > 3 || Math.abs(e.clientY - start.y) > 3)) {
            moved = true;
            boxEl = el('div', 'wb-pv-marquee');
            overlay.appendChild(boxEl);
        }
        if (!moved) return;
        const l = Math.min(e.clientX, start.x), t = Math.min(e.clientY, start.y);
        boxEl.style.left = `${(l - start.rect.left) / start.rect.width * 100}%`;
        boxEl.style.top = `${(t - start.rect.top) / start.rect.height * 100}%`;
        boxEl.style.width = `${Math.abs(e.clientX - start.x) / start.rect.width * 100}%`;
        boxEl.style.height = `${Math.abs(e.clientY - start.y) / start.rect.height * 100}%`;
    });
    overlay.addEventListener('pointerup', e => {
        if (!start) return;
        try { overlay.releasePointerCapture(e.pointerId); } catch { /* may not be captured */ }
        const wasMarquee = moved;
        const s = start; start = null; moved = false;
        if (boxEl) { boxEl.remove(); boxEl = null; }
        if (wasMarquee) {
            const mr = { left: Math.min(e.clientX, s.x), top: Math.min(e.clientY, s.y), right: Math.max(e.clientX, s.x), bottom: Math.max(e.clientY, s.y) };
            const ids = (p.notes || []).filter(elm => {
                const r = elm.getBoundingClientRect();
                return r.left < mr.right && r.right > mr.left && r.top < mr.bottom && r.bottom > mr.top;
            }).map(elm => elm._annoId).filter(Boolean);
            state?.dotNetRef?.invokeMethodAsync('OnAnnotationMarquee', ids, e.ctrlKey || e.metaKey);
        } else {
            state?.dotNetRef?.invokeMethodAsync('OnAnnotationClearSelection');
        }
    });
}

// Drag a note box's corner grip to resize it (ADR "Post-it note boxes" web parity): sets the box width from the
// cursor (width) + downward (height). On release, persists the normalized width/height via OnAnnotationResized.
function makeResizable(grip, m, p, id) {
    let start = null;
    grip.addEventListener('pointerdown', e => {
        if (TOUCH_ONLY || e.button !== 0) return;
        e.stopPropagation();
        e.preventDefault();
        const box = m.getBoundingClientRect();
        start = { left: box.left, top: box.top };
        try { grip.setPointerCapture(e.pointerId); } catch { /* best-effort */ }
    });
    grip.addEventListener('pointermove', e => {
        if (!start) return;
        const rect = p.overlay.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        const w = Math.max(90, e.clientX - start.left);  // px, min matches the CSS min-width
        const h = Math.max(24, e.clientY - start.top);   // px, at least one line
        m.style.width = `${clamp01(w / rect.width) * 100}%`;
        m.style.minHeight = `${clamp01(h / rect.height) * 100}%`;
    });
    grip.addEventListener('pointerup', e => {
        if (!start) return;
        try { grip.releasePointerCapture(e.pointerId); } catch { /* may not be captured */ }
        start = null;
        const rect = p.overlay.getBoundingClientRect();
        const box = m.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) return;
        const nw = clamp01(box.width / rect.width);
        const nh = clamp01(box.height / rect.height);
        state?.dotNetRef?.invokeMethodAsync('OnAnnotationResized', id, p.index, nw, nh);
    });
}

// Toggles placement mode: the next click on a page drops a note (see onClick).
export function setAddMode(on) {
    if (!state) return;
    state.addMode = !!on;
    state.host.classList.toggle('wb-pv-adding', !!on);
}

// Enters/leaves the preview's in-app full-screen (ADR 0295). On enter it (a) measures the bottom tab bar and
// publishes its height as `--wb-tab-h` so the fixed overlay stops just above the tabs (keeping them clickable),
// and (b) registers a document-level Escape listener that asks .NET to exit — kept here (not a Blazor
// @onkeydown) because the preview host owns its DOM and clicking into it moves focus away from any Blazor
// parent, so a component-level key handler wouldn't reliably see Escape.
let escHandler = null;
export function setFullscreen(dotNetRef, on) {
    if (escHandler) { document.removeEventListener('keydown', escHandler); escHandler = null; }
    if (on) {
        const tabs = document.querySelector('.wb-tabs');
        document.documentElement.style.setProperty('--wb-tab-h', `${tabs ? tabs.offsetHeight : 41}px`);
        escHandler = e => { if (e.key === 'Escape') dotNetRef.invokeMethodAsync('OnPreviewEscape'); };
        document.addEventListener('keydown', escHandler);
    }
}

// Page thumbnails as data URLs, for the intray's sort-pages dialog (#487, ADR 0575).
//
// Here rather than in a module of its own because pdf.js is already imported and configured in this file, and a
// second import would pull a second copy of the worker. The desktop does the same thing with PDFium — the two
// clients rasterise with what each already has, rather than the server growing a per-page rendition endpoint
// for PDFs that nothing else would use.
//
// Rendered small deliberately: a 40-page scan at preview scale is a lot of pixels to hold as base64 for
// pictures displayed 130 px wide.
export async function pageThumbnails(url, width) {
    const target = width || 260;
    const buf = await fetch(url).then(r => r.arrayBuffer());
    const pdf = await pdfjsLib.getDocument({ data: buf }).promise;
    const thumbnails = [];

    for (let i = 1; i <= pdf.numPages; i++) {
        const page = await pdf.getPage(i);
        const unscaled = page.getViewport({ scale: 1 });
        const viewport = page.getViewport({ scale: target / unscaled.width });
        const canvas = document.createElement('canvas');
        canvas.width = viewport.width;
        canvas.height = viewport.height;
        await page.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;
        thumbnails.push(canvas.toDataURL('image/png'));
    }

    return thumbnails;
}
