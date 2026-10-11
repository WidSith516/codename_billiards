// k_probe.mjs — measure the rewind impulse coefficient K independently.
//
// Geometry: L1's backstop ball 1 (23.5 px from history sample #1 of a 0-deg full-power
// strike). The strike never touches it; a full rewind passes sample #1 at ~V_BREAK and must
// kick it. We step one frame at a time, catch the frame where the ball first moves, and
// compare the delivered speed with j = max(0, (v_cue - v_target).n) computed from the
// snapshot sample the cue is on. Run on locked + prototype + my kernel.
//
//   node tools/verify_solver/k_probe.mjs
import fs from 'node:fs';
import { LockedGame, CFG_LOCKED, CFG_PROTOTYPE } from '../solver/lib/locked.mjs';
import { MyGame } from './mykernel.mjs';

const ORIGIN = { x: 292, y: 280 };
const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
const out = [];
const say = (s) => { out.push(s); console.log(s); };

function probe(label, make, isMine) {
  const g = make();
  g.shoot(0, 170);
  g.settle(30);
  const rest = { x: g.cue.x, y: g.cue.y };
  const hist = g.cue.history.slice();
  g.startRewind();
  let track = null;
  if (isMine) track = g.snapshot.slice();
  else track = g.cue.rewindTrack ? g.cue.rewindTrack.slice() : null;
  const ball0 = isMine ? g.balls.find((b) => b.id === 1) : g.ball(1);
  let firstMove = null;
  for (let i = 0; i < 800; i++) {
    const before = Math.hypot(ball0.vx, ball0.vy);
    g.stepOnce();
    const after = Math.hypot(ball0.vx, ball0.vy);
    if (before === 0 && after > 0 && !firstMove) {
      // the cue is on snapshot index (track.length-1 - i - 1) after this step
      const idx = track.length - 1 - (i + 1);
      const s = track[Math.max(0, idx)];
      const dx = ball0.x - s.x, dy = ball0.y - s.y;
      const d = Math.hypot(dx, dy);
      const vcx = -s.vx, vcy = -s.vy;
      const rel = ((vcx - 0) * dx + (vcy - 0) * dy) / (d || 1);
      firstMove = { i, idx, d, rel, delivered: after, sampleV: Math.hypot(s.vx, s.vy), cueV: Math.hypot(vcx, vcy), sample: s };
    }
  }
  if (isMine) { g.cue.rewinding = false; g.snapshot = null; g.cursor = -1; }
  else if (typeof g.endRewind === 'function') g.endRewind();
  if (!firstMove) { say(`  ${label}: no kick observed`); return; }
  const f = firstMove;
  say(`  ${label}: kick at crawl step ${f.i} (snapshot idx ${f.idx} of ${track.length});`
    + ` contact distance ${f.d.toFixed(3)} px; snapshot |v|=${f.sampleV.toFixed(2)}; cue rewind |v|=${f.cueV.toFixed(2)}`);
  say(`        derived j = max(0,(v_cue-v_target).n) = ${f.rel.toFixed(4)}   delivered |v_target| = ${f.delivered.toFixed(4)}   ratio = ${(f.delivered / f.rel).toFixed(6)}`);
}

say('=== rewind impulse coefficient on the L1 backstop geometry (design j = K * rel) ===');
probe('locked   ', () => {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: 276.942, y: 263.836 }], pockets: POCKETS });
  g.setLevelIds([1]);
  return g;
}, false);
probe('prototype', () => {
  const g = new LockedGame(CFG_PROTOTYPE);
  g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: 276.942, y: 263.836 }], pockets: POCKETS });
  g.setLevelIds([1]);
  return g;
}, false);
probe('mine     ', () => {
  const g = new MyGame();
  g.place({ cue: ORIGIN, balls: [{ id: 1, x: 276.942, y: 263.836 }], pockets: POCKETS });
  return g;
}, true);
fs.writeFileSync('tools/verify_solver/14_k_probe.txt', out.join('\n') + '\n');
