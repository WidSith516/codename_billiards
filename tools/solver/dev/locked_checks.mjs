// locked_checks.mjs — CONTROL 2: does the patched loop actually implement the locked table?
//
// Every assertion below is a number from 策划案/REWIND_core_params_v1.md §3/§4/§6/§7:
//   full-power glide 1236 px / stop 1.97 s ; rail |v| -> 0.92|v| + 120, fixed point 1500
//   V_CAP 2000 and no Infinity/NaN in 60 s ; rewind crawl 240 px/s ; release = stored v
//   rewind impulse j = max(0, (v_cue - v_target).n) * 1.0
// plus a throughput measurement that sets the solver's search budgets.
//
// Usage: node tools/solver/dev/locked_checks.mjs

import { LockedGame, CFG_LOCKED, CFG_PROTOTYPE } from '../lib/locked.mjs';
import { factoryLevel } from '../lib/level.mjs';

function empty() {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: { x: 292, y: 280 }, balls: [{ id: 1, x: 900, y: 470 }] });
  g.setLevelIds([1]);
  g.ball(1).pocketed = true;                    // isolate the cue ball entirely
  return g;
}

console.log('=== CONTROL 2a: constant deceleration 640 px/s^2 (locked §3) ===');
console.log('  cue fired -x (angle 180) from (292,280); left rail 214.8 px away, so');
console.log('  powers with v0^2/1280 < 214.8 never touch a rail. Distance measured by');
console.log('  integrating |dx| per step (NOT from the 2 px-quantised history).\n');
console.log('  power   v0(px/s)   glide(px)   theory v0^2/1280   err%    stop(s)   theory');
let worstGlide = 0;
for (const power of [20, 30, 40, 50, 55]) {
  const g = empty();
  g.shoot(180, power);
  const v0 = Math.hypot(g.cue.vx, g.cue.vy);
  let dist = 0, steps = 0, px = g.cue.x, py = g.cue.y;
  for (let i = 0; i < 3000; i++) {
    g.stepOnce(); steps++;
    dist += Math.hypot(g.cue.x - px, g.cue.y - py); px = g.cue.x; py = g.cue.y;
    if (g.atRest()) break;
  }
  const th = (v0 * v0) / 1280;
  worstGlide = Math.max(worstGlide, Math.abs(dist - th) / th);
  console.log(`  ${String(power).padStart(5)} ${v0.toFixed(3).padStart(10)} ${dist.toFixed(3).padStart(11)}`
    + ` ${th.toFixed(3).padStart(18)} ${((dist - th) / th * 100).toFixed(3).padStart(7)}`
    + ` ${(steps / 120).toFixed(4).padStart(10)} ${(v0 / 640).toFixed(4).padStart(8)}`);
}
console.log(`  worst glide error ${(worstGlide * 100).toFixed(3)}% (Euler integration of a constant`);
console.log('  deceleration: exact up to the final partial step)');

console.log('\n=== CONTROL 2b: rail transform |v| -> min(0.92|v| + 120, 2000) (locked §4) ===');
console.log('  read from actual rail events inside the physics step (mid-step safe)\n');
console.log('   ball   v_at_wall      v_after    0.92*v+120    ratio    exact?');
for (const power of [60, 80, 120, 170]) {
  const g = empty();
  g.trackRails = true;
  g.shoot(180, power);
  g.settle(25);
  for (const e of g.railEvents.filter((e) => e.id === 0).slice(0, 3)) {
    const pred = 0.92 * e.vIn + 120;
    console.log(`  ${String(e.id).padStart(5)} ${e.vIn.toFixed(4).padStart(12)} ${e.vOut.toFixed(4).padStart(12)}`
      + ` ${pred.toFixed(4).padStart(12)} ${(e.vOut / e.vIn).toFixed(5).padStart(8)}`
      + `   ${Math.abs(e.vOut - pred) < 1e-9 ? 'YES' : 'no'}`);
  }
}
{
  // fixed point check: a cue that keeps bouncing should approach 120/(1-0.92)=1500, never exceed
  const g = empty();
  g.trackRails = true;
  g.shoot(180, 170);
  g.settle(60);
  const ev = g.railEvents.filter((e) => e.id === 0);
  const vOuts = ev.map((e) => e.vOut);
  console.log(`  cue, full power, 60 s: ${ev.length} rail hits; max post-rail speed = ${Math.max(...vOuts).toFixed(2)} px/s`
    + ` (fixed point 120/(1-0.92)=1500, V_CAP 2000)`);
}

