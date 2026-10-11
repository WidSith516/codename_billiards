// adaptive_sweep.mjs — fairness fix for the tolerance sweeps: when the strike angle (or the
// cue start) is perturbed, the player would also re-choose the rewind. So at every perturbed
// strike we try EVERY legal rewind the level's own history offers:
//   - the full rewind (hold trackLen-1, ends at the origin, v=0), and
//   - every partial hold 1..trackLen-1 when the track is short (L5/L6: 6 samples),
// and we report whether ANY of them clears. This removes the "your hold was 1 step short"
// objection, so what is left is genuine strike geometry.
//
//   node tools/verify_solver/adaptive_sweep.mjs
import fs from 'node:fs';
import { loadLevel, normalizeLevel } from '../solver/lib/level.mjs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';

const locked = JSON.parse(fs.readFileSync('tools/solver/out/locked.json', 'utf8'));

function baseRun(level, angleDeg, power) {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel(level);
  g.setLevelIds(level.balls.map((b) => b.id));
  g.shoot(angleDeg, power);
  g.settle(60);
  return g;
}
/** true if ANY rewind choice clears after this strike */
function clearsAnyRewind(level, angleDeg, power, { maxHolds = 12 } = {}) {
  const g0 = baseRun(level, angleDeg, power);
  if (g0.pocketedLevel() === level.balls.length) return { ok: true, by: 'strike alone' };
  const trackLen = g0.cue.history.length;
  if (trackLen < 2) return { ok: false, trackLen };
  const snap = g0.snapshot();
  const holds = trackLen - 1 <= maxHolds ? [...Array(trackLen - 1).keys()].map((i) => i + 1) : [trackLen - 1];
  for (const k of holds) {
    const g = new LockedGame(CFG_LOCKED);
    g.setLevelIds(level.balls.map((b) => b.id));   // MUST match the level, or pocketedLevel() counts the 6 sentinels
    g.restore(snap);
    g.hold(k);
    g.settle(60);
    if (g.pocketedLevel() === level.balls.length) return { ok: true, by: `hold ${k}/${trackLen - 1}`, trackLen };
  }
  return { ok: false, trackLen, best: (() => { const g = new LockedGame(CFG_LOCKED); g.setLevelIds(level.balls.map((b) => b.id)); g.restore(snap); g.hold(trackLen - 1); g.settle(60); return g.pocketedLevel(); })() };
}

const seq = (from, to, step) => { const a = []; for (let v = from; v <= to + 1e-9; v += step) a.push(+v.toFixed(6)); return a; };
const out = [];
for (const entry of locked.levels) {
  if (entry.status !== 'SOLVED') continue;
  const level = loadLevel(entry.source);
  const strike = entry.strike;
  const a = (strike.angleDeg * Math.PI) / 180;
  const perp = { x: -Math.sin(a), y: Math.cos(a) };
  const angleOK = [];
  for (const d of seq(-1.5, 1.5, 0.05)) angleOK.push([d, clearsAnyRewind(level, strike.angleDeg + d, strike.power).ok]);
  const offOK = [];
  for (const d of seq(-0.5, 0.5, 0.01)) {
    const l2 = normalizeLevel({ ...level, cue: { x: level.cue.x + perp.x * d, y: level.cue.y + perp.y * d }, balls: level.balls.map((b) => ({ ...b })) });
    offOK.push([d, clearsAnyRewind(l2, strike.angleDeg, strike.power).ok]);
  }
  const win = (arr) => { const ok = arr.filter(([, v]) => v).map(([k]) => k); return ok.length ? { min: ok[0], max: ok[ok.length - 1], n: ok.length, total: arr.length } : { n: 0, total: arr.length }; };
  const aw = win(angleOK), ow = win(offOK);
  out.push({ level: entry.level, angle: aw, offset: ow, angleOK, offOK });
  console.log(`\n${entry.level}: ANY-rewind clearing windows`);
  console.log(`  angle : [${aw.n ? `${aw.min} .. ${aw.max}` : 'none'}]  (${aw.n}/${aw.total} samples)`);
  console.log(`          ${angleOK.map(([k, v]) => `${k >= 0 ? '+' : ''}${k.toFixed(2)}${v ? 'Y' : '.'}`).join(' ')}`);
  console.log(`  cue   : [${ow.n ? `${ow.min} .. ${ow.max}` : 'none'}]  (${ow.n}/${ow.total} samples)`);
  console.log(`          ${offOK.map(([k, v]) => `${k >= 0 ? '+' : ''}${k.toFixed(2)}${v ? 'Y' : '.'}`).join(' ')}`);
}
fs.writeFileSync('tools/verify_solver/20_adaptive_sweep.json', JSON.stringify(out, null, 1));
console.log('\nwrote tools/verify_solver/20_adaptive_sweep.json');
