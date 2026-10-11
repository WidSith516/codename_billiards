// fine_sweep.mjs — precise, fine-grained tolerance around the reported solution, on the
// solver's own model: the contiguous clearing window in strike angle and in cue-start offset.
//
//   node tools/verify_solver/fine_sweep.mjs
import fs from 'node:fs';
import { loadLevel, normalizeLevel } from '../solver/lib/level.mjs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';

const locked = JSON.parse(fs.readFileSync('tools/solver/out/locked.json', 'utf8'));

function clears(level, strike, rewinds) {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel(level);
  const ids = level.balls.map((b) => b.id);
  g.setLevelIds(ids);
  g.shoot(strike.angleDeg, strike.power);
  g.settle(60);
  for (const rw of rewinds ?? []) { if (!g.hold(rw.holdSteps)) break; g.settle(60); }
  return g.pocketedLevel() === ids.length;
}
const seq = (from, to, step) => { const a = []; for (let v = from; v <= to + 1e-9; v += step) a.push(+v.toFixed(6)); return a; };

const out = [];
for (const entry of locked.levels) {
  if (entry.status !== 'SOLVED') continue;
  const level = loadLevel(entry.source);
  const strike = entry.strike, rewinds = entry.rewinds ?? [];
  const a = (strike.angleDeg * Math.PI) / 180;
  const perp = { x: -Math.sin(a), y: Math.cos(a) };
  // angle window: scan outward until 3 consecutive failures on each side
  const angleOK = [], offsetOK = [];
  for (const d of seq(-1.5, 1.5, 0.05)) angleOK.push([d, clears(level, { angleDeg: strike.angleDeg + d, power: strike.power }, rewinds)]);
  for (const d of seq(-0.5, 0.5, 0.01)) {
    const l2 = normalizeLevel({ ...level, cue: { x: level.cue.x + perp.x * d, y: level.cue.y + perp.y * d }, balls: level.balls.map((b) => ({ ...b })) });
    offsetOK.push([d, clears(l2, strike, rewinds)]);
  }
  const window = (arr) => {
    const ok = arr.filter(([, v]) => v).map(([k]) => k);
    if (!ok.length) return { ok: false };
    return { ok: true, min: Math.min(...ok), max: Math.max(...ok), n: ok.length, total: arr.length, list: ok };
  };
  const aw = window(angleOK), ow = window(offsetOK);
  out.push({ level: entry.level, angleWindow: aw, offsetWindow: ow });
  console.log(`\n${entry.level} (${level.balls.length} balls, strike ${strike.angleDeg}deg/${strike.power})`);
  console.log(`  angle  : clearing deltas ${aw.ok ? `[${aw.list[0]} .. ${aw.list[aw.list.length - 1]}]` : 'none'} (${aw.ok ? aw.list.length : 0}/${angleOK.length} samples in -1.5..+1.5 deg)`);
  console.log(`           ${angleOK.map(([k, v]) => `${k >= 0 ? '+' : ''}${k.toFixed(2)}${v ? 'Y' : '.'}`).join(' ')}`);
  console.log(`  cue-off: clearing offsets ${ow.ok ? `[${ow.list[0]} .. ${ow.list[ow.list.length - 1]}]` : 'none'} (${ow.ok ? ow.list.length : 0}/${offsetOK.length} samples in -0.5..+0.5 px)`);
  console.log(`           ${offsetOK.map(([k, v]) => `${k >= 0 ? '+' : ''}${k.toFixed(2)}${v ? 'Y' : '.'}`).join(' ')}`);
}
fs.writeFileSync('tools/verify_solver/19_fine_sweep.json', JSON.stringify(out, null, 1));
console.log('\nwrote tools/verify_solver/19_fine_sweep.json');
