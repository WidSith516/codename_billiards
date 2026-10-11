// locked.mjs — the LOCKED physics model (策划案/REWIND_core_params_v1.md) applied at the
// simulation boundary of the frozen prototype, plus the unpatched prototype as a control.
//
// Why this file exists
// --------------------
// `h5/index.html` is frozen and implements the OLD model (exponential damping 1.07/s,
// unbounded x1.5 rails for the cue, 1.6x rewind impulse, point-in-circle pocketing,
// per-step history sampling). The lead locked a NEW model in
// `策划案/REWIND_core_params_v1.md`. We cannot edit the game, so we do what
// `tools/h5harness/vcap_proof.mjs` did — patch at the boundary — but now we re-implement
// the per-step loop instead of just clamping.
//
// We keep the prototype's DATA STRUCTURES and EVENT plumbing (via h5harness/sim.mjs:
// vm sandbox, balls, history, rewindTrack/rewindCursor, startRewind/endRewind, pockets
// array, table rect). We replace, per substep: friction+integration, rails, ball-ball
// collision, pocket test, history sampling, and the rewind cursor kinematics.
//
// Two configs are provided so the same search can be run on both:
//   CFG_PROTOTYPE — reproduces h5/index.html's simulation() exactly (used as a fidelity
//                   self-test AND as the "old physics" control for the verdict).
//   CFG_LOCKED    — the locked table.
//
// Fidelity notes / deviations are listed in tools/solver/README.md and REPORT.md.

import { Game } from '../../h5harness/sim.mjs';

// ------------------------------------------------------------------ configs

/** Locked table (策划案/REWIND_core_params_v1.md §8) + the two values it does not fix. */
export const CFG_LOCKED = Object.freeze({
  name: 'locked',
  // §2 strike
  powerMax: 170, speedPerPower: 7.4,
  // §3 friction: constant deceleration
  friction: 'const', frictionDecel: 640,
  // §4 rails: magnitude transform |v| -> min(|v|*0.92 + gain, cap)
  rail: 'locked', wallRestitution: 0.92, cueWallGain: 120, objectBallGain: 0,
  speedCap: 2000,
  // §5 pocket: centre inside radius AND velocity pointing at the pocket centre
  pocketRule: 'dir', pocketRadius: 24,
  // §6 history: uniform 2 px arc-length sampling (one sample per physics step MAX,
  //    interpolated so the spacing is really 2 px even when the ball moves further)
  history: 'arclen', historyStepPx: 2.0,
  // §6 rewind: position forced by the snapshot, ONE SAMPLE PER STEP;
  //    velocity taken from the stored record (never derived from positions)
  rewindKin: 'storedVel',
  // §7 rewind impulse: j = max(0, (v_cue - v_target).n) * K, K = 1.0 (must stay <= 1)
  impulse: 'k', rewindK: 1.0, clampTargetToMover: true,
  // NOT in the locked table: ball-ball restitution is only specified as "approximately
  // elastic". Carried over from the prototype (equal mass, e = 0.955).
  ballRestitution: 0.955,
  // §5 asks for continuous (TOI) collision. We approximate it with sub-stepping whose
  // size is chosen from the per-step displacement (chord-based, not a fixed count).
  substepMaxPx: 4.0, maxSubsteps: 8,
  toi: true,                 // §5: resolve ball-ball contacts at the swept contact geometry
  step: 1 / 120,
});

/** Unpatched h5/index.html simulation(). Control + fidelity self-test. */
export const CFG_PROTOTYPE = Object.freeze({
  name: 'prototype',
  powerMax: 170, speedPerPower: 7.4,
  friction: 'exp', expK: 1.07, stopBelow: 9,
  rail: 'proto', cueRailFactor: 1.5, objectRailFactor: 0.85,
  speedCap: Infinity,
  pocketRule: 'point', pocketRadius: 24,
  history: 'step', historyEps: 1e-5,
  rewindKin: 'posDelta',
  impulse: 'proto', rewindK: 1.6, rewindImpulseCap: 1300, clampTargetToMover: false,
  ballRestitution: 0.955,
  substepMaxPx: Infinity, maxSubsteps: 1,
  toi: false,
  step: 1 / 120,
});

