// run_balance.mjs -- attainability / balance Monte-Carlo for the REWIND prototype.
//
// Answers: with ONE legal strike, how many of the 10 object balls can be pocketed, and does
// the rewind verb make clearing the table plausible? Sweeps strike angle x power, then applies
// three rewind policies of increasing strength, on BOTH the shipped rack and a tight rack.
//
// Policies (each states exactly what produced its number):
//   G0  no rewind at all (the single strike only)
//   G1  fixed-hold rewind: press R, hold 45 physics steps (~0.375 s), release, settle; repeat
//   G2  lookahead-5: try holds {15,30,60,120,240} from a saved state, keep the one that pockets
//       the most object balls immediately (tie-break: fastest cue ball afterwards)
//   G3  grind = G2 with a 80-iteration budget on the best breaks, to test convergence to 10/10
//
// The player's ONLY per-rewind decision is where to release (the cursor index), so G2's sweep
// over release points is close to the human strategy space (it lacks multi-step planning).
//
// Run:  node tools/h5harness/run_balance.mjs [--quick]
// Writes balance.json next to this file. ASCII-only on purpose.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createGame, sourceFingerprint, summarize, round } from './sim.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DT = 1 / 120;
const QUICK = process.argv.includes('--quick');
const MAX_POWER = 170, SPEED_PER_POWER = 7.4;
const POWER_LIMIT = 1e5; // anything above this is the rail-boost blow-up, not a playable state

const RACK_BAND_ANGLES = [];
for (let a = -10; a <= 10; a += QUICK ? 2 : 1) RACK_BAND_ANGLES.push(a);
const ALL_ROUND_ANGLES = [];
for (let a = 0; a < 360; a += QUICK ? 10 : 5) ALL_ROUND_ANGLES.push(a);
const POWERS = QUICK ? [100, 170] : [80, 125, 170];

function playable(g) {
  for (const b of g.balls) {
    const s = Math.hypot(b.vx, b.vy);
    if (!Number.isFinite(b.x) || !Number.isFinite(b.y) || !Number.isFinite(s)) return false;
    if (s > POWER_LIMIT) return false;
  }
  return true;
}

function stats(g) {
  return {
    pocketed: g.pocketedCount(),
    cueSpeed: round(Math.hypot(g.cue().vx, g.cue().vy), 2),
    historyLen: g.cue().history.length,
    rewinds: g.state.stats.rewinds
  };
}

// One iteration of a rewind policy: hold R for `hold` steps, release, settle.
function rewindOnce(g, hold, settleCap) {
  g.pressR();
  if (!g.state.rewinding) return { attempted: false };
  let n = 0;
  while (g.state.rewinding && n < hold) { g.step(1); n++; }
  g.releaseR();
  const st = g.settle({ maxSeconds: settleCap });
  return { attempted: true, rewoundSteps: n, settleSeconds: st.seconds, autoStopped: !g.state.rewinding && n < hold };
}

function runBreak({ tight, angle, power, settleCap = 25 }) {
  const g = createGame();
  if (tight) g.tightenRack();
  const launch = g.shoot(angle, power);
  const st = g.settle({ maxSeconds: settleCap });
  const cue = g.cue();
  const obs = {
    angle, power, tight, launchSpeed: round(launch.speed, 2),
    pocketedAfterOneStrike: g.pocketedCount(),
    restSeconds: round(st.seconds, 3),
    maxObjectSpeed: round(g.metrics.maxObjectSpeed, 2),
    maxCueSpeed: Number.isFinite(g.metrics.maxSpeed) ? round(g.metrics.maxSpeed, 2) : 'Infinity',
    diverged: !playable(g),
    contactableBalls: g.contactableCount(Math.max(1, Math.floor(cue.history.length / 400)))
  };
  return { g, obs };
}

function policyG1(g, { maxIters = 30, hold = 45, stallLimit = 8, settleCap = 15 } = {}) {
  let best = g.pocketedCount(), stall = 0, iters = 0, diverged = false;
  const trace = [best];
  while (iters < maxIters && g.pocketedCount() < 10) {
    iters++;
    const r = rewindOnce(g, hold, settleCap);
    if (!r.attempted) break;
    if (!playable(g)) { diverged = true; break; }
    if (g.pocketedCount() > best) { best = g.pocketedCount(); stall = 0; } else stall++;
    trace.push(g.pocketedCount());
    if (stall >= stallLimit) break;
  }
  return { policy: 'G1_fixed_hold_' + hold, iters, pocketed: g.pocketedCount(), best, diverged, trace };
}

