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
        // Alt+, is the settings key. A browser reports a punctuation key by its own character, so
        // the generic rule below would hand C# the token 'A-,' and its switch would never match
        // the 'A-COMMA' the machine expects.
        if (k === ',') return 'A-COMMA';
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

/* The sixteen colours Logo's SETPC 0-15 offers. The first six are what an Apple II hi-res screen
   could really show; 6-15 are the extended palette (cyan, magenta, red, yellow, browns, greys)
   that Apple Logo II and the //e enhanced mode reached. They used to be folded onto the first six
   with a modulo, which is why a program that picked RED drew green.

   MaturinLogo.Core/Turtle/ApplePalette.Hex is the same list; TurtleGraphicsTests asserts the two
   agree, so a colour added on one side cannot quietly differ on the other. */
const PALETTE = [
    '#000000', '#33CC33', '#FFFFFF', '#AA66FF', '#FF9933', '#5588FF',
    '#33CCCC', '#FF55AA', '#FF3333', '#FFFF33', '#AA6633', '#FFB8C6',
    '#88BBFF', '#C8C8C8', '#777777', '#999999',
];

/* The six turtle shapes SHAPE 0-5 selects, drawn below in the same order.
   MaturinLogo.Core/Turtle/TurtleShapes.Names is the list the interpreter validates against. */
const SHAPES = ['ARROW', 'TRIANGLE', 'TURTLE', 'DOT', 'DIAMOND', 'BALL'];

/* The first six palette entries are the real hi-res colours, and only they get the television
   fringe. The extended greys are a modern addition and look wrong with red and blue edges. */
const HIRES_COLOURS = 6;

/* Reduce-motion, read from the operating system. When it is on, every tween snaps and the engine is
   told to draw instantly, because this is a setting the user chose for every application. */
let reducedMotion = false;

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
    // What is actually on screen right now, which lags behind `turtle` while a tween runs.
    shown: null,
    from: null,
    to: null,
    tweenStart: 0,
    tweenMs: 0,
    raf: 0,
};

// TEMP DEBUG - remove before shipping.
window.__gfx = gfx;
window.__paintLog = [];

/**
 * Reduce-motion, read from the operating system. When it is on, every tween snaps and the engine
 * is told to draw instantly, because this is a setting the user chose for every application.
 *
 * The current value is returned rather than called back into .NET: this runs while .NET is still
 * awaiting the interop call that installed it, and a re-entrant dispatch from inside one is not a
 * promise worth relying on. Later changes arrive on their own, from the media-query listener, at a
 * moment when nothing else is in flight.
 */
export function watchReducedMotion(dotNet) {
    const query = () => {
        try { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; }
        catch (e) { return false; }
    };

    reducedMotion = query();

    try {
        const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
        const handler = (e) => {
            reducedMotion = !!e.matches;
            if (dotNet) dotNet.invokeMethodAsync('OnReducedMotion', reducedMotion);
        };
        if (mq.addEventListener) mq.addEventListener('change', handler);
        else if (mq.addListener) mq.addListener(handler);
    } catch (e) {
        // No matchMedia at all: reduced motion stays off, which is the app's old behaviour.
    }

    return reducedMotion;
}

/** True when motion should be skipped entirely: the system says so, or Logo was set to instant. */
function snapMotion(ms) {
    return reducedMotion || !ms || ms <= 0;
}

/** Draw the turtle in whichever shape SHAPE selected. `t` is the interpolated state, so a spin
    and a glide both look like movement rather than like a screen refresh. */
/**
 * Draw the turtle.
 *
 * `lenOverride` is in scene units and is what the VIEWSHAPES chart passes: the turtle's own size is
 * right beside the line it is drawing and far too small to read inside a card. Everything else about
 * the drawing is unchanged, which is the point - the chart is painted by this function rather than by
 * a second copy of each shape, so it cannot show a shape the turtle is not capable of wearing.
 */
