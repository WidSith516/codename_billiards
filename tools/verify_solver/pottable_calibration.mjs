// pottable_calibration.mjs — is the pottability test in my_pottable.mjs too strict?
// It must report "pottable" for layouts that are KNOWN to clear:
//   * the six hand-designed levels at their reported strike angle, and
//   * a sample of rule-arm layouts the solver actually solved (from out/rand_*.json).
//   node tools/verify_solver/pottable_calibration.mjs
import fs from 'node:fs';
import { loadLevel } from '../solver/lib/level.mjs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';

const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
const R2 = 24.4, FRICTION = 640, POCKET_R = 24;

function kickPots(x, y, nx, ny, v) {
  if (v <= 0) return false;
  for (const [px, py] of POCKETS) {
    const tx = px - x, ty = py - y;
    const along = tx * nx + ty * ny;
    if (along <= 0) continue;
    const perp = Math.abs(tx * ny - ty * nx);
    if (perp > POCKET_R) continue;
    if (v >= Math.sqrt(2 * FRICTION * along)) return true;
  }
  return false;
}

/** per-ball verdict for one (level, strike): strike-potted, kick-pottable, or neither */
function assess(level, angle, power) {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel(level);
  g.setLevelIds(level.balls.map((b) => b.id));
  g.shoot(angle, power);
  g.settle(60);
  const out = [];
  for (const b of level.balls) {
    const ball = g.ball(b.id);
    if (ball.pocketed) { out.push({ id: b.id, how: 'strike' }); continue; }
    let how = null, bestRel = 0, bestPerp = null;
    const h = g.cue.history;
    for (let j = 0; j < h.length; j++) {
      const dx = ball.x - h[j].x, dy = ball.y - h[j].y;
      const d = Math.hypot(dx, dy);
      if (d >= R2 || d < 1e-6) continue;
      const nx = dx / d, ny = dy / d;
      const rel = (-h[j].vx - ball.vx) * nx + (-h[j].vy - ball.vy) * ny;
      if (rel <= 0) continue;
      if (rel > bestRel) bestRel = rel;
      if (kickPots(ball.x, ball.y, nx, ny, rel)) { how = 'kick'; break; }
    }
    out.push({ id: b.id, how, bestRel: +bestRel.toFixed(0), finalAt: [+ball.x.toFixed(1), +ball.y.toFixed(1)] });
  }
  return { potted: g.pocketedLevel(), total: level.balls.length, perBall: out };
}

console.log('=== calibration A: the six hand-designed levels at their reported strike ===');
for (const id of ['L1', 'L2', 'L3', 'L4', 'L5', 'L6']) {
  const lvl = loadLevel(`tools/solver/levels/${id}.json`);
  const locked = JSON.parse(fs.readFileSync('tools/solver/out/locked.json', 'utf8'));
  const e = locked.levels.find((x) => x.level === id);
  const r = assess(lvl, e.strike.angleDeg, e.strike.power);
  const allOk = r.perBall.every((b) => b.how);
  console.log(`  ${id}: ${r.perBall.map((b) => `b${b.id}=${b.how ?? `NO(${b.bestRel})`}`).join(' ')}  -> every ball pottable-strike-or-kick: ${allOk}`);
}

console.log('\n=== calibration B: rule-arm layouts the solver actually SOLVED ===');
const files = fs.readdirSync('tools/solver/out').filter((f) => /^rand_.*\.json$/.test(f));
const solved = [];
for (const f of files) {
  const j = JSON.parse(fs.readFileSync(`tools/solver/out/${f}`, 'utf8'));
  for (const r of j.results) if (r.mode === 'rule' && r.status === 'SOLVED') solved.push(r);
}
console.log(`  ${solved.length} solved rule-arm layouts found; testing the first 15 with the solver's own solving strike`);
let all = 0;
for (const r of solved.slice(0, 15)) {
  const lvl = { cue: { x: 292, y: 280 }, balls: r.balls.map((b, i) => ({ id: i + 1, x: b.x, y: b.y })), pockets: POCKETS };
  const res = assess(lvl, r.strike.angleDeg, r.strike.power);
  const ok = res.perBall.every((b) => b.how);
  if (ok) all++;
  console.log(`  ${r.i}: strike ${r.strike.angleDeg}deg -> ${res.perBall.map((b) => `b${b.id}=${b.how ?? `NO(${b.bestRel}@${b.finalAt})`}`).join(' ')}  all=${ok}`);
}
console.log(`\n  => pottability test agrees with the solver on ${all}/15 solved layouts`);
