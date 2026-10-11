// verify.mjs — re-check every SOLVED entry of a solver output file from scratch, on a fresh
// simulation, and print the exact input sequence. This is the artifact to hand to another
// implementation: if the Godot kernel plays this sequence and does not clear the level, the
// two physics models disagree, and this file says exactly where.
//
//   node tools/solver/verify.mjs tools/solver/out/locked.json
//
// Exit code 0 = every SOLVED entry replayed to a full clear; 1 = something did not.

import fs from 'node:fs';
import { loadLevel } from './lib/level.mjs';
import { replaySolution } from './lib/search.mjs';

const file = process.argv[2] ?? 'tools/solver/out/locked.json';
const data = JSON.parse(fs.readFileSync(file, 'utf8'));
let ok = 0, bad = 0, unsolved = 0;
console.log(`verifying ${file} (solver config "${data.config}")\n`);
for (const entry of data.levels) {
  const src = entry.source;
  if (!src) { console.log(`  ${entry.level}: no source path recorded, skipped`); continue; }
  const lvl = loadLevel(src);
  if (entry.status !== 'SOLVED') {
    unsolved++;
    console.log(`  ${entry.level.padEnd(12)} ${entry.status}  (best ${entry.bestNoRewind}/${entry.totalBalls} with the strike alone, ${entry.bestAny}/${entry.totalBalls} with rewinds)`);
    continue;
  }
  const rp = replaySolution(lvl, entry, data.config);
  const tag = rp.ok ? 'OK  ' : 'FAIL';
  if (rp.ok) ok++; else bad++;
  console.log(`  ${tag} ${entry.level.padEnd(12)} ${rp.pocketed}/${rp.total} potted, won=${rp.won}`
    + `   strike ${entry.strike.angleDeg}deg / power ${entry.strike.power}`);
  for (const [i, w] of (entry.rewinds ?? []).entries()) {
    console.log(`        rewind ${i + 1}: hold R for ${w.holdSteps} steps (${(w.holdSteps / 120).toFixed(2)} s)`
      + `${w.rewoundToOrigin ? ', walk back to the strike origin (auto-stop, v=0)' : `, release at path index ${w.releaseIndex} at ${w.releaseSpeed.toFixed(1)} px/s`}`);
  }
  if (!rp.ok) console.log('        replay log: ' + JSON.stringify(rp.log));
}
console.log(`\n${ok} replay-verified, ${bad} FAILED, ${unsolved} not solved`);
process.exit(bad ? 1 : 0);