function policyG2(g, { maxIters = 25, holds = [15, 30, 60, 120, 240], stallLimit = 6, settleCap = 15 } = {}) {
  let best = g.pocketedCount(), stall = 0, iters = 0, diverged = false;
  const trace = [best];
  while (iters < maxIters && g.pocketedCount() < 10) {
    iters++;
    const base = g.save();
    let bestCand = null;
    for (const h of holds) {
      g.restore(base);
      const r = rewindOnce(g, h, settleCap);
      if (!r.attempted) continue;
      if (!playable(g)) continue;
      const p = g.pocketedCount();
      const s = Math.hypot(g.cue().vx, g.cue().vy);
      const score = p * 1e6 + Math.min(500, s) * 1e3 + g.cue().history.length * 1e-3;
      if (!bestCand || score > bestCand.score) bestCand = { score, snap: g.save(), pocketed: p, hold: h };
    }
    if (!bestCand) { g.restore(base); diverged = true; break; }
    g.restore(bestCand.snap);
    if (g.pocketedCount() > best) { best = g.pocketedCount(); stall = 0; } else stall++;
    trace.push(g.pocketedCount());
    if (stall >= stallLimit) break;
  }
  return { policy: 'G2_lookahead_holds_' + holds.join('_'), iters, pocketed: g.pocketedCount(), best, diverged, trace };
}

function policyG3(g, opts) {
  const r = policyG2(g, Object.assign({ maxIters: 80, holds: [15, 30, 45, 60, 90, 120, 180, 240, 360], stallLimit: 25, settleCap: 15 }, opts));
  r.policy = 'G3_grind_lookahead9';
  return r;
}

function summarizePockets(list) {
  const s = list.slice().sort((a, b) => a - b);
  return {
    n: s.length, best: s[s.length - 1], p90: s[Math.floor(0.9 * (s.length - 1))],
    median: s[Math.floor(0.5 * (s.length - 1))], mean: round(s.reduce((a, b) => a + b, 0) / s.length, 3),
    zeroPocketBreaks: s.filter((x) => x === 0).length,
    clearedAll10: s.filter((x) => x === 10).length,
    atLeast1: s.filter((x) => x >= 1).length, atLeast3: s.filter((x) => x >= 3).length
  };
}

function phaseA(tight) {
  const rows = [];
  for (const [angles, tag] of [[RACK_BAND_ANGLES, 'rack_band'], [ALL_ROUND_ANGLES, 'all_round']]) {
    for (const angle of angles) {
      for (const power of POWERS) {
        const { g, obs } = runBreak({ tight, angle, power });
        rows.push(Object.assign({ sweep: tag }, obs));
        void g;
      }
    }
  }
  return rows;
}

const t0 = process.hrtime.bigint();
console.log('REWIND balance sweep');
const fp = sourceFingerprint();
console.log('prototype script sha256:', fp.scriptBodySha256);
console.log('quick mode:', QUICK, '| rack band angles:', RACK_BAND_ANGLES.length, '| all-round angles:', ALL_ROUND_ANGLES.length, '| powers:', JSON.stringify(POWERS));
console.log('');

const out = { generatedAt: new Date().toISOString(), fingerprint: fp, quick: QUICK, config: { RACK_BAND_ANGLES, ALL_ROUND_ANGLES, POWERS } };

