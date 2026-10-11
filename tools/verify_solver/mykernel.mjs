// mykernel.mjs — an INDEPENDENT from-spec implementation of the LOCKED physics model.
//
// Written by `verify-solver` from 策划案/REWIND_core_params_v1.md §1-§8 ONLY. It imports
// nothing from tools/solver/ and shares no code with tools/h5harness/sim.mjs (no node:vm,
// no prototype bytecode). It exists to answer one question: if a *different* implementer
// writes the locked table, do the six "SOLVED" levels still clear?
//
// Deliberate implementation differences vs tools/solver/lib/locked.mjs:
//   * substep size is 2 px of travel (vs 4 px) and the cap is 16 (vs 8) => a finer, more
//     accurate ball-ball collision resolution;
//   * rails/pocket are resolved once after integration and once after collisions, no
//     double-call inside the integration loop;
//   * the history sampler is a plain 2 px arc-length accumulator (re-implemented);
//   * the pointer round-trip in shoot() is OPTIONAL (`pointerArithmetic: false` uses the
//     exact closed form v = cos/sin * power * 7.4).
//
// Anything this kernel gets wrong is MY bug, not the solver's; every deviation that could
// change an outcome is listed above.

export const K = {
  R: 12.2,
  LEFT: 65, RIGHT: 975, TOP: 63, BOTTOM: 497,
  POCKETS: [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]],
  POCKET_R: 24,
  DT: 1 / 120,
  FRICTION: 640,          // px/s^2 constant deceleration
  BALL_E: 0.955,
  WALL_REST: 0.92, CUE_GAIN: 120, OBJ_GAIN: 0,
  CAP: 2000,
  HIST_PX: 2.0,
  CRAWL_PX_PER_S: 240,
  K_IMPULSE: 1.0,
  SPEED_PER_POWER: 7.4,
  POWER_MAX: 170,
};

export class MyGame {
  constructor({ substepPx = 2.0, maxSubsteps = 16, pointerArithmetic = true, substepMode = 'px' } = {}) {
    this.substepPx = substepPx;
    this.maxSubsteps = maxSubsteps;
    this.pointerArithmetic = pointerArithmetic;
    this.substepMode = substepMode;
    this.balls = [];
    this.steps = 0;
  }

  place(level) {
    this.pockets = level.pockets ? level.pockets.map((p) => [p[0], p[1]]) : K.POCKETS.map((p) => p.slice());
    this.table = level.table ? { ...level.table } : { left: K.LEFT, right: K.RIGHT, top: K.TOP, bottom: K.BOTTOM };
    this.cue = { id: 0, x: level.cue.x, y: level.cue.y, vx: 0, vy: 0, r: K.R, pocketed: false, rewinding: false, history: [{ x: level.cue.x, y: level.cue.y, vx: 0, vy: 0 }] };
    this.balls = [this.cue];
    for (const b of level.balls) this.balls.push({ id: b.id, x: b.x, y: b.y, vx: 0, vy: 0, r: K.R, pocketed: false, rewinding: false, history: [{ x: b.x, y: b.y, vx: 0, vy: 0 }] });
    this.levelIds = new Set(level.balls.map((b) => b.id));
    this.snapshot = null;
    this.cursor = -1;
    this.steps = 0;
    return this;
  }

  // ------------------------------------------------------------------ strike
  shoot(angleDeg, power) {
    const cue = this.cue;
    const a = (angleDeg * Math.PI) / 180;
    const drag = Math.max(12, Math.min(K.POWER_MAX, power));
    let vx, vy;
    if (this.pointerArithmetic) {
      // the game reads pointer coordinates off the canvas: down at the cue, move to
      // cue - d, up; releaseShot re-derives dx,dy by subtracting canvas coordinates.
      const px = cue.x - Math.cos(a) * drag, py = cue.y - Math.sin(a) * drag;
      const dx = cue.x - px, dy = cue.y - py;
      const len = Math.hypot(dx, dy);
      if (len < 12) return null;
      const p = Math.min(len, K.POWER_MAX);
      vx = (dx / len) * p * K.SPEED_PER_POWER;
      vy = (dy / len) * p * K.SPEED_PER_POWER;
    } else {
      vx = Math.cos(a) * drag * K.SPEED_PER_POWER;
      vy = Math.sin(a) * drag * K.SPEED_PER_POWER;
    }
    cue.vx = vx; cue.vy = vy;
    this.record(cue);
    return { vx, vy, speed: Math.hypot(vx, vy) };
  }

  // ------------------------------------------------------------------ history
  record(b) {
    const h = b.history;
    let last = h[h.length - 1];                 // MUST be reassigned inside the loop,
    let d = Math.hypot(b.x - last.x, b.y - last.y);   // otherwise the 2nd push duplicates the 1st
    let guard = 0;
    while (d >= K.HIST_PX && guard++ < 64) {
      const f = K.HIST_PX / d;
      h.push({ x: last.x + (b.x - last.x) * f, y: last.y + (b.y - last.y) * f, vx: b.vx, vy: b.vy });
      last = h[h.length - 1];
      d = Math.hypot(b.x - last.x, b.y - last.y);
    }
  }

