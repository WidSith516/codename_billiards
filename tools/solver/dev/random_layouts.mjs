// random_layouts.mjs — THE AUTHORING EXPERIMENT.
//
// Question: if a level designer places balls WITHOUT knowing the rule, how often is the
// level solvable? And does the geometric rule discovered in dev/fan.mjs actually convert
// blind placement into solvable levels?
//
// Three arms, identical ball count and identical solver budget:
//   random : every ball placed uniformly at random on the playable area
//   hybrid : 1 backstop ball (rule A) + the rest random
//   rule   : 2 backstop balls (rule A) + the rest on the cut rule (rule B), unverified
//
// Usage:
//   node tools/solver/dev/random_layouts.mjs --mode random --n 40 --seed 100 --balls 3 --out tools/solver/out/rand_random_100.json
//   node tools/solver/dev/random_layouts.mjs --mode all --n 40 --seed 100 --balls 3 --out tools/solver/out/rand_all_100.json

import fs from 'node:fs';
import path from 'node:path';
import { LockedGame, CFG_LOCKED } from '../lib/locked.mjs';
import { solveLevel, replaySolution } from '../lib/search.mjs';

const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf('--' + n); return i < 0 ? d : argv[i + 1]; };
const MODE = arg('mode', 'random');
const N = Number(arg('n', 40));
const SEED = Number(arg('seed', 1));
const BALLS = Number(arg('balls', 3));
const OUT = arg('out', null);
const CONFIG = arg('config', 'locked');

const ORIGIN = { x: 292, y: 280 };
const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
const TABLE = { left: 65, right: 975, top: 63, bottom: 497 };
const R = 12.2, R2 = 24.4;

// deterministic RNG so every shard is reproducible
let s0 = SEED >>> 0;
function rnd() {
  s0 |= 0; s0 = (s0 + 0x6D2B79F5) | 0;
  let t = Math.imul(s0 ^ (s0 >>> 15), 1 | s0);
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
}
const inside = (b) => b.x > TABLE.left + R + 6 && b.x < TABLE.right - R - 6
  && b.y > TABLE.top + R + 6 && b.y < TABLE.bottom - R - 6;
const okPlacement = (b, placed) => inside(b)
  && Math.hypot(b.x - ORIGIN.x, b.y - ORIGIN.y) > 34
  && !placed.some((p) => Math.hypot(p.x - b.x, p.y - b.y) < R2 + 4)
  && !POCKETS.some((p) => Math.hypot(p[0] - b.x, p[1] - b.y) < 44);

function randomBalls(k) {
  const out = [];
  let guard = 0;
  while (out.length < k && guard++ < 6000) {
    const b = { x: TABLE.left + R + 6 + rnd() * (TABLE.right - TABLE.left - 2 * R - 12),
      y: TABLE.top + R + 6 + rnd() * (TABLE.bottom - TABLE.top - 2 * R - 12) };
    if (okPlacement(b, out)) out.push(b);
  }
  return out;
}

const unit = (x, y) => { const L = Math.hypot(x, y) || 1; return [x / L, y / L]; };

function backstop(angleDeg, pocket, a = 23.5) {
  const d = angleDeg * Math.PI / 180;
  const s1 = [ORIGIN.x + 2 * Math.cos(d), ORIGIN.y + 2 * Math.sin(d)];
  const u = unit(pocket[0] - s1[0], pocket[1] - s1[1]);
  return { x: s1[0] + a * u[0], y: s1[1] + a * u[1], rule: 'backstop' };
}

function pickFan(angle, count) {
  const cands = POCKETS.map((P) => {
    const b = backstop(angle, P);
    const kick = -Math.cos(0) * 0;   // placeholder, strength computed below
    const d = angle * Math.PI / 180;
    const s1 = [ORIGIN.x + 2 * Math.cos(d), ORIGIN.y + 2 * Math.sin(d)];
    const u = unit(P[0] - s1[0], P[1] - s1[1]);
    const rev = ((angle + 180) % 360) * Math.PI / 180;
    const cosA = Math.cos(rev - Math.atan2(u[1], u[0]));
    const kickSpeed = cosA * 1258;
    const reach = kickSpeed * kickSpeed / 1280;
    const dist = Math.hypot(P[0] - b.x, P[1] - b.y);
    return { b, kick: kickSpeed, reach, dist };
  }).filter((c) => c.kick > 250 && c.reach > c.dist * 1.15)
    .sort((x, y) => y.kick - x.kick);
  const chosen = [];
  for (const c of cands) {
    if (chosen.length >= count) break;
    if (chosen.some((x) => Math.hypot(x.b.x - c.b.x, x.b.y - c.b.y) < R2 + 3)) continue;
    if (Math.hypot(c.b.x - ORIGIN.x, c.b.y - ORIGIN.y) < 21) continue;
    chosen.push(c);
  }
  return chosen.map((c) => c.b);
}

