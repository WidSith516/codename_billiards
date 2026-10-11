// compare_models.mjs — step-by-step A/B of tools/solver/lib/locked.mjs (theirs) against
// tools/verify_solver/mykernel.mjs (mine) on the same level + strike, to locate the first
// state divergence. This is due diligence: if my kernel is simply wrong, every "FAILED"
// number in replay_solutions.mjs is my bug, not the solver's.
//
//   node tools/verify_solver/compare_models.mjs [level] [angle] [power] [substepPx]
import fs from 'node:fs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';
import { loadLevel } from '../solver/lib/level.mjs';
import { MyGame } from './mykernel.mjs';

const lvlId = process.argv[2] ?? 'L5';
const angle = Number(process.argv[3] ?? 349);
const power = Number(process.argv[4] ?? 170);
const substepPx = Number(process.argv[5] ?? 2);

const lvl = loadLevel(`tools/solver/levels/${lvlId}.json`);
const theirs = new LockedGame(CFG_LOCKED);
theirs.placeLevel(lvl);
theirs.setLevelIds(lvl.balls.map((b) => b.id));
const mine = new MyGame({ substepPx, maxSubsteps: 64 });
mine.place({ cue: lvl.cue, balls: lvl.balls, pockets: lvl.pockets, table: lvl.table });

const st = theirs.shoot(angle, power);
const sm = mine.shoot(angle, power);
console.log(`${lvlId} strike ${angle}deg/${power}: theirs v=(${st.vx.toFixed(6)},${st.vy.toFixed(6)})  mine v=(${sm.vx.toFixed(6)},${sm.vy.toFixed(6)})  identical=${st.vx === sm.vx && st.vy === sm.vy}`);

let first = -1;
const N = 400;
for (let n = 1; n <= N; n++) {
  theirs.stepOnce();
  mine.stepOnce();
  let worst = 0, which = '';
  for (const a of theirs.balls) {
    const b = mine.balls.find((z) => z.id === a.id);
    if (!b) continue;                       // ids the level does not use: pocketed sentinels
    if (a.pocketed && b.pocketed) continue;  // both out of play: no meaningful comparison
    for (const k of ['x', 'y', 'vx', 'vy']) {
      const d = Math.abs(a[k] - b[k]);
      if (Number.isFinite(d) && d > worst) { worst = d; which = `ball${a.id}.${k} theirs=${a[k]} mine=${b[k]}`; }
    }
    if (a.pocketed !== b.pocketed) { worst = Math.max(worst, 1e9); which = `ball${a.id}.pocketed theirs=${a.pocketed} mine=${b.pocketed}`; }
  }
  if (worst > 1e-9 && first < 0) {
    first = n;
    console.log(`FIRST DIVERGENCE at step ${n}: ${which}  (worst |delta| = ${worst})`);
    console.log(`  theirs cue: (${theirs.cue.x.toFixed(4)},${theirs.cue.y.toFixed(4)}) v=(${theirs.cue.vx.toFixed(4)},${theirs.cue.vy.toFixed(4)}) hist=${theirs.cue.history.length} pocketed=${theirs.cue.pocketed}`);
    console.log(`  mine   cue: (${mine.cue.x.toFixed(4)},${mine.cue.y.toFixed(4)}) v=(${mine.cue.vx.toFixed(4)},${mine.cue.vy.toFixed(4)}) hist=${mine.cue.history.length} pocketed=${mine.cue.pocketed}`);
    for (const b of lvl.balls) {
      const A = theirs.ball(b.id), B = mine.balls.find((z) => z.id === b.id);
      console.log(`  ball${b.id}: theirs (${A.x.toFixed(3)},${A.y.toFixed(3)}) v=(${A.vx.toFixed(3)},${A.vy.toFixed(3)}) pk=${A.pocketed} | mine (${B.x.toFixed(3)},${B.y.toFixed(3)}) v=(${B.vx.toFixed(3)},${B.vy.toFixed(3)}) pk=${B.pocketed}`);
    }
  }
  if (n <= 4) {
    console.log(`  step ${n}: theirs cue (${theirs.cue.x.toFixed(3)},${theirs.cue.y.toFixed(3)}) v=${Math.hypot(theirs.cue.vx, theirs.cue.vy).toFixed(3)} hist=${theirs.cue.history.length} | mine (${mine.cue.x.toFixed(3)},${mine.cue.y.toFixed(3)}) v=${Math.hypot(mine.cue.vx, mine.cue.vy).toFixed(3)} hist=${mine.cue.history.length}`);
  }
  if (first > 0 && n > first + 2) break;
}
if (first < 0) console.log(`no divergence > 1e-9 in ${N} steps`);
console.log(`\nafter ${theirs.steps} steps: theirs potted=${theirs.pocketedLevel()}  mine potted=${mine.pocketedCount()}`);
