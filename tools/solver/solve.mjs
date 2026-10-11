// solve.mjs — CLI: decide a level SOLVED / UNSOLVED and print an example solution.
//
//   node tools/solver/solve.mjs tools/solver/levels/L1.json
//   node tools/solver/solve.mjs --all --config locked
//   node tools/solver/solve.mjs --all --config prototype --json tools/solver/out/prototype.json
//   node tools/solver/solve.mjs tools/solver/levels/L3.json --budget maxRewinds=6,beam=8,topStrikes=8
//
// Exit codes: 0 = at least one level SOLVED (or single level SOLVED), 1 = all UNSOLVED,
//             2 = usage/validation error.
//
// Every SOLVED result is REPLAYED on a fresh simulation before it is reported (see
// lib/search.mjs replaySolution); an unreplayable solution is downgraded to UNSOLVED.

import fs from 'node:fs';
import path from 'node:path';
import { listLevels, loadLevel, validateLevel } from './lib/level.mjs';
import { solveLevel, replaySolution } from './lib/search.mjs';

const argv = process.argv.slice(2);
const flag = (name, def = null) => {
  const i = argv.findIndex((a) => a === '--' + name);
  if (i < 0) return def;
  const v = argv[i + 1];
  return (v === undefined || v.startsWith('--')) ? true : v;
};
const files = argv.filter((a) => !a.startsWith('--') && a.endsWith('.json'));

const CONFIG = flag('config', 'locked');
const ALL = flag('all', false) === true;
const JSON_OUT = flag('json', null);
const QUIET = flag('quiet', false) === true;
const budgetOverride = {};
const bs = flag('budget', null);
if (typeof bs === 'string') {
  for (const kv of bs.split(',')) {
    const [k, v] = kv.split('=');
    budgetOverride[k] = v.includes('.') ? Number(v) : Number(v);
  }
}
const budget = { ...budgetOverride };
if (typeof budgetOverride.powers === 'string') budget.powers = budgetOverride.powers.split(';').map(Number);
if (typeof budgetOverride.releaseFractions === 'string') budget.releaseFractions = budgetOverride.releaseFractions.split(';').map(Number);

const targets = ALL ? listLevels() : (files.length ? files : listLevels());
if (!targets.length) { console.error('no levels found'); process.exit(2); }

const out = { config: CONFIG, when: new Date().toISOString(), levels: [] };
let solvedCount = 0;

for (const f of targets) {
  const level = loadLevel(f);
  const errs = validateLevel(level);
  if (errs.length) { console.error(`INVALID ${level.id}: ${errs.join('; ')}`); process.exit(2); }
  const res = solveLevel(level, { cfgName: CONFIG, budget });
  let replay = null;
  if (res.status === 'SOLVED') {
    replay = replaySolution(level, res, CONFIG);
    if (!replay.ok) { res.status = 'UNSOLVED(unreplayable)'; res.replay = replay; }
    else solvedCount++;
  }
  res.source = level.source;
  res.name = level.name;
  out.levels.push(res);
  if (!QUIET) {
    const secs = (res.stats.ms / 1000).toFixed(1);
    const ang = `${res.stats.anglesSearched} angles x ${budget.powers?.length ?? 4} powers = ${res.stats.strikesTried} strikes`;
    console.log(`\n${res.status.padEnd(12)} ${level.id.padEnd(14)} ${level.name}`);
    console.log(`  searched ${ang}, ${res.stats.sims} sims, ${secs} s`);
    if (res.status === 'SOLVED') {
      console.log(`  strike: angle ${res.strike.angleDeg}° power ${res.strike.power}`);
      if (!res.rewinds.length) console.log('  no rewind needed (potted by the strike itself)');
      for (const [i, r] of res.rewinds.entries()) {
        console.log(`  rewind ${i + 1}: hold ${r.holdSteps} steps (${(r.holdSteps / 120).toFixed(2)} s)`
          + ` -> release at path index ${r.releaseIndex}${r.rewoundToOrigin ? ' (walked back to the origin, v=0)' : ''}`
          + `, release speed ${r.releaseSpeed.toFixed(1)} px/s`);
      }
      console.log(`  replay check: ${replay.pocketed}/${replay.total} pocketed, won=${replay.won}`);
    } else {
      console.log(`  best: ${res.bestNoRewind} ball(s) with the strike alone, ${res.bestAny} with rewinds`);
      if (res.best?.plan?.length) console.log(`  best rewind plan tried: ${JSON.stringify(res.best.plan.map((p) => p.releaseFraction))}`);
    }
  }
}

if (JSON_OUT) {
  const p = path.isAbsolute(JSON_OUT) ? JSON_OUT : path.resolve(JSON_OUT);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, JSON.stringify(out, null, 2));
  if (!QUIET) console.log(`\nwrote ${p}`);
}

if (!QUIET) {
  console.log('\n=== summary ===');
  for (const r of out.levels) {
    console.log(`  ${r.level.padEnd(14)} ${r.config.padEnd(20)} ${r.status.padEnd(12)}`
      + ` bestNoRewind=${r.bestNoRewind}/${r.totalBalls} best=${r.bestAny}/${r.totalBalls}`
      + ` ${(r.stats.ms / 1000).toFixed(1)}s  angles=${r.stats.anglesSearched}`);
  }
}
process.exit(solvedCount > 0 ? 0 : 1);
