// make_levels.mjs — generates tools/solver/levels/L*.json using two geometric rules that
// were calibrated against the locked physics (see dev/fan.mjs):
//
// RULE A - backstop kick: the ball is potted by the REWIND, at zero energy cost to the cue.
//   B = origin + 2*d_hat + a * unit(P - origin - 2*d_hat),  a in [22.9, 24.4)
//   The rewind's last productive contact is history sample #1, because sample #0 still
//   stores the PRE-STRIKE velocity (0,0) and therefore carries no impulse. Contact at
//   sample #1 gives the ball exactly the ray direction sample#1 -> pocket, with
//   |j| = V_BREAK * cos(angle between -d_hat and that ray). Only pockets within +-60 deg
//   of the reverse strike direction get a usable kick.
//
// RULE B - cut: the ball is potted by the STRIKE.
//   B = s_j + 2R * unit(P - s_j) for a path sample s_j. The line of centres points at the
//   pocket, so the ball flies straight in with |v| = 0.9775 * (v_cue . n), and the cue
//   keeps sin(cut angle) of its speed -- which is why a chain of 55-60 deg cuts is
//   affordable and a chain of head-on hits is not.
//
// Every placement is VERIFIED by simulation against the real locked physics (the cue is
// deflected by each ball it hits, so the path changes after every insertion); a candidate
// is only accepted if every previously accepted ball is still potted.
//
// Usage: node tools/solver/dev/make_levels.mjs [--write] [--scan]

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { LockedGame, CFG_LOCKED } from '../lib/locked.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const LEVELS = path.resolve(HERE, '..', 'levels');
const WRITE = process.argv.includes('--write');
const SCAN = process.argv.includes('--scan');
const ORIGIN = { x: 292, y: 280 };
const POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
const R2 = 24.4, RB = 12.2;
const TABLE = { left: 65, right: 975, top: 63, bottom: 497 };

const unit = (x, y) => { const L = Math.hypot(x, y) || 1; return [x / L, y / L]; };

function insideTable(b) {
  return b.x > TABLE.left + RB + 3 && b.x < TABLE.right - RB - 3
    && b.y > TABLE.top + RB + 3 && b.y < TABLE.bottom - RB - 3;
}

function backstop(angleDeg, pocket, a = 23.5) {
  const d = angleDeg * Math.PI / 180;
  const s1 = [ORIGIN.x + 2 * Math.cos(d), ORIGIN.y + 2 * Math.sin(d)];
  const u = unit(pocket[0] - s1[0], pocket[1] - s1[1]);
  // kick multiplier = cos(angle between the REWIND travel direction (-d_hat) and u)
  const rev = ((angleDeg + 180) % 360) * Math.PI / 180;
  const kick = Math.cos(rev - Math.atan2(u[1], u[0]));
  const distBallPocket = Math.hypot(pocket[0] - (s1[0] + a * u[0]), pocket[1] - (s1[1] + a * u[1]));
  return {
    x: +(s1[0] + a * u[0]).toFixed(3), y: +(s1[1] + a * u[1]).toFixed(3),
    rule: `backstop kick: ball ${a} px from history sample #1 along sample#1->pocket(${pocket[0]},${pocket[1]})`,
    info: { kick: +(kick * 1258).toFixed(0), reach: +((kick * 1258) ** 2 / 1280).toFixed(0), dist: +distBallPocket.toFixed(0), pocket },
  };
}

/** Rule A selection: strongest kick first, must actually reach the pocket, no overlaps. */
function pickFan(angle, count) {
  const cands = POCKETS.map((P) => backstop(angle, P))
    .filter((b) => b.info.kick > 250 && b.info.reach > b.info.dist * 1.15)
    .sort((a, b) => b.info.kick - a.info.kick);
  const chosen = [];
  for (const c of cands) {
    if (chosen.length >= count) break;
    if (chosen.some((x) => Math.hypot(x.x - c.x, x.y - c.y) < R2 + 3)) continue;
    if (Math.hypot(c.x - ORIGIN.x, c.y - ORIGIN.y) < 21) continue;
    chosen.push(c);
  }
  return chosen;
}

