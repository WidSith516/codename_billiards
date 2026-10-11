// oracle_pottable.mjs — the honest geometric prediction for blind placement.
//
// The report argues "0.153^3 = 0.36 %, exactly what the 0/328 shows". That fixes the strike
// angle. This script computes the correct quantity instead: for each random 3-ball layout,
// over ALL 360 strike angles, does there exist an angle at which (a) every ball is inside the
// 2R rewind corridor and (b) the strike pots it or the rewind kick can actually send it to a
// pocket with enough speed? That is an UPPER bound on the one-strike-plus-one-rewind solve rate,
// i.e. an oracle test: if even this fails, no search can succeed.
//
//   node tools/verify_solver/oracle_pottable.mjs --layouts 328 [--free] [--seed N]
//
// --free uses an unconstrained sampler (no 34 px cue exclusion, no 44 px pocket exclusion).
import fs from 'node:fs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i < 0 ? d : Number(process.argv[i + 1]); };
const NLAY = arg('layouts', 328);
const NANG = arg('angles', 360);
const FREE = process.argv.includes('--free');
const SEED = arg('seed', 424242);
const ORIGIN = { x: 292, y: 280 };
const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
const TABLE = { left: 65, right: 975, top: 63, bottom: 497 };
const R = 12.2, R2 = 24.4, FRICTION = 640, POCKET_R = 24;

// --- 360 empty-table strike paths (the corridor precondition)
const paths = [];
for (let a = 0; a < NANG; a++) {
  const angle = (360 * a) / NANG;
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: -1000, y: -1000 }], pockets: POCKETS });
  g.setLevelIds([1]); g.ball(1).pocketed = true;
  g.shoot(angle, 170); g.settle(45);
  paths.push({ angle, p: g.cue.history.map((q) => ({ x: q.x, y: q.y })) });
}
function distToPath2(x, y, p, limit2) {
  let best = Infinity;
  for (let i = 0; i + 1 < p.length; i++) {
    const ax = p[i].x, ay = p[i].y, bx = p[i + 1].x, by = p[i + 1].y;
    const ex = bx - ax, ey = by - ay, len2 = ex * ex + ey * ey;
    let t = len2 > 0 ? ((x - ax) * ex + (y - ay) * ey) / len2 : 0;
    if (t < 0) t = 0; else if (t > 1) t = 1;
    const px = ax + ex * t - x, py = ay + ey * t - y;
    const d2 = px * px + py * py;
    if (d2 < best) { best = d2; if (best < limit2) return best; }
  }
  return best;
}

/** can a kick of speed `v` along unit (nx,ny) from (x,y) enter a pocket? */
function kickPots(x, y, nx, ny, v) {
  if (v <= 0) return false;
  for (const [px, py] of POCKETS) {
    const tx = px - x, ty = py - y;
    const along = tx * nx + ty * ny;
    if (along <= 0) continue;
    const perp = Math.abs(tx * ny - ty * nx);
    if (perp > POCKET_R) continue;                      // ray passes outside the capture circle
    if (v >= Math.sqrt(2 * FRICTION * along)) return true;
  }
  return false;
}

let s0 = 0;
function rnd() { s0 |= 0; s0 = (s0 + 0x6D2B79F5) | 0; let t = Math.imul(s0 ^ (s0 >>> 15), 1 | s0); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }
const insideBox = (b) => b.x > TABLE.left + R + 6 && b.x < TABLE.right - R - 6 && b.y > TABLE.top + R + 6 && b.y < TABLE.bottom - R - 6;
function sampleLayout(k) {
  const o = []; let guard = 0;
  while (o.length < k && guard++ < 6000) {
    const b = { x: TABLE.left + R + 6 + rnd() * (TABLE.right - TABLE.left - 2 * R - 12), y: TABLE.top + R + 6 + rnd() * (TABLE.bottom - TABLE.top - 2 * R - 12) };
    if (!insideBox(b)) continue;
    if (o.some((p) => Math.hypot(p.x - b.x, p.y - b.y) < R2 + 4)) continue;
    if (!FREE) {
      if (Math.hypot(b.x - ORIGIN.x, b.y - ORIGIN.y) <= 34) continue;
      if (POCKETS.some((p) => Math.hypot(p[0] - b.x, p[1] - b.y) < 44)) continue;
    }
    o.push(b);
  }
  return o;
}

const limit2 = R2 * R2;
let n = 0, corridorOK = 0, fullyPottable = 0, strikeOnly = 0;
const details = [];
s0 = SEED >>> 0;
for (let i = 0; i < NLAY; i++) {
  const balls = sampleLayout(3);
  if (balls.length < 3) continue;
  n++;
  const cands = paths.filter((p) => balls.every((b) => distToPath2(b.x, b.y, p.p, limit2) < limit2));
  if (!cands.length) continue;
  corridorOK++;
  let best = 0, bestAngle = null, bestKind = null;
  for (const c of cands) {
    const g = new LockedGame(CFG_LOCKED);
    g.placeLevel({ cue: ORIGIN, balls: balls.map((b, k) => ({ id: k + 1, x: b.x, y: b.y })), pockets: POCKETS });
    g.setLevelIds([1, 2, 3]);
    g.shoot(c.angle, 170);
    g.settle(60);
    const strikePots = g.pocketedLevel();
    if (strikePots === 3) { strikeOnly++; best = 3; bestAngle = c.angle; bestKind = 'strike'; break; }
    // every survivor must be kickable into a pocket
    const rows = g.corridor();
    let all = true;
    for (const r of rows) {
      const ball = g.ball(r.id);
      let ok = false;
      const h = g.cue.history;
      for (let j = 0; j < h.length && !ok; j++) {
        const dx = ball.x - h[j].x, dy = ball.y - h[j].y;
        const d = Math.hypot(dx, dy);
        if (d >= R2 || d < 1e-6) continue;
        const nx = dx / d, ny = dy / d;
        const rel = (-h[j].vx - ball.vx) * nx + (-h[j].vy - ball.vy) * ny;    // v_cue = -stored_v
        if (rel <= 0) continue;
        if (kickPots(ball.x, ball.y, nx, ny, rel)) ok = true;
      }
      if (!ok) { all = false; break; }
    }
    if (all) { best = 3; bestAngle = c.angle; bestKind = 'strike+kick'; break; }
    if (strikePots > best) { best = strikePots; bestAngle = c.angle; }
  }
  if (best === 3) fullyPottable++;
  details.push({ i, candAngles: cands.map((c) => c.angle), best, bestAngle, bestKind });
}
const arm = FREE ? 'free-sampler' : 'their-sampler';
const out = { arm, seed: SEED, n, corridorOK, corridorRate: corridorOK / n, fullyPottable, fullyPottableRate: fullyPottable / n, strikeOnly, details };
console.log(`arm                                                : ${arm} (seed ${SEED})`);
console.log(`layouts                                            : ${n}`);
console.log(`corridor precondition satisfied (some angle)       : ${corridorOK} (${(100 * corridorOK / n).toFixed(1)}%)`);
console.log(`GEOMETRICALLY pottable by 1 strike + 1 rewind kick : ${fullyPottable} (${(100 * fullyPottable / n).toFixed(1)}%)   <- upper bound on the solve rate`);
console.log(`  of which clearable by the strike alone           : ${strikeOnly}`);
const file = `tools/verify_solver/23_pottable_${FREE ? 'free' : 'their'}.json`;
fs.writeFileSync(file, JSON.stringify(out, null, 1));
console.log(`raw evidence: ${file}`);