  // ------------------------------------------------------------------ physics
  friction(b, h) {
    const sp = Math.hypot(b.vx, b.vy);
    if (sp <= 0) return false;
    const ns = sp - K.FRICTION * h;
    if (ns <= 0) { b.vx = 0; b.vy = 0; return true; }
    const k = ns / sp; b.vx *= k; b.vy *= k;
    return false;
  }

  pocketCheck(b) {
    for (const [px, py] of this.pockets) {
      const dx = px - b.x, dy = py - b.y;
      if (dx * dx + dy * dy >= K.POCKET_R * K.POCKET_R) continue;
      if (b.vx * dx + b.vy * dy > 0) { b.pocketed = true; b.vx = 0; b.vy = 0; return true; }
    }
    return false;
  }

  rails(b) {
    if (b.pocketed) return false;
    if (this.pocketCheck(b)) return false;
    const T = this.table, R = b.r;
    let bounced = false;
    if (b.x - R < T.left - 0.001) { b.x = T.left + R; b.vx = Math.abs(b.vx); bounced = true; }
    if (b.x + R > T.right + 0.001) { b.x = T.right - R; b.vx = -Math.abs(b.vx); bounced = true; }
    if (b.y - R < T.top - 0.001) { b.y = T.top + R; b.vy = Math.abs(b.vy); bounced = true; }
    if (b.y + R > T.bottom + 0.001) { b.y = T.bottom - R; b.vy = -Math.abs(b.vy); bounced = true; }
    if (!bounced) return false;
    const vIn = Math.hypot(b.vx, b.vy);
    const gain = b.id === 0 ? K.CUE_GAIN : K.OBJ_GAIN;
    const ns = Math.min(vIn * K.WALL_REST + gain, K.CAP);
    if (vIn > 0) { const k = ns / vIn; b.vx *= k; b.vy *= k; }
    return true;
  }

  /**
   * Ball-ball collisions for the substep that was just integrated. `px/py` are the ball
   * positions at the START of that substep (the swept segment is start -> now).
   * Semantics chosen to match the locked spec: resolve at the swept contact geometry; a
   * pair that was already overlapping when the substep began gets a positional correction
   * and no extra momentum.
   */
  collidePairs(h, px, py) {
    const bs = this.balls, n = bs.length, min = 2 * K.R, min2 = min * min;
    for (let i = 0; i < n; i++) {
      for (let j = i + 1; j < n; j++) {
        const a = bs[i], b = bs[j];
        if (a.pocketed || b.pocketed) continue;
        const rewCue = (a.id === 0 && a.rewinding) || (b.id === 0 && b.rewinding);
        if (rewCue) { this.rewindKick(a, b); continue; }
        const rxx = px[j] - px[i], ryy = py[j] - py[i];
        const r2 = rxx * rxx + ryy * ryy;
        if (r2 >= min2) {
          // swept solve over the substep just integrated
          const wx = (b.x - px[j]) - (a.x - px[i]);
          const wy = (b.y - py[j]) - (a.y - py[i]);
          const A = wx * wx + wy * wy;
          if (A <= 1e-12) continue;
          const B = 2 * (rxx * wx + ryy * wy);
          const C = r2 - min2;
          const disc = B * B - 4 * A * C;
          if (disc <= 0) continue;
          const u = (-B - Math.sqrt(disc)) / (2 * A);
          if (u < 0 || u > 1) continue;
          const cx = rxx + wx * u, cy = ryy + wy * u;
          const cl = Math.hypot(cx, cy) || 1;
          const nx = cx / cl, ny = cy / cl;
          const vAlong = (b.vx - a.vx) * nx + (b.vy - a.vy) * ny;
          if (vAlong >= 0) continue;
          const imp = -(1 + K.BALL_E) * vAlong / 2;
          a.vx -= imp * nx; a.vy -= imp * ny;
          b.vx += imp * nx; b.vy += imp * ny;
          continue;
        }
        // overlapping at substep start: positional correction, no added momentum
        const dx = b.x - a.x, dy = b.y - a.y, d2 = dx * dx + dy * dy;
        let dist = Math.sqrt(d2);
        let ldx = dx, ldy = dy;
        if (dist < 1e-4) { ldx = 1; ldy = 0; dist = 1; }
        const nx = ldx / dist, ny = ldy / dist;
        const pen = Math.max(0, min - dist) + 0.06;
        a.x -= nx * pen * 0.5; a.y -= ny * pen * 0.5;
        b.x += nx * pen * 0.5; b.y += ny * pen * 0.5;
        const vAlong = (b.vx - a.vx) * nx + (b.vy - a.vy) * ny;
        if (vAlong >= 0) continue;
        const imp = -(1 + K.BALL_E) * vAlong / 2;
        a.vx -= imp * nx; a.vy -= imp * ny;
        b.vx += imp * nx; b.vy += imp * ny;
      }
    }
  }