function trace(angle, power, balls) {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: balls.map((b, i) => ({ id: i + 1, x: b.x, y: b.y })), pockets: POCKETS });
  g.setLevelIds(balls.map((b, i) => i + 1));
  g.shoot(angle, power);
  g.settle(45);
  return g.cue.history.map((p) => ({ x: p.x, y: p.y, vx: p.vx, vy: p.vy }));
}

function pocketedByStrike(angle, power, balls) {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: balls.map((b, i) => ({ id: i + 1, x: b.x, y: b.y })), pockets: POCKETS });
  g.setLevelIds(balls.map((b, i) => i + 1));
  g.shoot(angle, power);
  g.settle(45);
  return balls.map((b, i) => g.ball(i + 1).pocketed);
}

/** Index of the first rail bounce in the path (direction change > 30 deg between samples). */
export function firstBounce(path) {
  for (let i = 6; i < path.length; i++) {
    const a = path[i], b = path[i - 6];
    const va = Math.hypot(a.vx, a.vy), vb = Math.hypot(b.vx, b.vy);
    if (va < 50 || vb < 50) continue;
    const cos = (a.vx * b.vx + a.vy * b.vy) / (va * vb);
    if (cos < 0.87) return i;                    // > 30 deg turn
  }
  return -1;
}

function cutCandidates(path, placed, minIndex = 0) {
  const out = [];
  for (let j = Math.max(2, minIndex); j < path.length; j++) {
    const s = path[j];
    const v = Math.hypot(s.vx, s.vy);
    if (v < 100) continue;
    for (const P of POCKETS) {
      const u = unit(P[0] - s.x, P[1] - s.y);
      const B = { x: s.x + R2 * u[0], y: s.y + R2 * u[1] };
      if (!insideTable(B)) continue;
      if (Math.hypot(B.x - ORIGIN.x, B.y - ORIGIN.y) < 30) continue;
      let clash = false;
      for (const p of placed) if (Math.hypot(B.x - p.x, B.y - p.y) < R2 + 1.5) { clash = true; break; }
      if (clash) continue;
      // The cue must not sweep past this ball long before the intended sample: being within
      // 2R of the immediately preceding samples is expected (that IS the contact), so only
      // reject a genuinely earlier encounter (>10 px of path earlier).
      let early = false;
      for (let i = 0; i < j - 5; i++) {
        if (Math.hypot(path[i].x - B.x, path[i].y - B.y) < R2) { early = true; break; }
      }
      if (early) continue;
      const cosCut = (s.vx * u[0] + s.vy * u[1]) / v;
      if (cosCut <= 0.15) continue;
      const ballV = 0.9775 * v * cosCut;
      const dist = Math.hypot(P[0] - B.x, P[1] - B.y);
      const reach = (ballV * ballV) / (2 * 640);
      const margin = reach - dist;
      if (margin <= 2) continue;
      out.push({
        j, B: { x: +B.x.toFixed(3), y: +B.y.toFixed(3) }, x: +B.x.toFixed(3), y: +B.y.toFixed(3), P,
        score: margin - 0.5 * j,
        info: { v: +v.toFixed(0), cut: +(Math.acos(cosCut) * 180 / Math.PI).toFixed(1), ballV: +ballV.toFixed(0), reach: +reach.toFixed(0), dist: +dist.toFixed(0), margin: +margin.toFixed(0), pocket: P },
        rule: `cut at path[${j}] (${s.x.toFixed(0)},${s.y.toFixed(0)}) v=${v.toFixed(0)} px/s, ${(Math.acos(cosCut) * 180 / Math.PI).toFixed(0)} deg cut -> pocket(${P[0]},${P[1]})`,
      });
    }
  }
  out.sort((a, b) => b.score - a.score);
  // thin duplicates near the same sample
  const kept = [];
  for (const c of out) {
    if (kept.some((k) => Math.hypot(k.B.x - c.B.x, k.B.y - c.B.y) < 20)) continue;
    kept.push(c);
    if (kept.length >= 45) break;
  }
  return kept;
}

