// diag_explode.mjs — why does a straight-down full-power shot blow up?
import { createGame } from './sim.mjs';
const DT = 1 / 120;
const g = createGame();
g.clearObjectBalls();
g.shoot(90, 170);
const cue = g.cue();
for (let i = 0; i < 260; i++) {
  const y0 = cue.y, vy0 = cue.vy, b0 = cue.pocketed;
  g.step(1);
  if (i < 12 || i % 20 === 0 || i > 200 && i < 230) {
    console.log(`step ${i + 1} y=${y0.toFixed(4)}->${cue.y.toFixed(4)} vy=${vy0.toFixed(3)}->${cue.vy.toFixed(3)} spd=${Math.hypot(cue.vx, cue.vy).toFixed(4)} pocketed=${b0}->${cue.pocketed} histLen=${cue.history.length}`);
  }
}
console.log('final', { x: cue.x, y: cue.y, vx: cue.vx, vy: cue.vy, pocketed: cue.pocketed, hist: cue.history.length });
console.log('pocket check: distance to (292,497) =', Math.hypot(cue.x - 292, cue.y - 497));
console.log('pockets', JSON.stringify(g.consts.pockets));
