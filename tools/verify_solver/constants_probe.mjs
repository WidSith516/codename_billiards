// constants_probe.mjs — independent measurement of the LOCKED constants, on the solver's
// own model AND on my from-spec kernel, so "the locked patches are actually applied" is
// measured rather than quoted.
//
//   node tools/verify_solver/constants_probe.mjs
import fs from 'node:fs';
import { LockedGame, CFG_LOCKED, CFG_PROTOTYPE } from '../solver/lib/locked.mjs';
import { MyGame } from './mykernel.mjs';

const ORIGIN = { x: 292, y: 280 };
const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
const L = (extra = {}) => ({ cue: ORIGIN, balls: [], pockets: POCKETS, ...extra });
const R = 12.2;
const lines = [];
const say = (s) => { lines.push(s); console.log(s); };

// ---------------------------------------------------------------- 1. rail transform
say('=== 1. rail transform: cue straight into the left rail (line of travel through y=280) ===');
say('    power 120 -> v0=888 px/s, glide 616 px: the locked model reaches the rail (214.8 px away).');
for (const [name, cfg] of [['locked   ', CFG_LOCKED], ['prototype', CFG_PROTOTYPE]]) {
  const g = new LockedGame(cfg);
  g.trackRails = true;
  g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: 900, y: 480 }] });
  g.setLevelIds([1]);
  g.ball(1).pocketed = true;
  g.shoot(180, 120);
  g.settle(10);
  const ev = g.railEvents.filter((e) => e.id === 0);
  say(`  ${name}: ${ev.length} cue rail events`);
  for (const e of ev.slice(0, 3)) {
    say(`    vIn=${e.vIn.toFixed(4)} vOut=${e.vOut.toFixed(4)}  locked(0.92vIn+120)=${(0.92 * e.vIn + 120).toFixed(4)}  proto(1.5vIn)=${(1.5 * e.vIn).toFixed(4)}`);
  }
}
{
  const g = new MyGame();
  g.place({ cue: ORIGIN, balls: [], pockets: POCKETS });
  g.shoot(180, 120);
  let prev = Math.hypot(g.cue.vx, g.cue.vy), prevSign = Math.sign(g.cue.vx);
  const ev = [];
  for (let i = 0; i < 4000; i++) {
    g.stepOnce();
    const s = Math.hypot(g.cue.vx, g.cue.vy), sg = Math.sign(g.cue.vx);
    if (sg !== prevSign && sg !== 0) { ev.push({ vIn: prev, vOut: s }); prevSign = sg; }
    prev = s;
    if (g.cue.vx === 0 && g.cue.vy === 0) break;
  }
  say(`  mine    : ${ev.length} cue rail events`);
  for (const e of ev.slice(0, 3)) say(`    vIn=${e.vIn.toFixed(4)} vOut=${e.vOut.toFixed(4)}  locked(0.92vIn+120)=${(0.92 * e.vIn + 120).toFixed(4)}`);
}

// ---------------------------------------------------------------- 2. glide distance
say('\n=== 2. glide distance, no rail contact (v0^2/1280 = locked; prototype uses exp damping) ===');
for (const [name, cfg] of [['locked', CFG_LOCKED], ['prototype', CFG_PROTOTYPE]]) {
  const out = [];
  for (const pw of [20, 40, 60, 80]) {
    const g = new LockedGame(cfg);
    g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: 900, y: 480 }] });
    g.setLevelIds([1]); g.ball(1).pocketed = true;
    g.shoot(0, pw);
    let dist = 0, px = g.cue.x, py = g.cue.y;
    for (let i = 0; i < 6000; i++) {
      g.stepOnce();
      dist += Math.hypot(g.cue.x - px, g.cue.y - py); px = g.cue.x; py = g.cue.y;
      if (g.cue.vx === 0 && g.cue.vy === 0) break;
    }
    const v0 = pw * 7.4;
    out.push(`${pw}:${dist.toFixed(1)}(th ${(v0 * v0 / 1280).toFixed(1)})`);
  }
  say(`  ${name}: ${out.join('  ')}`);
}
{
  const out = [];
  for (const pw of [20, 40, 60, 80]) {
    const g = new MyGame();
    g.place({ cue: ORIGIN, balls: [], pockets: POCKETS });
    g.shoot(0, pw);
    let dist = 0, px = g.cue.x, py = g.cue.y;
    for (let i = 0; i < 6000; i++) {
      g.stepOnce();
      dist += Math.hypot(g.cue.x - px, g.cue.y - py); px = g.cue.x; py = g.cue.y;
      if (g.cue.vx === 0 && g.cue.vy === 0) break;
    }
    const v0 = pw * 7.4;
    out.push(`${pw}:${dist.toFixed(1)}(th ${(v0 * v0 / 1280).toFixed(1)})`);
  }
  say(`  mine  : ${out.join('  ')}`);
}