/** Greedy, simulation-verified placement of `nCut` cut balls on top of the fan. */
function design(angle, power, nCut, fanCount = 2, verbose = false, afterBounce = 0, seedBalls = null) {
  const balls = seedBalls ? seedBalls.map((b) => ({ ...b })) : [];
  // rule A: fan balls, strongest kicks first, only pockets the rewind can actually convert
  const cands = seedBalls ? [] : pickFan(angle, fanCount);
  for (const c of cands) balls.push(c);
  const log = [...balls];
  if (verbose && cands.length) console.log(`   fan: ${cands.map((c) => `kick ${c.info.kick} px/s -> pocket(${c.info.pocket})`).join(', ')}`);

  for (let k = 0; k < nCut; k++) {
    const path = trace(angle, power, balls);
    // `afterBounce` forces the next cut to sit past that many rail bounces, which is what
    // makes a level require the bounce to reposition the corridor.
    let minIndex = 0;
    for (let b = 0; b < afterBounce; b++) {
      const fb = firstBounce(path);
      if (fb > minIndex) minIndex = fb + 2; else break;
    }
    const cands2 = cutCandidates(path, balls, minIndex);
    if (process.argv.includes('--debug')) {
      console.log(`   [debug] cut #${k + 1}: path ${path.length} samples, firstBounce=${firstBounce(path)}, minIndex=${minIndex}, ${cands2.length} candidates;`
        + ` top: ${cands2.slice(0, 3).map((c) => `j=${c.j} B=(${c.B.x},${c.B.y}) margin=${c.info.margin}`).join(' | ')}`);
    }
    let accepted = null;
    for (const c of cands2) {
      const trial = [...balls, c];
      const potted = pocketedByStrike(angle, power, trial);
      if (process.argv.includes('--debug') && cands2.indexOf(c) < 3) {
        console.log(`      try j=${c.j} B=(${c.B.x},${c.B.y}) -> potted ${JSON.stringify(potted)}`);
      }
      if (!potted[potted.length - 1]) continue;                 // the new ball must go in
      let allPrev = true;
      // only the CUT balls must be potted by the strike; the fan balls are potted by the
      // rewind, which is checked at the end by planCheck().
      for (let i = fanCount; i < balls.length; i++) if (!potted[i]) { allPrev = false; break; }
      if (!allPrev) continue;                                   // and nothing may regress
      accepted = c; break;
    }
    if (!accepted) { if (verbose) console.log(`   cut #${k + 1}: no verified placement found, stopping`); break; }
    balls.push(accepted);
    log.push(accepted);
    if (verbose) console.log(`   cut #${k + 1}: ${accepted.rule}`);
  }
  return { balls, log };
}

/** Full plan check: designed strike, then one full rewind (all fan balls must drop). */
function planCheck(angle, power, balls) {
  const g = new LockedGame(CFG_LOCKED);
  g.placeLevel({ cue: ORIGIN, balls: balls.map((b, i) => ({ id: i + 1, x: b.x, y: b.y })), pockets: POCKETS });
  g.setLevelIds(balls.map((b, i) => i + 1));
  g.shoot(angle, power);
  g.settle(45);
  const afterStrike = g.pocketedLevel();
  const trackLen = g.cue.history.length;
  g.hold(trackLen - 1);
  g.settle(45);
  return { afterStrike, potted: g.pocketedLevel(), total: balls.length, cuePathSamples: trackLen, rewindKicks: g.rewindHits };
}

// ------------------------------------------------------------------ scan mode

if (SCAN) {
  console.log('scanning strike angles: how many balls can one corridor serve, with/after rail bounces?\n');
  console.log('  angle | fan | cuts (no bounce req) | cuts after 1 bounce | cuts after 2 bounces | plan');
  const rows = [];
  for (let a = 0; a < 360; a += 15) {
    const d0 = design(a, 170, 5, 2, false, 0);
    const d1 = design(a, 170, 5, 2, false, 1);
    const d2 = design(a, 170, 5, 2, false, 2);
    const pc = planCheck(a, 170, d0.balls);
    const fanCount = d0.balls.filter((b) => b.info.kick).length;
    rows.push({ a, n0: d0.balls.length, n1: d1.balls.length, n2: d2.balls.length, pc });
    console.log(`  ${String(a).padStart(5)} | ${String(fanCount).padStart(3)} | ${String(d0.balls.length).padStart(19)}`
      + ` | ${String(d1.balls.length).padStart(19)} | ${String(d2.balls.length).padStart(20)}`
      + ` | ${pc.afterStrike}/${pc.total} strike, ${pc.potted}/${pc.total} rewind`);
  }
  const best = rows.slice().sort((x, y) => y.pc.potted - x.pc.potted || (y.n0 + y.n1 + y.n2) - (x.n0 + x.n1 + x.n2))[0];
  console.log(`\n  best angle ${best.a}: ${best.pc.potted}/${best.pc.total}, cuts by bounce: ${best.n0}/${best.n1}/${best.n2}`);
  process.exit(0);
}

