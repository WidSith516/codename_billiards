// my_coverage.mjs — independent recomputation of the "15.3 % rewind corridor" number, plus
// the number the report does NOT compute: the UNION of the corridors over all strike
// angles (the player/designer may pick any of 360).
//
//   node tools/verify_solver/my_coverage.mjs [--angles 360] [--grid 6] [--layouts 328]
//
// Rasterised: for every path sample, mark the grid cells within 2R of it (and the max rewind
// kick each cell can receive). O(samples * cells_in_radius) per angle, not O(cells * samples).
import fs from 'node:fs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';

const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf('--' + n); return i < 0 ? d : Number(argv[i + 1]); };
const NANG = arg('angles', 360);
const GRID = arg('grid', 6);
const NLAY = arg('layouts', 328);

const ORIGIN = { x: 292, y: 280 };
const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
const TABLE = { left: 65, right: 975, top: 63, bottom: 497 };
const R = 12.2, R2 = 24.4;

const paths = [];
for (let a = 0; a < NANG; a++) {
  const angle = (360 * a) / NANG;
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: -1000, y: -1000 }], pockets: POCKETS });
  g.setLevelIds([1]);
  g.ball(1).pocketed = true;
  g.shoot(angle, 170);
  g.settle(45);
  const h = g.cue.history;
  paths.push({ angle, xs: h.map((p) => p.x), ys: h.map((p) => p.y), vxs: h.map((p) => p.vx), vys: h.map((p) => p.vy) });
}
console.log(`traced ${paths.length} full-power strike paths (mean ${(paths.reduce((s, p) => s + p.xs.length, 0) / paths.length).toFixed(0)} samples)`);

const gx = [], gy = [];
for (let x = TABLE.left + R + 4; x <= TABLE.right - R - 4; x += GRID) gx.push(x);
for (let y = TABLE.top + R + 4; y <= TABLE.bottom - R - 4; y += GRID) gy.push(y);
const NX = gx.length, NY = gy.length, total = NX * NY;
const unionCorr = new Uint8Array(total);
const unionKick250 = new Uint8Array(total);
const unionPocket = new Uint8Array(total);
const maxKick = new Float64Array(total);
const perAngle = [];

for (const p of paths) {
  const corCell = new Uint8Array(total);
  const kickCell = new Float64Array(total);
  const n = p.xs.length;
  for (let i = 0; i < n; i++) {
    const sx = p.xs[i], sy = p.ys[i];
    const i0 = Math.max(0, Math.floor((sx - R2 - gx[0]) / GRID)), i1 = Math.min(NX - 1, Math.ceil((sx + R2 - gx[0]) / GRID));
    const j0 = Math.max(0, Math.floor((sy - R2 - gy[0]) / GRID)), j1 = Math.min(NY - 1, Math.ceil((sy + R2 - gy[0]) / GRID));
    for (let gi = i0; gi <= i1; gi++) {
      const dx0 = gx[gi] - sx;
      for (let gj = j0; gj <= j1; gj++) {
        const dy0 = gy[gj] - sy;
        const d2 = dx0 * dx0 + dy0 * dy0;
        if (d2 >= R2 * R2) continue;
        const idx = gi * NY + gj;
        corCell[idx] = 1;
        const d = Math.sqrt(d2) || 1e-9;
        const j = (-p.vxs[i] * dx0 - p.vys[i] * dy0) / d;
        if (j > kickCell[idx]) kickCell[idx] = j;
      }
    }
  }
  let inCor = 0, k250 = 0, kPocket = 0;
  for (let idx = 0; idx < total; idx++) {
    if (!corCell[idx]) continue;
    inCor++;
    unionCorr[idx] = 1;
    const kick = kickCell[idx];
    if (kick > maxKick[idx]) maxKick[idx] = kick;
    if (kick > 250) {
      k250++; unionKick250[idx] = 1;
      const gi = Math.floor(idx / NY), gj = idx % NY;
      const x = gx[gi], y = gy[gj];
      let need = Infinity;
      for (const P of POCKETS) need = Math.min(need, Math.sqrt(2 * 640 * Math.hypot(P[0] - x, P[1] - y)));
      if (kick > need) { kPocket++; unionPocket[idx] = 1; }
    }
  }
  perAngle.push({ angle: p.angle, inCorridor: inCor / total, kick250: k250 / total, kickPocket: kPocket / total });
}
const mean = (k) => perAngle.reduce((s, r) => s + r[k], 0) / perAngle.length;
const count = (arr) => arr.reduce((s, v) => s + v, 0) / arr.length;
const out = [];
const say = (s) => { out.push(s); console.log(s); };
say(`\n=== PART 1: per-angle coverage (rasterised, grid ${NX}x${NY} = ${total} pts, ${GRID} px) ===`);
say(`  MEAN over ${NANG} angles:  in 2R corridor ${(100 * mean('inCorridor')).toFixed(1)}%   kick>250 ${(100 * mean('kick250')).toFixed(1)}%   kick reaches NEAREST pocket ${(100 * mean('kickPocket')).toFixed(1)}%`);
const e15 = perAngle.filter((r) => Math.abs(r.angle % 15) < 1e-9);
say(`  the 24 angles dev/coverage.mjs uses (every 15 deg): in corridor ${(100 * (e15.reduce((s, r) => s + r.inCorridor, 0) / e15.length)).toFixed(1)}%`);
say(`\n=== PART 2: UNION over all ${NANG} strike angles (the player/designer picks the angle) ===`);
say(`  area inside SOME angle's 2R corridor          : ${(100 * count(unionCorr)).toFixed(1)}%`);
say(`  area where SOME angle gives a kick > 250 px/s : ${(100 * count(unionKick250)).toFixed(1)}%`);
say(`  area where SOME angle's kick reaches a pocket : ${(100 * count(unionPocket)).toFixed(1)}%   (nearest-pocket test, direction ignored)`);

