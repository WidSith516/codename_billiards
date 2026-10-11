// replay_solutions.mjs — independent replay of the six "SOLVED" levels on MY kernel
// (tools/verify_solver/mykernel.mjs), driven only by the reported (angle, power, holdSteps).
//
//   node tools/verify_solver/replay_solutions.mjs
//
// It also reports, for each level, what the strike ALONE pots at the reported angle/power
// (rewind load-bearing test), and re-runs everything under four kernel settings so that a
// pass/fail cannot be an artefact of my own substep choice.
import fs from 'node:fs';
import { MyGame, loadLevelFile } from './mykernel.mjs';

const SOLDIR = 'tools/solver/levels';
const locked = JSON.parse(fs.readFileSync('tools/solver/out/locked.json', 'utf8'));

const SETTINGS = [
  { name: 'mine:2px+ptr', substepPx: 2.0, maxSubsteps: 16, pointerArithmetic: true },
  { name: 'mine:1px+ptr', substepPx: 1.0, maxSubsteps: 32, pointerArithmetic: true },
  { name: 'mine:4px+ptr', substepPx: 4.0, maxSubsteps: 8, pointerArithmetic: true },
  { name: 'mine:2px-noptr', substepPx: 2.0, maxSubsteps: 16, pointerArithmetic: false },
];

function runOne(level, sol, setting, doRewinds) {
  const g = new MyGame(setting);
  g.place(level);
  g.shoot(sol.strike.angleDeg, sol.strike.power);
  g.settle(60);
  const afterStrike = g.pocketedCount();
  const trace = [{ stage: 'strike', potted: afterStrike, cue: { x: +g.cue.x.toFixed(2), y: +g.cue.y.toFixed(2) }, hist: g.cue.history.length }];
  if (doRewinds) {
    for (const rw of sol.rewinds ?? []) {
      const res = g.hold(rw.holdSteps);
      g.settle(60);
      trace.push({ stage: `rewind(${rw.holdSteps})`, potted: g.pocketedCount(), cursor: res ? res.cursor : null, trackLen: res ? res.trackLen : null, releaseSpeed: res ? +Math.hypot(res.release.vx, res.release.vy).toFixed(2) : null });
    }
  }
  return { total: level.balls.length, potted: g.pocketedCount(), won: g.won(), afterStrike, trace };
}

const summary = [];
for (const entry of locked.levels) {
  if (entry.status !== 'SOLVED') continue;
  const level = loadLevelFile(fs, `${SOLDIR}/${entry.level}.json`);
  console.log(`\n=== ${entry.level} (${level.balls.length} balls) reported strike ${entry.strike.angleDeg}deg/${entry.strike.power}, rewinds: ${(entry.rewinds ?? []).map((r) => r.holdSteps + ' steps').join(', ')} ===`);
  for (const s of SETTINGS) {
    const full = runOne(level, entry, s, true);
    const strikeOnly = runOne(level, entry, s, false);
    const ok = full.potted === full.total;
    console.log(`  ${s.name.padEnd(16)} full plan: ${full.potted}/${full.total} ${ok ? 'CLEARED' : 'FAILED '}  |  strike alone: ${strikeOnly.potted}/${strikeOnly.total}  |  per-stage pots: ${full.trace.map((t) => `${t.stage}=${t.potted}`).join(' -> ')}`);
    summary.push({ level: entry.level, setting: s.name, cleared: ok, potted: full.potted, total: full.total, strikeAlone: strikeOnly.potted, trace: full.trace });
  }
}
fs.writeFileSync('tools/verify_solver/06_replay_summary.json', JSON.stringify(summary, null, 1));
console.log('\nwrote tools/verify_solver/06_replay_summary.json');
const bySetting = {};
for (const r of summary) { bySetting[r.setting] ??= { cleared: 0, n: 0 }; bySetting[r.setting].n++; if (r.cleared) bySetting[r.setting].cleared++; }
console.log('\nCleared levels per setting:');
for (const [k, v] of Object.entries(bySetting)) console.log(`  ${k.padEnd(16)} ${v.cleared}/${v.n}`);