function drawTurtle(g, t, sx, sy, scale, lenOverride) {
    if (!t) return;

    // HT does not blink the turtle out; it fades over the same frames the move takes, so the
    // child sees where it went rather than losing it between two repaints.
    const alpha = t.alpha === undefined ? (t.visible === false ? 0 : 1) : t.alpha;
    if (alpha <= 0) return;

    // Logo heading 0 faces north and grows clockwise; scene y grows downwards, so north is -y.
    const rad = (t.heading * Math.PI) / 180;
    const fx = Math.sin(rad), fy = -Math.cos(rad);       // forward
    const rx = Math.cos(rad), ry = Math.sin(rad);        // right

    const cx = sx + t.x * scale;
    const cy = sy + t.y * scale;

    const len = lenOverride ? lenOverride * scale : Math.max(4, 9 * scale);

    // The wing barbs are a fixed fraction of the length, so a bigger turtle is a proportionally
    // bigger turtle rather than a long spike with stubby wings. At the default length this is
    // exactly the 4 * scale it has always been.
    const wing = Math.max(2, len * (4 / 9));
    const fill = paletteOf(t.color);

    g.save();
    g.globalAlpha = alpha;
    g.fillStyle = fill;
    g.strokeStyle = fill;

    switch (SHAPES[t.shape | 0] || 'ARROW') {
        case 'TRIANGLE': drawTriangle(g, cx, cy, fx, fy, rx, ry, len, wing); break;
        case 'TURTLE': drawShell(g, cx, cy, fx, fy, rx, ry, len); break;
        case 'DOT': drawDot(g, cx, cy, len); break;
        case 'DIAMOND': drawDiamond(g, cx, cy, fx, fy, rx, ry, len); break;
        case 'BALL': drawBall(g, cx, cy, len); break;
        default: drawArrow(g, cx, cy, fx, fy, rx, ry, len, wing); break;
    }

    g.restore();
}

function polygon(g, points) {
    g.beginPath();
    g.moveTo(points[0], points[1]);
    for (let i = 2; i < points.length; i += 2) g.lineTo(points[i], points[i + 1]);
    g.closePath();
    g.fill();
}

/** Apple Logo's turtle: a point ahead, two barbs behind, open at the back. */
function drawArrow(g, cx, cy, fx, fy, rx, ry, len, wing) {
    const tipX = cx + fx * len, tipY = cy + fy * len;
    const backX = cx - fx * len * 0.5, backY = cy - fy * len * 0.5;
    polygon(g, [
        tipX, tipY,
        backX - rx * wing, backY - ry * wing,
        backX, backY,
        backX + rx * wing, backY + ry * wing,
    ]);
}

/** A solid isoceles triangle, which is what a young Logo class expects FD to leave behind. */
function drawTriangle(g, cx, cy, fx, fy, rx, ry, len, wing) {
    polygon(g, [
        cx + fx * len, cy + fy * len,
        cx - fx * len * 0.6 - rx * wing * 1.3, cy - fy * len * 0.6 - ry * wing * 1.3,
        cx - fx * len * 0.6 + rx * wing * 1.3, cy - fy * len * 0.6 + ry * wing * 1.3,
    ]);
}

/** The turtle seen from above: a shell, a head, and four flippers. */
function drawShell(g, cx, cy, fx, fy, rx, ry, len) {
    const r = len * 0.75;

    // Flippers: four stubby diagonals under the shell.
    for (const [a, b] of [[1, 1], [1, -1], [-1, 1], [-1, -1]]) {
        const px = cx + (fx * a + rx * b) * r * 0.95;
        const py = cy + (fy * a + ry * b) * r * 0.95;
        g.beginPath();
        g.arc(px, py, r * 0.34, 0, Math.PI * 2);
        g.fill();
    }

    // Head, ahead of the shell.
    g.beginPath();
    g.arc(cx + fx * r * 1.15, cy + fy * r * 1.15, r * 0.32, 0, Math.PI * 2);
    g.fill();

    // Shell, then a ring so it reads as a shell and not as a blob.
    g.beginPath();
    g.ellipse(cx, cy, r, r * 0.85, Math.atan2(fy, fx), 0, Math.PI * 2);
    g.fill();

    g.save();
    g.globalAlpha *= 0.45;
    g.fillStyle = '#000000';
    g.beginPath();
    g.ellipse(cx, cy, r * 0.62, r * 0.5, Math.atan2(fy, fx), 0, Math.PI * 2);
    g.fill();
    g.restore();
}

/** A filled circle, which is the shape a child picks when they want the line to look continuous. */
function drawDot(g, cx, cy, len) {
    g.beginPath();
    g.arc(cx, cy, Math.max(1.5, len * 0.45), 0, Math.PI * 2);
    g.fill();
}

/** A four-pointed star: long points forward and back, short points to the sides. */
function drawDiamond(g, cx, cy, fx, fy, rx, ry, len) {
    const l = len * 1.0;   // length of the front/back points
    const w = len * 0.45;  // width of the side points
    polygon(g, [
        cx + fx * l, cy + fy * l,           // front point
        cx + rx * w, cy + ry * w,           // right point
        cx - fx * l, cy - fy * l,           // back point
        cx - rx * w, cy - ry * w,           // left point
    ]);
}

