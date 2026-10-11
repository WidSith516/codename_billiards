// Cross-check the break sweep: (a) verify the best-looking angle is not merely a
// speed-explosion artifact, and (b) re-run the whole sweep on a TIGHT rack to test
// whether the loose rack (1.295 px neighbour gap, column spacing 22.204 vs the correct
// 2R*cos30 = 21.131) is what is flattening the break.
//
// Rack patching is done by overriding the published __sim.init() wrapper: we let the
// prototype build its rack, then snap the object balls onto a proper triangle. No game
// physics is reimplemented here.
//
// Usage: node rack_ab.mjs
import { Game } from './sim.mjs';

const R = 12.2;

function tightRackPositions() {
  // Proper triangle: apex at the prototype's own apex ball position, columns spaced
  // 2R*cos30 apart so neighbours are exactly tangent.
  const apexX = 675, apexY = 280;
  const colDx = 2 * R * Math.cos(Math.PI / 6); // 21.1314
  const rowDy = 2 * R;                          // 24.4
  const out = [];
  for (let col = 0; col < 4; col++) {
    for (let row = 0; row <= col; row++) {
      out.push({ x: apexX + col * colDx, y: apexY + (row - col / 2) * rowDy });
    }
  }
  return out;
}

function runAngle(angleDeg, power, tight) {
  const g = new Game({ seed: 0x5eed });
  g.reset();
  if (tight) {
    const pos = tightRackPositions();
    const obj = g.balls.filter((b) => b.id !== 0).sort((a, b) => a.id - b.id);
    obj.forEach((b, i) => { b.x = pos[i].x; b.y = pos[i].y; b.vx = 0; b.vy = 0; });
    // let the patched rack settle for one step to resolve any contact
    g.step(1);
  }
  g.shoot(angleDeg, power);
  g.settle({ maxSeconds: 25, stopOnWin: true });
  const m = g.metrics;
  return {
    angle: angleDeg, pocketed: g.pocketedCount(), nan: m.nan,
    maxSpeed: Number.isFinite(m.maxSpeed) ? Math.round(m.maxSpeed) : 'Infinity',
    rest: g.atRest(), cuePocketed: g.cue().pocketed,
    boostEvents: m.boostEvents
  };
}

console.log('=== A. is the best angle a speed-explosion artifact? ===');
for (const ang of [218, 228, 132, 206, 110]) {
  const r = runAngle(ang, 170, false);
  console.log(`  ang=${String(ang).padStart(3)} pocketed=${r.pocketed} maxSpeed=${String(r.maxSpeed).padStart(9)}`
    + ` nan=${r.nan} rest=${r.rest} boostEvents=${r.boostEvents} cuePocketed=${r.cuePocketed ? 'YES' : 'no'}`);
}
console.log('  (compare maxSpeed against the discrete-collision safety bounds: 2070 / 2928 px/s)');

console.log('');
console.log('=== B. loose (as shipped) vs tight rack, full-power sweep ===');
const loose = [], tight = [];
for (let ang = 0; ang < 360; ang += 2) {
  loose.push(runAngle(ang, 170, false));
  tight.push(runAngle(ang, 170, true));
}

function report(name, rows) {
  const d = new Map();
  for (const r of rows) d.set(r.pocketed, (d.get(r.pocketed) ?? 0) + 1);
  const best = Math.max(...rows.map((r) => r.pocketed));
  const ge1 = rows.filter((r) => r.pocketed >= 1).length;
  const ge2 = rows.filter((r) => r.pocketed >= 2).length;
  const nan = rows.filter((r) => r.nan).length;
  const wild = rows.filter((r) => r.maxSpeed === 'Infinity' || r.maxSpeed > 1e5).length;
  console.log(`  ${name.padEnd(6)} best=${best}/10  >=1:${String(ge1).padStart(3)}/180  >=2:${String(ge2).padStart(2)}/180`
    + `  NaN:${String(nan).padStart(2)}  maxSpeed>1e5:${String(wild).padStart(2)}`);
  console.log(`         distribution ${[...d.entries()].sort((a, b) => a[0] - b[0]).map(([k, v]) => `${k}:${v}`).join('  ')}`);
  return { best, ge1, ge2, nan, wild };
}

const a = report('loose', loose);
const b = report('tight', tight);
console.log('');
console.log(`  best-angle delta : tight ${b.best} vs loose ${a.best}`);
console.log(`  >=1 angle delta  : tight ${b.ge1} vs loose ${a.ge1}`);
console.log('  NOTE maxSpeed figures above are dominated by the rail-boost divergence bug;');
console.log('       these are NOT valid break outcomes for balance purposes.');
