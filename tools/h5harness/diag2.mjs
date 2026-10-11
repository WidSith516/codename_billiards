// diag2.mjs — (a) divergence threshold, (b) NaN census in a long rewind session, (c) realistic-table speed sweep
import { createGame, summarize, round } from './sim.mjs';
const DT = 1 / 120;

console.log('--- (a) vertical flight L=409.6px: speed multiplier after 6 wall hits ---');
for (const v of [600, 800, 900, 1000, 1050, 1082, 1100, 1200, 1258, 1500]) {
  const g = createGame();
  g.clearObjectBalls();
  g.placeBall(0, 292, 280, 0, v);
  g.sim.set({ shotUsed: true });
  let bounces = 0, prevSign = Math.sign(v), peak = v, vAt = {};
  for (let i = 0; i < 120 * 20; i++) {
    g.step(1);
    const cue = g.cue();
    const s = Math.hypot(cue.vx, cue.vy);
    if (Number.isFinite(s)) peak = Math.max(peak, s);
    if (Math.sign(cue.vy) !== prevSign && cue.vy !== 0) { prevSign = Math.sign(cue.vy); bounces++; if (bounces <= 6) vAt[bounces] = round(s, 2); }
    if (bounces >= 7) break;
  }
  const cue = g.cue();
  console.log(`v0=${v} theory_threshold=${round(2.642 * 409.6, 1)} bounces=${bounces} speeds=${JSON.stringify(vAt)} peak=${Number.isFinite(peak) ? round(peak, 1) : 'Infinity'} final=${JSON.stringify({ x: cue.x, y: cue.y, vx: cue.vx, vy: cue.vy })}`);
}

console.log('\n--- (b) NaN census over a long rewind session (D4 scripted) ---');
const g = createGame();
g.shoot(0, 170);
g.settle({ maxSeconds: 20 });
let a = 1 >>> 0;
const rnd = () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
for (let k = 0; k < 80; k++) {
  g.pressR();
  let n = 0;
  while (g.state.rewinding && n < 60 + Math.floor(rnd() * 200)) { g.step(1); n++; }
  g.releaseR();
  g.settle({ maxSeconds: 8 });
}
const h = g.cue().history;
let bad = 0, nanX = 0;
const deltas = [];
for (let i = 1; i < h.length; i++) {
  const d = Math.hypot(h[i].x - h[i - 1].x, h[i].y - h[i - 1].y);
  if (!Number.isFinite(d)) bad++;
  if (!Number.isFinite(h[i].x) || !Number.isFinite(h[i].y)) nanX++;
  deltas.push(d);
}
console.log('entries', h.length, 'nonFiniteDeltas', bad, 'nonFiniteCoords', nanX, 'maxSpeed', g.metrics.maxSpeed);
const s = deltas.slice().filter(Number.isFinite).sort((x, y) => x - y);
console.log('finite summary', JSON.stringify(summarize(s.filter(Number.isFinite))));
console.log('p99 index', Math.round(0.99 * (deltas.length - 1)), 'value', deltas.slice().sort((x, y) => x - y)[Math.round(0.99 * (deltas.length - 1))]);
console.log('count of deltas < 0.0755:', deltas.filter((d) => d < 0.0755).length, 'count < 0.2:', deltas.filter((d) => d < 0.2).length);

console.log('\n--- (c) realistic 11-ball table: max cue speed per angle over 60 s ---');
for (const ang of [0, 45, 90, 135, 180, 225, 270, 315]) {
  const gg = createGame();
  gg.shoot(ang, 170);
  let firstDiv = -1;
  for (let i = 0; i < 120 * 60; i++) {
    gg.step(1);
    if (firstDiv < 0 && !Number.isFinite(gg.metrics.maxSpeed)) firstDiv = i;
    if (gg.metrics.maxSpeed > 1e6 && firstDiv < 0) firstDiv = i;
  }
  const cue = gg.cue();
  console.log(`angle=${ang} pocketed=${gg.pocketedCount()} maxSpeed=${Number.isFinite(gg.metrics.maxSpeed) ? round(gg.metrics.maxSpeed, 1) : 'Infinity'} divergedAtStep=${firstDiv} nan=${gg.metrics.nan} cue=${JSON.stringify([round(cue.x, 1), round(cue.y, 1), Number.isFinite(cue.vx) ? round(cue.vx, 1) : 'Inf', Number.isFinite(cue.vy) ? round(cue.vy, 1) : 'Inf'])}`);
}
