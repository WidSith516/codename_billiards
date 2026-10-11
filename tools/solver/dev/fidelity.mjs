// fidelity.mjs — CONTROL #1: prove the solver's step loop is the prototype's step loop.
//
// Method: run the SAME shot on (a) tools/h5harness/sim.mjs (which executes the frozen
// h5/index.html bytecode in a vm) and (b) lib/locked.mjs configured as CFG_PROTOTYPE.
// Compare every ball's (x, y, vx, vy, pocketed) after every single step, bit-for-bit.
//
// If this passes, then the ONLY differences between our solver and the shipped game are
// the locked-model patches, which is the property the whole report rests on.
//
// Usage: node tools/solver/dev/fidelity.mjs

import { Game } from '../../h5harness/sim.mjs';
import { LockedGame, CFG_PROTOTYPE } from '../lib/locked.mjs';
import { factoryLevel } from '../lib/level.mjs';

const CASES = [
  { angle: 0, power: 170 }, { angle: 17.6, power: 170 }, { angle: 90, power: 170 },
  { angle: 115, power: 170 }, { angle: 218, power: 170 }, { angle: 45, power: 100 },
  { angle: 250, power: 170 }, { angle: 305, power: 60 },
];
const STEPS = 720;   // 6 s

const lvl = factoryLevel();

function snapshotHarness(g) {
  return g.balls.map((b) => [b.x, b.y, b.vx, b.vy, b.pocketed ? 1 : 0]);
}

console.log('=== CONTROL 1: solver step loop vs h5harness/sim.mjs (prototype config) ===');
console.log(`shot: full 11-ball factory rack, ${STEPS} steps @ 1/120 s, 8 angles\n`);

let worst = 0, worstWhere = '', allBitEqual = true, divergentCases = 0;
for (const c of CASES) {
  const h = new Game({ seed: 0x5eed });
  h.reset(); h.track = false;
  h.shoot(c.angle, c.power);

  const s = new LockedGame(CFG_PROTOTYPE);
  s.placeLevel({ ...lvl, balls: factoryRackSpec(), cue: { x: 292, y: 280 } });
  s.shoot(c.angle, c.power);

  let mismatchStep = -1, maxAbs = 0;
  for (let i = 0; i < STEPS; i++) {
    h.step(1);
    s.stepOnce();
    const A = snapshotHarness(h), B = s.balls.map((b) => [b.x, b.y, b.vx, b.vy, b.pocketed ? 1 : 0]);
    for (let k = 0; k < A.length; k++) {
      for (let f = 0; f < 4; f++) {
        const d = Math.abs(A[k][f] - B[k][f]);
        if (d > maxAbs) maxAbs = d;
        if (d > worst) { worst = d; worstWhere = `angle ${c.angle} step ${i} ball ${k} field ${f}`; }
      }
      if (A[k][4] !== B[k][4] && mismatchStep < 0) mismatchStep = i;
    }
  }
  const exact = maxAbs === 0;
  if (!exact) allBitEqual = false;
  if (mismatchStep >= 0 || !exact) divergentCases++;
  console.log(`  angle ${String(c.angle).padStart(6)} power ${c.power}: max |delta| = ${maxAbs.toExponential(3)}`
    + `  ${exact ? 'BIT-IDENTICAL' : 'DIFFERS (first pocket mismatch @ step ' + mismatchStep + ')'}`);
}
console.log(`\n  verdict: ${allBitEqual ? 'PASS — bit-identical on all ' + CASES.length + ' shots x ' + STEPS + ' steps'
  : 'FAIL — ' + divergentCases + '/' + CASES.length + ' shots differ; worst ' + worst.toExponential(3) + ' at ' + worstWhere}`);
console.log('  (bit-identical = every x, y, vx, vy equal to the last bit, including the');
console.log('   unbounded x1.5 rail divergence and the Infinity blow-ups, if any)\n');

function factoryRackSpec() {
  const R = 12.2; const out = []; let id = 1;
  for (let col = 0; col < 4; col++)
    for (let row = 0; row <= col; row++)
      out.push({ id: id++, x: 675 + col * R * 1.82, y: 280 + (row - col / 2) * R * 2.12 });
  return out;
}
