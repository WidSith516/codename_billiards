// smoke.mjs — quick sanity check that the vm harness drives the real prototype.
import { createGame, sourceFingerprint, round } from './sim.mjs';

const fp = sourceFingerprint();
console.log('fingerprint', fp);

const g = createGame();
console.log('balls', g.balls.length, 'cue', JSON.stringify(g.cue()));
console.log('consts', JSON.stringify({ T: g.consts.T, R: g.consts.R, STEP: g.consts.STEP, pockets: g.consts.pockets }));
console.log('ui pocketed =', JSON.stringify(g.uiText('pocketed')), 'status =', JSON.stringify(g.uiText('statusText')));

const shot = g.shoot(0, 170);
console.log('shot', shot, 'shotUsed', g.state.shotUsed);
const s1 = g.settle({ maxSeconds: 30 });
console.log('settle', s1, 'pocketed', g.pocketedCount(), 'hist', g.cue().history.length);
console.log('metrics', JSON.stringify(g.metrics));

// rewind sanity
const rw = g.rewindSteps(30);
console.log('rewind released', rw.released, 'cursor', rw.releaseIndex, 'track len', rw.track.length);
console.log('post-rewind state', JSON.stringify({ pocketed: g.cue().pocketed, x: round(g.cue().x), y: round(g.cue().y) }));

// render + determinism
try { g.render(); console.log('render ok'); } catch (e) { console.log('render FAILED', e.message); }

const a = createGame(); a.shoot(3, 140); a.settle({ maxSeconds: 20 });
const b = createGame(); b.shoot(3, 140); b.settle({ maxSeconds: 20 });
console.log('determinism: pocketedA', a.pocketedCount(), 'pocketedB', b.pocketedCount(),
  'posA', JSON.stringify(a.balls.map(x => [round(x.x, 6), round(x.y, 6), x.pocketed])),
  'equal', JSON.stringify(a.balls.map(x => [round(x.x, 6), round(x.y, 6), x.pocketed])) ===
           JSON.stringify(b.balls.map(x => [round(x.x, 6), round(x.y, 6), x.pocketed])));
