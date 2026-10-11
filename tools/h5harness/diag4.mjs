// diag4.mjs -- do the blown-up shots actually pass balls through each other?
// Detector: for every pair, project (p_j - p_i) on the relative-velocity direction; a sign flip
// means they swapped sides. If the minimum sampled centre distance never went below 2R during the
// whole run, the pair passed through each other without any contact being registered.
import { createGame } from './sim.mjs';
const DT = 1 / 120, R = 12.2, TWO_R = 2 * R;

function scan(angle, power, tight) {
  const g = createGame();
  if (tight) g.tightenRack();
  g.shoot(angle, power);
  const pairs = [];
  for (let i = 0; i < 11; i++) for (let j = i + 1; j < 11; j++) pairs.push({ i, j, minD: Infinity, flipped: false, prevSign: 0, flipStep: -1, speedAtFlip: 0 });
  const maxStepDisp = new Array(11).fill(0);
  const maxSpeed = new Array(11).fill(0);
  let step = 0;
  for (; step < 120 * 25; step++) {
    const before = g.balls.map((b) => ({ x: b.x, y: b.y }));
    g.step(1);
    const B = g.balls;
    if (!B.every((b) => Number.isFinite(b.x) && Number.isFinite(b.y))) break;
    for (let k = 0; k < 11; k++) {
      const d = Math.hypot(B[k].x - before[k].x, B[k].y - before[k].y);
      if (d > maxStepDisp[k]) maxStepDisp[k] = d;
      const s = Math.hypot(B[k].vx, B[k].vy);
      if (s > maxSpeed[k]) maxSpeed[k] = s;
    }
    for (const p of pairs) {
      const a = B[p.i], b = B[p.j];
      const dx = b.x - a.x, dy = b.y - a.y;
      const d = Math.hypot(dx, dy);
      if (d < p.minD) p.minD = d;
      const rvx = b.vx - a.vx, rvy = b.vy - a.vy;
      const rv = Math.hypot(rvx, rvy);
      if (rv > 1e-9) {
        const along = (dx * rvx + dy * rvy) / rv;
        const sgn = along > 0 ? 1 : -1;
        if (p.prevSign !== 0 && sgn !== p.prevSign) {
          p.flipped = true;
          p.flipStep = step;
          p.speedAtFlip = Math.max(Math.hypot(a.vx, a.vy), Math.hypot(b.vx, b.vy));
          p.maxStep = Math.max(maxStepDisp[p.i], maxStepDisp[p.j]);
        }
        p.prevSign = sgn;
      }
    }
  }
  // A contact always leaves the pair separated by exactly 2R+0.06 = 24.46 px, so require a
  // comfortable margin above that to rule the separation signature out.
  const CLEAR = 25.5;
  const passThroughs = pairs.filter((p) => p.flipped && p.minD > CLEAR);
  const first = passThroughs.sort((a, b) => b.speedAtFlip - a.speedAtFlip)[0];
  let trace = null;
  if (first) {
    const g2 = createGame();
    if (tight) g2.tightenRack();
    g2.shoot(angle, power);
    const rows = [];
    for (let s = 0; s <= first.flipStep + 2; s++) {
      g2.step(1);
      if (s >= first.flipStep - 2) {
        const A = g2.balls[first.i], B = g2.balls[first.j];
        rows.push({
          step: s + 1,
          ballA: [Math.round(A.x * 100) / 100, Math.round(A.y * 100) / 100, Math.round(Math.hypot(A.vx, A.vy))],
          ballB: [Math.round(B.x * 100) / 100, Math.round(B.y * 100) / 100, Math.round(Math.hypot(B.vx, B.vy))],
          centreDistance: Math.round(Math.hypot(B.x - A.x, B.y - A.y) * 1000) / 1000
        });
      }
    }
    trace = { pair: [first.i, first.j], flipStep: first.flipStep, lastSteps: rows };
  }
  return {
    angle, power, tight, steps: step,
    pairsThatSwappedSidesWithoutContact: passThroughs.length,
    pairsThatSwappedSides_distanceOver25_5px: passThroughs.length,
    examples: passThroughs.slice(0, 4).map((p) => ({
      pair: [p.i, p.j], minCenterDistance: Math.round(p.minD * 1000) / 1000,
      flipStep: p.flipStep, relSpeedAtFlip: Math.round(p.speedAtFlip),
      ballStepPxAtFlip: Math.round((p.speedAtFlip / 120) * 100) / 100
    })),
    verbatimTrace: trace,
    maxBallStepPx: Math.round(Math.max(...maxStepDisp) * 100) / 100,
    maxSpeedAnyBall: Math.round(Math.max(...maxSpeed))
  };
}

for (const [a, p, t] of [[115, 170, false], [245, 170, false], [110, 170, false], [250, 170, false], [280, 170, true], [110, 170, true], [0, 170, false], [45, 170, false]]) {
  console.log(JSON.stringify(scan(a, p, t)));
}
