// diag5.mjs -- airtight tunnelling test.
//
// The prototype moves each ball in a straight line of length |v|*dt and only tests OVERLAP at the
// sampled end-of-step positions (collideBalls). So a contact is provably missed when, inside one
// step, the two balls' travelled SEGMENTS come closer than 2R while no sampled end-of-step centre
// distance is below 2R. Segment-segment distance is exact up to floating point.
import { createGame } from './sim.mjs';
const DT = 1 / 120, R = 12.2, TWO_R = 2 * R;

function segPoint(px, py, ax, ay, bx, by) {
  const dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
  let t = l2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / l2 : 0;
  t = Math.max(0, Math.min(1, t));
  return Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
}
function segSeg(a1, a2, b1, b2) {
  const d1 = (b2[0] - b1[0]) * (a1[1] - b1[1]) - (b2[1] - b1[1]) * (a1[0] - b1[0]);
  const d2 = (b2[0] - b1[0]) * (a2[1] - b1[1]) - (b2[1] - b1[1]) * (a2[0] - b1[0]);
  const d3 = (a2[0] - a1[0]) * (b1[1] - a1[1]) - (a2[1] - a1[1]) * (b1[0] - a1[0]);
  const d4 = (a2[0] - a1[0]) * (b2[1] - a1[1]) - (a2[1] - a1[1]) * (b2[0] - a1[0]);
  if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return 0;
  return Math.min(
    segPoint(a1[0], a1[1], b1[0], b1[1], b2[0], b2[1]),
    segPoint(a2[0], a2[1], b1[0], b1[1], b2[0], b2[1]),
    segPoint(b1[0], b1[1], a1[0], a1[1], a2[0], a2[1]),
    segPoint(b2[0], b2[1], a1[0], a1[1], a2[0], a2[1])
  );
}

function scan(angle, power, tight, steps = 120 * 25) {
  const g = createGame();
  if (tight) g.tightenRack();
  g.shoot(angle, power);
  const n = 11;
  let missedSteps = 0, minInStepSegDist = Infinity, worstStep = -1, worstPair = null, worstSpeed = 0;
  let minSampled = Infinity;
  let maxStepPx = 0;
  for (let s = 0; s < steps; s++) {
    const before = g.balls.map((b) => [b.x, b.y]);
    g.step(1);
    const B = g.balls;
    if (!B.every((b) => Number.isFinite(b.x) && Number.isFinite(b.y))) break;
    for (let k = 0; k < n; k++) {
      const d = Math.hypot(B[k].x - before[k][0], B[k].y - before[k][1]);
      if (d > maxStepPx) maxStepPx = d;
    }
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        const ai = [before[i][0], before[i][1]], aj = [B[i].x, B[i].y];
        const bi = [before[j][0], before[j][1]], bj = [B[j].x, B[j].y];
        const seg = segSeg(ai, aj, bi, bj);
        const sampled = Math.hypot(B[j].x - B[i].x, B[j].y - B[i].y);
        if (sampled < minSampled) minSampled = sampled;
        if (seg < minInStepSegDist) {
          minInStepSegDist = seg;
          worstStep = s + 1; worstPair = [i, j];
          worstSpeed = Math.max(Math.hypot(B[i].vx, B[i].vy), Math.hypot(B[j].vx, B[j].vy));
        }
        // missed contact: the swept paths overlap inside the step, but the sampled end-of-step
        // distance never did. A detected contact always leaves the pair at exactly 2R+0.06 =
        // 24.46 px (the positional separation), so require a margin above that. Steps where either
        // ball was displaced by more than its own velocity allows (a positional push from another
        // pair) are excluded, because then the straight-line segment model is invalid.
        const moveI = Math.hypot(B[i].x - before[i][0], B[i].y - before[i][1]);
        const moveJ = Math.hypot(B[j].x - before[j][0], B[j].y - before[j][1]);
        const allowI = Math.hypot(B[i].vx, B[i].vy) * DT * 1.05 + 0.05;
        const allowJ = Math.hypot(B[j].vx, B[j].vy) * DT * 1.05 + 0.05;
        if (moveI > allowI || moveJ > allowJ) continue;
        if (seg < TWO_R && sampled > 25.0) {
          missedSteps++;
          if (missedSteps <= 3 && !scan.firstExample) {
            scan.firstExample = { angle, power, tight, step: s + 1, pair: [i, j],
              inStepSegDistance: Math.round(seg * 1000) / 1000, sampledDistance: Math.round(sampled * 1000) / 1000,
              speeds: [Math.round(Math.hypot(B[i].vx, B[i].vy)), Math.round(Math.hypot(B[j].vx, B[j].vy))],
              positionsBefore: [ai.map((v) => Math.round(v * 10) / 10), bi.map((v) => Math.round(v * 10) / 10)],
              positionsAfter: [[Math.round(B[i].x * 10) / 10, Math.round(B[i].y * 10) / 10], [Math.round(B[j].x * 10) / 10, Math.round(B[j].y * 10) / 10]] };
          }
        }
      }
    }
  }
  return { angle, power, tight, missedContactSteps: missedSteps, minInStepSegmentDistance: Math.round(minInStepSegDist * 1000) / 1000,
    worstStep, worstPair, worstSpeed, maxBallStepPx: Math.round(maxStepPx * 100) / 100, sampledTwoR: TWO_R };
}

const rows = [];
for (const [a, p, t] of [[115, 170, false], [110, 170, false], [250, 170, false], [245, 170, false], [280, 170, true], [110, 170, true], [0, 170, false], [45, 170, false], [90, 100, false]]) {
  rows.push(scan(a, p, t));
}
console.log(JSON.stringify({ rows, firstExample: scan.firstExample || null }, null, 1));
