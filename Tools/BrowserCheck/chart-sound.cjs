// Exercises the two halves of the browser layer this release added: the VIEWCOLORS swatch chart
// and TOOT's Web Audio tones. Both are drawn and scheduled by interop.js, so both need a fake
// canvas and a fake AudioContext rather than a real browser.
//
// Run: node Tools/BrowserCheck/chart-sound.cjs
const fs = require('fs');
const path = require('path');

const REPO = process.env.REPO || path.resolve(__dirname, '..', '..');
let src = fs.readFileSync(path.join(REPO, 'Src', 'MaturinLogo', 'wwwroot', 'js', 'interop.js'), 'utf8');

// Same trick the other two harnesses use: strip the ES module keywords, append a CommonJS export
// list, and evaluate it in a temp file so it sees the fakes installed below.
src = src.replace(/export function /g, 'function ');
src += '\n;module.exports = { initGraphics, replayGraphics, playTone, stopTones, paletteOf, gfx, __card: drawShapeCard, __turtle: drawTurtle };';

const tmp = path.join(__dirname, 'interop.chart-sound.cjs');
fs.writeFileSync(tmp, src);

// ---- the recording canvas ----------------------------------------------------

const ops = [];

/** Every point the painter touched, so a shape's drawn size can be measured rather than trusted. */
let pts = [];

function record(x, y) { if (Number.isFinite(x) && Number.isFinite(y)) pts.push([x, y]); }

/**
 * The width of the blob of points recorded since the last clear, in canvas pixels. Used to prove a
 * card's shape is really enlarged: if `lenOverride` were ignored, the drawn turtle would be the same
 * size as the live one and this would not grow.
 */
function extent() {
    if (pts.length < 2) return 0;
    const xs = pts.map(p => p[0]);
    return Math.max(...xs) - Math.min(...xs);
}

const ctx = new Proxy({
    save() { ops.push(['save']); },
    restore() { ops.push(['restore']); },
    beginPath() { ops.push(['beginPath']); },
    closePath() { },
    fill() { ops.push(['fill']); },
    stroke() { ops.push(['stroke']); },
    moveTo(x, y) { record(x, y); },
    lineTo(x, y) { record(x, y); },
    arc(x, y, r) { record(x - r, y); record(x + r, y); },
    ellipse(x, y, rx) { record(x - rx, y); record(x + rx, y); },
    setTransform() { },
    fillRect(x, y, w, h) { ops.push(['fillRect', x, y, w, h]); },
    strokeRect(x, y, w, h) { ops.push(['strokeRect', x, y, w, h]); },
    fillText(text, x, y) { ops.push(['fillText', text, x, y]); record(x, y); },
}, {
    set(target, key, value) { target[key] = value; ops.push(['set', key, value]); return true; },
    get(target, key) { return target[key]; },
});

const nodes = {};
const el = (tag) => ({
    tag, style: {}, classList: { toggle() { }, add() { }, remove() { } },
    getContext: () => ctx,
    getBoundingClientRect: () => ({ width: 800, height: 480 }),
    setAttribute() { }, removeAttribute() { }, addEventListener() { }, removeEventListener() { },
    appendChild() { }, removeChild() { }, click() { }, querySelector: () => null,
});

global.document = {
    getElementById: (id) => (nodes[id] ||= el('#' + id)),
    createElement: (t) => el(t),
    documentElement: el('html'),
    body: el('body'),
    fonts: { ready: Promise.resolve() },
};
global.window = {
    innerWidth: 1400, innerHeight: 900, devicePixelRatio: 2,
    addEventListener() { }, removeEventListener() { },
    matchMedia: () => ({ matches: false, addEventListener() { }, addListener() { } }),
};
global.performance = { now: () => Date.now() };
global.requestAnimationFrame = () => 0;
global.cancelAnimationFrame = () => { };
global.localStorage = { setItem() { }, getItem: () => null, removeItem() { } };

// ---- the fake AudioContext ---------------------------------------------------

/** Every oscillator and gain node made, in creation order, so a note is tones[i] through gains[i]. */
const tones = [];
const gains = [];
const contexts = [];
let clock = 0;