console.log('\n=== CONTROL 2c: V_CAP 2000, no Infinity/NaN over 60 s (locked §4 assertion) ===');
{
  const lvl = factoryLevel();
  let maxV = 0, nan = 0, inf = 0, capHits = 0, maxRewind = 0;
  for (const angle of [0, 45, 90, 115, 170, 218, 250, 305]) {
    const gg = new LockedGame(CFG_LOCKED);
    gg.placeLevel(lvl);
    gg.setLevelIds(lvl.balls.map((b) => b.id));
    gg.shoot(angle, 170);
    for (let i = 0; i < 7200; i++) {
      gg.stepOnce();
      for (const b of gg.balls) {
        const s = Math.hypot(b.vx, b.vy);
        if (Number.isNaN(s)) nan++;
        else if (!Number.isFinite(s)) inf++;
        else if (s > maxV) maxV = s;
      }
    }
    // then rewind the whole path and release: does anything exceed the cap?
    const before = gg.pocketedLevel();
    for (let k = 0; k < 6 && !gg.won; k++) {
      const h = gg.cue.history.length;
      gg.hold(h + 5);
      gg.settle(25);
      for (const b of gg.balls) {
        const s = Math.hypot(b.vx, b.vy);
        if (Number.isNaN(s)) nan++;
        else if (!Number.isFinite(s)) inf++;
        else if (s > maxRewind) maxRewind = s;
      }
    }
    capHits += gg.capEvents;
    if (before !== gg.pocketedLevel()) { /* rewind did something */ }
  }
  console.log(`  8 full-power breaks x 60 s + full rewinds: max speed ${maxV.toFixed(3)} px/s`
    + `, max during rewind ${maxRewind.toFixed(3)}, NaN ${nan}, Infinity ${inf}, cap events ${capHits}`);
  console.log(`  PASS = ${maxV <= 2000 && maxRewind <= 2000 && nan === 0 && inf === 0}`);
}

console.log('\n=== CONTROL 2d: rewind crawl = 240 px/s, release = stored v negated (locked §6) ===');
{
  const g = empty();
  g.shoot(0, 170); g.settle(25);
  const snap = g.cue.history.slice();
  const K = 50;
  g.hold(K);
  const crawled = Math.hypot(g.cue.x - snap[snap.length - 1].x, g.cue.y - snap[snap.length - 1].y);
  const stored = snap[snap.length - 1 - K];
  const relErr = Math.hypot(g.cue.vx + stored.vx, g.cue.vy + stored.vy) / Math.hypot(stored.vx, stored.vy);
  console.log(`  history at press = ${snap.length} entries (${(snap.length * 2).toFixed(0)} px of path)`);
  console.log(`  held R ${K} steps = ${(K / 120).toFixed(3)} s -> cue moved back ${crawled.toFixed(3)} px`
    + ` = ${(crawled / (K / 120)).toFixed(2)} px/s (locked 240)`);
  console.log(`  release velocity vs -(stored v at that entry): relative error ${relErr.toExponential(3)}`);
  console.log(`  release speed ${Math.hypot(g.cue.vx, g.cue.vy).toFixed(4)} px/s vs stored entry speed ${Math.hypot(stored.vx, stored.vy).toFixed(4)}`);
  const full = ((snap.length - 1) / 120).toFixed(2);
  console.log(`  stated cost of the fixed crawl: rewinding this whole path takes ${full} s of held R`);
}

