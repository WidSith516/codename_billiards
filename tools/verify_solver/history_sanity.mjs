// history_sanity.mjs — is my kernel's arclen history well-formed (no duplicate samples, exact
// ~2 px spacing) and does it match the solver's sampler? Run at several substep sizes.
//   node tools/verify_solver/history_sanity.mjs
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';
import { MyGame } from './mykernel.mjs';

const ORIGIN = { x: 292, y: 280 };
const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];

function report(label, hist, arc) {
  let minGap = Infinity, maxGap = 0, dup = 0, total = 0;
  for (let i = 1; i < hist.length; i++) {
    const g = Math.hypot(hist[i].x - hist[i - 1].x, hist[i].y - hist[i - 1].y);
    total += g;
    if (g < minGap) minGap = g;
    if (g > maxGap) maxGap = g;
    if (g < 1e-9) dup++;
  }
  console.log(`  ${label.padEnd(22)} samples=${String(hist.length).padStart(4)} arc=${total.toFixed(1)} px  gap min=${minGap.toFixed(4)} max=${maxGap.toFixed(4)}  duplicates=${dup}`);
}

{
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: -1000, y: -1000 }], pockets: POCKETS });
  g.setLevelIds([1]); g.ball(1).pocketed = true;
  g.shoot(0, 170); g.settle(60);
  report('solver (4 px substep)', g.cue.history);
}
for (const sp of [1, 2, 4]) {
  const g = new MyGame({ substepPx: sp, maxSubsteps: 64 });
  g.place({ cue: ORIGIN, balls: [{ id: 1, x: -1000, y: -1000 }], pockets: POCKETS });
  g.shoot(0, 170); g.settle(60);
  report(`mine (${sp} px substep)`, g.cue.history);
}
// pathological case that would have tripped the old sampler: a huge single substep
{
  const g = new MyGame({ substepPx: 20, maxSubsteps: 1 });
  g.place({ cue: ORIGIN, balls: [{ id: 1, x: -1000, y: -1000 }], pockets: POCKETS });
  g.shoot(0, 170);
  for (let i = 0; i < 3; i++) g.stepOnce();
  report('mine (20 px substep)', g.cue.history);
}