/**
 * SENSITIVITY CHECK ONLY — not a candidate for the locked model.
 * Same locked physics, but the rewind crawl rate follows the stored speed profile
 * (the reading the lead rejected in §6.1) instead of a flat 240 px/s.
 */
export const CFG_LOCKED_STOREDCRAWL = Object.freeze({
  ...CFG_LOCKED, name: 'locked-storedcrawl', rewindKin: 'storedCrawl',
});

export const CONFIGS = {
  locked: CFG_LOCKED,                          // ← the locked model (primary)
  prototype: CFG_PROTOTYPE,                    // ← unpatched h5 control
  'locked-storedcrawl': CFG_LOCKED_STOREDCRAWL, // ← sensitivity check (§6.1 alternative)
  'locked-protoKin': Object.freeze({ ...CFG_LOCKED, name: 'locked-protoKin', rewindKin: 'posDelta' }),
};

// ------------------------------------------------------------------ the sim

export class LockedGame {
  constructor(cfg = CFG_LOCKED) {
    this.cfg = cfg;
    this.g = new Game({ seed: 0x5eed });   // same PRNG seed as the harness
    this.g.track = false;                  // skip per-step metric bookkeeping (perf)
    this.sim = this.g.sim;
    this.T = this.g.consts.T;              // live object -> mutable in place
    this.pockets = this.g.consts.pockets;  // live array  -> mutable in place
    this.steps = 0;
    this.time = 0;
    this.rewindHits = 0;
    this.capEvents = 0;
    this.trackRails = false;
    this.railEvents = [];
  }

  // balls[] is REPLACED by init(), so always read it live.
  get balls() { return this.g.balls; }
  ball(id) { return this.g.balls.find((b) => b.id === id); }
  get cue() { return this.ball(0); }
  get won() { return this.g.state.won; }
  get rewinding() { return this.g.state.rewinding; }

  // ---------------------------------------------------------- level setup

  /** Reset to a level definition (cue start, object balls, optional pockets/table). */
  placeLevel(level) {
    this.g.reset();                                     // init() -> fresh 11-ball world
    this.sim.set({ won: false, shotUsed: false, rewinding: false, selected: 0, nowTime: 0 });
    if (level.table) Object.assign(this.T, level.table);
    if (level.pockets) {
      const arr = this.pockets;                          // mutate IN PLACE: game holds this array
      arr.length = 0;
      for (const p of level.pockets) arr.push([p[0], p[1]]);
    }
    const ids = new Set(level.balls.map((b) => b.id));
    for (const b of this.balls) {
      if (b.id !== 0 && !ids.has(b.id)) { b.pocketed = true; b.vx = 0; b.vy = 0; }
    }
    const seed = (b, x, y) => {
      b.x = x; b.y = y; b.vx = 0; b.vy = 0;
      b.pocketed = false; b.reverse = false; b.rewindTrack = null; b.rewindCursor = -1;
      b.history = [{ x, y, vx: 0, vy: 0 }];
    };
    for (const spec of level.balls) seed(this.ball(spec.id), spec.x, spec.y);
    seed(this.cue, level.cue.x, level.cue.y);
    this.steps = 0; this.time = 0; this.rewindHits = 0; this.capEvents = 0;
    return this;
  }

  /** Ball ids that belong to the level (everything else is a pre-pocketed sentinel). */
  setLevelIds(ids) { this._levelIds = ids ? new Set(ids) : null; return this; }

  pocketedLevel() {
    if (!this._levelIds) return this.g.balls.filter((b) => b.id !== 0 && b.pocketed).length;
    let n = 0;
    for (const b of this.g.balls) if (b.id !== 0 && this._levelIds.has(b.id) && b.pocketed) n++;
    return n;
  }
  remainingLevel() {
    const out = [];
    for (const b of this.g.balls) if (b.id !== 0 && !b.pocketed && (!this._levelIds || this._levelIds.has(b.id))) out.push(b);
    return out;
  }

