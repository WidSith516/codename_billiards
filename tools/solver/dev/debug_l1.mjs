// debug_l1.mjs — why does the solver not find L1's plan (which the designer verified)?
import { loadLevel } from '../lib/level.mjs';
import { LockedGame, CFG_LOCKED } from '../lib/locked.mjs';
import { solveLevel } from '../lib/search.mjs';

const lvl = loadLevel('tools/solver/levels/L1.json');
console.log('level', lvl.id, lvl.balls.length, 'balls; budget', JSON.stringify(lvl.budget));

// 1. the designer's plan, straight up
const g = new LockedGame(CFG_LOCKED);
g.placeLevel(lvl); g.setLevelIds(lvl.balls.map((b) => b.id));
g.shoot(0, 170); g.settle(45);
const tl = g.cue.history.length;
console.log('strike 0/170 -> pocketed', g.pocketedLevel(), 'path', tl, 'samples');
console.log('corridor:', JSON.stringify(g.corridor().map((c) => ({ id: c.id, dist: +c.dist.toFixed(2), in: c.inCorridor }))));
g.hold(tl - 1); g.settle(45);
console.log('full rewind -> pocketed', g.pocketedLevel(), 'kicks', g.rewindHits);

// 2. what does the solver think
const res = solveLevel(lvl, { cfgName: 'locked' });
console.log('\nsolver:', res.status, 'bestNoRewind', res.bestNoRewind, 'bestAny', res.bestAny, 'best', JSON.stringify(res.best));
console.log('corridorAtBest', JSON.stringify(res.corridorAtBest));
console.log('stats', JSON.stringify(res.stats));

// 3. reproduce the solver's own seed scan for angle 0
const g2 = new LockedGame(CFG_LOCKED);
g2.placeLevel(lvl); g2.setLevelIds(lvl.balls.map((b) => b.id));
const base = g2.snapshot();
g2.restore(base); g2.shoot(0, 170); g2.settle(25);
console.log('\nreproduced solve-level strike 0/170: pocketed', g2.pocketedLevel(),
  'path', g2.cue.history.length, 'shotUsed', g2.g.state.shotUsed);
const snap = g2.snapshot();
g2.restore(snap);
const t2 = g2.cue.history.length;
const r = g2.hold(Math.max(1, Math.min(t2 - 1, Math.round(1.0 * (t2 - 1)))));
g2.settle(25);
console.log('reproduced rewind: hold', r.cursor, 'pocketed', g2.pocketedLevel(), 'kicks', g2.rewindHits);
