// evidence.mjs — collects the machine-checkable geometric evidence for each level:
//  * per ball: distance to the FIRST path segment vs to the whole recorded path, so
//    "requires a rail bounce" is a number, not a claim;
//  * per ball: the best rewind kick available on the path (locked §7 impulse, stationary
//    target) and whether that kick can actually reach a pocket;
//  * the designed-plan result (strike + one full rewind), and the same strike with NO
//    rewind, so the rewind's contribution is quantified.
//
// Usage: node tools/solver/dev/evidence.mjs [--json out.json]

import fs from 'node:fs';
import { loadLevel, listLevels } from '../lib/level.mjs';
import { LockedGame, CONFIGS } from '../lib/locked.mjs';

const JSON_OUT = (() => { const i = process.argv.indexOf('--json'); return i < 0 ? null : process.argv[i + 1]; })();
const CFG = CONFIGS[process.argv.includes('--prototype') ? 'prototype' : 'locked'];

/** Velocity direction change detector: index of the first rail bounce. */
function bounces(path) {
  const idx = [];
  for (let i = 6; i < path.length; i++) {
    const a = path[i], b = path[i - 6];
    const va = Math.hypot(a.vx, a.vy), vb = Math.hypot(b.vx, b.vy);
    if (va < 50 || vb < 50) continue;
    const cos = (a.vx * b.vx + a.vy * b.vy) / (va * vb);
    if (cos < 0.87) { idx.push(i); i += 20; }
  }
  return idx;
}

const out = { config: CFG.name, levels: [] };
for (const f of listLevels()) {
  const lvl = loadLevel(f);
  const g = new LockedGame(CFG);
  g.placeLevel(lvl);
  g.setLevelIds(lvl.balls.map((b) => b.id));
  const start = g.remainingLevel().map((b) => ({ id: b.id, x: b.x, y: b.y }));
  g.shoot(lvl.budget?.designAngle ?? 0, 170);   // placeholder strike, replaced below
  const rec = { level: lvl.id, name: lvl.name, balls: lvl.balls.length, perBall: [] };
  out.levels.push(rec);
}
if (!JSON_OUT) console.log('(pass --json to write)');

// The strike actually used per level is the one the DESIGNER used; it is stored in the
// level note as "<angle>deg". Recover it from the note, and fall back to a scan.
function designedStrike(lvl) {
  if (lvl.design?.strike?.angleDeg !== undefined) return lvl.design.strike.angleDeg;
  const m = /(\d+(?:\.\d+)?)deg/.exec(lvl.note ?? '');
  return m ? Number(m[1]) : null;
}