  // ------------------------------------------------------------- stepping

  step(n = 1) { for (let i = 0; i < n; i++) this.stepOnce(); return this; }

  stepOnce() {
    const cfg = this.cfg, dt = cfg.step, balls = this.balls, cue = this.cue;
    // --- (1) rewind cursor advance: exactly ONE snapshot sample per physics step.
    //         The cue is position-forced here; it is NOT integrated by physics this step.
    if (cue.reverse) {
      if (cfg.rewindKin === 'storedCrawl') {
        // Sensitivity variant: crawl rate = stored speed (rejected in §6.1, kept for A/B).
        if (cue.rewindF === null || cue.rewindF === undefined) cue.rewindF = cue.rewindTrack.length - 1;
        const i0 = Math.max(0, Math.min(cue.rewindTrack.length - 1, Math.floor(cue.rewindF)));
        const s = cue.rewindTrack[i0];
        const adv = (Math.hypot(s.vx, s.vy) * dt) / cfg.historyStepPx;
        let f = cue.rewindF - Math.max(0, adv);
        if (f <= 0) {
          cue.x = cue.rewindTrack[0].x; cue.y = cue.rewindTrack[0].y;
          cue.vx = 0; cue.vy = 0; cue.rewindF = 0; cue.rewindCursor = 0;
          cue.pocketed = false; this.sim.endRewind(); this._record(cue);
        } else {
          cue.rewindF = f;
          const j0 = Math.floor(f), j1 = Math.min(cue.rewindTrack.length - 1, j0 + 1), fr = f - j0;
          const a = cue.rewindTrack[j0], bb = cue.rewindTrack[j1];
          cue.x = a.x + (bb.x - a.x) * fr; cue.y = a.y + (bb.y - a.y) * fr;
          cue.vx = -s.vx; cue.vy = -s.vy;
          cue.rewindCursor = j0;
          cue.pocketed = false;
          this._cap(cue); this._record(cue);
        }
      } else if (cue.rewindCursor > 0) {
        const p = cue.rewindTrack[--cue.rewindCursor];
        const ox = cue.x, oy = cue.y;
        cue.x = p.x; cue.y = p.y;
        if (cfg.rewindKin === 'storedVel') { cue.vx = -p.vx; cue.vy = -p.vy; }
        else { cue.vx = (cue.x - ox) / dt; cue.vy = (cue.y - oy) / dt; }
        cue.pocketed = false;                       // rewind un-pockets the cue (proto semantics)
        this._cap(cue);
        this._record(cue);
      } else {
        this.sim.endRewind();                       // reaches the snapshot origin -> stop
        cue.vx = 0; cue.vy = 0;
      }
    }
    // --- (2) physics substeps for every ball that is not rewinding
    let maxDisp = 0;
    for (const b of balls) {
      if (b.pocketed || b.reverse) continue;
      const d = Math.hypot(b.vx, b.vy) * dt;
      if (d > maxDisp) maxDisp = d;
    }
    const sub = Math.max(1, Math.min(cfg.maxSubsteps, Math.ceil(maxDisp / cfg.substepMaxPx)));
    const h = dt / sub;
    for (let s = 0; s < sub; s++) this._substep(h);
    // --- (3) history for the cue. The prototype config records here (once per step, after
    //         collisions) and is kept bit-identical to h5/index.html; the locked arclen
    //         config already recorded inside each substep, before collision resolution.
    if (cfg.history === 'step' && !cue.reverse && !cue.pocketed) this._record(cue);
    this.steps++; this.time += dt;
  }

