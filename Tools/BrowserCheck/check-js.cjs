// Loads interop.js with just enough of a browser to exercise the turtle tween and the palette,
// then asserts the behaviour the C# side promises a child. Run: node check-js.js
const fs = require('fs');
const path = require('path');

const REPO = process.env.REPO || 'C:\\Dev\\MaturinLogo';
let src = fs.readFileSync(path.join(REPO, 'Src', 'MaturinLogo', 'wwwroot', 'js', 'interop.js'), 'utf8');

// Turn the ES module into something evaluable here: strip export keywords, append the names we want.
src = src.replace(/export function /g, 'function ');
src += '\n;module.exports = { setTurtle, paletteOf, replayGraphics, appendGraphics, watchReducedMotion, __gfx: gfx, __snap: snapMotion, __lerp: lerpTurtle, __palette: PALETTE, __shapes: SHAPES, __draw: drawTurtle };';

const calls = [];
global.document = {
  getElementById: () => null,
  documentElement: { setAttribute() {}, removeAttribute() {}, classList: { toggle() {} } },
  createElement: () => ({ style: {}, click() {}, remove() {} }),
  body: { appendChild() {}, removeChild() {} },
};
global.window = {
  innerWidth: 1200, innerHeight: 800, devicePixelRatio: 1,
  addEventListener() {}, removeEventListener() {},
  matchMedia: (q) => ({ matches: process.env.REDUCED === '1', media: q, addEventListener() {}, addListener() {} }),
};
global.performance = { now: () => Date.now() };
let rafQueue = [];
global.requestAnimationFrame = (fn) => { rafQueue.push(fn); return rafQueue.length; };
global.cancelAnimationFrame = () => { rafQueue = []; };
global.localStorage = { setItem() {}, getItem() { return null; }, removeItem() {} };
global.Blob = function () {};
global.URL = { createObjectURL: () => '', revokeObjectURL() {} };
global.FileReader = function () {};

const tmp = path.join(__dirname, 'interop.cjs');
fs.writeFileSync(tmp, src);
const ml = require(tmp);
fs.unlinkSync(tmp);

function assert(cond, msg) { if (!cond) { console.error('FAIL: ' + msg); process.exitCode = 1; } else { console.log('ok   - ' + msg); } }

// ---- the palette -------------------------------------------------------------
assert(ml.__palette.length === 16, 'the canvas has 16 colours, not six');
assert(ml.paletteOf(8) === '#FF3333', 'SETPC 8 is RED on the canvas (was folded onto green)');
assert(ml.paletteOf(15) === '#999999', 'SETPC 15 is GRAY');
assert(ml.paletteOf(6) === '#33CCCC', 'SETPC 6 is CYAN');
assert(ml.paletteOf(0) === '#000000', 'index 0 is the background');
assert(ml.__shapes.length === 6, 'six shapes are drawn');

// ---- the turtle tween ---------------------------------------------------------
ml.setTurtle(JSON.stringify({ x: 140, y: 96, heading: 0, color: 2, visible: true, shape: 0, size: 1, ms: 100 }));
assert(ml.__gfx.shown.heading === 0, 'speed 100-equivalent snap starts at the target');

// A slow move: the turtle must be mid-way, not already there.
ml.setTurtle(JSON.stringify({ x: 140, y: 96, heading: 0, color: 2, visible: true, shape: 0, size: 1, ms: 0 }));
ml.setTurtle(JSON.stringify({ x: 200, y: 96, heading: 90, color: 2, visible: true, shape: 0, size: 1, ms: 1000 }));
assert(rafQueue.length === 1, 'a tween schedules a frame');

// Run one frame far in the future and check it landed.
const pending = rafQueue.splice(0, rafQueue.length);
pending.forEach((fn) => fn(Date.now() + 5000));
assert(Math.abs(ml.__gfx.shown.x - 200) < 0.001, 'the tween finishes at the target x');
assert(Math.abs(ml.__gfx.shown.heading - 90) < 0.001, 'the tween finishes at the target heading');

