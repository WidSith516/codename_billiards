// Break-attainability sweep, run by the Team Lead against recon-h5's harness
// (tools/h5harness/sim.mjs), which executes the FROZEN h5/index.html bytecode in a
// node:vm sandbox with a stub DOM. This script adds NO game logic; it only drives
// the public Game API.
//
// The headline design question: with exactly ONE strike and then only rewind, is a
// single strike able to pocket any object ball at all, and what is the best it can do?
// No rewind is used here, so this is a LOWER BOUND on what the game can achieve.
//
// Usage: node break_sweep.mjs [--stepDeg 2] [--power 170]
import { Game } from './sim.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const m = a.match(/^--(\w+)=?(.*)$/);
    return m ? [m[1], m[2] === '' ? true : m[2]] : [a, true];
  })
);
const STEP_DEG = Number(args.stepDeg ?? 2);
const POWER = Number(args.power ?? 170);
const SETTLE_S = Number(args.settle ?? 25);

const rows = [];
let worst = null;

for (let ang = 0; ang < 360; ang += STEP_DEG) {
  const g = new Game({ seed: 0x5eed });
  g.reset();
  g.shoot(ang, POWER);
  g.settle({ maxSeconds: SETTLE_S, stopOnWin: true });
  const m = g.metrics;
  const row = {
    angle: ang,
    pocketed: g.pocketedCount(),
    nan: m.nan,
    divergedAtStep: m.divergedAtStep,
    maxSpeed: Number.isFinite(m.maxSpeed) ? Math.round(m.maxSpeed) : 'Infinity',
    maxObjectSpeed: Number.isFinite(m.maxObjectSpeed) ? Math.round(m.maxObjectSpeed) : 'Infinity',
    history: g.history().length,
    rest: g.atRest(),
    cueX: Math.round(g.cue().x), cueY: Math.round(g.cue().y),
    cuePocketed: g.cue().pocketed
  };
  rows.push(row);
  if (!worst || row.pocketed > worst.pocketed) worst = row;
}

const byPocketed = new Map();
for (const r of rows) byPocketed.set(r.pocketed, (byPocketed.get(r.pocketed) ?? 0) + 1);
const nanRows = rows.filter((r) => r.nan);
const notAtRest = rows.filter((r) => !r.rest);

console.log(`break sweep: power=${POWER} step=${STEP_DEG}deg angles=${rows.length} settle=${SETTLE_S}s`);
console.log('');
console.log('=== pocketed object balls after ONE strike (no rewind) ===');
for (const k of [...byPocketed.keys()].sort((a, b) => a - b)) {
  const n = byPocketed.get(k);
  const pctv = (n / rows.length) * 100;
  console.log(`  ${k} pocketed : ${String(n).padStart(4)} angles (${pctv.toFixed(1)}%)`);
}
console.log('');
console.log(`best single strike : ${worst.pocketed}/10 at angle ${worst.angle}deg`);
console.log(`angles reaching >=1: ${rows.filter((r) => r.pocketed >= 1).length}/${rows.length}`);
console.log(`angles reaching >=2: ${rows.filter((r) => r.pocketed >= 2).length}/${rows.length}`);
console.log(`angles reaching >=3: ${rows.filter((r) => r.pocketed >= 3).length}/${rows.length}`);
console.log('');
console.log(`NaN / non-finite state : ${nanRows.length}/${rows.length} angles` +
  (nanRows.length ? ` (e.g. angles ${nanRows.slice(0, 8).map((r) => r.angle).join(', ')})` : ''));
console.log(`never came to rest     : ${notAtRest.length}/${rows.length}` +
  (notAtRest.length ? ` (e.g. angles ${notAtRest.slice(0, 8).map((r) => r.angle).join(', ')})` : ''));
console.log('');
console.log('=== top 12 angles by pocketed count ===');
for (const r of [...rows].sort((a, b) => b.pocketed - a.pocketed || String(a.maxSpeed).localeCompare(String(b.maxSpeed))).slice(0, 12)) {
  console.log(`  ang=${String(r.angle).padStart(3)}  pocketed=${r.pocketed}  maxSpeed=${String(r.maxSpeed).padStart(8)}`
    + `  cue=(${r.cueX},${r.cueY})  cuePocketed=${r.cuePocketed ? 'YES' : 'no'}  nan=${r.nan}`);
}

// A distribution summary that can be quoted directly in the design doc.
const dist = [...byPocketed.entries()].sort((a, b) => a[0] - b[0])
  .map(([k, v]) => `${k}:${v}`).join('  ');
console.log('');
console.log(`DISTRIBUTION pocketed->count : ${dist}`);
console.log(`ATTAINABLE_WITHOUT_REWIND   : ${worst.pocketed}/10`);
