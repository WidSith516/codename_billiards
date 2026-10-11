// diag_rewind_hold.mjs — why do L5/L6 report a rewind of only 4-5 steps?
// Uses the solver's OWN locked model (read-only import) but drives it with our own loop.
// Re-runnable:  node tools/verify_solver/diag_rewind_hold.mjs
import { loadLevel } from '../solver/lib/level.mjs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';

const SOL = {
  L1: { a: 0, p: 170, hold: 650 },
  L2: { a: 0, p: 170, hold: 60 },
  L3: { a: 15, p: 170, hold: 493 },
  L4: { a: 12, p: 170, hold: 88 },
  L5: { a: 349, p: 170, hold: 5 },
  L6: { a: 11, p: 170, hold: 4 },
};

for (const id of ['L1', 'L2', 'L3', 'L4', 'L5', 'L6']) {
  const lvl = loadLevel(`tools/solver/levels/${id}.json`);
  const s = SOL[id];
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel(lvl);
  g.setLevelIds(lvl.balls.map((b) => b.id));
  const cue = g.cue;
  g.shoot(s.a, s.p);
  const trace = [];
  let steps = 0;
  while (steps < 60 / CFG_LOCKED.step) {
    g.stepOnce(); steps++;
    trace.push({ n: steps, h: cue.history.length, x: +cue.x.toFixed(1), y: +cue.y.toFixed(1), s: +Math.hypot(cue.vx, cue.vy).toFixed(1), pk: cue.pocketed });
    if (g.g.state.won) break;
    if (g.atRest()) break;
  }
  const pottedBefore = g.pocketedLevel();
  const hLen = cue.history.length;
  const restPos = { x: +cue.x.toFixed(2), y: +cue.y.toFixed(2) };
  // now the reported rewind
  const r = g.hold(s.hold);
  while (hLen >= 0) { g.stepOnce(); steps++; if (g.g.state.won) break; if (g.atRest()) break; }
  console.log(`${id}: strike ${s.a}deg/${s.p}`);
  console.log(`   after settle: steps=${steps} potted=${pottedBefore}/${lvl.balls.length} won=${g.g.state.won} cueHistoryLen=${hLen} cueAtRest=(${restPos.x},${restPos.y}) cuePocketed=${trace.some((t) => t.pk)}`);
  console.log(`   cue travelled px (sum |dx|) = ${trace.reduce((acc, t, i) => acc + (i ? Math.hypot(t.x - trace[i - 1].x, t.y - trace[i - 1].y) : 0), 0).toFixed(1)}`);
  console.log(`   hold(${s.hold}) -> cursor=${r.cursor} trackLen=${r.track} rewoundToOrigin=${r.rewoundToOrigin} releaseSpeed=${Math.hypot(r.release.vx, r.release.vy).toFixed(2)} releaseAt=(${r.release.x.toFixed(1)},${r.release.y.toFixed(1)})`);
  console.log(`   after rewind: potted=${g.pocketedLevel()}/${lvl.balls.length} won=${g.g.state.won}`);
  // where did the strike actually go? first/last few trace points
  const first = trace.slice(0, 3).map((t) => `(${t.x},${t.y})@${t.s}`).join(' ');
  const last = trace.slice(-3).map((t) => `(${t.x},${t.y})@${t.s}`).join(' ');
  console.log(`   trace head: ${first}`);
  console.log(`   trace tail: ${last}`);
  console.log('');
}
