// Does REWIND make the game completable? The break sweep established that one strike
// alone tops out at 2/10 balls. The open question is whether the rewind lever can carry
// the remaining 8.
//
// Policy space explored (all through the public Game API, no game logic reimplemented):
//   1. STRIKE_ONLY      : one strike, settle. (lower bound)
//   2. STRIKE_REWIND    : strike (partial power) -> hold R for k steps -> release -> settle.
//   3. REPEATED_REWIND  : strike -> then loop { rewind k steps, release, settle } n times.
//                         This is the "player keeps reusing the history" strategy and is
//                         the most generous reading of the core mechanic.
//
// A speed cap is NOT applied here, so these numbers are flattering to the prototype:
// whatever they show is an UPPER bound, not a realistic one.
//
// Usage: node rewind_policy.mjs
import { Game } from './sim.mjs';

const SETTLE_S = 20;

function settle(g, seconds = SETTLE_S) {
  g.settle({ maxSeconds: seconds, stopOnWin: true });
}

function runPolicy({ angle, power, rewinds, rewindSteps }) {
  const g = new Game({ seed: 0x5eed });
  g.reset();
  g.shoot(angle, power);
  if (rewinds > 0) {
    settle(g, 3);                       // let the strike develop before rewinding
    for (let i = 0; i < rewinds; i++) {
      if (g.history().length < 2) break;
      g.pressR();
      g.step(rewindSteps);
      g.releaseR();
      settle(g, 12);
    }
  } else {
    settle(g);
  }
  const m = g.metrics;
  return {
    pocketed: g.pocketedCount(),
    nan: m.nan,
    maxSpeed: Number.isFinite(m.maxSpeed) ? Math.round(m.maxSpeed) : 'Infinity',
    rest: g.atRest(),
    cueX: Math.round(g.cue().x), cueY: Math.round(g.cue().y),
    cuePocketed: g.cue().pocketed,
    history: g.history().length
  };
}

console.log('=== 1. STRIKE_ONLY (reference: known best 2/10) ===');
let best1 = 0;
for (let a = 0; a < 360; a += 6) {
  const r = runPolicy({ angle: a, power: 170, rewinds: 0, rewindSteps: 0 });
  if (r.pocketed > best1) best1 = r.pocketed;
}
console.log(`  best over 60 angles = ${best1}/10`);

console.log('');
console.log('=== 2. STRIKE + ONE REWIND, sweeping rewind depth (angle x power x depth) ===');
let best2 = { pocketed: -1 };
const ANG = [206, 218, 180, 190, 160, 240, 0, 30, 90];
const POW = [40, 80, 120, 170];
const DEPTH = [60, 120, 240, 480, 960];
let n2 = 0;
for (const angle of ANG) {
  for (const power of POW) {
    for (const rewindSteps of DEPTH) {
      const r = runPolicy({ angle, power, rewinds: 1, rewindSteps });
      n2++;
      if (r.pocketed > best2.pocketed) best2 = { ...r, angle, power, rewindSteps };
    }
  }
}
console.log(`  scanned ${n2} combinations`);
console.log(`  best = ${best2.pocketed}/10  at angle=${best2.angle} power=${best2.power} rewindDepth=${best2.rewindSteps} steps`
  + `  (cuePocketed=${best2.cuePocketed ? 'YES' : 'no'}, maxSpeed=${best2.maxSpeed}, nan=${best2.nan})`);

console.log('');
console.log('=== 3. REPEATED REWIND (the generous "keep reusing history" strategy) ===');
let best3 = { pocketed: -1 };
const NREW = [2, 4, 8];
let n3 = 0;
for (const angle of ANG) {
  for (const power of POW) {
    for (const rewindSteps of DEPTH) {
      for (const rewinds of NREW) {
        const r = runPolicy({ angle, power, rewinds, rewindSteps });
        n3++;
        if (r.pocketed > best3.pocketed) best3 = { ...r, angle, power, rewinds, rewindSteps };
      }
    }
  }
}
console.log(`  scanned ${n3} combinations`);
console.log(`  best = ${best3.pocketed}/10  at angle=${best3.angle} power=${best3.power}`
  + ` rewinds=${best3.rewinds} x ${best3.rewindSteps} steps`
  + `  (cuePocketed=${best3.cuePocketed ? 'YES' : 'no'}, maxSpeed=${best3.maxSpeed}, nan=${best3.nan})`);