/** A ball: a circle with a highlight, so it does not read as the DOT shape at small sizes. */
function drawBall(g, cx, cy, len) {
    const r = Math.max(2, len * 0.7);
    g.beginPath();
    g.arc(cx, cy, r, 0, Math.PI * 2);
    g.fill();

    g.save();
    g.globalAlpha *= 0.35;
    g.fillStyle = '#FFFFFF';
    g.beginPath();
    g.arc(cx - r * 0.3, cy - r * 0.3, r * 0.35, 0, Math.PI * 2);
    g.fill();
    g.restore();
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

/** True for the six colours a real hi-res screen produced, which are the ones that fringed. */
function isHiRes(colour) {
    const index = ((colour % PALETTE.length) + PALETTE.length) % PALETTE.length;
    return index < HIRES_COLOURS;
}

/**
 * One stroke with the Apple II's chromatic fringe: the red and blue sub-pixel edges a real
 * television smears around a bright line. Drawn as two faint offset copies under the true colour.
 * The extended palette (6-15) is not a hi-res colour, so it is drawn clean.
 */
function strokeSegment(g, seg, sx, sy, scale) {
    const x1 = sx + seg.x1 * scale;
    const y1 = sy + seg.y1 * scale;
    const x2 = sx + seg.x2 * scale;
    const y2 = sy + seg.y2 * scale;
    const width = Math.max(1, (seg.width || 1) * scale);

    g.lineWidth = width;
    g.lineCap = 'round';

    if (isHiRes(seg.color)) {
        const fringe = Math.max(0.6, scale * 0.5);

        g.strokeStyle = 'rgba(255, 60, 60, 0.35)';
        g.beginPath(); g.moveTo(x1 - fringe, y1); g.lineTo(x2 - fringe, y2); g.stroke();

        g.strokeStyle = 'rgba(60, 90, 255, 0.35)';
        g.beginPath(); g.moveTo(x1 + fringe, y1); g.lineTo(x2 + fringe, y2); g.stroke();
    }

    g.strokeStyle = paletteOf(seg.color);
    g.beginPath(); g.moveTo(x1, y1); g.lineTo(x2, y2); g.stroke();
}

function drawLabel(g, label, sx, sy, scale) {
    g.fillStyle = paletteOf(label.color);
    g.font = `${Math.max(8, Math.round(9 * scale))}px 'VT323', monospace`;
    g.textBaseline = 'middle';
    g.fillText(label.text || '', sx + label.x1 * scale, sy + label.y1 * scale);
}

/**
 * A palette swatch for the VIEWCOLORS chart: the colour filling the box, its number and name
 * centred inside, and an outline drawn in the *caption* colour.
 *
 * The outline is not decoration. Colour 0 is black on a black background, so a swatch of it with
 * only a black label would be an empty rectangle - the outline is what makes the box itself
 * visible, and it is the same colour as the text so the two always read together.
 */
function drawSwatch(g, sw, sx, sy, scale) {
    const x = sx + sw.x1 * scale;
    const y = sy + sw.y1 * scale;
    const w = sw.width * scale;
    const h = sw.height * scale;

    g.fillStyle = paletteOf(sw.color);
    g.fillRect(x, y, w, h);

    const ink = paletteOf(sw.textcolor);
    g.strokeStyle = ink;
    g.lineWidth = Math.max(1, scale);
    g.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);

    g.fillStyle = ink;
    g.font = `${Math.max(8, Math.round(10 * scale))}px 'VT323', monospace`;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(sw.text || '', x + w / 2, y + h / 2);
    g.textAlign = 'start';
}

/**
 * A shape card for the VIEWSHAPES chart: an outlined box, the turtle drawn in the middle of it
 * wearing that shape, and the number and name centred underneath.
 *
 * The shape is drawn by `drawTurtle` itself, pointed north, with the length the chart asked for.
 * Sharing the painter is the whole design: a chart that drew its own copy of "car" could quietly
 * disagree with the turtle, and this one cannot.
 */