  _substep(h) {
    const cfg = this.cfg, balls = this.balls;
    const n = balls.length;
    if (this._px === undefined || this._px.length !== n) {
      this._px = new Float64Array(n); this._py = new Float64Array(n);
    }
    for (let i = 0; i < n; i++) { this._px[i] = balls[i].x; this._py[i] = balls[i].y; }
    for (const b of balls) {
      if (b.reverse || b.pocketed) continue;
      const sp = Math.hypot(b.vx, b.vy);
      if (sp > 0) {
        if (cfg.friction === 'const') {
          const ns = sp - cfg.frictionDecel * h;
          if (ns <= 0) { b.vx = 0; b.vy = 0; }                    // hard stop, no reversal
          else { const k = ns / sp; b.vx *= k; b.vy *= k; }
        } else {
          const damp = Math.exp(-cfg.expK * h);
          b.vx *= damp; b.vy *= damp;
          if (Math.hypot(b.vx, b.vy) < cfg.stopBelow) { b.vx = 0; b.vy = 0; }
        }
        b.x += b.vx * h; b.y += b.vy * h;
      }
      this._rails(b);
    }
    // Locked §6 records (position, velocity) every HISTORY_STEP_PX. The sample must carry
    // the velocity the ball HAD at that position, so the locked config records between
    // integration and collision resolution -- otherwise every interpolated sample inside a
    // step where a collision happens gets stamped with the POST-collision velocity, and the
    // rewind then replays a wrong speed profile (this silently crippled the backstop kick,
    // whose whole value comes from sample #1 still holding V_BREAK).
    if (cfg.history === 'arclen') {
      const cue = this.cue;
      if (!cue.reverse && !cue.pocketed) this._record(cue);
    }
    for (let i = 0; i < n; i++)
      for (let j = i + 1; j < n; j++) this._collide(balls[i], balls[j], this._px[i], this._py[i], this._px[j], this._py[j]);
    for (const b of balls) if (!b.pocketed && !b.reverse) this._rails(b);
    for (const b of balls) if (!b.pocketed && !b.reverse) this._cap(b);
  }

  _cap(b) {
    const cap = this.cfg.speedCap;
    if (!Number.isFinite(cap)) return;
    const s = Math.hypot(b.vx, b.vy);
    if (s > cap) { const k = cap / s; b.vx *= k; b.vy *= k; this.capEvents++; }
  }

  _pocketHit(b) {
    const R = this.cfg.pocketRadius;
    for (const [px, py] of this.pockets) {
      const dx = px - b.x, dy = py - b.y;
      if (dx * dx + dy * dy >= R * R) continue;
      if (this.cfg.pocketRule === 'point') return true;
      // §5: centre inside the circle AND velocity pointing at the pocket centre
      if (b.vx * dx + b.vy * dy > 0) return true;
    }
    return false;
  }

  _rails(b) {
    const cfg = this.cfg, T = this.T, R = b.r;
    if (this._pocketHit(b)) {
      b.pocketed = true; b.vx = 0; b.vy = 0;
      if (b.id !== 0 && this.balls.every((o) => o.id === 0 || o.pocketed)) this.sim.set({ won: true });
      return;
    }
    let bounced = false;
    if (b.x - R < T.left - 0.001) { b.x = T.left + R; b.vx = Math.abs(b.vx); bounced = true; }
    if (b.x + R > T.right + 0.001) { b.x = T.right - R; b.vx = -Math.abs(b.vx); bounced = true; }
    if (b.y - R < T.top - 0.001) { b.y = T.top + R; b.vy = Math.abs(b.vy); bounced = true; }
    if (b.y + R > T.bottom + 0.001) { b.y = T.bottom - R; b.vy = -Math.abs(b.vy); bounced = true; }
    if (!bounced) return;
    const vIn = Math.hypot(b.vx, b.vy);
    if (cfg.rail === 'proto') {
      const f = b.id === 0 ? cfg.cueRailFactor : cfg.objectRailFactor;
      b.vx *= f; b.vy *= f;                                    // H5: one factor per step
    } else {
      // locked §4: DIRECTION is reflected above; MAGNITUDE becomes |v|*0.92 + gain (cue only)
      const gain = b.id === 0 ? cfg.cueWallGain : cfg.objectBallGain;
      const ns = Math.min(vIn * cfg.wallRestitution + gain, cfg.speedCap);
      if (vIn > 0) { const k = ns / vIn; b.vx *= k; b.vy *= k; }
    }
    if (this.trackRails) {
      this.railEvents.push({ id: b.id, vIn, vOut: Math.hypot(b.vx, b.vy), x: b.x, y: b.y, t: this.steps });
      if (this.railEvents.length > 4000) this.railEvents.shift();
    }
  }

