// coverage.mjs — WHY blind placement fails: how much of the table does one strike's rewind
// corridor actually cover?
//
// For a set of strike angles, trace the real path, then measure over a grid of the playable
// area:  (a) the fraction of points inside the 2R corridor,
//        (b) the fraction with a rewind kick > 250 px/s (enough to travel ~50 px),
//        (c) the fraction with a kick that could reach the nearest pocket.
//
// A level with n object balls placed at random needs (in the best case) all n inside a
// corridor, so the blind-placement solve rate is bounded by (coverage)^n. Compare that with
// the measured rates in dev/analyze_random.mjs.
//
// Usage: node tools/solver/dev/coverage.mjs [--config locked]

import { LockedGame, CONFIGS } from '../lib/locked.mjs';

const argv = process.argv.slice(2);
const cfgName = (() => { const i = argv.indexOf('--config'); return i < 0 ? 'locked' : argv[i + 1]; })();
const CFG = CONFIGS[cfgName];
const ORIGIN = { x: 292, y: 280 };
const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
const TABLE = { left: 65, right: 975, top: 63, bottom: 497 };
const R = 12.2, R2 = 24.4;

const gx = [], gy = [];
for (let x = TABLE.left + R + 4; x <= TABLE.right - R - 4; x += 12) gx.push(x);
for (let y = TABLE.top + R + 4; y <= TABLE.bottom - R - 4; y += 12) gy.push(y);
const total = gx.length * gy.length;

const rows = [];
for (let angle = 0; angle < 360; angle += 15) {
  const g = new LockedGame(CFG);
  g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: -1000, y: -1000 }], pockets: POCKETS });
  g.setLevelIds([1]);
  g.ball(1).pocketed = true;              // cue only: measure the path of a clean strike
  g.shoot(angle, 170);
  g.settle(45);
  const h = g.cue.history;
  let inCor = 0, kick250 = 0, kickPocket = 0;
  for (const x of gx) {
    for (const y of gy) {
      let best = Infinity, kick = 0;
      for (let i = 0; i < h.length; i++) {
        const dx = x - h[i].x, dy = y - h[i].y;
        const d = Math.hypot(dx, dy);
        if (d < best) best = d;
        if (d < R2 && d > 1e-6) {
          const j = (-h[i].vx * dx - h[i].vy * dy) / d;
          if (j > kick) kick = j;
        }
      }
      if (best < R2) inCor++;
      if (kick > 250) {
        kick250++;
        // can that kick reach a pocket from here?
        let need = Infinity;
        for (const P of POCKETS) need = Math.min(need, Math.sqrt(2 * 640 * Math.hypot(P[0] - x, P[1] - y)));
        if (kick > need) kickPocket++;
      }
    }
  }
  rows.push({
    angle, pathSamples: h.length, pathPx: +(h.length * 2).toFixed(0),
    inCorridor: inCor / total, kick250: kick250 / total, kickToPocket: kickPocket / total,
  });
}

const avg = (k) => rows.reduce((s, r) => s + r[k], 0) / rows.length;
console.log(`=== rewind-corridor coverage of the playable area (config ${CFG.name}) ===`);
console.log(`grid ${gx.length}x${gy.length} = ${total} points covering ${((TABLE.right - TABLE.left) * (TABLE.bottom - TABLE.top)).toFixed(0)} px^2\n`);
console.log('  angle  path(px)  in corridor  kick>250  kick can reach a pocket');
for (const r of rows) {
  console.log(`  ${String(r.angle).padStart(5)} ${String(r.pathPx).padStart(9)}`
    + `  ${(100 * r.inCorridor).toFixed(1).padStart(10)}%  ${(100 * r.kick250).toFixed(1).padStart(7)}%`
    + `  ${(100 * r.kickToPocket).toFixed(1).padStart(22)}%`);
}
const A = { inCorridor: avg('inCorridor'), kick250: avg('kick250'), kickToPocket: avg('kickToPocket') };
console.log(`\n  MEAN over ${rows.length} full-power strike angles, for ONE strike + its rewind:`);
console.log(`    area inside the 2R corridor        : ${(100 * A.inCorridor).toFixed(1)}%`);
console.log(`    area with a rewind kick > 250 px/s : ${(100 * A.kick250).toFixed(1)}%`);
console.log(`    area whose best kick reaches a pocket: ${(100 * A.kickToPocket).toFixed(1)}%`);
console.log('\n  blind-placement prediction for n balls (all must be reachable):');
for (const n of [2, 3, 4, 5]) {
  console.log(`    n=${n}: corridor-only ${(100 * A.inCorridor ** n).toFixed(3)}%   kick-to-pocket ${(100 * A.kickToPocket ** n).toFixed(4)}%`);
}
console.log(`\n  NOTE: this is a lower bound on the authoring problem only in the sense that it`);
console.log(`  ignores that several strike angles can be combined with several rewind rounds.`);
