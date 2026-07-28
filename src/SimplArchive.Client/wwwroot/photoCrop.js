// Profile-photo crop overlay (ADR "User profile photo"). Renders a picked image with a draggable +
// resizable SQUARE crop box; crop() rasterizes the selected square into a size×size PNG (base64). An ES
// module imported on demand by ProfilePhotoDialog, same pattern as preview.js / dropUpload.js.

const states = new WeakMap();
const MIN = 32; // minimum crop box size in display px

export function load(host, dataUrl) {
    host.innerHTML = '';
    host.style.position = 'relative';
    host.style.userSelect = 'none';
    host.style.touchAction = 'none';
    host.style.display = 'inline-block';
    host.style.lineHeight = '0';

    const img = document.createElement('img');
    img.src = dataUrl;
    img.draggable = false;
    img.style.display = 'block';
    img.style.maxWidth = '100%';
    img.style.maxHeight = '340px';

    const box = document.createElement('div');
    box.style.position = 'absolute';
    box.style.boxSizing = 'border-box';
    box.style.border = '2px solid #fff';
    box.style.boxShadow = '0 0 0 9999px rgba(0,0,0,.45)';
    box.style.cursor = 'move';

    const handle = document.createElement('div');
    handle.style.position = 'absolute';
    handle.style.right = '-7px';
    handle.style.bottom = '-7px';
    handle.style.width = '14px';
    handle.style.height = '14px';
    handle.style.background = '#fff';
    handle.style.borderRadius = '50%';
    handle.style.cursor = 'nwse-resize';
    box.appendChild(handle);

    host.appendChild(img);
    host.appendChild(box);

    const state = { img, box, rect: { x: 0, y: 0, size: 0 } };
    states.set(host, state);

    const init = () => {
        const dw = img.clientWidth, dh = img.clientHeight;
        const size = Math.floor(Math.min(dw, dh) * 0.8);
        state.rect = { x: Math.round((dw - size) / 2), y: Math.round((dh - size) / 2), size };
        paint(state);
    };
    if (img.complete && img.clientWidth) init(); else img.onload = init;

    const clampMove = (x, y) => {
        const dw = img.clientWidth, dh = img.clientHeight, s = state.rect.size;
        return { x: Math.max(0, Math.min(x, dw - s)), y: Math.max(0, Math.min(y, dh - s)) };
    };

    let drag = null;
    const onDown = (e, mode) => {
        e.preventDefault();
        drag = { mode, px: e.clientX, py: e.clientY, start: { ...state.rect } };
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
    };
    const onMove = (e) => {
        if (!drag) return;
        const dx = e.clientX - drag.px, dy = e.clientY - drag.py;
        const dw = img.clientWidth, dh = img.clientHeight;
        if (drag.mode === 'move') {
            const p = clampMove(drag.start.x + dx, drag.start.y + dy);
            state.rect.x = p.x; state.rect.y = p.y;
        } else {
            // Resize keeping square; grow by the larger delta, clamp to the image edges.
            let size = drag.start.size + Math.max(dx, dy);
            size = Math.max(MIN, Math.min(size, dw - state.rect.x, dh - state.rect.y));
            state.rect.size = Math.round(size);
        }
        paint(state);
    };
    const onUp = () => {
        drag = null;
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
    };
    box.addEventListener('pointerdown', (e) => { if (e.target !== handle) onDown(e, 'move'); });
    handle.addEventListener('pointerdown', (e) => onDown(e, 'resize'));
}

function paint(state) {
    const { box, rect } = state;
    box.style.left = rect.x + 'px';
    box.style.top = rect.y + 'px';
    box.style.width = rect.size + 'px';
    box.style.height = rect.size + 'px';
}

// Rasterize the selected square into a size×size PNG; returns the base64 (no data-URL prefix).
export function crop(host, size) {
    const s = states.get(host);
    if (!s) return null;
    const { img, rect } = s;
    const scale = img.naturalWidth / img.clientWidth;
    const canvas = document.createElement('canvas');
    canvas.width = size;
    canvas.height = size;
    const ctx = canvas.getContext('2d');
    ctx.drawImage(img, rect.x * scale, rect.y * scale, rect.size * scale, rect.size * scale, 0, 0, size, size);
    return canvas.toDataURL('image/png').split(',')[1];
}

export function dispose(host) {
    states.delete(host);
    if (host) host.innerHTML = '';
}
