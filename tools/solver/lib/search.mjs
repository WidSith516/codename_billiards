// search.mjs — the level solvability solver.
//
// Question answered: does there exist a legal input sequence (ONE strike + any number of
// REWIND operations, rules exactly as frozen in h5/index.html) that pockets every object
// ball — and if so, what is one example?
//
// Search structure (two stages, coarse to fine):
//   Stage 1  strike scan: angle x power grid. Every candidate is one shoot + settle.
//            Scored by balls pocketed, then by "how reachable the survivors are".
//   Stage 2  angle refinement around the promising strikes (finer angular step).
//   Stage 3  rewind beam search on the top-K strikes. A node is a state where the cue is
//            at rest; an action is (press R, hold k steps, release, settle). The release
//            point is parameterised as a FRACTION of the cue's recorded path, because the
//            holding time in steps is meaningless across paths of different length.
//
// The rewind count is NOT limited by the game (h5/index.html has no rewind budget), so
// UNSOLVED always means "not found inside the searched budget", never "impossible".

import { LockedGame, CONFIGS } from './locked.mjs';
import { DEFAULT_BUDGET } from './level.mjs';

// ------------------------------------------------------------------ scoring

function potDistance(ball, level) {
  let best = Infinity;
  for (const [px, py] of level.pockets) {
    const d = Math.hypot(px - ball.x, py - ball.y);
    if (d < best) best = d;
  }
  return best;
}

/**
 * Heuristic value of a position. Pocketed balls dominate; among equal counts we prefer
 * survivors that sit in (or near) the rewritable corridor and close to a pocket, because
 * that is exactly what the rewind sweep needs to pot them.
 */
function evaluate(g, level, n) {
  const pocketed = g.pocketedLevel();
  const cor = g.corridor();
  let near = 0, nearish = 0, distSum = 0, potential = 0;
  for (const c of cor) {
    if (c.inCorridor) near++;
    if (c.dist < 60) nearish++;
    distSum += Math.min(c.dist, 300);
    potential += Math.min(c.kick, 1300);
  }
  const rem = g.remainingLevel();
  let potSum = 0;
  for (const b of rem) potSum += Math.min(potDistance(b, level), 600);
  // The `potential` term is what makes the search able to prefer a strike that pots NOTHING
  // but leaves every survivor inside the rewind corridor -- the shape route C actually wins
  // with -- over a strike that pots one ball and leaves the rest unreachable.
  const score = pocketed * 1000 + potential * 2 + near * 20 + nearish * 5
    - distSum / Math.max(1, cor.length) * 0.5
    - potSum / Math.max(1, rem.length) * 0.05;
  return { pocketed, corridor: cor, near, nearish, distSum, potential, potSum, score, remaining: rem.length };
}

// ------------------------------------------------------------------ the solver