console.log('');
console.log('=== 4. OMITTED: "rewind-friendly geometry" test (recorded as a failed attempt) ===');
// Two attempts at a hand-authored "balls on the cue history path, aimed at a pocket"
// level both returned 0/10, and BOTH FAILURES WERE THE TEST'S FAULT, not the game's:
//
//   Attempt 1: 10 balls in a perfect row at y=280. y=280 is mid-table and the only
//     pockets are at the corners and mid-rails (y=63 and y=497), so no pocket existed
//     along that line at all; and a collinear row is a degenerate impact that barely
//     scatters (measured: ball 1 moved 15 px).
//
//   Attempt 2: balls moved to y=130 to aim at the top-left corner. Instrumenting the
//     minimum ball-to-pocket distance showed the closest any ball ever came was 67.03 px
//     against a capture radius of 24 px - the row simply never went near a pocket. Worse,
//     at y=130 the balls did not even touch the cue ball's line at y=280, so the shot was
//     a total miss from the start.
//
// Conclusion: constructing a solvable hand-authored level by hand-editing coordinates is
// exactly the error-prone work that the design doc's "solvability solver" (section 4.2 /
// milestone M4) exists to automate. Doing it ad hoc here would produce a misleading
// number, so this test is deliberately not reported as a result.
// The honest upper bound for "rewind helps" comes from test 5 below.

console.log('');
console.log('=== 5. CAPPED rewind scan (removes the divergence bug from the measurement) ===');
const CAP = 2000;
function runCapped({ angle, power, rewinds, rewindSteps }) {
  const g = new Game({ seed: 0x5eed });
  g.reset();
  g.shoot(angle, power);
  const settleCapped = (seconds) => {
    const dt = g.consts.STEP; const n = Math.round(seconds / dt);
    for (let i = 0; i < n; i++) {
      g.step(1);
      for (const b of g.balls) {
        const s = Math.hypot(b.vx, b.vy);
        if (Number.isFinite(s) && s > CAP) { const k = CAP / s; b.vx *= k; b.vy *= k; }
      }
      if (g.state.won) break;
    }
  };
  if (rewinds > 0) {
    settleCapped(3);
    for (let i = 0; i < rewinds; i++) {
      if (g.history().length < 2) break;
      g.pressR(); g.step(rewindSteps); g.releaseR();
      settleCapped(12);
    }
  } else settleCapped(20);
  return { pocketed: g.pocketedCount(), nan: g.metrics.nan };
}
let bestCapped = { pocketed: -1 };
for (const angle of [206, 56, 218, 160, 0, 90]) {
  for (const power of [80, 170]) {
    for (const rewindSteps of [120, 240, 480]) {
      for (const rewinds of [0, 1, 3]) {
        const r = runCapped({ angle, power, rewinds, rewindSteps });
        if (r.pocketed > bestCapped.pocketed) bestCapped = { ...r, angle, power, rewinds, rewindSteps };
      }
    }
  }
}
console.log(`  best CAPPED rewind result = ${bestCapped.pocketed}/10`
  + `  at angle=${bestCapped.angle} power=${bestCapped.power} rewinds=${bestCapped.rewinds}x${bestCapped.rewindSteps}`);

console.log('');
console.log('SUMMARY');
console.log(`  strike only (uncapped sweep) : ${best1}/10   [findings: honest capped value is 2/10]`);
console.log(`  strike + 1 rewind (uncapped) : ${best2.pocketed}/10  (angle ${best2.angle}, maxSpeed ${best2.maxSpeed} -> bugged)`);
console.log(`  strike + N rewinds (uncapped): ${best3.pocketed}/10  (maxSpeed ${best3.maxSpeed} -> bugged)`);
console.log(`  best with cap ${CAP} + rewind  : ${bestCapped.pocketed}/10`);
console.log('  UNCAPPED numbers are inflated by the rail-boost divergence bug; only capped');
console.log('  numbers are usable for balance decisions.');