function drawShapeCard(g, c, sx, sy, scale) {
    const x = sx + c.x1 * scale;
    const y = sy + c.y1 * scale;
    const w = c.width * scale;
    const h = c.height * scale;

    const ink = paletteOf(c.textcolor);

    g.strokeStyle = ink;
    g.lineWidth = Math.max(1, scale);
    g.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);

    drawTurtle(g, {
        x: (c.x1 + c.width / 2),
        y: (c.y1 + c.height / 2),
        heading: 0,
        color: c.color,
        visible: true,
        alpha: 1,
        shape: c.shape,
    }, sx, sy, scale, c.shapelength);

    g.fillStyle = ink;
    g.font = `${Math.max(8, Math.round(10 * scale))}px 'VT323', monospace`;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(c.text || '', sx + c.textx * scale, sy + c.texty * scale);
    g.textAlign = 'start';
}

function paint() {
    const g = gfx.ctx;
    if (!g || !gfx.el) { window.__paintLog.push({ fn: 'paint', skip: 'no-ctx' }); return; }
    const w = gfx.el.width, h = gfx.el.height;

    g.setTransform(1, 0, 0, 1, 0, 0);
    g.fillStyle = paletteOf(gfx.bg);
    g.fillRect(0, 0, w, h);
    window.__paintLog.push({ fn: 'paint', w, h, bg: gfx.bg, fill: paletteOf(gfx.bg), items: gfx.items.length });

    // Fit 280x192 into the region and keep the aspect ratio, or a circle comes out an ellipse.
    const scale = Math.min(w / HIRES_W, h / HIRES_H);
    if (!isFinite(scale) || scale <= 0) { window.__paintLog.push({ fn: 'paint', bail: 'scale' }); return; }
    const sx = (w - HIRES_W * scale) / 2;
    const sy = (h - HIRES_H * scale) / 2;

    try {
        for (const item of gfx.items) {
            if (item.kind === 'segment') strokeSegment(g, item, sx, sy, scale);
            else if (item.kind === 'label') drawLabel(g, item, sx, sy, scale);
            else if (item.kind === 'swatch') drawSwatch(g, item, sx, sy, scale);
            else if (item.kind === 'shape') drawShapeCard(g, item, sx, sy, scale);
        }
    } catch (e) {
        window.__paintLog.push({ fn: 'paint', itemsError: String(e && e.stack || e) });
    }

    // The turtle is drawn last so it always sits above the picture it is making.
    try {
        drawTurtle(g, gfx.shown || gfx.turtle, sx, sy, scale);
        window.__paintLog.push({ fn: 'paint', done: true });
    } catch (e) {
        window.__paintLog.push({ fn: 'paint', error: String(e && e.stack || e) });
    }
}

/**
 * Update the turtle's live position and heading. Sent on every repaint because it moves between
 * strokes, and it is not part of the replayable item list.
 *
 * The turtle does not simply appear at its new spot: `ms` (which comes from SETSPEED) says how
 * long the move should take, and the frames between the old and new state are drawn in between.
 * That is what makes a `RT 90` look like a turn instead of a jump, and it is the difference
 * between watching a turtle draw and watching a screen refresh.
 */
export function setTurtle(json) {
    const target = json ? JSON.parse(json) : null;
    const previous = gfx.shown || gfx.turtle;

    gfx.turtle = target;

    const ms = target ? target.ms : 0;
    if (!target || snapMotion(ms) || !previous) {
        stopTween();
        gfx.shown = target ? Object.assign({}, target, { alpha: target.visible === false ? 0 : 1 }) : null;
        paint();
        return;
    }

    // Start the tween from wherever the turtle actually is on screen, not from the previous
    // target: two commands arriving in the same frame must not teleport it backwards.
    gfx.from = Object.assign({}, gfx.shown);
    gfx.to = Object.assign({}, target, { alpha: target.visible === false ? 0 : 1 });
    if (gfx.from.alpha === undefined) gfx.from.alpha = gfx.from.visible === false ? 0 : 1;
    gfx.tweenStart = performance.now();
    gfx.tweenMs = ms;

    if (!gfx.raf) gfx.raf = requestAnimationFrame(tweenFrame);
    else paint();
}

function stopTween() {
    if (gfx.raf) cancelAnimationFrame(gfx.raf);
    gfx.raf = 0;
}

/** One animation frame of the turtle's move; stops itself once the target is reached. */
function tweenFrame(now) {
    gfx.raf = 0;

    const t = gfx.tweenMs > 0 ? Math.min(1, (now - gfx.tweenStart) / gfx.tweenMs) : 1;
    // Ease out so a short hop decelerates into its spot rather than stopping dead.
    const eased = 1 - Math.pow(1 - t, 2);

    gfx.shown = lerpTurtle(gfx.from, gfx.to, eased);
    paint();

    if (t < 1) gfx.raf = requestAnimationFrame(tweenFrame);
    else gfx.shown = Object.assign({}, gfx.to);
}