export function solveLevel(level, opts = {}) {
  const cfgName = opts.cfgName ?? 'locked';
  const cfg = CONFIGS[cfgName];
  if (!cfg) throw new Error('unknown config ' + cfgName);
  const b = { ...DEFAULT_BUDGET, ...(level.budget ?? {}), ...(opts.budget ?? {}) };
  const t0 = Date.now();
  const g = new LockedGame(cfg);
  g.placeLevel(level);
  const ids = level.balls.map((x) => x.id);
  g.setLevelIds(ids);
  const n = ids.length;

  const stats = {
    config: cfgName, level: level.id, balls: n,
    anglesSearched: 0, strikesTried: 0, rewindSims: 0, sims: 0,
    bestNoRewind: 0, bestAny: 0, depthUsed: 0, ms: 0,
  };
  const base = g.snapshot();

  function strike(angle, power, settleMax) {
    g.restore(base);
    const s = g.shoot(angle, power);
    if (!s) return null;
    g.settle(settleMax);
    stats.sims++;
    const ev = evaluate(g, level, n);
    return { angle, power, v0: s.speed, ...ev, snap: g.snapshot() };
  }

  // ---- Stage 1: coarse strike scan --------------------------------------
  const coarse = [];
  for (let a = b.angleStart; a < b.angleEnd - 1e-9; a += b.angleStep) {
    stats.anglesSearched++;
    for (const pw of b.powers) {
      const c = strike(a, pw, b.settleMaxSeconds);
      if (!c) continue;
      stats.strikesTried++;
      if (c.pocketed > stats.bestNoRewind) stats.bestNoRewind = c.pocketed;
      if (c.pocketed === n) {
        return finish({ status: 'SOLVED', strike: { angleDeg: a, power: pw }, rewinds: [], cand: c, stats, t0, g, level });
      }
      coarse.push(c);
    }
  }
  coarse.sort((x, y) => y.score - x.score || y.pocketed - x.pocketed);
  stats.bestAny = Math.max(stats.bestAny, coarse.length ? coarse[0].pocketed : 0);
  // keep the geometry of the most promising strikes for the authoring statistics: the best
  // by potential (what the rewind can still kick) and the best by score.
  stats.topCandidates = [
    ...coarse.slice().sort((x, y) => y.potential - x.potential).slice(0, 6),
    ...coarse.slice(0, 3),
  ];

  // ---- Stage 2: refine the angle around the best coarse strikes ----------
  const refined = [];
  const topForRefine = coarse.slice(0, Math.max(1, Math.min(3, b.topStrikes)));
  for (const c of topForRefine) {
    refined.push(c);
    const deltas = [];
    for (let d = -b.angleStep; d <= b.angleStep + 1e-9; d += b.refineStep) {
      if (Math.abs(d) < 1e-9) continue;
      deltas.push(d);
    }
    for (const d of deltas) {
      stats.anglesSearched++;
      const cc = strike(c.angle + d, c.power, b.settleMaxSeconds);
      if (!cc) continue;
      stats.strikesTried++;
      if (cc.pocketed === n) {
        return finish({ status: 'SOLVED', strike: { angleDeg: c.angle + d, power: cc.power }, rewinds: [], cand: cc, stats, t0, g, level });
      }
      if (cc.pocketed > stats.bestNoRewind) stats.bestNoRewind = cc.pocketed;
      refined.push(cc);
    }
  }
  // power refinement on the single best strike
  if (coarse.length) {
    const best = coarse[0];
    for (const dp of [-30, -15, 15, 30]) {
      const pw = Math.max(12, Math.min(170, best.power + dp));
      const cc = strike(best.angle, pw, b.settleMaxSeconds);
      if (!cc) continue;
      stats.strikesTried++;
      if (cc.pocketed === n) {
        return finish({ status: 'SOLVED', strike: { angleDeg: best.angle, power: pw }, rewinds: [], cand: cc, stats, t0, g, level });
      }
      refined.push(cc);
    }
  }
  refined.sort((x, y) => y.score - x.score || y.pocketed - x.pocketed);

  // ---- Stage 3: rewind beam search on the top-K strikes ------------------
  // Seed selection matters: a strike that pots ONE ball already scores 1000, which buries
  // the strike that pots none but leaves every survivor inside the rewritable corridor --
  // and that second strike is the one route C can actually finish with. So we take the
  // union of the best-by-score, best-by-pocketed and best-by-corridor strikes.
  const pick = (arr, n, key) => arr.slice().sort((x, y) => key(y) - key(x)).slice(0, n);
  const seedList = [];
  const pushSeeds = (list) => { for (const c of list) if (!seedList.some((s) => s.angle === c.angle && s.power === c.power)) seedList.push(c); };
  // pool = the whole coarse scan plus the refined strikes (the refined set alone is only the
  // neighbourhood of the best-by-score strikes, which is NOT where a rewind-solvable strike
  // necessarily lives -- that bug made the solver miss its own easiest level, L1).
  const pool = [...coarse, ...refined];
  pushSeeds(pick(pool, 4, (c) => c.pocketed));
  pushSeeds(pick(pool, b.topStrikes, (c) => c.score));
  pushSeeds(pick(pool, Math.ceil(b.topStrikes / 2), (c) => c.near * 2 + c.nearish));
  pushSeeds(pick(pool, Math.ceil(b.topStrikes / 2) + 2, (c) => c.potential));
  const seeds = seedList.slice(0, b.topStrikes + 6);
  if (opts.debug) {
    console.error('[solver] seeds: ' + seeds.map((s) => `${s.angle}/${s.power} potted=${s.pocketed} near=${s.near} nearish=${s.nearish} score=${s.score.toFixed(0)}`).join('  |  '));
  }

  let bestTrace = null;
  for (const seed of seeds) {
    const res = beamFrom(g, seed, b, n, level, stats);
    if (res && res.pocketed === n) {
      stats.bestAny = n;                    // the winning plan clears the level
      stats.depthUsed = res.plan.length;
      return finish({ status: 'SOLVED', strike: { angleDeg: seed.angle, power: seed.power }, rewinds: res.plan, cand: seed, stats, t0, g, level });
    }
    if (res && (!bestTrace || res.pocketed > bestTrace.pocketed)) bestTrace = { ...res, angle: seed.angle, power: seed.power };
    if (res && res.pocketed > stats.bestAny) stats.bestAny = res.pocketed;
  }

  return finish({
    status: 'UNSOLVED', strike: null, rewinds: [], best: bestTrace,
    cand: seeds[0] ?? null, stats, t0, g, level,
  });
}