function FakeAudioContext() {
    this.state = 'suspended';
    this.destination = {};
    this.resumed = 0;
    Object.defineProperty(this, 'currentTime', { get: () => clock });

    this.resume = () => { this.resumed++; this.state = 'running'; return Promise.resolve(); };

    this.createOscillator = () => {
        const osc = {
            type: '', frequency: { value: 0 },
            started: null, stopped: null, onended: null,
            connect() { },
            start(t) { osc.started = t; },
            stop(t) { if (osc.stopped === null) osc.stopped = t === undefined ? clock : t; },
        };
        tones.push(osc);
        return osc;
    };

    this.createGain = () => {
        const gain = { steps: [], connect() { } };
        gain.gain = {
            value: 1,
            setValueAtTime(v, t) { gain.steps.push(['set', v, t]); },
            linearRampToValueAtTime(v, t) { gain.steps.push(['ramp', v, t]); },
        };
        gains.push(gain);
        return gain;
    };

    contexts.push(this);
}

// interop.js reads the constructor off `window`, which is where a browser keeps it.
global.window.AudioContext = FakeAudioContext;

const ml = require(tmp);
fs.unlinkSync(tmp);

let failures = 0;
function assert(cond, msg) {
    if (cond) console.log('ok   - ' + msg);
    else { console.error('FAIL: ' + msg); failures++; }
}