// Shortest arc: 350 -> 0 is ten degrees, not three hundred and fifty the long way.
let l = ml.__lerp({ x: 0, y: 0, heading: 350, alpha: 1 }, { x: 0, y: 0, heading: 0, alpha: 1 }, 0.5);
assert(l.heading > 350 || l.heading < 5, 'a spin near north takes the short way round, got ' + l.heading.toFixed(1));

// HOME from 350 must not unwind the whole program.
l = ml.__lerp({ x: 0, y: 0, heading: 350, alpha: 1 }, { x: 0, y: 0, heading: 0, alpha: 1 }, 0.5);
assert(Math.abs(l.heading - 355) < 0.001, 'halfway from 350 to 0 is 355, got ' + l.heading.toFixed(1));

// HT fades rather than blinking.
ml.setTurtle(JSON.stringify({ x: 140, y: 96, heading: 0, color: 2, visible: true, shape: 0, size: 1, ms: 0 }));
ml.setTurtle(JSON.stringify({ x: 140, y: 96, heading: 0, color: 2, visible: false, shape: 0, size: 1, ms: 1000 }));
let fadeFrames = rafQueue.splice(0, rafQueue.length);
fadeFrames.forEach((fn) => fn(Date.now() + 500));      // halfway through the second
assert(ml.__gfx.shown.alpha > 0 && ml.__gfx.shown.alpha < 1,
    'HT is half-faded mid-tween rather than gone, got ' + ml.__gfx.shown.alpha.toFixed(2));
fadeFrames = rafQueue.splice(0, rafQueue.length);
fadeFrames.forEach((fn) => fn(Date.now() + 5000));
assert(ml.__gfx.shown.alpha === 0, 'HT ends fully faded');

// ---- reduced motion -------------------------------------------------------------
process.env.REDUCED = '1';
let reported = null;
const initial = ml.watchReducedMotion({ invokeMethodAsync: (name, value) => { reported = value; return Promise.resolve(); } });
assert(initial === true, 'watchReducedMotion returns the current value for .NET to read at init');
assert(ml.__snap(1000) === true, 'reduced motion makes every tween snap');
assert(reported === null, 'the value at init is not also dispatched back into .NET');

// A later change does arrive on the callback, which is when nothing else is in flight.
process.env.REDUCED = '0';
let captured = null;
global.window.matchMedia = (q) => ({ matches: false, media: q, addEventListener: (t, fn) => { captured = fn; } });
ml.watchReducedMotion({ invokeMethodAsync: (name, value) => { reported = value; return Promise.resolve(); } });
captured({ matches: true });
assert(reported === true, 'a change in the system setting is pushed to .NET');
assert(ml.__snap(1000) === true, 'and the canvas stops tweening');
captured({ matches: false });
assert(ml.__snap(1000) === false, 'turning it off lets the tween run again');

// ---- the shape survives a tween ----------------------------------------------------
ml.setTurtle(JSON.stringify({ x: 140, y: 96, heading: 0, color: 2, visible: true, shape: 2, size: 1, ms: 0 }));
assert(ml.__gfx.shown.shape === 2, 'SHAPE 2 reaches the drawn state');

// ---- drawing does not throw for any shape or colour --------------------------------
const g = {
  save() {}, restore() {}, beginPath() {}, moveTo() {}, lineTo() {}, closePath() {}, fill() {},
  arc() {}, ellipse() {}, fillRect() {}, stroke() {}, setTransform() {},
  fillStyle: '', strokeStyle: '', lineWidth: 1, lineCap: '', globalAlpha: 1, font: '', textBaseline: '',
};
for (let shape = 0; shape < 6; shape++) {
  for (let color = 0; color < 16; color++) {
    ml.__draw(g, { x: 10, y: 10, heading: 45, color, visible: true, shape, size: 1, alpha: 1 }, 0, 0, 2);
  }
}
assert(true, 'every shape draws in every colour without throwing');

ml.__draw(g, null, 0, 0, 2);
ml.__draw(g, { x: 1, y: 1, heading: 0, color: 1, visible: false, shape: 0, alpha: 0 }, 0, 0, 2);
assert(true, 'a hidden or missing turtle draws nothing rather than failing');

console.log(process.exitCode ? '\nSOME CHECKS FAILED' : '\nall checks passed');
