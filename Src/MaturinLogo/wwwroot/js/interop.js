// Maturin Logo browser interop.
//
// Four jobs: capture every keystroke as a token the interpreter understands, scale the 40x24 grid
// to fit the window, paint the turtle's page on a canvas, and talk to localStorage / the download
// plumbing. This file never touches the text grid's DOM - Blazor owns that.

let dotNetRef = null;
let keyHandler = null;
let fitHandler = null;

// ---- keyboard ---------------------------------------------------------------

/**
 * Map a KeyboardEvent to a token, or null when the key is not the app's business.
 *
 * Alt stands in for the Open Apple key. Tokens are uppercase and stable so the C# side can switch
 * on them without caring about the physical layout.
 */
function mapKey(e) {
    const k = e.key;

    switch (k) {
        case 'ArrowUp': return 'UP';
        case 'ArrowDown': return 'DOWN';
        case 'ArrowLeft': return 'LEFT';
        case 'ArrowRight': return 'RIGHT';
        case 'Home': return 'HOME';
        case 'End': return 'END';
        case 'PageUp': return 'PGUP';
        case 'PageDown': return 'PGDN';
        case 'Backspace': return 'BKSP';
        case 'Delete': return 'DEL';
        case 'Enter': return 'ENTER';
        case 'Escape': return 'ESC';
        case 'Tab': return 'TAB';
        case ' ': return 'SPACE';
    }

    // Ctrl shortcuts: browsers let Ctrl+/ through where Alt+/ usually does not, so Ctrl+/ is the
    // dependable route to the quick reference on layouts where "?" is not on the 1 key.
    if (e.ctrlKey && !e.altKey && !e.metaKey) {
        if (k === '/') return 'C-SLASH';
        if (k === 's' || k === 'S') return 'C-S';   // the browser save dialog must not open
        return null;
    }

    if (e.altKey && !e.ctrlKey && !e.metaKey) {
        if (k === '?') return 'A-?';
        if (k === '/') return 'A-SLASH';
        // Some layouts deliver AltGr-composed characters instead of a plain letter; take the
        // uppercase base so A-S always means the same command.
        if (k.length === 1) return 'A-' + k.toUpperCase();
        return null;
    }

    if (e.metaKey) return null;   // leave the operating system's keys alone

    if (k.length === 1) return k;
    return null;
}

function onKeydown(e) {
    // If focus sits on a native control, let the browser handle it. Forwarding Enter while the
    // Blazor error banner's Reload link has focus reloads the page mid-program.
    const t = e.target;
    if (t && t.closest && t.closest('a, button, input, select, textarea')) return;

    const token = mapKey(e);
    if (token === null) return;

    // Everything else is swallowed: no page scroll, no browser find-bar, no menu focus.
    e.preventDefault();
    e.stopPropagation();
    dotNetRef.invokeMethodAsync('OnKey', token);
}

/** Attach the key handler and the resize watcher. */
export function initApp(ref, cols, rows) {
    dotNetRef = ref;
    if (keyHandler) window.removeEventListener('keydown', keyHandler, true);
    keyHandler = onKeydown;
    window.addEventListener('keydown', keyHandler, true);

    if (fitHandler) window.removeEventListener('resize', fitHandler);
    // Resizing the window changes the font size AND the canvas: a canvas keeps its old bitmap when
    // its CSS box grows, so without this the turtle picture is stretched until the next full replay.
    fitHandler = () => { fitGrid(cols, rows); resizeGraphics(); };
    window.addEventListener('resize', fitHandler);

    fitGrid(cols, rows);
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(() => fitGrid(cols, rows));
}

/**
 * Choose a font size so cols x rows of a 0.5:1 aspect grid fills the window. One number drives
 * the whole machine because every other dimension in the CSS is expressed in em.
 */
export function fitGrid(cols, rows) {
    const el = document.getElementById('ml-screen');
    if (!el) return 0;
    const w = window.innerWidth - 16;
    const h = window.innerHeight - 16;
    const size = Math.max(8, Math.floor(Math.min(w / (cols * 0.5), h / rows)));
    el.style.fontSize = size + 'px';
    return size;
}

// ---- theme ------------------------------------------------------------------

export function setTheme(theme, crt) {
    if (theme === 'amber') document.documentElement.setAttribute('data-theme', 'amber');
    else document.documentElement.removeAttribute('data-theme');
    document.documentElement.classList.toggle('crt-on', !!crt);
}

// ---- cooperative yielding ---------------------------------------------------

/**
 * Resolve on the next animation frame so the browser can paint while a long Logo program runs.
 *
 * Without this, Blazor WASM's single thread means REPEAT 10000 [FD 1] freezes the page and the
 * child has no way to stop it. A timer is armed as a backup because animation frames stop firing
 * in a hidden tab, where a program that only yields on frames would look hung forever.
 */
