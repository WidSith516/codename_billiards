// dbg_collide.mjs — log every ball-ball collision call for step 1 in BOTH models, to find
// out exactly which branch produces their result and whether mine is a faithful reading.
//   node tools/verify_solver/dbg_collide.mjs [level] [angle] [power] [mySubstepPx]
import { LockedGame, CFG_LOCKED } from '../solver/lib/locked.mjs';
import { loadLevel } from '../solver/lib/level.mjs';
import { MyGame } from './mykernel.mjs';

const lvlId = process.argv[2] ?? 'L5';
const angle = Number(process.argv[3] ?? 349);
const power = Number(process.argv[4] ?? 170);
const myPx = Number(process.argv[5] ?? 4);

const lvl = loadLevel(`tools/solver/levels/${lvlId}.json`);
const theirs = new LockedGame(CFG_LOCKED);
theirs.placeLevel(lvl);
theirs.setLevelIds(lvl.balls.map((b) => b.id));

const log = [];
const origCollide = theirs._collide.bind(theirs);
theirs._collide = (a, b, ...rest) => {
  const dx = b.x - a.x, dy = b.y - a.y, d2 = dx * dx + dy * dy;
  const before = `${a.id}:(${a.x.toFixed(3)},${a.y.toFixed(3)},${a.vx.toFixed(2)},${a.vy.toFixed(2)}) ${b.id}:(${b.x.toFixed(3)},${b.y.toFixed(3)},${b.vx.toFixed(2)},${b.vy.toFixed(2)})`;
  origCollide(a, b, ...rest);
  const after = `${a.id}:(${a.x.toFixed(3)},${a.y.toFixed(3)},${a.vx.toFixed(2)},${a.vy.toFixed(2)}) ${b.id}:(${b.x.toFixed(3)},${b.y.toFixed(3)},${b.vx.toFixed(2)},${b.vy.toFixed(2)})`;
  log.push({ pair: `${a.id}-${b.id}`, d2: +d2.toFixed(2), min2: (2 * a.r) ** 2, before, after });
};

theirs.shoot(angle, power);
console.log('--- THEIRS step 1 ---');
const beforeAll = theirs.balls.map((b) => `${b.id}:(${b.x.toFixed(3)},${b.y.toFixed(3)})`).join(' ');
theirs.stepOnce();
console.log('start pos: ' + beforeAll);
for (const l of log) console.log(`  pair ${l.pair.padEnd(5)} d2=${String(l.d2).padStart(8)} min2=${l.min2.toFixed(2)} ${l.d2 < l.min2 ? 'OVERLAP' : 'swept '}\n     before ${l.before}\n     after  ${l.after}`);
console.log('end pos:   ' + theirs.balls.map((b) => `${b.id}:(${b.x.toFixed(3)},${b.y.toFixed(3)})`).join(' '));

// -------- mine
const mine = new MyGame({ substepPx: myPx, maxSubsteps: 64 });
mine.place({ cue: lvl.cue, balls: lvl.balls, pockets: lvl.pockets, table: lvl.table });
const mlog = [];
const origPairs = mine.collidePairs.bind(mine);
mine.collidePairs = (h) => {
  const snap = mine.balls.map((b) => ({ id: b.id, x: b.x, y: b.y, vx: b.vx, vy: b.vy }));
  origPairs(h);
  const after = mine.balls.map((b) => ({ id: b.id, x: b.x, y: b.y, vx: b.vx, vy: b.vy }));
  for (let i = 0; i < snap.length; i++) {
    const a = snap[i], b = after[i];
    if (Math.abs(a.vx - b.vx) > 1e-9 || Math.abs(a.vy - b.vy) > 1e-9 || Math.abs(a.x - b.x) > 1e-9) {
      mlog.push(`ball${a.id} v (${a.vx.toFixed(2)},${a.vy.toFixed(2)}) -> (${b.vx.toFixed(2)},${b.vy.toFixed(2)})  pos d=(${(b.x - a.x).toFixed(4)},${(b.y - a.y).toFixed(4)})`);
    }
  }
};
mine.shoot(angle, power);
console.log('\n--- MINE step 1 (substepPx=' + myPx + ') ---');
console.log('start pos: ' + mine.balls.map((b) => `${b.id}:(${b.x.toFixed(3)},${b.y.toFixed(3)})`).join(' '));
mine.stepOnce();
for (const l of mlog) console.log('  changed: ' + l);
console.log('end pos:   ' + mine.balls.map((b) => `${b.id}:(${b.x.toFixed(3)},${b.y.toFixed(3)})`).join(' '));
console.log('end vel:   ' + mine.balls.map((b) => `${b.id}:(${b.vx.toFixed(2)},${b.vy.toFixed(2)})`).join(' '));
console.log('\nTHEIRS end vel: ' + theirs.balls.map((b) => `${b.id}:(${b.vx.toFixed(2)},${b.vy.toFixed(2)})`).join(' '));