  _collide(a, b, ax0, ay0, bx0, by0) {
    if (a.pocketed || b.pocketed) return;
    const dx = b.x - a.x, dy = b.y - a.y, d2 = dx * dx + dy * dy;
    const min = a.r + b.r, min2 = min * min;
    // Locked §5 asks for continuous collision (TOI), not "move then test". We keep the
    // discrete step but resolve the impulse at the exact contact geometry of the swept
    // segment. This must take priority even when the pair ends the substep overlapping:
    // using the end-of-substep normal instead rotates a cut shot's line of centres by up
    // to ~7 deg at a 4 px substep, which is the difference between potting and missing.
    if (this.cfg.toi && ax0 !== undefined && !a.reverse && !b.reverse) {
      const rxx = bx0 - ax0, ryy = by0 - ay0;
      const r2 = rxx * rxx + ryy * ryy;
      if (r2 >= min2) {                       // not already overlapping at the substep start
        const dax = a.x - ax0, day = a.y - ay0, dbx = b.x - bx0, dby = b.y - by0;
        const wx = dbx - dax, wy = dby - day;
        const A = wx * wx + wy * wy;
        if (A > 1e-12) {
          const B = 2 * (rxx * wx + ryy * wy);
          const C = r2 - min2;
          const disc = B * B - 4 * A * C;
          if (disc > 0) {
            const u1 = (-B - Math.sqrt(disc)) / (2 * A);
            if (u1 >= 0 && u1 <= 1) {
              const cx = (bx0 + dbx * u1) - (ax0 + dax * u1);
              const cy = (by0 + dby * u1) - (ay0 + day * u1);
              const cl = Math.hypot(cx, cy) || 1;
              const nx = cx / cl, ny = cy / cl;
              const vAlong = (b.vx - a.vx) * nx + (b.vy - a.vy) * ny;
              if (vAlong < 0) {
                const impulse = -(1 + this.cfg.ballRestitution) * vAlong / 2;
                a.vx -= impulse * nx; a.vy -= impulse * ny;
                b.vx += impulse * nx; b.vy += impulse * ny;
              }
            }
          }
        }
        return;                               // no contact inside this substep
      }
    } else if (d2 >= min2) {
      return;                                 // legacy path (prototype control): move-then-test
    }
    let dist = Math.sqrt(d2);
    let ldx = dx, ldy = dy;
    if (dist < 0.0001) { ldx = 1; ldy = 0; dist = 1; }
    const nx = ldx / dist, ny = ldy / dist;
    const penetration = Math.max(0, min - dist) + 0.06;

    if (a.reverse && b.reverse) return;
    if (a.reverse || b.reverse) {
      const mover = a.reverse ? a : b, target = a.reverse ? b : a;
      const fX = a.reverse ? nx : -nx, fY = a.reverse ? ny : -ny;   // normal from mover to target
      target.x += fX * penetration; target.y += fY * penetration;
      const rel = (mover.vx - target.vx) * fX + (mover.vy - target.vy) * fY;
      if (rel > 0) {
        const cfg = this.cfg;
        const j = cfg.impulse === 'proto'
          ? Math.min(cfg.rewindImpulseCap, cfg.rewindK * rel)
          : cfg.rewindK * rel;
        target.vx += fX * j; target.vy += fY * j;
        if (cfg.clampTargetToMover) {
          // lead's backstop: the struck ball may never leave faster than the cue's approach
          const st = Math.hypot(target.vx, target.vy), sm = Math.hypot(mover.vx, mover.vy);
          if (st > sm && st > 0) { const k = sm / st; target.vx *= k; target.vy *= k; }
        }
        this.rewindHits++;
      }
      return;   // the rewinding cue is never deflected (GDD §4.4.1 / locked §7)
    }
    // ordinary collision: prototype's equal-mass impulse with restitution e
    a.x -= nx * penetration * 0.5; a.y -= ny * penetration * 0.5;
    b.x += nx * penetration * 0.5; b.y += ny * penetration * 0.5;
    const rvx = b.vx - a.vx, rvy = b.vy - a.vy, vAlong = rvx * nx + rvy * ny;
    if (vAlong >= 0) return;
    const impulse = -(1 + this.cfg.ballRestitution) * vAlong / 2;
    a.vx -= impulse * nx; a.vy -= impulse * ny;
    b.vx += impulse * nx; b.vy += impulse * ny;
  }

