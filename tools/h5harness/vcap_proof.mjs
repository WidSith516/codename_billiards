// Causal proof that the headline break results are an artifact of the rail-boost
// divergence bug, not real gameplay.
//
// Method: leave ALL prototype logic untouched, but clamp speed to a physically sane
// ceiling every step (v <= CAP). If the multi-ball "good" breaks collapse under the cap,
// they were produced by the divergence bug. If they survive, they are genuine.
//
// Usage: node vcap_proof.mjs
import { Game } from './sim.mjs';

const CAP = Number(process.env.CAP ?? 2000);   // below the 2070/2928 tunnelling bounds
const SETTLE = 25;

function run(angleDeg, power, cap) {
  const g = new Game({ seed: 0x5eed });
  g.reset();
  g.shoot(angleDeg, power);
  const dt = g.consts.STEP;
  const n = Math.round(SETTLE / dt);
  let maxSpeed = 0, clamped = 0;
  for (let i = 0; i < n; i++) {
    g.step(1);
    for (const b of g.balls) {
      const s = Math.hypot(b.vx, b.vy);
      if (!Number.isFinite(s)) { maxSpeed = Infinity; continue; }
      if (s > maxSpeed) maxSpeed = s;
      if (s > cap) {
        const k = cap / s;
        b.vx *= k; b.vy *= k; clamped++;
      }
    }
  }
  return { pocketed: g.pocketedCount(), maxSpeed: Number.isFinite(maxSpeed) ? Math.round(maxSpeed) : 'Infinity', clamped };
}

const INTERESTING = [218, 228, 132, 206, 110, 128, 104, 256, 204, 154];

console.log(`=== same angles, UNCAPPED vs speed-capped at ${CAP} px/s ===`);
console.log(`  ${'ang'.padStart(4)} ${'uncapped'.padStart(9)} ${'capped'.padStart(7)} ${'v_capped'.padStart(9)} ${'clamps'.padStart(7)}  verdict`);
for (const ang of INTERESTING) {
  const u = run(ang, 170, Infinity);
  const c = run(ang, 170, CAP);
  const verdict = c.pocketed === u.pocketed ? 'survives the cap -> genuine'
    : `COLLAPSES ${u.pocketed}->${c.pocketed} -> bug artifact`;
  console.log(`  ${String(ang).padStart(4)} ${String(u.pocketed).padStart(9)} ${String(c.pocketed).padStart(7)}`
    + ` ${String(c.maxSpeed).padStart(9)} ${String(c.clamped).padStart(7)}  ${verdict}`);
}

console.log('');
console.log(`=== full sweep, capped at ${CAP} px/s ===`);
const rows = [];
for (let ang = 0; ang < 360; ang += 2) rows.push({ ang, ...run(ang, 170, CAP) });
const d = new Map();
for (const r of rows) d.set(r.pocketed, (d.get(r.pocketed) ?? 0) + 1);
const best = Math.max(...rows.map((r) => r.pocketed));
const bestAngles = rows.filter((r) => r.pocketed === best).map((r) => r.ang);
console.log(`  best = ${best}/10  at angles ${bestAngles.join(', ')}`);
console.log(`  distribution ${[...d.entries()].sort((a, b) => a[0] - b[0]).map(([k, v]) => `${k}:${v}`).join('  ')}`);
console.log(`  angles reaching >=1: ${rows.filter((r) => r.pocketed >= 1).length}/180`);
console.log(`  angles reaching >=2: ${rows.filter((r) => r.pocketed >= 2).length}/180`);
console.log(`  total speed clamps applied: ${rows.reduce((s, r) => s + r.clamped, 0)}`);
console.log('');
console.log('HONEST_ATTAINABLE_WITH_ONE_STRIKE_NO_REWIND = ' + best + '/10');
