// fan.mjs — calibrate the "backstop kick": the rewind's contact happens at history sample #1
// (2 px along the strike direction), because sample #0 stores the PRE-STRIKE velocity (0,0)
// and therefore carries no impulse. So the design rule is:
//
//     B = origin + 2*d_hat + a * unit(P - origin - 2*d_hat),   a in [22.9, 24.4)
//
// i.e. put the object ball `a` px from the SECOND history sample, on the ray from that
// sample to the target pocket. Then contact at sample #1 gives the ball exactly the pocket
// direction and |j| = V * cos(angle between -d_hat and that ray).
//
// Usage: node tools/solver/dev/fan.mjs

import { LockedGame, CFG_LOCKED } from '../lib/locked.mjs';

const ORIGIN = { x: 292, y: 280 };
const PK = [[65, 63], [65, 497]];
const ALL_PK = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];

export function fanBalls(angleDeg, a) {
  const d = angleDeg * Math.PI / 180;
  const dhat = [Math.cos(d), Math.sin(d)];
  const s1 = [ORIGIN.x + 2 * dhat[0], ORIGIN.y + 2 * dhat[1]];
  const out = [];
  for (const [i, p] of PK.entries()) {
    const L = Math.hypot(p[0] - s1[0], p[1] - s1[1]);
    const u = [(p[0] - s1[0]) / L, (p[1] - s1[1]) / L];
    out.push({ id: i + 1, x: s1[0] + a * u[0], y: s1[1] + a * u[1] });
  }
  return out;
}

function run(angle, power, a) {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: fanBalls(angle, a), pockets: ALL_PK });
  g.setLevelIds([1, 2]);
  g.shoot(angle, power);
  g.settle(40);
  const trackLen = g.cue.history.length;
  g.hold(trackLen - 1);
  g.settle(40);
  return { potted: g.pocketedLevel(), kicks: g.rewindHits,
    balls: [1, 2].map((id) => ({ id, x: +g.ball(id).x.toFixed(1), y: +g.ball(id).y.toFixed(1), p: g.ball(id).pocketed })) };
}

console.log('== backstop kick: rule B = origin + 2*d_hat + a*unit(P - origin - 2*d_hat) ==');
console.log('   strike power 170, then ONE full rewind. Table = balls pocketed / 2\n');
const as = [22.6, 22.9, 23.2, 23.5, 23.8, 24.1, 24.4, 24.7];
const angles = [];
for (let a = -8; a <= 8; a += 1) angles.push(a);
console.log('   a   |' + angles.map((x) => String(x).padStart(4)).join(''));
for (const a of as) {
  let line = `${a.toFixed(1).padStart(5)} |`;
  for (const ang of angles) line += String(run(ang, 170, a).potted).padStart(4);
  console.log(line);
}

console.log('\n== detail at a=23.5, angle 0 ==');
const r = run(0, 170, 23.5);
console.log('  pocketed', r.potted, 'kicks', r.kicks, JSON.stringify(r.balls));
{
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: fanBalls(0, 23.5), pockets: ALL_PK });
  g.setLevelIds([1, 2]);
  const b = g.ball(1);
  console.log(`  ball1 at (${b.x.toFixed(2)},${b.y.toFixed(2)}): ${Math.hypot(b.x - 292, b.y - 280).toFixed(2)} px from cue start`);
  g.shoot(0, 170); g.settle(40);
  const h = g.cue.history;
  for (let j = 0; j <= 3; j++) {
    const s = h[j];
    const d = Math.hypot(s.x - b.x, s.y - b.y);
    const n = [(b.x - s.x) / d, (b.y - s.y) / d];
    const rel = (-s.vx) * n[0] + (-s.vy) * n[1];
    console.log(`   track[${j}] d=${d.toFixed(2)}${d < 24.4 ? ' CONTACT' : '        '}`
      + ` storedV=(${s.vx.toFixed(0)},${s.vy.toFixed(0)}) kickDir=${(Math.atan2(n[1], n[0]) * 180 / Math.PI).toFixed(1)} deg rel=${rel.toFixed(0)} px/s`);
  }
  const pocketDir = Math.atan2(63 - b.y, 65 - b.x) * 180 / Math.PI;
  console.log(`  pocket direction from ball = ${pocketDir.toFixed(1)} deg -> aim error closes to ~0 at a=23.5`);
}
