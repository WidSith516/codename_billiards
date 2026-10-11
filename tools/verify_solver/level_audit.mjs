// level_audit.mjs — honesty audit of the six level files, on the solver's own model.
//
//   node tools/verify_solver/level_audit.mjs
//
// For each level: cue/ball geometry, how much the cue actually travels during the strike,
// what the strike alone pots (reported strike AND best over the solver's own 384-angle scan),
// what the rewind adds, and how similar the six levels are to each other.
import fs from 'node:fs';
import { loadLevel } from '../solver/lib/level.mjs';
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';

const locked = JSON.parse(fs.readFileSync('tools/solver/out/locked.json', 'utf8'));
const R2 = 24.4;
const lines = [];
const say = (s) => { lines.push(s); console.log(s); };

function arcLen(p) { let L = 0; for (let i = 1; i < p.length; i++) L += Math.hypot(p[i].x - p[i - 1].x, p[i].y - p[i - 1].y); return L; }

function run(level, angle, power, doRewind) {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel(level);
  g.setLevelIds(level.balls.map((b) => b.id));
  g.shoot(angle, power);
  g.settle(60);
  const path = g.cue.history.slice();
  const strikePots = g.pocketedLevel();
  const rest = { x: g.cue.x, y: g.cue.y };
  let afterRewind = strikePots;
  if (doRewind) {
    g.hold(path.length - 1);
    g.settle(60);
    afterRewind = g.pocketedLevel();
  }
  return { pathLen: path.length, arc: arcLen(path), strikePots, afterRewind, rest, minDist: 0 };
}

const rows = [];
say('=== level geometry + rewind necessity (solver model, config locked) ===');
say('');
say('level | balls | cue path samples | cue arc (px) | reported strike | strike pots | strike+1fullrewind | best strike-only over 384-angle scan | rewind hold');
say('---|---|---|---|---|---|---|---|---');
for (const e of locked.levels) {
  if (e.level === 'L0_factory') continue;
  const lvl = loadLevel(e.source);
  const rep = run(lvl, e.strike.angleDeg, e.strike.power, true);
  const des = lvl.design ? run(lvl, lvl.design.strike.angleDeg, lvl.design.strike.power, true) : null;
  const rw = (e.rewinds ?? []).map((r) => r.holdSteps).join(',');
  say(`${e.level} | ${lvl.balls.length} | ${rep.pathLen} | ${rep.arc.toFixed(1)} | ${e.strike.angleDeg}deg/${e.strike.power} | ${rep.strikePots}/${lvl.balls.length} | ${rep.afterRewind}/${lvl.balls.length} | ${e.bestNoRewind}/${e.totalBalls} | ${rw} steps`);
  rows.push({ level: e.level, balls: lvl.balls.length, cue: lvl.cue, ballPos: lvl.balls.map((b) => ({ id: b.id, x: b.x, y: b.y, note: b.note })),
    reportedPathSamples: rep.pathLen, reportedArc: +rep.arc.toFixed(1), reportedStrikePots: rep.strikePots,
    reportedAfterRewind: rep.afterRewind, bestStrikeOnly: e.bestNoRewind, designPathSamples: des?.pathLen, designAngle: lvl.design?.strike?.angleDeg,
    rewindHold: rw });
}

// ---- where does each ball sit relative to the cue at t=0?
say('\n=== ball placement vs the cue ball at t=0 (all six levels) ===');
say('level | ball | pos | dist to cue | note');
for (const r of rows) {
  for (const b of r.ballPos) {
    const d = Math.hypot(b.x - r.cue.x, b.y - r.cue.y);
    say(`${r.level} | ${b.id} | (${b.x}, ${b.y}) | ${d.toFixed(2)} | ${(b.note || '').slice(0, 70)}`);
  }
}

// ---- how similar are the levels to each other?
say('\n=== pairwise similarity: number of object balls that coincide within 3 px ===');
say('(also: same cue origin? and the same ball count)');
const ids = rows.map((r) => r.level);
say('pair | coincident balls (<=3 px) | same cue | balls');
for (let i = 0; i < rows.length; i++) {
  for (let j = i + 1; j < rows.length; j++) {
    const A = rows[i], B = rows[j];
    let n = 0;
    for (const a of A.ballPos) for (const b of B.ballPos) if (Math.hypot(a.x - b.x, a.y - b.y) <= 3) { n++; break; }
    const sameCue = Math.hypot(A.cue.x - B.cue.x, A.cue.y - B.cue.y) < 0.01;
    if (n > 0 || sameCue) say(`${A.level}-${B.level} | ${n} | ${sameCue} | ${A.balls} vs ${B.balls}`);
  }
}

// ---- how much of each level's ball set is "touching the cue path" (i.e. 0 margin)?
say('\n=== contact margin: min distance from each ball to the cue path, vs 2R = 24.4 ===');
say('level | ball | min dist to path | margin (24.4 - d) | in contact at t=0?');
for (const e of locked.levels) {
  if (e.level === 'L0_factory') continue;
  const lvl = loadLevel(e.source);
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel(lvl);
  g.setLevelIds(lvl.balls.map((b) => b.id));
  g.shoot(e.strike.angleDeg, e.strike.power);
  g.settle(60);
  const path = g.cue.history;
  for (const b of lvl.balls) {
    let best = Infinity;
    for (let i = 0; i + 1 < path.length; i++) {
      const ax = path[i].x, ay = path[i].y, bx = path[i + 1].x, by = path[i + 1].y;
      const ex = bx - ax, ey = by - ay, len2 = ex * ex + ey * ey;
      let t = len2 > 0 ? ((b.x - ax) * ex + (b.y - ay) * ey) / len2 : 0;
      if (t < 0) t = 0; else if (t > 1) t = 1;
      const d = Math.hypot(ax + ex * t - b.x, ay + ey * t - b.y);
      if (d < best) best = d;
    }
    const d0 = Math.hypot(b.x - lvl.cue.x, b.y - lvl.cue.y);
    say(`${e.level} | ${b.id} | ${best.toFixed(3)} | ${(R2 - best).toFixed(3)} | ${d0 < R2 ? `YES (d=${d0.toFixed(2)})` : 'no'}`);
  }
}
fs.writeFileSync('tools/verify_solver/18_level_audit.json', JSON.stringify(rows, null, 1));
say('\nwrote tools/verify_solver/18_level_audit.json');
fs.writeFileSync('tools/verify_solver/18_level_audit.txt', lines.join('\n') + '\n');