/** design with the bounce requirement passed through to the first placement batch */
function designSpec(spec) {
  const a = spec.angle, p = spec.power, fan = spec.fan ?? 2;
  const d1 = design(a, p, spec.cuts ?? 0, fan, true, spec.bounce ?? 0);
  let balls = d1.balls, log = d1.log;
  let n = spec.cutsAfter ?? 0, b = spec.bounceAfter ?? 1;
  while (n > 0) {
    const d2 = design(a, p, n, fan, true, b, balls);
    if (d2.balls.length <= balls.length) break;
    balls = d2.balls; log = d2.log; break;
  }
  return { balls, log };
}

// ------------------------------------------------------------------ L0..L6

const specs = [
  { id: 'L0_factory', name: 'h5/index.html factory rack (control)', angle: null, power: 170, cuts: 0, fan: 0,
    note: 'The layout shipped inside h5/index.html init(), byte-identical. Control: this is the level the h5harness balance report measured (median 0 balls potted per break over 279 breaks, 0 clears).' },
  { id: 'L1', name: 'Backstop fan - 2 balls, one rewind, no rail bounce', angle: 0, power: 170, fan: 2, cuts: 0, bounce: 0,
    note: 'Easiest family (rule A). Both balls sit behind the cue start, 23.5 px from history sample #1, one per left corner pocket. The strike never touches them (it moves away from them); the rewind kicks them at 1258*cos(43.5 deg) = 912 px/s straight into their pockets. No rail bounce is involved in reachability - the bounce only lengthens the corridor.',
    budget: { angleStep: 4, powers: [120, 170], releaseFractions: [0.5, 1.0], maxRewinds: 3, beam: 4, topStrikes: 4 } },
  { id: 'L2', name: 'Fan + cut shots - 4 balls, no rail bounce needed', angle: 0, power: 170, fan: 2, cuts: 2, bounce: 0,
    note: 'L1 plus cut shots (rule B): each cut ball is placed exactly 2R from a first-segment path sample, on the line of centres to a pocket, so the strike pots it with the cue keeping sin(cut angle) of its speed. Both mechanisms in one level, still no rail bounce needed for reachability.',
    budget: { angleStep: 2, powers: [110, 140, 170], releaseFractions: [0.3, 0.6, 1.0], maxRewinds: 3, beam: 5, topStrikes: 5 } },
  { id: 'L3', name: 'Rail-bounce only balls - every cut is past the first bounce', angle: 15, power: 170, fan: 2, cuts: 0, bounce: 0, cutsAfter: 2, bounceAfter: 1,
    note: 'Both cut balls are forced onto the path AFTER the first rail bounce, so the strike cannot reach them until the bounce has repositioned the corridor (REPORT.md prints segment-1 distance vs whole-path distance for every ball). The two backstop balls ride the rewind.',
    budget: { angleStep: 2, powers: [130, 170], releaseFractions: [0.3, 0.6, 1.0], maxRewinds: 4, beam: 5, topStrikes: 5 } },
  { id: 'L4', name: 'One first-segment cut plus two post-bounce cuts - 5 balls', angle: 15, power: 170, fan: 2, cuts: 1, bounce: 0, cutsAfter: 2, bounceAfter: 1,
    note: 'Both mechanisms at once: two rewind kicks, one cut on the first path segment, two cuts that only exist after a rail bounce has repositioned the corridor.',
    budget: { angleStep: 2, powers: [130, 170], releaseFractions: [0.3, 0.6, 1.0], maxRewinds: 4, beam: 6, topStrikes: 6 } },
  { id: 'L5', name: 'Multi-bounce path - cuts spread over two bounces', angle: 345, power: 170, fan: 2, cuts: 2, bounce: 0, cutsAfter: 3, bounceAfter: 1,
    note: 'Two rewind kicks plus cuts spread over the path after one and two rail bounces. The cue must be aimed so that it still has energy several segments later.',
    budget: { angleStep: 2, powers: [140, 170], releaseFractions: [0.3, 0.6, 1.0], maxRewinds: 4, beam: 6, topStrikes: 6 } },
  { id: 'L6', name: 'Hardest candidate - deepest bounce requirement', angle: 15, power: 170, fan: 2, cuts: 2, bounce: 0, cutsAfter: 4, bounceAfter: 2,
    note: 'Stress case for route C: two rewind kicks and as many cuts as the designer can verify, with the later ones forced past TWO rail bounces. If the count stops short, the limiting factor is how many balls one strike corridor can serve, not the search budget.',
    budget: { angleStep: 2, powers: [150, 170], releaseFractions: [0.25, 0.5, 0.75, 1.0], maxRewinds: 5, beam: 6, topStrikes: 6 } },
];