/**
 * Blend two turtle states. The heading takes the short way round, so `RT 350` spins seven degrees
 * the wrong way rather than three hundred and fifty the right one, and HOME does not unwind the
 * whole program.
 */
function lerpTurtle(a, b, t) {
    let dHeading = ((b.heading - a.heading) % 360 + 540) % 360 - 180;
    return {
        x: a.x + (b.x - a.x) * t,
        y: a.y + (b.y - a.y) * t,
        heading: a.heading + dHeading * t,
        color: b.color,
        visible: b.visible,
        shape: b.shape,
        size: b.size,
        alpha: a.alpha + (b.alpha - a.alpha) * t,
    };
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
    window.__paintLog.push({ fn: 'replayGraphics', bg: gfx.bg, items: gfx.items.length, textRows, gridRows });
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

// ---- sound ------------------------------------------------------------------

/**
 * The one AudioContext Logo needs, made lazily and kept for the life of the page.
 *
 * Browsers will not let a page make noise until the user has done something, and a click on a key
 * counts. So the context is only built the first time a tone is asked for, and is resumed then too:
 * a context created before the first gesture sits in state "suspended" and every note scheduled
 * into it is quietly lost, which is exactly the bug that makes a sound command look broken.
 */
let audio = null;

/**
 * End of the last scheduled note, on the audio clock. Tones queue behind each other rather than
 * starting together, so `TOOT 440 0.3 TOOT 550 0.3` is two notes in sequence even though Logo ran
 * both commands in the same millisecond.
 */
let toneUntil = 0;

/** Notes still waiting to be heard, so ESC can throw them away. */
const liveTones = new Set();

function audioContext() {
    const Ctor = window.AudioContext || window.webkitAudioContext;
    if (!Ctor) return null;

    if (!audio) {
        audio = new Ctor();
        toneUntil = 0;
    }
    if (audio.state === 'suspended') audio.resume();
    return audio;
}

/**
 * Play one square wave, the waveform an 8-bit machine had.
 *
 * `Docs/8-bit Sound Code Example.htm` is the recipe this follows: an oscillator through a gain node
 * held well below full scale. The gain is only ever 0.2 because a square wave is all harmonics and
 * at full volume it is a harsh buzz rather than a note. The tiny ramp at each end is not decoration
 * either: switching a square wave on and off mid-cycle is an audible click, and a few milliseconds
 * of fade is what makes it a note instead.
 */
export function playTone(hertz, seconds) {
    const ctx = audioContext();
    if (!ctx) return;

    const start = Math.max(ctx.currentTime, toneUntil);
    const stop = start + Math.max(0.02, seconds);
    toneUntil = stop;

    const osc = ctx.createOscillator();
    const gain = ctx.createGain();

    osc.type = 'square';
    osc.frequency.value = Math.min(8000, Math.max(16, hertz));

    const attack = 0.008;
    gain.gain.setValueAtTime(0.0001, start);
    gain.gain.linearRampToValueAtTime(0.2, start + attack);
    gain.gain.setValueAtTime(0.2, Math.max(start + attack, stop - attack));
    gain.gain.linearRampToValueAtTime(0.0001, stop);

    osc.connect(gain);
    gain.connect(ctx.destination);

    osc.start(start);
    osc.stop(stop);

    // A Set, and each note removes itself when it has been heard, so a long tune costs no more than
    // a short one. An array plus `filter` here was quadratic: `REPEAT 2000 [TOOT 440 0.02]` built a
    // list that had to be copied apart once per note.
    const tone = {
        stop() {
            // stop() with no argument means "stop now", and Web Audio refuses it for an oscillator
            // that has not started yet - which cannot happen here, but a note that throws on the way
            // out of a break would leave the rest of the queue playing.
            try { osc.stop(); } catch { /* already finished */ }
            liveTones.delete(tone);
        },
    };

    osc.start(start);
    osc.stop(stop);

    liveTones.add(tone);
    osc.onended = () => liveTones.delete(tone);
}

/** Silence everything queued: ESC, and the sound switch turning itself off. */
export function stopTones() {
    // Back to "nothing is scheduled", or the next note after a break would wait for a tune the child
    // just threw away.
    toneUntil = 0;

    // Copy first: each tone.stop() removes itself from the very set being walked.
    for (const tone of [...liveTones]) tone.stop();
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
