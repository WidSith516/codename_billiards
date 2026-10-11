// potmap.mjs — DESIGN TOOL. For one strike, where on the table can a single ball sit and
// still be pocketed (a) by the strike itself, (b) only after a full rewind?
//
// Method per grid cell: place exactly one object ball there, shoot, settle, record; if it
// was not potted but IS inside the rewind corridor (centre within 2R of the cue's recorded
// path) then hold R for the whole path and settle again, and record.
//
// Usage:
//   node tools/solver/dev/potmap.mjs --angle 0 --power 170 [--config locked] [--step 25]
//
// Legend:  S potted by the strike   R potted only after the rewind   o inside the corridor
//          but not potted          . outside the corridor (rewind cannot reach it)

import { LockedGame, CONFIGS } from '../lib/locked.mjs';
import { FACTORY_CUE, FACTORY_POCKETS, FACTORY_TABLE } from '../lib/level.mjs';

const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf('--' + n); return i < 0 ? d : argv[i + 1]; };
const ANGLES = String(arg('angle', '0')).split(',').map(Number);
const POWER = Number(arg('power', 170));
const CFG = CONFIGS[arg('config', 'locked')];
const STEP = Number(arg('step', 25));
const R = 12.2, T = FACTORY_TABLE;

function run(angle, power, x, y) {
  const g = new LockedGame(CFG);
  g.placeLevel({ cue: FACTORY_CUE, balls: [{ id: 1, x, y }] });
  g.setLevelIds([1]);
  g.shoot(angle, power);
  g.settle(30);
  if (g.ball(1).pocketed) return { kind: 'S', ball: g.ball(1) };
  const cor = g.corridor()[0];
  if (!cor.inCorridor) return { kind: '.', dist: cor.dist };
  const trackLen = g.cue.history.length;
  g.hold(Math.max(1, trackLen - 1));
  g.settle(30);
  // did the ball get any velocity at all (i.e. was it actually touched)?
  const touched = g.rewindHits > 0;
  if (g.ball(1).pocketed) return { kind: 'R', dist: cor.dist, touched };
  return { kind: 'o', dist: cor.dist, touched, ballx: g.ball(1).x, bally: g.ball(1).y };
}

const xs = [];
for (let x = T.left + R; x <= T.right - R + 0.01; x += STEP) xs.push(Math.round(x * 100) / 100);
const ys = [];
for (let y = T.top + R; y <= T.bottom - R + 0.01; y += STEP) ys.push(Math.round(y * 100) / 100);

console.log(`config=${CFG.name}  power=${POWER}  grid ${xs.length}x${ys.length} step ${STEP} px`);
console.log(`cue at (${FACTORY_CUE.x},${FACTORY_CUE.y}); pockets ${JSON.stringify(FACTORY_POCKETS)}`);

for (const angle of ANGLES) {
  const t0 = Date.now();
  const counts = { S: 0, R: 0, o: 0, '.': 0 };
  const rows = [];
  const inCorridor = [];
  for (const y of ys) {
    let line = '';
    for (const x of xs) {
      // never place a ball inside a pocket capture circle
      const nearPocket = FACTORY_POCKETS.some(([px, py]) => Math.hypot(px - x, py - y) < 30);
      if (nearPocket) { line += '#'; continue; }
      const r = run(angle, POWER, x, y);
      counts[r.kind]++;
      line += r.kind;
      if (r.kind === 'R') inCorridor.push({ x, y, dist: r.dist });
    }
    rows.push(line);
  }
  console.log(`\n=== angle ${angle}  power ${POWER}  (${((Date.now() - t0) / 1000).toFixed(1)} s) ===`);
  console.log(`  S (strike pots it) = ${counts.S}    R (rewind pots it) = ${counts.R}`
    + `    o (in corridor, survives) = ${counts.o}    . (unreachable) = ${counts['.']}`);
  console.log(`  x: ${xs[0]} .. ${xs[xs.length - 1]} step ${STEP};  y: ${ys[0]} .. ${ys[ys.length - 1]}  (# = inside a pocket mouth)`);
  console.log('     ' + xs.map((_, i) => (i % 4 === 0 ? '|' : ' ')).join(''));
  for (let i = 0; i < rows.length; i++) {
    console.log(`  ${String(ys[i]).padStart(6)} ${rows[i]}`);
  }
  if (inCorridor.length) {
    const d = inCorridor.map((c) => c.dist).sort((a, b) => a - b);
    console.log(`  R-cells corridor distance: min ${d[0].toFixed(2)} median ${d[Math.floor(d.length / 2)].toFixed(2)} max ${d[d.length - 1].toFixed(2)} px (2R = 24.4)`);
  }
}
