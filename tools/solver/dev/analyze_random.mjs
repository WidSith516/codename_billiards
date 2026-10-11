// analyze_random.mjs — aggregates the level-authoring experiment.
//
// Reads every tools/solver/out/rand_*.json produced by dev/random_layouts.mjs and reports,
// per arm, the replay-verified solve rate, plus the geometric feature that separates solved
// from unsolved layouts: how many of the object balls are inside the rewind corridor of the
// best strike with a kick strong enough to reach a pocket.
//
// Usage: node tools/solver/dev/analyze_random.mjs

import fs from 'node:fs';
import path from 'node:path';

const dir = 'tools/solver/out';
const files = fs.readdirSync(dir).filter((f) => /^rand_.*\.json$/.test(f));
const rows = [];
for (const f of files) {
  const j = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
  for (const r of j.results) rows.push(r);
}
if (!rows.length) { console.log('no rand_*.json found'); process.exit(1); }

const arms = [...new Set(rows.map((r) => r.mode))];
const pct = (a, b) => b ? `${(100 * a / b).toFixed(1)}%` : 'n/a';

console.log(`level-authoring experiment: ${rows.length} layouts, ${files.length} files`
  + ` (${JSON.parse(fs.readFileSync(path.join(dir, files[0]), 'utf8')).config} physics, 3 object balls)\n`);

console.log('  arm      layouts  solved  solve rate   median sims   median bestNoRewind');
for (const arm of arms) {
  const rs = rows.filter((r) => r.mode === arm);
  const solved = rs.filter((r) => r.status === 'SOLVED').length;
  const med = (k) => {
    const v = rs.map((r) => r[k] ?? 0).sort((a, b) => a - b);
    return v.length ? v[Math.floor(v.length / 2)] : 0;
  };
  console.log(`  ${arm.padEnd(8)} ${String(rs.length).padStart(7)}  ${String(solved).padStart(6)}  ${pct(solved, rs.length).padStart(10)}`
    + `   ${String(med('sims')).padStart(11)}   ${String(med('bestNoRewind')).padStart(17)}`);
}

// --- histograms ---------------------------------------------------------------------
// (The per-candidate corridor/kick fields were added to the solver AFTER this data set was
//  collected, so they are absent here; dev/coverage.mjs measures that geometry directly
//  instead, and re-running random_layouts.mjs regenerates them.)
const reached = (r) => (r.status === 'SOLVED' ? 3 : (r.bestAny ?? 0));

console.log('\n  balls potted by the best plan the solver found (strike + up to 3 rewinds):');
console.log('  balls | layouts');
for (let k = 0; k <= 3; k++) {
  const n = rows.filter((r) => reached(r) === k).length;
  if (n) console.log(`  ${String(k).padStart(5)} | ${String(n).padStart(7)}`);
}
console.log('\n  balls potted by the strike alone (bestNoRewind):');
console.log('  balls | layouts   (random layouts only: how often a strike pots nothing)');
for (let k = 0; k <= 3; k++) {
  const rs = rows.filter((r) => (r.bestNoRewind ?? 0) === k);
  if (!rs.length) continue;
  const solved = rs.filter((r) => r.status === 'SOLVED').length;
  console.log(`  ${String(k).padStart(5)} | ${String(rs.length).padStart(7)}   solved ${solved} (${pct(solved, rs.length)})`);
}
console.log('\n  solved layouts by rewind count:');
{
  const solved = rows.filter((r) => r.status === 'SOLVED');
  const by = {};
  for (const r of solved) by[r.rewinds] = (by[r.rewinds] ?? 0) + 1;
  console.log('  rewinds | layouts');
  for (const k of Object.keys(by).sort()) console.log(`  ${String(k).padStart(7)} | ${String(by[k]).padStart(7)}`);
}

const solvedRows = rows.filter((r) => r.status === 'SOLVED');
console.log('\n  solved layouts, example solutions:');
for (const r of solvedRows.slice(0, 8)) {
  console.log(`    ${r.mode}/${r.i}: strike ${r.strike.angleDeg}deg/${r.strike.power}, rewinds ${r.rewinds}`
    + `, maxPotential ${r.maxPotential}, maxCorridorBalls ${r.maxCorridorBalls}`);
}
const out = {
  layouts: rows.length, arms: arms.map((arm) => {
    const rs = rows.filter((r) => r.mode === arm);
    return { arm, n: rs.length, solved: rs.filter((r) => r.status === 'SOLVED').length };
  }),
  rows: rows.map((r) => ({
    mode: r.mode, i: r.i, status: r.status, bestNoRewind: r.bestNoRewind, bestAny: r.bestAny,
    maxPotential: r.maxPotential, maxCorridorBalls: r.maxCorridorBalls, sims: r.sims, ms: r.ms,
    kickable: ((r.topCandidates ?? []).slice().sort((a, b) => b.potential - a.potential)[0]?.corridor ?? []).filter((c) => c.kick > 250).length,
    strike: r.strike, balls: r.balls,
  })),
};
fs.writeFileSync('tools/solver/out/random_summary.json', JSON.stringify(out, null, 1));
console.log('\nwrote tools/solver/out/random_summary.json');