// ---------------------------------------------------------------- 3. crawl + release
say('\n=== 3. rewind crawl rate and release velocity (§6 / §6.1) ===');
function crawlProbe(label, makePlaced) {
  const g = makePlaced();
  g.shoot(0, 170);
  g.settle(30);
  const n0 = g.cue.history.length;
  const at = { x: g.cue.x, y: g.cue.y };
  const storedAt = g.cue.history[n0 - 51];
  g.hold(50);
  const back = Math.hypot(g.cue.x - at.x, g.cue.y - at.y);
  const target = { x: storedAt.x, y: storedAt.y };
  const landed = Math.hypot(g.cue.x - target.x, g.cue.y - target.y);
  const relSpeed = Math.hypot(g.cue.vx, g.cue.vy);
  const storedSpeed = Math.hypot(storedAt.vx, storedAt.vy);
  const dot = (g.cue.vx * -storedAt.vx + g.cue.vy * -storedAt.vy) / (relSpeed * storedSpeed || 1);
  say(`  ${label}: history=${n0} entries; 50 held steps moved the cue ${back.toFixed(4)} px (${(back / (50 / 120)).toFixed(2)} px/s; locked 240);`
    + ` landed ${landed.toFixed(6)} px from snapshot[${n0 - 51}]; release |v|=${relSpeed.toFixed(4)} vs stored |v|=${storedSpeed.toFixed(4)}; cos(release,-stored)=${dot.toFixed(9)}`);
}
crawlProbe('theirs', () => {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: 900, y: 480 }] });
  g.setLevelIds([1]); g.ball(1).pocketed = true;
  return g;
});
crawlProbe('mine  ', () => { const g = new MyGame(); g.place({ cue: ORIGIN, balls: [{ id: 1, x: 900, y: 480 }], pockets: POCKETS }); return g; });

// ---------------------------------------------------------------- 4. pocket rule
say('\n=== 4. pocket rule: a ball INSIDE the 24 px circle but moving AWAY from the centre ===');
{
  // cue rolled to rest exactly 12 px to the right of the left-top pocket centre, then give it
  // a velocity pointing away from the pocket: locked §5 must NOT pocket it, the prototype's
  // point-in-circle rule must.
  for (const [name, cfg] of [['locked', CFG_LOCKED], ['prototype', CFG_PROTOTYPE]]) {
    const g = new LockedGame(cfg);
    g.placeLevel({ cue: { x: 65 + 12, y: 63 }, balls: [{ id: 1, x: 900, y: 480 }] });
    g.setLevelIds([1]); g.ball(1).pocketed = true;
    g.cue.vx = 200; g.cue.vy = 0;             // moving away from the pocket centre (65,63)
    g.cue.vx = 200; g.cue.vy = 0;
    g.stepOnce();
    say(`  ${name}: cue pocketed after 1 step = ${g.cue.pocketed}  (locked §5 expects false, prototype expects true)`);
  }
  const g = new MyGame();
  g.place({ cue: { x: 65 + 12, y: 63 }, balls: [], pockets: POCKETS });
  g.cue.vx = 200; g.stepOnce();
  say(`  mine  : cue pocketed after 1 step = ${g.cue.pocketed}`);
}

// ---------------------------------------------------------------- 5. rewind impulse K
say('\n=== 5. rewind impulse: j vs (v_cue - v_target).n  (locked K = 1.0, prototype 1.6x capped 1300) ===');
function impulseProbe(cfg, label) {
  const g = new LockedGame(cfg);
  // cue fires right, stops; one object ball placed 23 px to the RIGHT of a mid-path sample
  // so the crawl-back passes it and kicks it.
  g.placeLevel({ cue: ORIGIN, balls: [{ id: 1, x: 292 + 300, y: 280 }] });
  g.setLevelIds([1]);
  g.shoot(0, 60);
  g.settle(30);
  const track = g.cue.history.slice();
  // hold until the cursor reaches 0 so the cue sweeps the whole path
  const r = g.hold(track.length - 1);
  g.settle(30);
  const b = g.ball(1);
  const bs = Math.hypot(b.vx, b.vy);
  say(`  ${label}: ball1 after full rewind: |v|=${bs.toFixed(3)} at (${b.x.toFixed(2)},${b.y.toFixed(2)}) pocketed=${b.pocketed}; cue release ${r ? Math.hypot(r.release.vx, r.release.vy).toFixed(2) : 'n/a'}`);
  return bs;
}
const a1 = impulseProbe(CFG_LOCKED, 'locked  ');
const a2 = impulseProbe(CFG_PROTOTYPE, 'prototype');

// ---------------------------------------------------------------- 6. configs really differ
say('\n=== 6. same strike, same level: locked vs prototype outcome ===');
{
  const lvl = JSON.parse(fs.readFileSync('tools/solver/levels/L1.json', 'utf8'));
  const balls = lvl.balls.map((b) => ({ id: b.id, x: b.x, y: b.y, type: 'plain' }));
  for (const [name, cfg] of [['locked', CFG_LOCKED], ['prototype', CFG_PROTOTYPE]]) {
    const g = new LockedGame(cfg);
    g.placeLevel({ cue: lvl.cue, balls, pockets: POCKETS });
    g.setLevelIds(balls.map((b) => b.id));
    g.shoot(0, 170);
    g.settle(60);
    const strikePots = g.pocketedLevel();
    const stow = g.cue.history.length;
    say(`  ${name}: strike pots ${strikePots}/2, cue path = ${stow} samples (${(stow * 2).toFixed(0)} px), cue at (${g.cue.x.toFixed(1)},${g.cue.y.toFixed(1)})`);
  }
}
fs.writeFileSync('tools/verify_solver/13_constants_probe.txt', lines.join('\n') + '\n');
say('\nwrote tools/verify_solver/13_constants_probe.txt');