const rows = [];
for (const f of listLevels()) {
  const lvl = loadLevel(f);
  if (lvl.id === 'L0_factory') continue;
  const angle = designedStrike(lvl) ?? 0;
  const g = new LockedGame(CFG);
  g.placeLevel(lvl);
  g.setLevelIds(lvl.balls.map((b) => b.id));
  const start = g.remainingLevel().map((b) => ({ id: b.id, x: b.x, y: b.y }));
  const shot = g.shoot(angle, 170);
  g.settle(45);
  const path = g.cue.history;
  const bn = bounces(path);
  const seg1End = bn.length ? bn[0] : path.length;
  const afterStrike = g.pocketedLevel();
  const strikePotted = start.map((s) => ({ id: s.id, potted: g.ball(s.id).pocketed }));
  // corridor evidence after the strike settles (the state a rewind actually acts on)
  const cor = g.corridor();
  const perBall = [];
  for (const s of start) {
    const b0 = { x: s.x, y: s.y };
    const pottedByStrike = g.ball(s.id).pocketed;
    // corridor geometry is measured from where the ball STARTED, so a ball potted by the
    // strike still gets its "was it reachable only after a bounce" number.
    let d1 = Infinity, dall = Infinity;
    for (let i = 0; i < path.length; i++) {
      const d = Math.hypot(path[i].x - b0.x, path[i].y - b0.y);
      if (d < dall) dall = d;
      if (i < seg1End) d1 = Math.min(d1, d);
    }
    const b = g.ball(s.id);
    const c = cor.find((x) => x.id === s.id);
    let bestPocket = null;
    for (const P of lvl.pockets) {
      const dist = Math.hypot(P[0] - b0.x, P[1] - b0.y);
      const need = Math.sqrt(2 * 640 * dist);
      if (!bestPocket || need < bestPocket.need) bestPocket = { pocket: P, dist: +dist.toFixed(0), need: +need.toFixed(0) };
    }
    perBall.push({
      id: s.id, pottedByStrike,
      distToSegment1: +d1.toFixed(1),
      distToWholePath: +dall.toFixed(1),
      needsRailBounce: d1 > 24.4 && dall < 24.4,
      maxRewindKick: pottedByStrike ? null : +(c?.kick ?? 0).toFixed(0),
      nearestPocket: bestPocket,
      kickReachesPocket: pottedByStrike ? null : (c?.kick ?? 0) > bestPocket.need,
      finalX: +b.x.toFixed(1), finalY: +b.y.toFixed(1),
    });
  }
  const trackLen = path.length;
  g.hold(Math.max(1, trackLen - 1));
  g.settle(45);
  const afterPlan = g.pocketedLevel();
  rows.push({
    level: lvl.id, name: lvl.name, balls: lvl.balls.length, angle, power: 170,
    v0: +shot.speed.toFixed(1), pathSamples: trackLen, railBounces: bn.length,
    strikePocketed: afterStrike, planPocketed: afterPlan, rewindKicks: g.rewindHits,
    clearedByDesignedPlan: afterPlan === lvl.balls.length, perBall,
  });
}

console.log(`=== designed-plan evidence (config ${CFG.name}) ===\n`);
for (const r of rows) {
  console.log(`${r.level}  ${r.name}`);
  console.log(`   designed strike ${r.angle}deg/${r.power} (v0=${r.v0} px/s); path ${r.pathSamples} samples, ${r.railBounces} rail bounce(s)`);
  console.log(`   strike alone pots ${r.strikePocketed}/${r.balls}; with ONE full rewind ${r.planPocketed}/${r.balls} (${r.rewindKicks} rewind impulse(s))`);
  for (const b of r.perBall) {
    console.log(`     ball ${b.id}: dist to segment#1 = ${b.distToSegment1} px, to whole path = ${b.distToWholePath} px`
      + `${b.needsRailBounce ? '  <== REACHABLE ONLY AFTER A RAIL BOUNCE' : ''}`
      + (b.pottedByStrike
        ? '  [potted by the strike]'
        : `; best rewind kick ${b.maxRewindKick} px/s vs ${b.nearestPocket.need} px/s needed for pocket(${b.nearestPocket.pocket}) at ${b.nearestPocket.dist} px -> ${b.kickReachesPocket ? 'rewind can pot it' : 'rewind CANNOT pot it'}`));
  }
  console.log('');
}

const summary = {
  config: CFG.name,
  levels: rows.map((r) => ({
    level: r.level, balls: r.balls, angle: r.angle, power: r.power,
    pathSamples: r.pathSamples, railBounces: r.railBounces,
    strikePocketed: r.strikePocketed, planPocketed: r.planPocketed,
    clearedByDesignedPlan: r.clearedByDesignedPlan,
    ballsNeedingRailBounce: r.perBall.filter((b) => b.needsRailBounce).length,
    ballsKickedByRewind: r.perBall.filter((b) => !b.pottedByStrike && b.maxRewindKick > 200).length,
    perBall: r.perBall,
  })),
};
if (JSON_OUT) { fs.writeFileSync(JSON_OUT, JSON.stringify(summary, null, 1)); console.log('wrote', JSON_OUT); }