function cutBalls(angle, power, k, placed) {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: placed.map((b, i) => ({ id: i + 1, x: b.x, y: b.y })), pockets: POCKETS });
  g.setLevelIds(placed.map((b, i) => i + 1));
  g.shoot(angle, power); g.settle(45);
  const p = g.cue.history;
  const out = [];
  for (let j = 6; j < p.length && out.length < k; j += 3) {
    const s = p[j];
    const v = Math.hypot(s.vx, s.vy);
    if (v < 200) break;
    for (const P of POCKETS) {
      if (out.length >= k) break;
      const u = unit(P[0] - s.x, P[1] - s.y);
      const B = { x: s.x + R2 * u[0], y: s.y + R2 * u[1], rule: 'cut' };
      if (!inside(B)) continue;
      if (B.x < TABLE.left + 20 || B.x > TABLE.right - 20) continue;
      if (Math.hypot(B.x - ORIGIN.x, B.y - ORIGIN.y) < 34) continue;
      if ([...placed, ...out].some((q) => Math.hypot(q.x - B.x, q.y - B.y) < R2 + 4)) continue;
      if (!POCKETS.some((q) => Math.hypot(q[0] - B.x, q[1] - B.y) < 30)) { /* fine */ }
      const cosCut = (s.vx * u[0] + s.vy * u[1]) / v;
      const ballV = 0.9775 * v * cosCut;
      const dist = Math.hypot(P[0] - B.x, P[1] - B.y);
      if (ballV * ballV / 1280 < dist * 1.2) continue;
      out.push(B);
    }
  }
  return out;
}

const LEVEL_BUDGET = {
  angleStep: 2, powers: [130, 170], releaseFractions: [0.3, 0.6, 1.0],
  maxRewinds: 3, beam: 5, topStrikes: 6, refineStep: 0.5,
};

function makeLayout(mode) {
  if (mode === 'random') return randomBalls(BALLS);
  if (mode === 'hybrid') {
    const ang = rnd() * 360;
    const fan = pickFan(ang, 1);
    const rest = randomBalls(BALLS - fan.length, );
    const placed = [...fan];
    for (const b of rest) if (okPlacement(b, placed)) placed.push(b);
    let guard = 0;
    while (placed.length < BALLS && guard++ < 400) {
      const b = randomBalls(1)[0];
      if (b && okPlacement(b, placed)) placed.push(b);
    }
    return placed;
  }
  // rule
  const ang = rnd() * 360;
  const fan = pickFan(ang, 2);
  const cuts = cutBalls(ang, 170, BALLS - fan.length, fan);
  const placed = [...fan];
  for (const b of cuts) if (okPlacement(b, placed)) placed.push(b);
  let guard = 0;
  while (placed.length < BALLS && guard++ < 400) {
    const b = randomBalls(1)[0];
    if (b && okPlacement(b, placed)) placed.push(b);
  }
  return placed;
}

const results = [];
const modes = MODE === 'all' ? ['random', 'hybrid', 'rule'] : [MODE];
for (const mode of modes) {
  const t0 = Date.now();
  let solved = 0;
  for (let i = 0; i < N; i++) {
    const balls = makeLayout(mode);
    if (balls.length < BALLS) continue;
    const lvl = {
      id: `${mode}_${SEED}_${i}`, name: mode, cue: ORIGIN,
      balls: balls.map((b, k) => ({ id: k + 1, x: +b.x.toFixed(2), y: +b.y.toFixed(2), type: 'plain' })),
      pockets: POCKETS, table: TABLE, budget: LEVEL_BUDGET,
    };
    let res;
    try { res = solveLevel(lvl, { cfgName: CONFIG }); } catch (e) { res = { status: 'ERROR', error: String(e) }; }
    let replayed = false;
    if (res.status === 'SOLVED') {
      const rp = replaySolution(lvl, res, CONFIG);
      replayed = rp.ok;
      if (!rp.ok) res.status = 'UNSOLVED(unreplayable)';
      else solved++;
    }
    results.push({
      mode, i, balls: lvl.balls.map((b) => ({ x: b.x, y: b.y, rule: balls[Math.min(balls.length - 1, lvl.balls.indexOf(b))]?.rule })),
      status: res.status, replayed,
      bestNoRewind: res.bestNoRewind, bestAny: res.bestAny,
      maxPotential: res.maxPotential, maxCorridorBalls: res.maxCorridorBalls,
      totalBalls: BALLS, ms: res.stats?.ms ?? 0, sims: res.stats?.sims ?? 0,
      strike: res.strike, rewinds: res.rewinds?.length ?? 0,
      topCandidates: (res.topCandidates ?? []).slice(0, 3),
    });
  }
  const secs = ((Date.now() - t0) / 1000).toFixed(1);
  console.log(`${mode}: ${solved}/${N} SOLVED (replay-verified) in ${secs}s`);
}

if (OUT) {
  const p = path.isAbsolute(OUT) ? OUT : path.resolve(OUT);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, JSON.stringify({ mode: MODE, seed: SEED, balls: BALLS, n: N, config: CONFIG, results }, null, 1));
  console.log('wrote', p);
}
