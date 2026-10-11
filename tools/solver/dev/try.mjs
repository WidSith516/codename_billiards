// try.mjs — DESIGN PROBE. Run one explicit plan against one level and explain what happened.
//
//   node tools/solver/dev/try.mjs --level tools/solver/levels/L1.json --angle 0 --power 170 \
//        --seq 1.0            # release fractions, comma separated (1.0 = walk back to origin)
//   node tools/solver/dev/try.mjs --level ... --scan 0,360,5 --power 170   # angle scan, no rewind
//
// Prints, for every object ball, where it started, whether the strike touched it, its
// distance to the recorded path, and whether/when it was pocketed.

import { LockedGame, CONFIGS } from '../lib/locked.mjs';
import { loadLevel, validateLevel } from '../lib/level.mjs';

const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf('--' + n); return i < 0 ? d : argv[i + 1]; };
const CFG = CONFIGS[arg('config', 'locked')];

function runPlan(level, angle, power, fractions, verbose = true) {
  const g = new LockedGame(CFG);
  g.placeLevel(level);
  g.setLevelIds(level.balls.map((b) => b.id));
  const start = g.remainingLevel().map((b) => ({ id: b.id, x: b.x, y: b.y }));
  g.shoot(angle, power);
  const v0 = Math.hypot(g.cue.vx, g.cue.vy);
  g.settle(40);
  const afterStrike = g.pocketedLevel();
  const cor = g.corridor();
  const out = {
    angle, power, v0, afterStrike, afterStrikeWho: g.remainingLevel().map((b) => b.id),
    corridor: cor, pathLen: g.pathLength(), pathPts: g.cue.history.length,
    cueRest: { x: +g.cue.x.toFixed(1), y: +g.cue.y.toFixed(1) },
    steps: [], fracs: fractions,
  };
  if (verbose) {
    console.log(`  strike angle ${angle} power ${power}: v0=${v0.toFixed(1)} px/s`);
    for (const s of start) {
      const b = g.ball(s.id);
      const c = cor.find((c) => c.id === s.id) ?? { dist: -1 };
      console.log(`    ball ${s.id} (${s.x.toFixed(0)},${s.y.toFixed(0)}) -> `
        + (b.pocketed ? `POCKETED by the strike` : `(${b.x.toFixed(0)},${b.y.toFixed(0)})`)
        + (b.pocketed ? '' : `  dist to path ${c.dist.toFixed(2)}${c.inCorridor ? ' IN CORRIDOR' : ''}`));
    }
    console.log(`    path: ${out.pathPts} samples, ${out.pathLen.toFixed(0)} px, cue rests at (${out.cueRest.x},${out.cueRest.y})`);
  }
  let k = 0;
  for (const f of fractions) {
    k++;
    const trackLen = g.cue.history.length;
    const hold = Math.max(1, Math.min(trackLen - 1, Math.round(f * (trackLen - 1))));
    const r = g.hold(hold);
    g.settle(40);
    const p = g.pocketedLevel();
    out.steps.push({
      step: k, frac: f, hold, cursor: r.cursor, toOrigin: r.rewoundToOrigin,
      releaseSpeed: +Math.hypot(r.release.vx, r.release.vy).toFixed(1),
      pocketed: p, cue: { x: +g.cue.x.toFixed(1), y: +g.cue.y.toFixed(1) }, rewindHits: g.rewindHits,
    });
    if (verbose) {
      console.log(`    rewind ${k}: f=${f} hold=${hold} steps (${(hold / 120).toFixed(2)} s)`
        + ` cursor=${r.cursor}${r.rewoundToOrigin ? ' [walked to origin, v=0]' : ''}`
        + ` release=${Math.hypot(r.release.vx, r.release.vy).toFixed(1)} px/s`
        + ` -> pocketed ${p}/${level.balls.length}, cue at (${g.cue.x.toFixed(0)},${g.cue.y.toFixed(0)}), rewind kicks ${g.rewindHits}`);
    }
    if (p === level.balls.length) break;
  }
  out.final = g.pocketedLevel();
  out.won = g.won;
  out.remaining = g.remainingLevel().map((b) => ({ id: b.id, x: +b.x.toFixed(1), y: +b.y.toFixed(1) }));
  return out;
}

const file = arg('level', null);
if (!file) { console.error('need --level'); process.exit(2); }
const level = loadLevel(file);
const errs = validateLevel(level);
if (errs.length) { console.error('INVALID: ' + errs.join('; ')); process.exit(2); }
const power = Number(arg('power', 170));
const seq = String(arg('seq', '1.0')).split(',').map(Number);

console.log(`level ${level.id} "${level.name}"  ${level.balls.length} balls  config ${CFG.name}`);
console.log(`cue (${level.cue.x},${level.cue.y})  balls ${level.balls.map((b) => `${b.id}:(${b.x.toFixed(0)},${b.y.toFixed(0)})`).join(' ')}`);

const scan = arg('scan', null);
if (scan) {
  const [a0, a1, st] = String(scan).split(',').map(Number);
  console.log(`\nangle scan ${a0}..${a1} step ${st}, power ${power}, no rewind:`);
  const rows = [];
  for (let a = a0; a < a1; a += st) {
    const r = runPlan(level, a, power, [], false);
    rows.push(r);
  }
  rows.sort((x, y) => y.afterStrike - x.afterStrike);
  for (const r of rows.slice(0, 12)) {
    const inCor = r.corridor.filter((c) => c.inCorridor).length;
    console.log(`  angle ${String(r.angle).padStart(6)}  strike pots ${r.afterStrike}/${level.balls.length}`
      + `  corridor has ${inCor} ball(s)  path ${r.pathLen.toFixed(0)} px  cue rest (${r.cueRest.x},${r.cueRest.y})`);
  }
  const best = Math.max(...rows.map((r) => r.afterStrike));
  console.log(`  best by strike alone: ${best}/${level.balls.length}`);
} else {
  const angles = String(arg('angle', '0')).split(',').map(Number);
  for (const a of angles) {
    console.log(`\n=== angle ${a} ===`);
    runPlan(level, a, power, seq, true);
  }
}