console.log('\n=== CONTROL 2e: rewind impulse j = max(0,(v_cue-v_target).n)*1.0 (locked §7) ===');
{
  // state-injection probe (same technique the h5harness probes use): after the strike has
  // settled, drop the object ball 20 px off the cue's own path, at the point the rewind
  // cursor will reach in K steps, then rewind and compare the measured dv to the formula.
  const CUE_START = { x: 292, y: 280 };
  for (const lateral of [0, 10, 20, 23.5]) {
    const g = empty();
    g.shoot(0, 170);
    g.settle(25);
    const track = g.cue.history;
    const K = 60;
    const tgt = track[track.length - 1 - K];
    // place the ball laterally offset from the point the cue is about to arrive at
    const b = g.ball(1);
    b.pocketed = false; b.vx = 0; b.vy = 0;
    b.x = tgt.x; b.y = tgt.y + lateral;
    g.startRewind();
    let measured = null, predicted = null, contactN = null, vCue = null;
    for (let i = 0; i < K + 40; i++) {
      const pv = { vx: g.cue.vx, vy: g.cue.vy };
      const bv = { vx: b.vx, vy: b.vy };
      g.stepOnce();
      if (!measured && (b.vx !== bv.vx || b.vy !== bv.vy)) {
        const nx = b.x - g.cue.x, ny = b.y - g.cue.y, L = Math.hypot(nx, ny);
        contactN = L;
        const ux = nx / L, uy = ny / L;
        const rel = (pv.vx - bv.vx) * ux + (pv.vy - bv.vy) * uy;
        predicted = Math.max(0, rel);
        vCue = Math.hypot(pv.vx, pv.vy);
        measured = Math.hypot(b.vx, b.vy);
      }
    }
    const ratio = measured / predicted;
    console.log(`  lateral offset ${String(lateral).padStart(5)} px:`
      + ` |v_target| = ${measured.toFixed(3)} px/s, j = ${predicted.toFixed(3)},`
      + ` ratio K = ${ratio.toFixed(6)}${Math.abs(ratio - 1) < 0.02 ? '  OK' : '  <-- clamp/thin hit'}`);
    if (lateral === 20) console.log(`      (cue approach speed at contact ${vCue.toFixed(2)} px/s, centre distance ${contactN.toFixed(2)} px)`);
  }
}

console.log('\n=== THROUGHPUT (sets the search budgets) ===');
{
  const lvl = factoryLevel();
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel(lvl); g.setLevelIds(lvl.balls.map((b) => b.id));
  const N = 40;
  const t0 = Date.now();
  let steps = 0;
  for (let i = 0; i < N; i++) {
    g.placeLevel(lvl); g.setLevelIds(lvl.balls.map((b) => b.id));
    g.shoot((i * 7) % 360, 170);
    const r = g.settle(25);
    steps += r.steps;
  }
  const ms = Date.now() - t0;
  console.log(`  locked config, 11-ball rack, strike+settle: ${N} shots in ${ms} ms`);
  console.log(`  -> ${(ms / N).toFixed(2)} ms per strike-settle, ${steps} physics steps total, `
    + `${(steps / (ms / 1000)).toFixed(0)} steps/s, ${(N / (ms / 1000)).toFixed(1)} sims/s (single process)`);

  const g2 = new LockedGame(CFG_PROTOTYPE);
  g2.placeLevel(lvl); g2.setLevelIds(lvl.balls.map((b) => b.id));
  const t1 = Date.now();
  for (let i = 0; i < N; i++) {
    g2.placeLevel(lvl); g2.setLevelIds(lvl.balls.map((b) => b.id));
    g2.shoot((i * 7) % 360, 170);
    g2.settle(25);
  }
  console.log(`  prototype config (substep=1): ${((Date.now() - t1) / N).toFixed(2)} ms per strike-settle`);
}
