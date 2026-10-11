// sweep_tolerance.mjs — how much numeric difference does each "SOLVED" level tolerate?
//
// Uses the SOLVER'S OWN model (tools/solver/lib/locked.mjs) so the answer cannot be blamed
// on my kernel. For each level it replays the reported plan while perturbing one thing at a
// time:
//   * strike angle   (+/- 0.25 ... 8 deg)   -- "can a human aim it?"
//   * strike power   (+/- 1 ... 10)
//   * cue start pos  (+/- 0.05 ... 2 px perpendicular to the strike) -- "does a sub-pixel
//                      difference between two implementations matter?"
//   * hold steps     (+/- 1 ... 40)         -- "can a human hold R that precisely?"
//
//   node tools/verify_solver/sweep_tolerance.mjs
import fs from 'node:fs';
import { loadLevel, normalizeLevel } from '../solver/lib/level.mjs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';

const locked = JSON.parse(fs.readFileSync('tools/solver/out/locked.json', 'utf8'));

function clears(level, strike, rewinds, cfgName = 'locked') {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel(level);
  const ids = level.balls.map((b) => b.id);
  g.setLevelIds(ids);
  const s = g.shoot(strike.angleDeg, strike.power);
  if (!s) return { ok: false, potted: 0, total: ids.length };
  g.settle(60);
  for (const rw of rewinds ?? []) {
    if (!g.hold(rw.holdSteps)) break;
    g.settle(60);
  }
  const potted = g.pocketedLevel();
  return { ok: potted === ids.length, potted, total: ids.length };
}

function perturbCue(level, dx, dy) {
  const l = normalizeLevel({ ...level, cue: { x: level.cue.x + dx, y: level.cue.y + dy }, balls: level.balls.map((b) => ({ ...b })) });
  l.source = level.source;
  return l;
}

const out = [];
for (const entry of locked.levels) {
  if (entry.status !== 'SOLVED') continue;
  const level = loadLevel(entry.source);
  const strike = entry.strike;
  const rewinds = entry.rewinds ?? [];
  const base = clears(level, strike, rewinds);
  const a = (strike.angleDeg * Math.PI) / 180;
  const perp = { x: -Math.sin(a), y: Math.cos(a) };   // perpendicular to the strike direction
  const row = { level: entry.level, balls: level.balls.length, baseline: base, angle: {}, power: {}, cue: {}, hold: {} };
  for (const d of [-8, -4, -2, -1, -0.5, -0.25, 0, 0.25, 0.5, 1, 2, 4, 8]) {
    row.angle[d] = clears(level, { angleDeg: strike.angleDeg + d, power: strike.power }, rewinds);
  }
  for (const d of [-10, -5, -2, -1, 0, 1, 2, 5, 10]) {
    row.power[d] = clears(level, { angleDeg: strike.angleDeg, power: strike.power + d }, rewinds);
  }
  for (const d of [-2, -1, -0.5, -0.2, -0.1, -0.05, 0, 0.05, 0.1, 0.2, 0.5, 1, 2]) {
    row.cue[d] = clears(perturbCue(level, perp.x * d, perp.y * d), strike, rewinds);
  }
  for (const d of [-40, -20, -10, -2, -1, 0, 1, 2, 10, 20, 40]) {
    const rw = rewinds.map((r) => ({ ...r, holdSteps: Math.max(1, r.holdSteps + d) }));
    row.hold[d] = clears(level, strike, rw);
  }
  out.push(row);
  const fmt = (obj, unit) => Object.entries(obj).map(([k, v]) => `${k >= 0 ? '+' : ''}${k}${unit}:${v.potted === v.total ? 'OK' : `${v.potted}/${v.total}`}`).join(' ');
  console.log(`\n=== ${entry.level} (${level.balls.length} balls) baseline ${base.potted}/${base.total} ===`);
  console.log('  angle  ' + fmt(row.angle, 'deg'));
  console.log('  power  ' + fmt(row.power, ''));
  console.log('  cue    ' + fmt(row.cue, 'px'));
  console.log('  hold   ' + fmt(row.hold, ' steps'));
}
fs.writeFileSync('tools/verify_solver/10_tolerance.json', JSON.stringify(out, null, 1));
console.log('\nwrote tools/verify_solver/10_tolerance.json');

// summary: the widest symmetric half-width that still clears
console.log('\n--- tolerance summary (widest |delta| that still clears, on THEIR model) ---');
console.log('level  angle(deg)  power  cue-offset(px)  hold(steps)');
for (const r of out) {
  const half = (obj, keys) => {
    let w = 0;
    for (const k of keys) { if (Number(k) === 0) continue; const v = obj[k]; if (v && v.potted === v.total) w = Math.max(w, Math.abs(Number(k))); else break; }
    return w;
  };
  const ak = Object.keys(r.angle).filter((k) => Number(k) < 0).map(Number).sort((x, y) => y - x).map(String);
  const pk = Object.keys(r.power).filter((k) => Number(k) < 0).map(Number).sort((x, y) => y - x).map(String);
  const ck = Object.keys(r.cue).filter((k) => Number(k) < 0).map(Number).sort((x, y) => y - x).map(String);
  const hk = Object.keys(r.hold).filter((k) => Number(k) < 0).map(Number).sort((x, y) => y - x).map(String);
  console.log(`${r.level.padEnd(6)} ${String(half(r.angle, ak)).padStart(10)} ${String(half(r.power, pk)).padStart(6)} ${String(half(r.cue, ck)).padStart(15)} ${String(half(r.hold, hk)).padStart(12)}`);
}
