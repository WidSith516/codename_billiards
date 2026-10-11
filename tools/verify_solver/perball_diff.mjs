// perball_diff.mjs — which ball is missed, and where, when the same reported solution is
// replayed on the solver's model vs my independent kernel.
//   node tools/verify_solver/perball_diff.mjs
import fs from 'node:fs';
import { loadLevel } from '../solver/lib/level.mjs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';
import { MyGame } from './mykernel.mjs';

const locked = JSON.parse(fs.readFileSync('tools/solver/out/locked.json', 'utf8'));
const lvls = locked.levels.filter((e) => e.status === 'SOLVED');

function theirRun(e) {
  const lvl = loadLevel(e.source);
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel(lvl); g.setLevelIds(lvl.balls.map((b) => b.id));
  g.shoot(e.strike.angleDeg, e.strike.power);
  g.settle(60);
  const afterStrike = lvl.balls.filter((b) => g.ball(b.id).pocketed).map((b) => b.id);
  for (const rw of e.rewinds ?? []) { g.hold(rw.holdSteps); g.settle(60); }
  const potted = lvl.balls.filter((b) => g.ball(b.id).pocketed).map((b) => b.id);
  const missed = lvl.balls.filter((b) => !g.ball(b.id).pocketed).map((b) => ({ id: b.id, x: +g.ball(b.id).x.toFixed(2), y: +g.ball(b.id).y.toFixed(2) }));
  return { total: lvl.balls.length, afterStrike, potted, missed, lvl };
}
function myRun(e, substepPx) {
  const lvl = loadLevel(e.source);
  const g = new MyGame({ substepPx, maxSubsteps: 64 });
  g.place({ cue: lvl.cue, balls: lvl.balls, pockets: lvl.pockets, table: lvl.table });
  g.shoot(e.strike.angleDeg, e.strike.power);
  g.settle(60);
  const afterStrike = lvl.balls.filter((b) => g.balls.find((z) => z.id === b.id).pocketed).map((b) => b.id);
  for (const rw of e.rewinds ?? []) { g.hold(rw.holdSteps); g.settle(60); }
  const potted = lvl.balls.filter((b) => g.balls.find((z) => z.id === b.id).pocketed).map((b) => b.id);
  const missed = lvl.balls.filter((b) => !g.balls.find((z) => z.id === b.id).pocketed).map((b) => {
    const z = g.balls.find((q) => q.id === b.id);
    return { id: b.id, x: +z.x.toFixed(2), y: +z.y.toFixed(2) };
  });
  return { total: lvl.balls.length, afterStrike, potted, missed };
}
console.log('level | model        | strike pots | after rewind | missed balls (final pos)');
for (const e of lvls) {
  const t = theirRun(e);
  console.log(`${e.level.padEnd(6)}| theirs       | ${t.afterStrike.join(',') || '-'} | ${t.potted.join(',') || '-'} of ${t.total} | ${t.missed.map((m) => `${m.id}@(${m.x},${m.y})`).join(' ') || 'none'}`);
  for (const sp of [1, 2, 4]) {
    const m = myRun(e, sp);
    console.log(`${''.padEnd(6)}| mine ${sp}px    | ${m.afterStrike.join(',') || '-'} | ${m.potted.join(',') || '-'} of ${m.total} | ${m.missed.map((z) => `${z.id}@(${z.x},${z.y})`).join(' ') || 'none'}`);
  }
}