  /** §7: j = max(0, (v_cue - v_target).n) * K along n; the cue is NOT deflected. */
  rewindKick(a, b) {
    const mover = (a.id === 0 && a.rewinding) ? a : b;
    const target = mover === a ? b : a;
    const dx = target.x - mover.x, dy = target.y - mover.y;
    const d = Math.hypot(dx, dy);
    if (d >= 2 * K.R) return;
    let nx, ny;
    if (d < 1e-4) { nx = 1; ny = 0; } else { nx = dx / d; ny = dy / d; }
    const pen = Math.max(0, 2 * K.R - d) + 0.06;
    target.x += nx * pen; target.y += ny * pen;
    const rel = (mover.vx - target.vx) * nx + (mover.vy - target.vy) * ny;
    if (rel > 0) {
      const j = Math.max(0, rel) * K.K_IMPULSE;
      target.vx += nx * j; target.vy += ny * j;
      // lead's requested backstop clamp: |v_target| <= |v_cue|
      const st = Math.hypot(target.vx, target.vy), sm = Math.hypot(mover.vx, mover.vy);
      if (st > sm && st > 0) { const k = sm / st; target.vx *= k; target.vy *= k; }
    }
  }

  // ------------------------------------------------------------------ stepping
  step(k = 1) { for (let i = 0; i < k; i++) this.stepOnce(); return this; }

  stepOnce() {
    const dt = K.DT;
    const cue = this.cue;
    // --- rewind cursor: one snapshot sample per physics step (= 240 px/s at 2 px/sample)
    if (cue.rewinding) {
      if (this.cursor > 0) {
        const p = this.snapshot[--this.cursor];
        cue.x = p.x; cue.y = p.y;
        cue.vx = -p.vx; cue.vy = -p.vy;
        cue.pocketed = false;
        this.record(cue);                    // §6: the live history keeps appending
      } else {
        cue.rewinding = false; cue.vx = 0; cue.vy = 0; this.snapshot = null;
      }
    }
    // --- substep count from the fastest ball's per-step travel
    let maxDisp = 0;
    for (const b of this.balls) {
      if (b.pocketed || b.rewinding) continue;
      const d = Math.hypot(b.vx, b.vy) * dt;
      if (d > maxDisp) maxDisp = d;
    }
    const sub = Math.max(1, Math.min(this.maxSubsteps, Math.ceil(maxDisp / this.substepPx)));
    const h = dt / sub;
    const px = new Array(this.balls.length), py = new Array(this.balls.length);
    for (let s = 0; s < sub; s++) {
      for (let i = 0; i < this.balls.length; i++) { px[i] = this.balls[i].x; py[i] = this.balls[i].y; }
      for (const b of this.balls) {
        if (b.pocketed || b.rewinding) continue;
        if (b.vx !== 0 || b.vy !== 0) {
          this.friction(b, h);
          b.x += b.vx * h; b.y += b.vy * h;
        }
        this.rails(b);
      }
      if (!cue.rewinding) this.record(cue);
      this.collidePairs(h, px, py);
      for (const b of this.balls) { if (!b.pocketed && !b.rewinding) this.rails(b); }
      for (const b of this.balls) {
        if (b.pocketed || b.rewinding) continue;
        const sp = Math.hypot(b.vx, b.vy);
        if (sp > K.CAP) { const k = K.CAP / sp; b.vx *= k; b.vy *= k; }
      }
      if (this.won()) break;
    }
    this.steps++;
  }

  // ------------------------------------------------------------------ driving
  won() { return this.balls.every((b) => b.id === 0 || b.pocketed); }
  pocketedCount() { let n = 0; for (const b of this.balls) if (b.id !== 0 && this.levelIds.has(b.id) && b.pocketed) n++; return n; }
  remaining() { return this.balls.filter((b) => b.id !== 0 && this.levelIds.has(b.id) && !b.pocketed); }
  atRest() { for (const b of this.balls) { if (b.pocketed || b.rewinding) continue; if (b.vx !== 0 || b.vy !== 0) return false; } return true; }
  settle(maxSeconds = 30) {
    const cap = Math.ceil(maxSeconds / K.DT);
    let n = 0;
    while (n < cap) { this.stepOnce(); n++; if (this.won()) break; if (this.atRest()) break; }
    return n;
  }
  startRewind() {
    const cue = this.cue;
    if (cue.history.length < 2) return false;
    this.snapshot = cue.history.slice();
    this.cursor = this.snapshot.length - 1;
    cue.rewinding = true;
    return true;
  }
  hold(k) {
    const ok = this.startRewind();
    if (!ok) return null;
    const trackLen = this.snapshot.length;
    this.step(k);
    const at = { x: this.cue.x, y: this.cue.y, vx: this.cue.vx, vy: this.cue.vy };
    const cursor = this.cursor;
    this.cue.rewinding = false; this.snapshot = null; this.cursor = -1;
    return { trackLen, cursor, release: at, rewoundToOrigin: cursor <= 0 };
  }
}

export function loadLevelFile(fsMod, file) {
  const raw = JSON.parse(fsMod.readFileSync(file, 'utf8'));
  return {
    id: raw.id,
    cue: raw.cue,
    balls: raw.balls.map((b) => ({ id: b.id, x: b.x, y: b.y })),
    pockets: raw.pockets, table: raw.table,
  };
}
