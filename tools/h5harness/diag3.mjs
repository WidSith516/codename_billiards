// diag3.mjs -- why does the constructed pass-through search find nothing?
import { createGame } from './sim.mjs';
const DT = 1 / 120, BALL_R = 12.2;
const distPointSeg = (px, py, a, b) => {
  const dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
  let t = l2 > 0 ? ((px - a.x) * dx + (py - a.y) * dy) / l2 : 0;
  t = Math.max(0, Math.min(1, t));
  return Math.hypot(px - (a.x + t * dx), py - (a.y + t * dy));
};
for (const angle of [45, 135, 90, 200, 250, 70, 110]) {
  const g = createGame();
  g.clearObjectBalls();
  g.shoot(angle, 170);
  const path = [];
  for (let i = 0; i < 2000; i++) {
    g.step(1);
    const c = g.cue(); const s = Math.hypot(c.vx, c.vy);
    if (!Number.isFinite(s) || s > 1e7) break;
    path.push({ x: c.x, y: c.y, s, i, vx: c.vx, vy: c.vy });
  }
  const fast = path.filter((p) => p.s >= 1900);
  let cand = 0, built = 0, hit = 0, pass = 0;
  const T = g.consts.T;
  for (const imp of [0, 12, 20, 22, 23.5]) {
    for (let k = path.length - 1; k >= 40; k -= 11) {
      const p = path[k];
      if (p.s < 1900) break;
      const nx = -p.vy / p.s, ny = p.vx / p.s;
      const bx = p.x + nx * imp, by = p.y + ny * imp;
      if (bx < T.left + BALL_R || bx > T.right - BALL_R || by < T.top + BALL_R || by > T.bottom - BALL_R) continue;
      let blocked = false;
      for (let i = 1; i <= k - 2; i++) if (distPointSeg(bx, by, path[i - 1], path[i]) < 2 * BALL_R + 1) { blocked = true; break; }
      cand++;
      if (blocked) continue;
      built++;
      const g2 = createGame(); g2.clearObjectBalls();
      const ball = g2.placeBall(1, bx, by, 0, 0);
      g2.shoot(angle, 170);
      let h = false;
      for (let i = 0; i < 2000; i++) {
        g2.step(1); const c = g2.cue();
        if (!Number.isFinite(Math.hypot(c.vx, c.vy))) break;
        if (Math.hypot(ball.vx, ball.vy) > 1e-9 || Math.hypot(ball.x - bx, ball.y - by) > 1e-6) { h = true; break; }
      }
      if (h) hit++;
      else {
        let contMin = Infinity;
        for (let i = 1; i < path.length; i++) contMin = Math.min(contMin, distPointSeg(bx, by, path[i - 1], path[i]));
        if (contMin < 2 * BALL_R - 0.5) pass++;
        console.log(`  PASS-THROUGH angle=${angle} imp=${imp} ballAt=(${bx.toFixed(1)},${by.toFixed(1)}) cueSpeed=${p.s.toFixed(0)} contMin=${contMin.toFixed(2)}`);
      }
    }
  }
  console.log(`angle=${angle}: path=${path.length} fastSamples=${fast.length} candidates=${cand} unblocked=${built} hit=${hit} passThrough=${pass}`);
}
