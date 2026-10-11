// my_random.mjs — independent re-run of the "random layouts are unsolvable" experiment.
//
// Three arms, all on the solver's own search engine (tools/solver/lib/search.mjs, read-only)
// so that ONLY the budget and the sampling distribution differ:
//   R1  their budget  : the exact LEVEL_BUDGET used by dev/random_layouts.mjs
//   R2  hand budget   : the budget the hand-designed levels got (1 deg grid, 0.25 refine,
//                       more rewinds/beam) with their sampling
//   R3  hand budget + UNCONSTRAINED sampling: no "34 px from the cue" and no "44 px from a
//                       pocket" exclusion -- i.e. including exactly the region the designed
//                       levels put their balls in.
//
//   node tools/verify_solver/my_random.mjs --n 100 --seed 900001
//
// Writes tools/verify_solver/11_myrand_<arm>.json and prints a summary.
import fs from 'node:fs';
import { solveLevel, replaySolution } from '../solver/lib/search.mjs';

const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf('--' + n); return i < 0 ? d : argv[i + 1]; };
const N = Number(arg('n', 100));
const SEED0 = Number(arg('seed', 900001));
const BALLS = Number(arg('balls', 3));
const ORIGIN = { x: 292, y: 280 };
const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
const TABLE = { left: 65, right: 975, top: 63, bottom: 497 };
const R = 12.2, R2 = 24.4;

let s0 = 0;
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
// R3: only the physical constraint (no overlap of centres), no design-shaped exclusions
const okPlacementFree = (b, placed) => inside(b)
  && !placed.some((p) => Math.hypot(p.x - b.x, p.y - b.y) < R2 + 4);

function sampleBalls(k, free) {
  const out = [];
  let guard = 0;
  const ok = free ? okPlacementFree : okPlacement;
  while (out.length < k && guard++ < 6000) {
    const b = { x: TABLE.left + R + 6 + rnd() * (TABLE.right - TABLE.left - 2 * R - 12),
      y: TABLE.top + R + 6 + rnd() * (TABLE.bottom - TABLE.top - 2 * R - 12) };
    if (ok(b, out)) out.push(b);
  }
  return out;
}

const BUDGETS = {
  their: { angleStep: 2, powers: [130, 170], releaseFractions: [0.3, 0.6, 1.0], maxRewinds: 3, beam: 5, topStrikes: 6, refineStep: 0.5 },
  hand: { angleStep: 1, powers: [130, 170], releaseFractions: [0.3, 0.6, 1.0], maxRewinds: 4, beam: 6, topStrikes: 6, refineStep: 0.25 },
  hand8: { angleStep: 1, powers: [110, 140, 170], releaseFractions: [0.15, 0.3, 0.5, 0.7, 0.85, 1.0], maxRewinds: 5, beam: 8, topStrikes: 8, refineStep: 0.25 },
};

const ARMS = [
  { name: 'R1_theirBudget', budget: BUDGETS.their, seed: SEED0 + 1, free: false },
  { name: 'R2_handBudget', budget: BUDGETS.hand, seed: SEED0 + 2, free: false },
  { name: 'R3_handBudget_free', budget: BUDGETS.hand, seed: SEED0 + 3, free: true },
  { name: 'R4_hand8_free', budget: BUDGETS.hand8, seed: SEED0 + 4, free: true },
];

const summary = [];
for (const arm of ARMS) {
  s0 = arm.seed >>> 0;
  const rows = [];
  let solved = 0, tried = 0;
  const t0 = Date.now();
  for (let i = 0; i < N; i++) {
    const balls = sampleBalls(BALLS, arm.free);
    if (balls.length < BALLS) continue;
    tried++;
    const lvl = {
      id: `${arm.name}_${i}`, name: arm.name, cue: ORIGIN,
      balls: balls.map((b, k) => ({ id: k + 1, x: +b.x.toFixed(2), y: +b.y.toFixed(2), type: 'plain' })),
      pockets: POCKETS, table: TABLE, budget: arm.budget,
    };
    let res;
    try { res = solveLevel(lvl, { cfgName: 'locked' }); } catch (e) { res = { status: 'ERROR', error: String(e) }; }
    let replayed = false;
    if (res.status === 'SOLVED') {
      const rp = replaySolution(lvl, res, 'locked');
      replayed = rp.ok;
      if (!rp.ok) res.status = 'UNSOLVED(unreplayable)'; else solved++;
    }
    rows.push({ i, balls: lvl.balls.map((b) => ({ x: b.x, y: b.y })), status: res.status, replayed,
      bestNoRewind: res.bestNoRewind, bestAny: res.bestAny, sims: res.stats?.sims, ms: res.stats?.ms,
      strike: res.strike, rewinds: res.rewinds?.length ?? 0 });
    if ((i + 1) % 10 === 0) console.log(`  ${arm.name}: ${i + 1}/${N} done, solved=${solved}, ${((Date.now() - t0) / 1000).toFixed(0)}s`);
  }
  const secs = ((Date.now() - t0) / 1000).toFixed(0);
  const medSims = rows.map((r) => r.sims ?? 0).sort((a, b) => a - b)[Math.floor(rows.length / 2)];
  const hist = {};
  for (const r of rows) { const k = r.status === 'SOLVED' ? 3 : (r.bestAny ?? 0); hist[k] = (hist[k] ?? 0) + 1; }
  summary.push({ arm: arm.name, n: tried, solved, rate: tried ? solved / tried : 0, secs: +secs, medianSims: medSims, hist });
  console.log(`${arm.name}: ${solved}/${tried} SOLVED (${(100 * solved / (tried || 1)).toFixed(1)}%) in ${secs}s, median sims ${medSims}`);
  fs.writeFileSync(`tools/verify_solver/11_myrand_${arm.name}.json`, JSON.stringify({ arm: arm.name, budget: arm.budget, free: arm.free, seed: arm.seed, balls: BALLS, n: tried, solved, rows }, null, 1));
}
fs.writeFileSync('tools/verify_solver/11_myrand_summary.json', JSON.stringify(summary, null, 1));
console.log('\n' + JSON.stringify(summary, null, 1));
console.log('wrote tools/verify_solver/11_myrand_summary.json');