for (const tight of [false, true]) {
  const label = tight ? 'tight' : 'shipped';
  console.log('=== rack geometry:', label, '===');
  const A = phaseA(tight);
  const firstStrike = A.map((r) => r.pocketedAfterOneStrike);
  console.log('phase A (one strike, ' + A.length + ' shots):', JSON.stringify(summarizePockets(firstStrike)));
  console.log('  diverged shots:', A.filter((r) => r.diverged).length,
    '| max cue speed seen:', Math.max(...A.map((r) => (r.maxCueSpeed === 'Infinity' ? 1e9 : r.maxCueSpeed))),
    '| median rest s:', round(summarize(A.map((r) => r.restSeconds)).median, 2),
    '| median contactable balls after break:', round(summarize(A.map((r) => r.contactableBalls)).median, 2));
  out['phaseA_' + label] = { summary: summarizePockets(firstStrike), rows: A };

  // policies on a ranked subset (best first): top by first-strike pockets, then by contactable balls
  const ranked = A.slice().sort((a, b) =>
    (b.pocketedAfterOneStrike - a.pocketedAfterOneStrike) || (b.contactableBalls - a.contactableBalls));
  const g1Rows = [], g2Rows = [], g3Rows = [];
  const g1Take = QUICK ? 40 : 120;
  const g2Take = QUICK ? 20 : 48;
  const g3Take = QUICK ? 6 : 12;
  for (const row of ranked.slice(0, Math.max(g1Take, g2Take, g3Take))) {
    const idx = ranked.indexOf(row);
    if (idx < g1Take) {
      const { g, obs } = runBreak({ tight, angle: row.angle, power: row.power });
      const r = policyG1(g);
      g1Rows.push(Object.assign({ angle: row.angle, power: row.power, startPocketed: obs.pocketedAfterOneStrike }, r));
    }
    if (idx < g2Take) {
      const { g, obs } = runBreak({ tight, angle: row.angle, power: row.power });
      const r = policyG2(g);
      g2Rows.push(Object.assign({ angle: row.angle, power: row.power, startPocketed: obs.pocketedAfterOneStrike }, r));
    }
    if (idx < g3Take) {
      const { g, obs } = runBreak({ tight, angle: row.angle, power: row.power });
      const r = policyG3(g);
      g3Rows.push(Object.assign({ angle: row.angle, power: row.power, startPocketed: obs.pocketedAfterOneStrike }, r));
    }
    console.log('  break a=' + row.angle + ' p=' + row.power + ' first=' + row.pocketedAfterOneStrike +
      ' contactable=' + row.contactableBalls);
  }
  const g1P = g1Rows.map((r) => r.pocketed), g2P = g2Rows.map((r) => r.pocketed), g3P = g3Rows.map((r) => r.pocketed);
  console.log('  G1 fixed-hold      :', JSON.stringify(summarizePockets(g1P)), '| diverged runs:', g1Rows.filter((r) => r.diverged).length);
  console.log('  G2 lookahead-5     :', JSON.stringify(summarizePockets(g2P)), '| diverged runs:', g2Rows.filter((r) => r.diverged).length);
  console.log('  G3 grind lookahead9:', JSON.stringify(summarizePockets(g3P)), '| diverged runs:', g3Rows.filter((r) => r.diverged).length);
  out['policies_' + label] = {
    G1: { summary: summarizePockets(g1P), rows: g1Rows },
    G2: { summary: summarizePockets(g2P), rows: g2Rows },
    G3: { summary: summarizePockets(g3P), rows: g3Rows }
  };
  // head-to-head on the SAME breaks so the geometries are comparable
  const head = [];
  for (let i = 0; i < g2Rows.length; i++) {
    const key = g2Rows[i];
    head.push({ angle: key.angle, power: key.power, tightPocketed: key.pocketed });
  }
  out['head_' + label] = head;
  console.log('');
}

// head-to-head table: same (angle, power) pairs on both geometries, G2 policy
const shipped = {}, tightMap = {};
for (const r of out.policies_shipped.G2.rows) shipped[r.angle + ':' + r.power] = r.pocketed;
for (const r of out.policies_tight.G2.rows) tightMap[r.angle + ':' + r.power] = r.pocketed;
const pairs = Object.keys(shipped).filter((k) => k in tightMap);
const headToHead = pairs.map((k) => ({ key: k, shipped: shipped[k], tight: tightMap[k], delta: tightMap[k] - shipped[k] }));
out.headToHeadG2 = {
  pairs: headToHead,
  shippedBest: Math.max(...headToHead.map((h) => h.shipped)),
  tightBest: Math.max(...headToHead.map((h) => h.tight)),
  shippedMean: round(headToHead.reduce((a, b) => a + b.shipped, 0) / headToHead.length, 3),
  tightMean: round(headToHead.reduce((a, b) => a + b.tight, 0) / headToHead.length, 3),
  tighterWins: headToHead.filter((h) => h.delta > 0).length,
  shippedWins: headToHead.filter((h) => h.delta < 0).length,
  draws: headToHead.filter((h) => h.delta === 0).length
};
console.log('HEAD TO HEAD (G2, identical shots, shipped vs tight rack):');
console.log('  shipped best=' + out.headToHeadG2.shippedBest + ' mean=' + out.headToHeadG2.shippedMean +
  ' | tight best=' + out.headToHeadG2.tightBest + ' mean=' + out.headToHeadG2.tightMean +
  ' | tight wins=' + out.headToHeadG2.tighterWins + ' shipped wins=' + out.headToHeadG2.shippedWins + ' draws=' + out.headToHeadG2.draws);

out.wallMs = Number(process.hrtime.bigint() - t0) / 1e6;
fs.writeFileSync(path.join(HERE, 'balance.json'), JSON.stringify(out, null, 2));
console.log('wrote balance.json (' + round(out.wallMs / 1000, 1) + ' s)');