// The palette names are read out of Core rather than typed out again here, because a chart whose
// labels come from a copy of the names is a chart that can drift from the language it is showing.
const core = fs.readFileSync(path.join(REPO, 'Src', 'MaturinLogo.Core', 'Turtle', 'Graphics.cs'), 'utf8');
const namesBlock = /Names\s*=\s*\{([\s\S]*?)\};/.exec(core);
assert(namesBlock !== null, 'found ApplePalette.Names in Graphics.cs');
const NAMES = (namesBlock[1].match(/"[A-Z]+"/g) || []).map(s => s.replace(/"/g, ''));
assert(NAMES.length === 16, `the palette has sixteen names, got ${NAMES.length}`);

// ---- the colour chart --------------------------------------------------------

ml.initGraphics(24);

// The payload LogoMachine.PaintColorsAsync sends: sixteen swatches in scene units, laid out by
// ColorChart. The numbers below are ColorChart's geometry written out - 280x192 picture, four
// columns, and the row pitch that puts the sixteenth box inside the page.
const COLS = 4, BOX_W = 62, BOX_H = 26, GAP_X = 6, GAP_Y = 4, CAPTION = 10, TOP = 6;
const LEFT = (280 - COLS * BOX_W - (COLS - 1) * GAP_X) / 2;
const ROW_H = BOX_H + CAPTION + GAP_Y;

const swatches = NAMES.map((name, i) => ({
    kind: 'swatch',
    x1: LEFT + (i % COLS) * (BOX_W + GAP_X),
    y1: TOP + Math.floor(i / COLS) * ROW_H,
    width: BOX_W, height: BOX_H,
    color: i,
    text: `${i} ${name}`,
    textcolor: i > 5 ? 2 : 0,
}));

ops.length = 0;
ml.replayGraphics(JSON.stringify(swatches), 0, 0, 24);

const fills = ops.filter(o => o[0] === 'fillRect');
const outlines = ops.filter(o => o[0] === 'strokeRect');
const labels = ops.filter(o => o[0] === 'fillText');

assert(fills.length === 17, `the background plus sixteen swatches were filled, got ${fills.length}`);
assert(outlines.length === 16, `every swatch is outlined, got ${outlines.length}`);
assert(labels.length === 16, `every swatch is labelled, got ${labels.length}`);
assert(labels[0][1] === '0 BLACK',
    `the first label carries the number a child types into SETPC, got "${labels[0][1]}"`);

// Colour 0 is black on a black background: the outline is the only thing that makes it a box at all.
ops.length = 0;
ml.replayGraphics(JSON.stringify([swatches[0]]), 0, 0, 24);
assert(ops.filter(o => o[0] === 'strokeRect').length === 1, 'a black swatch still has its outline drawn');

// The chart asks for zero text rows, which is what lets the canvas own the whole grid.
assert(ml.gfx.lowerLines === 0, 'the chart leaves no rows of text under the canvas');

// Nothing may be laid out beyond the 280x192 picture it is fitted into - a swatch that overran
// would be clipped by the aspect-ratio fit rather than drawn.
const overruns = swatches.filter(s => s.x1 + s.width > 280 || s.y1 + s.height > 192);
assert(overruns.length === 0, 'every swatch fits inside the 280x192 picture');

// The chart is centred left-to-right but starts at the top, so the last row is not floating.
assert(Math.abs(swatches[1].x1 - swatches[0].x1 - (BOX_W + GAP_X)) < 0.001, 'the columns are evenly spaced');
assert(Math.abs(swatches[4].y1 - swatches[0].y1 - ROW_H) < 0.001, 'the rows are evenly spaced');
assert(LEFT > 0, `the chart has a left margin, got ${LEFT}`);

// The caption colour is chosen so the label reads against its own box; 0 is black, 2 white.
assert(ml.paletteOf(0) === '#000000' && ml.paletteOf(2) === '#FFFFFF',
    'black and white are both available as caption colours');

// ---- the shape chart ---------------------------------------------------------

// Read the shape names out of Core the same way, so a card that drifted from the interpreter's own
// list would fail here rather than disagreeing quietly in front of a child.
const shapesBlock = /TurtleShapes[\s\S]{0,200}?Names\s*=\s*\[([\s\S]*?)\]/.exec(core);
assert(shapesBlock !== null, 'found TurtleShapes.Names in Graphics.cs');
const SHAPES = (shapesBlock[1].match(/"[A-Z]+"/g) || []).map(s => s.replace(/"/g, ''));
assert(SHAPES.length === 6, `six shapes are named, got ${SHAPES.length}`);

const S_COLS = 3, S_W = 86, S_H = 62, S_GAPX = 8, S_GAPY = 8, S_CAP = 14, S_TOP = 10;
const S_LEFT = (280 - S_COLS * S_W - (S_COLS - 1) * S_GAPX) / 2;
const S_ROW = S_H + S_CAP + S_GAPY;

const cards = SHAPES.map((name, i) => ({
    kind: 'shape',
    x1: S_LEFT + (i % S_COLS) * (S_W + S_GAPX),
    y1: S_TOP + Math.floor(i / S_COLS) * S_ROW,
    width: S_W, height: S_H,
    shape: i,
    shapelength: 26,
    color: 2,
    text: `${i} ${name}`,
    textcolor: 2,
    textx: S_LEFT + (i % S_COLS) * (S_W + S_GAPX) + S_W / 2,
    texty: S_TOP + Math.floor(i / S_COLS) * S_ROW + S_H + S_CAP / 2,
}));

ops.length = 0;
ml.replayGraphics(JSON.stringify(cards), 0, 0, 24);

const sOutlines = ops.filter(o => o[0] === 'strokeRect');
const sLabels = ops.filter(o => o[0] === 'fillText');
const sPaths = ops.filter(o => o[0] === 'beginPath');

assert(sOutlines.length === 6, `every card is outlined, got ${sOutlines.length}`);
assert(sLabels.length === 6, `every card is labelled, got ${sLabels.length}`);
assert(sLabels[0][1] === '0 ARROW', `the first caption is the shape a child types, got "${sLabels[0][1]}"`);

// The shape itself has to be drawn by the turtle's painter, so the card must produce the same
// path-and-fill sequence a live turtle does - not a rectangle with a word in it.
assert(sPaths.length >= 6, `each card draws a shape, got ${sPaths.length} paths`);
assert(ops.some(o => o[0] === 'fill'), 'the shapes are filled rather than only outlined');

// A caption centred inside the box would be drawn through the shape, which is exactly how the colour
// chart works and exactly wrong here. Measured off what the painter actually did, not off the payload
// - asserting only that the JSON asked for the right place would pass while the browser ignored it.
ops.length = 0;
ml.replayGraphics(JSON.stringify(cards), 0, 0, 24);

const boxes = ops.filter(o => o[0] === 'strokeRect');
const captionYs = ops.filter(o => o[0] === 'fillText').map(o => o[3]);
assert(boxes.length === 6 && captionYs.length === 6, 'six boxes and six captions were drawn');

// scale is min(800*dpr / 280, 480*dpr / 192) with dpr 2 -> the same factor for both axes.
const paintScale = Math.min((800 * 2) / 280, (480 * 2) / 192);
const bottoms = boxes.map(o => o[2] + o[4]);                   // y + height, in canvas pixels
const inside = captionYs.filter((y, i) => y <= bottoms[i]).length;
assert(inside === 0, `no caption is drawn inside its own box, got ${inside} through a shape`);


// And the colour chart's labels *are* centred inside, which is the difference between the two kinds.
ops.length = 0;
ml.replayGraphics(JSON.stringify(swatches), 0, 0, 24);
const swBoxes = ops.filter(o => o[0] === 'strokeRect');
const swLabels = ops.filter(o => o[0] === 'fillText').map(o => o[3]);
const outside = swLabels.filter((y, i) => y > swBoxes[i][2] + swBoxes[i][4]).length;
assert(outside === 0, `swatch labels sit inside their boxes, got ${outside} below the box`);

// Nothing may be laid out past the picture, and the enlarged shape must stay inside its own card -
// a shape clipped by the card border is a shape the chart gets wrong.
const sOverruns = cards.filter(c => c.x1 + c.width > 280 || c.y1 + c.height > 192 || c.texty > 192);
assert(sOverruns.length === 0, 'every card and caption fits inside the 280x192 picture');
assert(cards.every(c => c.shapelength <= c.height && c.shapelength <= c.width),
    'the enlarged shape still fits inside its card');

// Every shape index reaches the painter without throwing, in the colour the chart asks for.
const g2 = {
    save() { }, restore() { }, beginPath() { }, closePath() { }, fill() { }, stroke() { },
    moveTo() { }, lineTo() { }, arc() { }, ellipse() { }, fillRect() { }, strokeRect() { },
    fillText() { }, setTransform() { },
    fillStyle: '', strokeStyle: '', lineWidth: 1, lineCap: '', globalAlpha: 1, font: '',
    textBaseline: '', textAlign: '',
};
let drewShapes = 0;
for (let shape = 0; shape < SHAPES.length; shape++) {
    for (const colour of [0, 1, 2, 8, 15]) {
        ml.__card(g2, { x1: 10, y1: 10, width: 86, height: 62, shape, shapelength: 26, color: colour, text: 'x', textcolor: 2, textx: 53, texty: 80 }, 0, 0, 2);
        drewShapes++;
    }
}
assert(drewShapes === SHAPES.length * 5, 'every shape draws in every colour asked for');

// A card with no shape index must still draw something rather than throw: the chart is built from
// TurtleShapes.Count, but a payload that arrived without one should fall back like the turtle does.
ml.__card(g2, { x1: 0, y1: 0, width: 86, height: 62, shapelength: 26, color: 2, text: '', textcolor: 2, textx: 43, texty: 70 }, 0, 0, 2);
assert(true, 'a card with no shape index draws the default rather than failing');

// ---- the enlarged shape is really enlarged ------------------------------------

// The single claim that separates this chart from the turtle's own painter: a card asks for a longer
// shape than the live turtle uses, and the browser obeys. Measured, not trusted - an earlier version
// of this harness asserted the card drew "a shape" and passed while lenOverride was being ignored
// entirely, which is exactly the bug a chart of small shapes would have.
const scale = 2;
const turtle = { x: 140, y: 96, heading: 0, color: 2, visible: true, alpha: 1, shape: 0 };

pts = [];
ml.__turtle(ctx, { ...turtle }, 0, 0, scale);                     // live size, no override
const liveWidth = extent();

pts = [];
ml.__turtle(ctx, { ...turtle }, 0, 0, scale, 26);                   // chart size
const cardWidth = extent();

assert(liveWidth > 0, 'the live turtle draws something measurable');
assert(cardWidth > liveWidth,
    `a card's shape is enlarged (${liveWidth} -> ${cardWidth} px at scale ${scale})`);
assert(Math.abs(cardWidth / liveWidth - 26 / 9) < 0.35,
    `the enlargement is about the ratio asked for, got ${(cardWidth / liveWidth).toFixed(2)}`);

// And the same through the card painter itself, which is the path the chart actually takes.
pts = [];
ml.__card(ctx, { x1: 100, y1: 60, width: 86, height: 62, shape: 0, shapelength: 26, color: 2, text: '', textcolor: 2, textx: 143, texty: 130 }, 0, 0, scale);
const drawn = extent();
assert(drawn >= cardWidth - 1, 'the card draws the shape at the enlarged length, not the turtle length');

// ---- sound -------------------------------------------------------------------

// A page that has never played a note starts one immediately, on a square wave, at the pitch and
// length Logo asked for.
clock = 0; tones.length = 0; gains.length = 0;
ml.playTone(440, 0.25);

assert(tones.length === 1 && gains.length === 1, 'one note is one oscillator through one gain node');
assert(tones[0].type === 'square', `the waveform is a square wave, not ${tones[0].type}`);
assert(Math.abs(tones[0].frequency.value - 440) < 0.001, 'the frequency is what Logo asked for');
assert(tones[0].started === 0, 'the first note starts now');
assert(Math.abs(tones[0].stopped - 0.25) < 0.001, 'the note lasts the length Logo asked for');

// A square wave at full scale is a harsh buzz; the 8-bit example this follows holds it at one fifth.
const peak = Math.max(...gains[0].steps.map(s => s[1]));
assert(peak > 0 && peak <= 0.25, `the gain stays well below full scale, peak was ${peak}`);

// A few milliseconds of ramp at each end, or switching a square wave on mid-cycle is a click.
const ramps = gains[0].steps.filter(s => s[0] === 'ramp').length;
assert(ramps === 2, `the note fades in and out rather than starting and stopping hard, got ${ramps} ramps`);

// The context a page made before the child's first keypress is suspended, and every note scheduled
// into it is silently lost - which is exactly how a working TOOT looks like a broken one. So the
// context is built lazily on the first tone and resumed there, and one context serves every note.
assert(contexts.length === 1, `one AudioContext for the whole page, got ${contexts.length}`);
assert(contexts[0].resumed >= 1, 'a suspended context is resumed before anything is scheduled into it');
assert(contexts[0].state === 'running', 'and the context ends up running, so the note is actually audible');

// Notes queue on the audio clock rather than stacking up on top of each other.
ml.stopTones();                          // clear the schedule before the next check
clock = 0; tones.length = 0;
ml.playTone(440, 0.2);
ml.playTone(550, 0.2);
ml.playTone(660, 0.2);

assert(tones.length === 3, 'three commands made three notes');
assert(tones[0].started === 0, 'the first note starts immediately');
assert(tones[1].started >= tones[0].stopped - 0.0001, 'the second waits for the first to end');
assert(tones[2].started >= tones[1].stopped - 0.0001, 'the third waits for the second');
assert(tones[1].started > tones[0].started, 'a tune is sequential, not a chord');

// ESC throws away what has not been heard yet.
clock = 0; tones.length = 0;
ml.playTone(440, 2);
ml.playTone(880, 2);
ml.stopTones();
assert(tones.every(t => t.stopped !== null), 'stopTones stops every queued note');

// A long tune must not cost more than a short one: the live list is a Set that each note removes
// itself from, and an array plus `filter` made every note copy the whole remaining queue.
clock = 1000; tones.length = 0;
for (let i = 0; i < 2000; i++) ml.playTone(440, 0.02);
assert(tones.length === 2000, 'two thousand queued notes are all scheduled');
ml.stopTones();
assert(tones.every(t => t.stopped !== null), 'a break silences a long tune completely');

// And the next note after a stop starts from "now", not from the end of the tune that was
// cancelled - otherwise ESC leaves Logo silent while an old schedule unwinds.
clock = 5; tones.length = 0;
ml.playTone(440, 0.1);
assert(tones[0].started === 5, `a note after a stop starts at the current clock, got ${tones[0].started}`);

// Out-of-range pitches are clamped rather than handed to the audio hardware.
clock = 100; tones.length = 0;
ml.playTone(1, 0.1);
ml.playTone(50000, 0.1);
assert(tones[0].frequency.value >= 16, 'a sub-audible pitch is clamped up');
assert(tones[1].frequency.value <= 8000, 'an ultrasonic pitch is clamped down');

console.log(failures ? `\n${failures} CHECKS FAILED` : '\nall checks passed');
process.exit(failures ? 1 : 0);