  _record(b) {
    const cfg = this.cfg, h = b.history;
    if (!h.length) { h.push({ x: b.x, y: b.y, vx: b.vx, vy: b.vy }); return; }
    if (cfg.history === 'step') {
      const old = h[h.length - 1];
      if (Math.hypot(b.x - old.x, b.y - old.y) > cfg.historyEps) h.push({ x: b.x, y: b.y, vx: b.vx, vy: b.vy });
      return;
    }
    // locked §6: append every time the accumulated displacement reaches HISTORY_STEP_PX.
    // A fast ball moves further than that in one step, so interpolate along the straight
    // segment it just travelled (direction is constant *within* a substep).
    let last = h[h.length - 1];
    let d = Math.hypot(b.x - last.x, b.y - last.y);
    let guard = 0;
    while (d >= cfg.historyStepPx && guard++ < 64) {
      const f = cfg.historyStepPx / d;
      h.push({ x: last.x + (b.x - last.x) * f, y: last.y + (b.y - last.y) * f, vx: b.vx, vy: b.vy });
      last = h[h.length - 1];
      d = Math.hypot(b.x - last.x, b.y - last.y);
    }
  }

  // ------------------------------------------------------------- gameplay

  /**
   * The one strike. angleDeg is screen-space (y down), power 12..170 drag pixels.
   * The arithmetic below is deliberately identical to the harness's pointer-event path
   * (sim.mjs shoot(): drag vector -> hypot -> power -> *7.4) so the trajectory is
   * bit-for-bit the game's, rounding included.
   */
  shoot(angleDeg, power) {
    const cue = this.cue;
    const a = (angleDeg * Math.PI) / 180;
    const drag = Math.max(12, Math.min(this.cfg.powerMax, power));
    // reproduce the game's pointer path: pointerdown at the cue, drag to (from - vector),
    // pointerup -> releaseShot() re-derives dx,dy from canvas coordinates. Going through
    // those subtractions matters: they are not always exact, and a 1-ULP difference in the
    // launch velocity grows into a completely different trajectory after rail bounces.
    const fromX = cue.x, fromY = cue.y;
    const px = fromX - Math.cos(a) * drag, py = fromY - Math.sin(a) * drag;
    const dx = cue.x - px, dy = cue.y - py;
    const len = Math.hypot(dx, dy);
    if (len < 12) return null;                              // releaseShot() ignores short drags
    const p = Math.min(len, this.cfg.powerMax);
    cue.vx = (dx / len) * p * this.cfg.speedPerPower;
    cue.vy = (dy / len) * p * this.cfg.speedPerPower;
    this.sim.set({ shotUsed: true });
    this._record(cue);
    return { vx: cue.vx, vy: cue.vy, speed: Math.hypot(cue.vx, cue.vy) };
  }

  startRewind() { this.sim.startRewind(); return this; }
  endRewind() { this.sim.endRewind(); return this; }

  /** press R, advance k steps, release. Returns the release state. */
  hold(k) {
    this.cue.rewindF = null;                 // only used by the storedCrawl sensitivity config
    this.startRewind();
    const track = this.cue.rewindTrack ? this.cue.rewindTrack.length : 0;
    const accepted = this.rewinding;
    this.step(k);
    const at = { x: this.cue.x, y: this.cue.y, vx: this.cue.vx, vy: this.cue.vy };
    const cursor = this.cue.rewindCursor;
    this.endRewind();
    return { accepted, track, cursor, release: at, rewoundToOrigin: cursor <= 0 };
  }