// ---------------------------------------------------------------- part 3: layouts
let s0 = 0;
function rnd() { s0 |= 0; s0 = (s0 + 0x6D2B79F5) | 0; let t = Math.imul(s0 ^ (s0 >>> 15), 1 | s0); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }
const insideBox = (b) => b.x > TABLE.left + R + 6 && b.x < TABLE.right - R - 6 && b.y > TABLE.top + R + 6 && b.y < TABLE.bottom - R - 6;
const sampleLayout = (k, free) => {
  const o = []; let guard = 0;
  while (o.length < k && guard++ < 6000) {
    const b = { x: TABLE.left + R + 6 + rnd() * (TABLE.right - TABLE.left - 2 * R - 12), y: TABLE.top + R + 6 + rnd() * (TABLE.bottom - TABLE.top - 2 * R - 12) };
    if (!insideBox(b)) continue;
    if (o.some((p) => Math.hypot(p.x - b.x, p.y - b.y) < R2 + 4)) continue;
    if (!free) {
      if (Math.hypot(b.x - ORIGIN.x, b.y - ORIGIN.y) <= 34) continue;
      if (POCKETS.some((p) => Math.hypot(p[0] - b.x, p[1] - b.y) < 44)) continue;
    }
    o.push(b);
  }
  return o;
};
function distToPath2(x, y, p, limit2) {
  const n = p.xs.length; let best = Infinity;
  for (let i = 0; i + 1 < n; i++) {
    const ax = p.xs[i], ay = p.ys[i], bx = p.xs[i + 1], by = p.ys[i + 1];
    const ex = bx - ax, ey = by - ay, len2 = ex * ex + ey * ey;
    let t = len2 > 0 ? ((x - ax) * ex + (y - ay) * ey) / len2 : 0;
    if (t < 0) t = 0; else if (t > 1) t = 1;
    const px = ax + ex * t - x, py = ay + ey * t - y;
    const d2 = px * px + py * py;
    if (d2 < best) { best = d2; if (best < limit2) return best; }
  }
  return best;
}
say(`\n=== PART 3: geometric reachability of random 3-ball layouts (${NLAY} layouts per arm, ${NANG} angles) ===`);
const limit2 = R2 * R2;
const arms = [];
for (const [armName, free, seed] of [['their-sampler', false, 424242], ['free-sampler', true, 777001]]) {
  s0 = seed >>> 0;
  let some = 0, n2 = 0;
  const counts = [];
  for (let i = 0; i < NLAY; i++) {
    const balls = sampleLayout(3, free);
    if (balls.length < 3) continue;
    n2++;
    let hits = 0;
    for (const p of paths) {
      let all = true;
      for (const b of balls) { if (distToPath2(b.x, b.y, p, limit2) >= limit2) { all = false; break; } }
      if (all) hits++;
    }
    counts.push(hits);
    if (hits) some++;
  }
  const meanAngles = counts.reduce((a, b) => a + b, 0) / counts.length;
  arms.push({ armName, n: n2, someAngleCoversAll: some, rate: some / n2, meanAnglesPerLayout: +meanAngles.toFixed(2) });
  say(`  ${armName.padEnd(15)}: ${some}/${n2} layouts have SOME angle whose 2R corridor covers all 3 balls (${(100 * some / n2).toFixed(1)}%), mean ${meanAngles.toFixed(2)}/${NANG} angles work`);
}
say(`\n  compare: the report's fixed-angle prediction 0.153^3 = ${(100 * Math.pow(0.153, 3)).toFixed(2)}%`);
say(`  the corridor test is NECESSARY but not sufficient (the kick must also point at a pocket`);
say(`  with enough speed), so the true blind-placement geometric rate is below these numbers.`);
say('\nwrote tools/verify_solver/15_coverage_mine.json');
fs.writeFileSync('tools/verify_solver/15_coverage_mine.json', JSON.stringify({ perAngleMean: { inCorridor: mean('inCorridor'), kick250: mean('kick250'), kickPocket: mean('kickPocket') }, union: { corridor: count(unionCorr), kick250: count(unionKick250), pocket: count(unionPocket) }, arms, grid: GRID, nAngles: NANG, totalCells: total }, null, 1));
fs.writeFileSync('tools/verify_solver/15_coverage_mine.txt', out.join('\n') + '\n');