export function yieldFrame() {
    return new Promise((resolve) => {
        let done = false;
        const finish = () => { if (!done) { done = true; resolve(); } };
        requestAnimationFrame(finish);
        setTimeout(finish, 32);
    });
}

// ---- graphics page ----------------------------------------------------------

const HIRES_W = 280;
const HIRES_H = 192;

/* The six colours an Apple II hi-res screen could actually show. Logo's SETPC 0-15 is folded onto
   these by the caller, so what appears is what the real machine was capable of. */
const PALETTE = [
    '#000000', '#33CC33', '#FFFFFF', '#AA66FF', '#FF9933', '#5588FF',
];

const gfx = {
    el: null,
    ctx: null,
    items: [],
    bg: 0,
    gridRows: 24,
    lowerLines: 0,
    // The turtle is live state, not part of the replayed picture, so it is sent separately and
    // drawn on top. Hidden until Logo says otherwise, matching Apple Logo's HT/ST.
    turtle: null,
};

/** Draw the turtle as the arrow Apple Logo showed: a point ahead, two barbs behind. */
function drawTurtle(g, t, sx, sy, scale) {
    if (!t || t.visible === false) return;

    // Logo heading 0 faces north and grows clockwise; scene y grows downwards, so north is -y.
    const rad = (t.heading * Math.PI) / 180;
    const fx = Math.sin(rad), fy = -Math.cos(rad);       // forward
    const rx = Math.cos(rad), ry = Math.sin(rad);        // right

    const cx = sx + t.x * scale;
    const cy = sy + t.y * scale;

    const len = Math.max(4, 9 * scale);
    const wing = Math.max(2, 4 * scale);

    // Tip, then the two back corners.
    const tipX = cx + fx * len, tipY = cy + fy * len;
    const backX = cx - fx * len * 0.5, backY = cy - fy * len * 0.5;
    const lX = backX - rx * wing, lY = backY - ry * wing;
    const rX = backX + rx * wing, rY = backY + ry * wing;

    g.fillStyle = paletteOf(t.color);
    g.beginPath();
    g.moveTo(tipX, tipY);
    g.lineTo(lX, lY);
    g.lineTo(backX, backY);
    g.lineTo(rX, rY);
    g.closePath();
    g.fill();
}

export function initGraphics(gridRows) {
    gfx.gridRows = gridRows;
    gfx.el = document.getElementById('ml-canvas');
    gfx.ctx = gfx.el ? gfx.el.getContext('2d') : null;
    resizeGraphics();
}

/** Redraw at the current size: the picture is replayed from its item list, never stretched. */
function resizeGraphics() {
    if (!gfx.el || !gfx.ctx) return;
    const holder = document.getElementById('ml-graphics');
    const screen = document.getElementById('ml-screen');
    if (!holder || !screen) return;

    const rect = screen.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return;

    const cellH = rect.height / gfx.gridRows;
    const topRows = gfx.gridRows - gfx.lowerLines;
    const heightPx = Math.max(1, Math.round(cellH * topRows));
    const widthPx = Math.max(1, Math.round(rect.width));

    const dpr = Math.min(3, window.devicePixelRatio || 1);
    gfx.el.width = Math.round(widthPx * dpr);
    gfx.el.height = Math.round(heightPx * dpr);
    holder.style.height = heightPx + 'px';

    paint();
}

function paletteOf(colour) {
    const index = ((colour % PALETTE.length) + PALETTE.length) % PALETTE.length;
    return PALETTE[index];
}

/**
 * One stroke with the Apple II's chromatic fringe: the red and blue sub-pixel edges a real
 * television smears around a bright line. Drawn as two faint offset copies under the true colour.
 */
function strokeSegment(g, seg, sx, sy, scale) {
    const x1 = sx + seg.x1 * scale;
    const y1 = sy + seg.y1 * scale;
    const x2 = sx + seg.x2 * scale;
    const y2 = sy + seg.y2 * scale;
    const width = Math.max(1, (seg.width || 1) * scale);
    const fringe = Math.max(0.6, scale * 0.5);

    g.lineWidth = width;
    g.lineCap = 'round';

    g.strokeStyle = 'rgba(255, 60, 60, 0.35)';
    g.beginPath(); g.moveTo(x1 - fringe, y1); g.lineTo(x2 - fringe, y2); g.stroke();

    g.strokeStyle = 'rgba(60, 90, 255, 0.35)';
    g.beginPath(); g.moveTo(x1 + fringe, y1); g.lineTo(x2 + fringe, y2); g.stroke();

    g.strokeStyle = paletteOf(seg.color);
    g.beginPath(); g.moveTo(x1, y1); g.lineTo(x2, y2); g.stroke();
}