const results = [];
for (const spec of specs) {
  let lvl;
  if (spec.id === 'L0_factory') {
    const R = 12.2, balls = []; let id = 1;
    for (let col = 0; col < 4; col++)
      for (let row = 0; row <= col; row++)
        balls.push({ id: id++, x: +(675 + col * R * 1.82).toFixed(2), y: +(280 + (row - col / 2) * R * 2.12).toFixed(2), note: 'factory rack position' });
    lvl = { id: spec.id, name: spec.name, note: spec.note, cue: ORIGIN, balls, pockets: POCKETS, table: TABLE, budget: { angleStep: 4, powers: [120, 170], releaseFractions: [0.3, 0.6, 1.0], maxRewinds: 3, beam: 4, topStrikes: 4 } };
    results.push({ lvl, plan: planCheck(0, 170, balls), angle: 0, power: 170, log: [] });
    continue;
  }
  const d = designSpec(spec);
  lvl = {
    id: spec.id, name: spec.name, note: spec.note, cue: ORIGIN,
    balls: d.balls.map((b, i) => ({ id: i + 1, x: b.x, y: b.y, note: b.rule })),
    pockets: POCKETS, table: TABLE, budget: spec.budget,
  };
  results.push({ lvl, plan: planCheck(spec.angle, spec.power, d.balls), angle: spec.angle, power: spec.power, log: d.log });
}

console.log('\n=== designed levels: plan check (designed strike, then ONE full rewind) ===\n');
for (const r of results) {
  const problems = [];
  const all = [...r.lvl.balls.map((b, i) => ({ id: i + 1, x: b.x, y: b.y })), { id: 0, x: ORIGIN.x, y: ORIGIN.y }];
  for (let i = 0; i < all.length; i++)
    for (let j = i + 1; j < all.length; j++) {
      const d2 = Math.hypot(all[i].x - all[j].x, all[i].y - all[j].y);
      if (d2 < R2 - 0.5) problems.push(`overlap ${all[i].id}-${all[j].id} d=${d2.toFixed(2)}`);
    }
  console.log(`${r.lvl.id}  ${r.lvl.name}`);
  console.log(`   ${r.lvl.balls.length} balls, designed strike ${r.angle}deg/${r.power}`
    + ` -> strike alone ${r.plan.afterStrike}/${r.plan.total}, one full rewind ${r.plan.potted}/${r.plan.total}`
    + `, path ${r.plan.cuePathSamples} samples, rewind kicks ${r.plan.rewindKicks}`
    + `${r.plan.potted === r.plan.total ? '   <<< designed plan clears the level' : ''}`);
  if (problems.length) console.log(`   geometry notes: ${problems.join('; ')}`);
  for (const [i, b] of r.lvl.balls.entries()) console.log(`     ${i + 1}. (${b.x},${b.y}) ${b.note}`);
  console.log('');
}

if (WRITE) {
  for (const r of results) {
    fs.writeFileSync(path.join(LEVELS, r.lvl.id + '.json'), JSON.stringify(r.lvl, null, 2) + '\n');
  }
  console.log('wrote', results.map((r) => r.lvl.id + '.json').join(', '));
}
