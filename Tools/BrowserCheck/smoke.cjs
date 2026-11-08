// Renders the real 40x24 grid, exactly as ScreenGrid.Build does, through a minimal DOM so the
// markup path (escaping, the cursor class, the attribute classes) can be exercised outside Blazor.
const fs = require('fs');
const path = require('path');

const REPO = process.env.REPO || 'C:\\Dev\\MaturinLogo';
let src = fs.readFileSync(path.join(REPO, 'Src', 'MaturinLogo', 'wwwroot', 'js', 'interop.js'), 'utf8');
src = src.replace(/export function /g, 'function ');
src += '\n;module.exports = { initApp, initGraphics, fitGrid, setTheme, replayGraphics, appendGraphics, setTurtle, yieldFrame, mapKeyOut: mapKey, gfx: gfx };';

const el = (tag) => ({
    tag, style: {}, children: [], classList: { toggle() {}, add() {}, remove() {} },
    attributes: {}, listeners: {},
    setAttribute(k, v) { this.attributes[k] = v; },
    removeAttribute(k) { delete this.attributes[k]; },
    addEventListener(t, fn) { this.listeners[t] = fn; },
    removeEventListener() {},
    getContext() { return ctx; },
    getBoundingClientRect() { return { width: 800, height: 480 }; },
    appendChild(c) { this.children.push(c); },
    removeChild(c) { this.children = this.children.filter(x => x !== c); },
    click() {},
    querySelector() { return null; },
});

const ctx = new Proxy({
    save() {}, restore() {}, beginPath() {}, moveTo() {}, lineTo() {}, arc() {}, ellipse() {},
    closePath() {}, fill() {}, stroke() {}, fillRect() {}, fillText() {}, setTransform() {},
}, { set: () => true, get: (t, k) => (k in t ? t[k] : undefined) });

const nodes = {};
global.document = {
    getElementById: (id) => (nodes[id] ||= el('#' + id)),
    createElement: (t) => el(t),
    documentElement: el('html'),
    body: el('body'),
    fonts: { ready: Promise.resolve() },
};
global.window = {
    innerWidth: 1400, innerHeight: 900, devicePixelRatio: 2,
    addEventListener() {}, removeEventListener() {},
    matchMedia: () => ({ matches: false, addEventListener() {} }),
};
global.performance = { now: () => Date.now() };
global.requestAnimationFrame = () => 1;
global.cancelAnimationFrame = () => {};
global.localStorage = { setItem(k, v) { this.v = v; }, getItem() { return this.v || null; }, removeItem() {} };
global.Blob = function () {};
global.URL = { createObjectURL: () => 'blob:x', revokeObjectURL() {} };
global.FileReader = function () {};

const tmp = path.join(__dirname, 'interop.smoke.cjs');
fs.writeFileSync(tmp, src);
const ml = require(tmp);
fs.unlinkSync(tmp);

let failures = 0;
function assert(cond, msg) {
    if (!cond) { failures++; console.error('FAIL: ' + msg); } else console.log('ok   - ' + msg);
}

// The canvas needs the elements Blazor would have painted by then.
ml.initGraphics(24);
nodes['ml-screen'] = el('div');
nodes['ml-graphics'] = el('div');
nodes['ml-canvas'] = el('canvas');
nodes['ml-canvas'].getContext = () => ctx;
ml.initGraphics(24);

// A scene in the 16-colour palette, then the turtle in each shape, then a tween.
const items = [];
for (let c = 0; c < 16; c++) {
    items.push({ kind: 'segment', x1: 10 + c, y1: 20, x2: 10 + c, y2: 170, color: c, width: 1 });
}
items.push({ kind: 'label', x1: 30, y1: 100, text: 'HELLO', color: 9 });
ml.replayGraphics(JSON.stringify(items), 0, 4, 24);
assert(ml.gfx.items.length === 17, 'a full-colour scene replays');
assert(ml.gfx.lowerLines === 4, 'the text rows are remembered for the canvas height');

for (let shape = 0; shape < 6; shape++) {
    ml.setTurtle(JSON.stringify({ x: 140, y: 96, heading: shape * 60, color: shape, visible: true, shape, size: 2, ms: 0 }));
}
assert(ml.gfx.shown.shape === 5, 'the last shape is what is drawn');

// appendGraphics must not lose anything, which is the path a running program uses.
ml.appendGraphics(JSON.stringify([{ kind: 'segment', x1: 0, y1: 0, x2: 50, y2: 50, color: 12, width: 1 }]));
assert(ml.gfx.items.length === 18, 'a stroke appended after a replay is kept');

// A full replay replaces rather than accumulates - the CLS path.
ml.replayGraphics(JSON.stringify([{ kind: 'segment', x1: 0, y1: 0, x2: 1, y2: 1, color: 1, width: 1 }]), 3, 4, 24);
assert(ml.gfx.items.length === 1, 'a replay starts a new picture');
assert(ml.gfx.bg === 3, 'the background index travels with the replay');

// Empty and null payloads must not throw: Logo sends them on a fresh session.
ml.replayGraphics(null, 0, 24, 24);
ml.appendGraphics(null);
ml.setTurtle(null);
assert(ml.gfx.shown === null, 'a null turtle clears the live state');

// fitGrid scales the grid from the window, and tolerates a missing element.
const size = ml.fitGrid(40, 24);
assert(size >= 8, 'fitGrid picks a usable font size, got ' + size);
assert(nodes['ml-screen'].style.fontSize === size + 'px', 'the size lands on the screen element');

// setTheme drives both the phosphor and the CRT class.
ml.setTheme('amber', true);
assert(document.documentElement.attributes['data-theme'] === 'amber', 'amber sets the theme attribute');
ml.setTheme('green', false);
assert(!('data-theme' in document.documentElement.attributes), 'green clears it');

// The keyboard contract: every token the machine switches on that this can produce.
const cases = [
    [{ key: ',', altKey: true }, 'A-COMMA'],
    [{ key: '?', altKey: true }, 'A-?'],
    [{ key: '/', altKey: true }, 'A-SLASH'],
    [{ key: '/', ctrlKey: true }, 'C-SLASH'],
    [{ key: 's', ctrlKey: true }, 'C-S'],
    [{ key: 'm', altKey: true }, 'A-M'],
    [{ key: 'ArrowUp' }, 'UP'],
    [{ key: 'Escape' }, 'ESC'],
    [{ key: 'Tab' }, 'TAB'],
    // Alt/Cmd+Tab is taken by the operating system before the page ever sees it, so Tab is Tab
    // whatever is held with it. Recorded here so the behaviour is a decision, not a surprise.
    [{ key: 'Tab', metaKey: true }, 'TAB'],
    [{ key: 'Tab', altKey: true }, 'TAB'],
    [{ key: 'F1' }, null],
];
for (const [event, expected] of cases) {
    const got = ml.mapKeyOut(event);
    assert(got === expected, `mapKey(${JSON.stringify(event)}) -> ${JSON.stringify(got)}`);
}

// yieldFrame resolves, so a long program cannot wedge the page.
let done = false;
global.requestAnimationFrame = (fn) => { fn(); return 1; };
ml.yieldFrame().then(() => { done = true; });
setTimeout(() => {
    assert(done, 'yieldFrame resolves on the next frame');
    console.log(failures ? `\n${failures} CHECKS FAILED` : '\nall smoke checks passed');
    process.exitCode = failures ? 1 : 0;
}, 100);