function drawLabel(g, label, sx, sy, scale) {
    g.fillStyle = paletteOf(label.color);
    g.font = `${Math.max(8, Math.round(9 * scale))}px 'VT323', monospace`;
    g.textBaseline = 'middle';
    g.fillText(label.text || '', sx + label.x1 * scale, sy + label.y1 * scale);
}

function paint() {
    const g = gfx.ctx;
    if (!g || !gfx.el) return;
    const w = gfx.el.width, h = gfx.el.height;

    g.setTransform(1, 0, 0, 1, 0, 0);
    g.fillStyle = paletteOf(gfx.bg);
    g.fillRect(0, 0, w, h);

    // Fit 280x192 into the region and keep the aspect ratio, or a circle comes out an ellipse.
    const scale = Math.min(w / HIRES_W, h / HIRES_H);
    if (!isFinite(scale) || scale <= 0) return;
    const sx = (w - HIRES_W * scale) / 2;
    const sy = (h - HIRES_H * scale) / 2;

    for (const item of gfx.items) {
        if (item.kind === 'segment') strokeSegment(g, item, sx, sy, scale);
        else if (item.kind === 'label') drawLabel(g, item, sx, sy, scale);
    }

    // The turtle is drawn last so it always sits above the picture it is making.
    drawTurtle(g, gfx.turtle, sx, sy, scale);
}

/**
 * Update the turtle's live position and heading. Sent on every repaint because it moves between
 * strokes, and it is not part of the replayable item list.
 */
export function setTurtle(json) {
    gfx.turtle = json ? JSON.parse(json) : null;
    paint();
}

/**
 * Replace the whole picture. Called on start-up, on session load, and whenever the region changes,
 * because resizing a canvas throws away its pixels.
 *
 * `textRows` is how many rows at the bottom belong to the listener; the picture takes the rest.
 */
export function replayGraphics(itemsJson, background, textRows, gridRows) {
    if (gridRows) gfx.gridRows = gridRows;
    if (typeof textRows === 'number') gfx.lowerLines = Math.max(0, Math.min(gfx.gridRows, textRows));
    gfx.bg = background | 0;
    gfx.items = itemsJson ? JSON.parse(itemsJson) : [];
    resizeGraphics();
}

/** Add freshly drawn items without rebuilding the list - the path a running program uses. */
export function appendGraphics(itemsJson) {
    if (!itemsJson) return;
    const added = JSON.parse(itemsJson);
    for (const item of added) gfx.items.push(item);
    paint();
}


// ---- storage ----------------------------------------------------------------

/**
 * Read the app's one localStorage key. The JSON is parsed by C#; the browser layer only moves the
 * string, so the storage format stays a Core concern with a version field.
 */
export function readStore(key) {
    try { return localStorage.getItem(key); } catch (e) { return null; }
}

export function writeStore(key, json) {
    try { localStorage.setItem(key, json); return true; } catch (e) { return false; }
}

/** True when localStorage is present and writable, so the UI can warn instead of losing work. */
export function storageAvailable() {
    try {
        const probe = '__ml_probe__';
        localStorage.setItem(probe, '1');
        localStorage.removeItem(probe);
        return true;
    } catch (e) { return false; }
}

// ---- download ---------------------------------------------------------------

export function downloadFile(fileName, content) {
    const blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/**
 * Open a file picker and hand back the chosen file's text, used to load a .logo program.
 * Resolves to null when the child cancels: the input fires 'cancel' in current browsers, and the
 * promise is also settled on window focus so a picker that reports nothing cannot hang the app.
 */
export function pickFile(accept) {
    return new Promise((resolve) => {
        let settled = false;
        const done = (value) => { if (!settled) { settled = true; resolve(value); } };

        const input = document.createElement('input');
        input.type = 'file';
        if (accept) input.accept = accept;
        input.onchange = () => {
            const file = input.files && input.files[0];
            if (!file) { done(null); return; }
            const reader = new FileReader();
            reader.onload = () => done(String(reader.result || ''));
            reader.onerror = () => done(null);
            reader.readAsText(file);
        };
        input.oncancel = () => done(null);

        const onFocus = () => {
            window.removeEventListener('focus', onFocus);
            // Give the change event a chance to win first.
            setTimeout(() => done(null), 400);
        };
        window.addEventListener('focus', onFocus);

        input.click();
    });
}

// ---- diagnostics ------------------------------------------------------------

/** Surface a .NET fault in the browser console and on screen rather than losing it. */
export function reportError(message) {
    console.error('[MaturinLogo] ' + message);
    let el = document.getElementById('ml-error');
    if (!el) {
        el = document.createElement('div');
        el.id = 'ml-error';
        el.style.cssText = 'position:fixed;bottom:0;left:0;right:0;z-index:9999;' +
            'background:#400;color:#f88;font:14px monospace;padding:6px;white-space:pre-wrap;';
        document.body.appendChild(el);
    }
    el.textContent = '[Maturin Logo error] ' + message;
}