  atRest() {
    for (const b of this.balls) { if (b.pocketed) continue; if (b.vx !== 0 || b.vy !== 0) return false; }
    return true;
  }

  settle(maxSeconds = 30) {
    const cap = Math.ceil(maxSeconds / this.cfg.step);
    let n = 0;
    while (n < cap) {
      this.stepOnce(); n++;
      if (this.g.state.won) break;
      if (this.atRest()) break;
    }
    return { steps: n, atRest: this.atRest(), timedOut: n >= cap && !this.atRest() };
  }

  // ------------------------------------------------------------ snapshots

  /** Shallow history copies: history entries are never mutated in place, only appended. */
  snapshot() {
    return {
      won: this.g.state.won, shotUsed: this.g.state.shotUsed, rewinding: this.g.state.rewinding,
      steps: this.steps, time: this.time, rewindHits: this.rewindHits, capEvents: this.capEvents,
      balls: this.balls.map((b) => ({
        x: b.x, y: b.y, vx: b.vx, vy: b.vy, pocketed: b.pocketed, reverse: b.reverse,
        rewindCursor: b.rewindCursor, rewindF: b.rewindF ?? null,
        rewindTrack: b.rewindTrack ? b.rewindTrack.slice() : null,
        history: b.history.slice(),
      })),
    };
  }

  restore(s) {
    const balls = this.balls;
    for (let i = 0; i < balls.length; i++) {
      const b = balls[i], d = s.balls[i];
      b.x = d.x; b.y = d.y; b.vx = d.vx; b.vy = d.vy;
      b.pocketed = d.pocketed; b.reverse = d.reverse; b.rewindCursor = d.rewindCursor;
      b.rewindF = d.rewindF ?? null;
      b.rewindTrack = d.rewindTrack ? d.rewindTrack.slice() : null;
      b.history = d.history.slice();
    }
    this.sim.set({ won: s.won, shotUsed: s.shotUsed, rewinding: s.rewinding });
    this.steps = s.steps; this.time = s.time;
    this.rewindHits = s.rewindHits; this.capEvents = s.capEvents;
    return this;
  }

  // ---------------------------------------------------------- diagnostics

  /**
   * Min distance from each remaining level ball to the cue's recorded path (the corridor),
   * plus the best rewind KICK that path can deliver to it: for every history sample within
   * the 2R contact radius, the kick the locked §7 impulse would give is
   *   j = max(0, (v_cue_rewind - v_target) . n),  v_cue_rewind = -stored_v,  n = unit(ball - sample)
   * reported for a stationary target. That single number is the honest predictor of whether
   * a ball can still be potted by rewinding, and it is what the search orders candidates by.
   */
  corridor() {
    const h = this.cue.history;
    const out = [];
    for (const b of this.remainingLevel()) {
      let best = Infinity, kick = 0;
      for (let i = 0; i < h.length; i++) {
        const dx = b.x - h[i].x, dy = b.y - h[i].y;
        const d = Math.hypot(dx, dy);
        if (d < best) best = d;
        if (d < 2 * this.g.consts.R && d > 1e-6) {
          const j = (-h[i].vx * dx - h[i].vy * dy) / d;      // rel with v_target = 0
          if (j > kick) kick = j;
        }
      }
      out.push({ id: b.id, dist: best, kick, inCorridor: best < 2 * this.g.consts.R });
    }
    return out;
  }

  pathLength() {
    const h = this.cue.history;
    let L = 0;
    for (let i = 1; i < h.length; i++) L += Math.hypot(h[i].x - h[i - 1].x, h[i].y - h[i - 1].y);
    return L;
  }
}

export function makeGame(cfgName = 'locked') {
  const cfg = CONFIGS[cfgName];
  if (!cfg) throw new Error('unknown config ' + cfgName);
  return new LockedGame(cfg);
}