function beamFrom(g, seed, b, n, level, stats) {
  let beam = [{ snap: seed.snap, plan: [], pocketed: seed.pocketed, score: seed.score, depth: 0 }];
  let best = { pocketed: seed.pocketed, plan: [] };
  for (let depth = 1; depth <= b.maxRewinds; depth++) {
    const children = [];
    for (const node of beam) {
      g.restore(node.snap);
      const trackLen = g.cue.history.length;
      if (trackLen < 2) continue;
      for (const f of b.releaseFractions) {
        g.restore(node.snap);
        const k = Math.max(1, Math.min(trackLen - 1, Math.round(f * (trackLen - 1))));
        const r = g.hold(k);
        g.settle(b.settleMaxSeconds);
        stats.sims++; stats.rewindSims++;
        const ev = evaluate(g, level, n);
        const plan = [...node.plan, {
          releaseFraction: f, holdSteps: k,
          releaseIndex: r.cursor, rewoundToOrigin: r.rewoundToOrigin,
          releaseSpeed: Math.hypot(r.release.vx, r.release.vy),
        }];
        if (ev.pocketed > best.pocketed) best = { pocketed: ev.pocketed, plan };
        if (ev.pocketed === n) { stats.depthUsed = depth; return { pocketed: ev.pocketed, plan }; }
        children.push({ snap: g.snapshot(), plan, pocketed: ev.pocketed, score: ev.score, depth });
      }
    }
    if (!children.length) break;
    children.sort((x, y) => y.pocketed - x.pocketed || y.score - x.score);
    beam = children.slice(0, b.beam);
    stats.depthUsed = depth;
    if (best.pocketed > beam[0].pocketed) break;   // no further progress found at this depth
  }
  return best;
}

function finish(o) {
  o.stats.ms = Date.now() - o.t0;
  o.stats.bestAny = Math.max(o.stats.bestAny || 0, o.best?.pocketed ?? 0, o.cand ? o.cand.pocketed : 0);
  const topC = (o.stats.topCandidates ?? []);
  const bestPot = topC.slice().sort((a, b) => b.potential - a.potential)[0] ?? null;
  return {
    level: o.level.id,
    config: o.stats.config,
    status: o.status,
    strike: o.strike,
    rewinds: o.rewinds,
    pocketed: o.cand ? o.cand.pocketed : 0,
    totalBalls: o.stats.balls,
    bestNoRewind: o.stats.bestNoRewind,
    bestAny: o.stats.bestAny,
    best: o.best ?? null,
    maxPotential: bestPot ? +bestPot.potential.toFixed(0) : 0,
    maxCorridorBalls: topC.length ? Math.max(...topC.map((c) => c.near)) : 0,
    corridorAtBest: o.cand ? o.cand.corridor.map((c) => ({ id: c.id, dist: +c.dist.toFixed(2), kick: +(c.kick ?? 0).toFixed(0), inCorridor: c.inCorridor })) : [],
    // geometry summary of the most promising strikes, used by the level-authoring statistics
    topCandidates: (o.stats.topCandidates ?? []).map((c) => ({
      angle: c.angle, power: c.power, pocketed: c.pocketed, near: c.near,
      potential: +c.potential.toFixed(0), score: +c.score.toFixed(0),
      corridor: c.corridor.map((x) => ({ id: x.id, dist: +x.dist.toFixed(1), kick: +x.kick.toFixed(0), in: x.inCorridor })),
    })),
    stats: o.stats,
  };
}

// ------------------------------------------------------------------ replay

/** Re-run a solution on a fresh sim; the only thing that counts as evidence. */
export function replaySolution(level, solution, cfgName = solution.config ?? 'locked') {
  const cfg = CONFIGS[cfgName];
  const g = new LockedGame(cfg);
  g.placeLevel(level);
  const ids = level.balls.map((x) => x.id);
  g.setLevelIds(ids);
  const log = [];
  if (!solution.strike) return { ok: false, reason: 'no strike in solution', log };
  const s = g.shoot(solution.strike.angleDeg, solution.strike.power);
  log.push({ op: 'shoot', angle: solution.strike.angleDeg, power: solution.strike.power, v0: +s.speed.toFixed(3) });
  g.settle(60);
  log.push({ op: 'settle', pocketed: g.pocketedLevel(), steps: g.steps });
  for (const rw of solution.rewinds ?? []) {
    const trackLen = g.cue.history.length;
    const k = rw.holdSteps ?? Math.max(1, Math.min(trackLen - 1, Math.round((rw.releaseFraction ?? 1) * (trackLen - 1))));
    const r = g.hold(k);
    g.settle(60);
    log.push({
      op: 'rewind', holdSteps: k, releaseIndex: r.cursor, rewoundToOrigin: r.rewoundToOrigin,
      releaseSpeed: +Math.hypot(r.release.vx, r.release.vy).toFixed(3),
      pocketed: g.pocketedLevel(), cueAt: { x: +g.cue.x.toFixed(2), y: +g.cue.y.toFixed(2) },
    });
  }
  return {
    ok: g.pocketedLevel() === ids.length,
    won: g.won, pocketed: g.pocketedLevel(), total: ids.length, log,
  };
}
