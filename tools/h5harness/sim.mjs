// sim.mjs — headless harness for the REWIND prototype (h5/index.html).
//
// Strategy: the prototype is a single <script> IIFE with no exports. We slice the
// script body out of the HTML, append a tiny epilogue that publishes the internal
// bindings onto globalThis, and evaluate the WHOLE thing (unmodified game logic)
// inside a node:vm context with stub DOM / canvas / rAF. Nothing in the game logic
// is rewritten, patched or "cleaned up"; the only injected code is the epilogue.
//
// Public API: createGame(), Game (step/settle/shoot/pressR/...), probe helpers.

import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const ROOT = path.resolve(HERE, '..', '..');
export const H5_PATH = path.join(ROOT, 'h5', 'index.html');
export const GDD_PATH = path.join(ROOT, '策划案', 'REWIND_one_shot_GDD_v1.md');

// Published onto globalThis INSIDE the IIFE scope, so `let` bindings are reachable.
// It only reads/assigns existing bindings — it adds no game logic.
const EPILOGUE = [
  'globalThis.__sim = {',
  '  simulation, startRewind, endRewind, init, record, rails, inPocket, collideBalls, ballById, render,',
  '  consts: { W, H, T, R, STEP, pockets, ballColors },',
  '  ui,',
  '  get: () => ({ balls, selected, rewinding, shotUsed, won, stats, particles, paused, accumulator, nowTime }),',
  '  set: (patch) => {',
  '    for (const k in patch) {',
  '      if (k === "selected") selected = patch[k];',
  '      else if (k === "rewinding") rewinding = patch[k];',
  '      else if (k === "shotUsed") shotUsed = patch[k];',
  '      else if (k === "won") won = patch[k];',
  '      else if (k === "paused") paused = patch[k];',
  '      else if (k === "nowTime") nowTime = patch[k];',
  '      else if (k === "accumulator") accumulator = patch[k];',
  '      else if (k === "lastTime") lastTime = patch[k];',
  '      else if (k === "particles") particles = patch[k];',
  '      else if (k === "stats") stats = patch[k];',
  '      else if (k === "trails") trails = patch[k];',
  '      else throw new Error("harness: unknown state key " + k);',
  '    }',
  '  }',
  '};'
].join('\n');

// ---------------------------------------------------------------- source slicing

export function readHtml() {
  return fs.readFileSync(H5_PATH, 'utf8');
}

export function extractGameSource(html = readHtml()) {
  // Slice rule (provenance): take the LAST match of /<script>([\s\S]*?)<\/script>/ in the file,
  // so the body is every byte after the last "<script>" (exclusive) up to the next "</script>"
  // (exclusive). The epilogue below is appended immediately before the final "})();" and is
  // therefore NOT part of scriptSha256(); hashes are over the raw UTF-8 file bytes (LF endings).
  const re = /<script>([\s\S]*?)<\/script>/g;
  let m, last = null;
  while ((m = re.exec(html)) !== null) last = m;
  if (!last) throw new Error('no <script> block found in h5/index.html');
  let src = last[1];
  const idx = src.lastIndexOf('})();');
  if (idx < 0) throw new Error('IIFE tail "})();" not found — cannot inject epilogue');
  return src.slice(0, idx) + '\n' + EPILOGUE + '\n' + src.slice(idx);
}

/** Raw prototype script body with no instrumentation at all (hash source of truth). */
export function rawScriptBody(html = readHtml()) {
  const re = /<script>([\s\S]*?)<\/script>/g;
  let m, last = null;
  while ((m = re.exec(html)) !== null) last = m;
  if (!last) throw new Error('no <script> block found in h5/index.html');
  return last[1];
}

export function sha256(s) {
  return crypto.createHash('sha256').update(s).digest('hex');
}

export function sourceFingerprint() {
  const html = readHtml();
  const body = rawScriptBody(html);
  // character offsets of the script body inside the file, for fully reproducible slicing
  const openTag = html.lastIndexOf('<script>');
  const bodyStart = openTag + '<script>'.length;
  return {
    htmlSha256: sha256(Buffer.from(html, 'utf8')),
    scriptBodySha256: sha256(Buffer.from(body, 'utf8')),
    instrumentedSourceSha256: sha256(Buffer.from(extractGameSource(html), 'utf8')),
    sliceRule: 'html.slice(html.lastIndexOf("<script>")+8, html.lastIndexOf("<script>")+8+body.length) — last <script> block, exclusive delimiters, epilogue not included',
    bodyStartChar: bodyStart,
    bodyLengthChars: body.length,
    fileBytes: Buffer.byteLength(html, 'utf8'),
    epilogueIncluded: false
  };
}

// ------------------------------------------------------------------ DOM stubs

function makeClassList() {
  const set = new Set();
  return {
    _set: set,
    add: (c) => set.add(c),
    remove: (c) => set.delete(c),
    contains: (c) => set.has(c),
    toggle: (c, force) => {
      if (force === undefined) { set.has(c) ? set.delete(c) : set.add(c); }
      else if (force) set.add(c);
      else set.delete(c);
    }
  };
}

function makeElement(id) {
  const handlers = new Map();
  return {
    id,
    textContent: '',
    innerHTML: '',
    classList: makeClassList(),
    _handlers: handlers,
    addEventListener(type, fn) {
      if (!handlers.has(type)) handlers.set(type, []);
      handlers.get(type).push(fn);
    },
    removeEventListener(type, fn) {
      const a = handlers.get(type);
      if (a) { const i = a.indexOf(fn); if (i >= 0) a.splice(i, 1); }
    },
    dispatch(type, ev) {
      for (const fn of handlers.get(type) || []) fn(ev);
    },
    setPointerCapture() {}, releasePointerCapture() {},
    getBoundingClientRect() { return { left: 0, top: 0, width: 1040, height: 560, right: 1040, bottom: 560 }; }
  };
}

// Every ctx member is a no-op method that returns the proxy, so chained calls
// (createLinearGradient(...).addColorStop()) work. Property sets are absorbed.
function makeCtxProxy() {
  let proxy;
  const store = {};
  const noop = () => proxy;
  proxy = new Proxy(store, {
    get(t, prop) {
      if (prop === Symbol.toPrimitive) return () => '[ctx]';
      if (prop === 'toString') return () => '[ctx]';
      if (Object.prototype.hasOwnProperty.call(t, prop)) return t[prop];
      return noop;
    },
    set(t, prop, v) { t[prop] = v; return true; }
  });
  return proxy;
}

function makeEvent(over = {}) {
  return Object.assign({ preventDefault() {}, stopPropagation() {}, pointerId: 1, repeat: false }, over);
}

// ---------------------------------------------------------------------- Game

export const BALL_R = 12.2;

export class Game {
  constructor({ seed = 0x5eed, source = null } = {}) {
    this.source = source || extractGameSource();
    this.virtualNow = 1000;
    this.rafCallback = null;
    this.steps = 0;
    this.track = true;
    this._prevCueSpeed = 0;
    this.metrics = Game.blankMetrics();

    const els = new Map();
    const ctxProxy = makeCtxProxy();
    const document = {
      getElementById(id) {
        if (!els.has(id)) els.set(id, makeElement(id));
        return els.get(id);
      }
    };
    const canvas = document.getElementById('table');
    canvas.getContext = () => ctxProxy;
    this.document = document;
    this.canvas = canvas;
    this.ctx = ctxProxy;
    this.elements = els;

    const winHandlers = new Map();
    const sandbox = {
      console,
      performance: { now: () => this.virtualNow },
      requestAnimationFrame: (cb) => { this.rafCallback = cb; return 1; },
      cancelAnimationFrame: () => {},
      document,
      addEventListener: (type, fn) => {
        if (!winHandlers.has(type)) winHandlers.set(type, []);
        winHandlers.get(type).push(fn);
      },
      removeEventListener: () => {}
    };
    sandbox.window = sandbox;
    this.winHandlers = winHandlers;
    this.sandbox = sandbox;
    vm.createContext(sandbox);
    vm.runInContext(
      'Math.random = (function(){var a=' + (seed >>> 0) + '>>>0;return function(){a|=0;a=a+0x6D2B79F5|0;' +
      'var t=Math.imul(a^a>>>15,1|a);t=t+Math.imul(t^t>>>7,61|t)^t;return ((t^t>>>14)>>>0)/4294967296;};})();',
      sandbox, { filename: 'prng' }
    );
    vm.runInContext(this.source, sandbox, { filename: 'h5/index.html::script' });
    this.sim = sandbox.__sim;
    if (!this.sim) throw new Error('epilogue did not publish __sim');
    this.consts = this.sim.consts;
  }

  static blankMetrics() {
    return {
      maxSpeed: 0, maxSpeedBall: -1, maxSpeedStep: -1,
      maxObjectSpeed: 0, maxRewindSpeed: 0,
      maxEntryDelta: 0, maxRewindJump: 0, maxPenetration: 0,
      maxHistory: 0, nan: false, outOfBounds: 0, boostEvents: 0,
      maxStepMultiplier: 0, maxStepMultiplierAt: -1, divergedAtStep: -1,
      steps: 0
    };
  }

  // ---- state access -------------------------------------------------------

  get balls() { return this.sim.get().balls; }
  get state() { return this.sim.get(); }
  get cues() { return this.sim.consts; }
  cue() { return this.balls.find((b) => b.id === 0); }
  ball(id) { return this.balls.find((b) => b.id === id); }
  pocketedCount() { return this.balls.filter((b) => b.id !== 0 && b.pocketed).length; }
  history() { return this.cue().history; }
  uiText(id) { return this.document.getElementById(id).textContent; }
  uiHtml(id) { return this.document.getElementById(id).innerHTML; }

  reset() {
    this.sim.init();
    this.sim.set({ nowTime: 0, particles: [], accumulator: 0, stats: { rewinds: 0, hits: 0 } });
    this.sim.get().balls.forEach((b) => { b.rewindTrack = null; b.rewindCursor = -1; b.reverse = false; });
    this.steps = 0;
    this._prevCueSpeed = 0;
    this.metrics = Game.blankMetrics();
    return this;
  }

  // ---- stepping -----------------------------------------------------------

  step(n = 1) {
    const dt = this.consts.STEP;
    for (let i = 0; i < n; i++) {
      this.sim.simulation(dt);
      this.steps++;
      if (this.track) this.trackStep();
    }
    return this;
  }

  trackStep() {
    const m = this.metrics;
    m.steps++;
    for (const b of this.balls) {
      if (!Number.isFinite(b.x) || !Number.isFinite(b.y) || !Number.isFinite(b.vx) || !Number.isFinite(b.vy)) {
        m.nan = true;
        if (m.divergedAtStep < 0) m.divergedAtStep = this.steps;
      }
      const s = Math.hypot(b.vx, b.vy);
      if (!Number.isFinite(s)) {
        m.nan = true;
        if (m.divergedAtStep < 0) m.divergedAtStep = this.steps;
      }
      if (b.reverse) {
        if (s > m.maxRewindSpeed) m.maxRewindSpeed = s;
      } else {
        if (s > m.maxSpeed) { m.maxSpeed = s; m.maxSpeedBall = b.id; m.maxSpeedStep = this.steps; }
        if (s > 1e6 && m.divergedAtStep < 0) m.divergedAtStep = this.steps;
        if (b.id !== 0 && s > m.maxObjectSpeed) m.maxObjectSpeed = s;
      }
      const T = this.consts.T, R = this.consts.R;
      if (!b.pocketed && !b.reverse &&
        (b.x < T.left + R - 0.5 || b.x > T.right - R + 0.5 || b.y < T.top + R - 0.5 || b.y > T.bottom - R + 0.5)) {
        if (!this.sim.inPocket(b)) m.outOfBounds++;
      }
    }
    const cue = this.cue();
    // per-step speed multiplier (a rail boost alone shows up as 1.4867)
    const sc = Math.hypot(cue.vx, cue.vy);
    if (this._prevCueSpeed > 1 && Number.isFinite(sc)) {
      const mul = sc / this._prevCueSpeed;
      if (mul > m.maxStepMultiplier) { m.maxStepMultiplier = mul; m.maxStepMultiplierAt = this.steps; }
    }
    this._prevCueSpeed = sc;
    const h = cue.history;
    if (h.length > m.maxHistory) m.maxHistory = h.length;
    if (h.length >= 2) {
      const d = Math.hypot(h[h.length - 1].x - h[h.length - 2].x, h[h.length - 1].y - h[h.length - 2].y);
      if (d > m.maxEntryDelta) m.maxEntryDelta = d;
    }
    if (cue.reverse && h.length >= 2) {
      const d = Math.hypot(h[h.length - 1].x - h[h.length - 2].x, h[h.length - 1].y - h[h.length - 2].y);
      if (d > m.maxRewindJump) m.maxRewindJump = d;
    }
  }

  // ---- events (real handlers registered by the prototype) ------------------

  key(type, code, over = {}) {
    const ev = makeEvent(Object.assign({ code, key: code === 'KeyR' ? 'r' : ' ' }, over));
    for (const fn of this.winHandlers.get(type) || []) fn(ev);
    return ev;
  }

  pointer(type, x, y, over = {}) {
    const ev = makeEvent(Object.assign({ clientX: x, clientY: y }, over));
    this.canvas.dispatch(type, ev);
    return ev;
  }

  /** Real three-event drag shot. angleDeg: screen-space direction (y down). power: 12..170. */
  shoot(angleDeg, power) {
    const cue = this.cue();
    const a = (angleDeg * Math.PI) / 180;
    const len = Math.max(12, Math.min(170, power));
    const dx = Math.cos(a) * len, dy = Math.sin(a) * len;
    const from = { x: cue.x, y: cue.y };
    this.pointer('pointerdown', from.x, from.y);
    this.pointer('pointermove', from.x - dx, from.y - dy);
    this.pointer('pointerup', from.x - dx, from.y - dy);
    const speed = Math.hypot(cue.vx, cue.vy);
    if (speed > this.metrics.maxSpeed) {
      this.metrics.maxSpeed = speed; this.metrics.maxSpeedBall = 0; this.metrics.maxSpeedStep = this.steps;
    }
    return { vx: cue.vx, vy: cue.vy, speed };
  }

  /** Attempt a second stroke; returns cue speed delta (expected 0 after the first shot). */
  attemptSecondShot(angleDeg, power) {
    const cue = this.cue();
    const before = { vx: cue.vx, vy: cue.vy };
    this.shoot(angleDeg, power);
    return { dvx: cue.vx - before.vx, dvy: cue.vy - before.vy, speedDelta: Math.hypot(cue.vx - before.vx, cue.vy - before.vy) };
  }

  pressR() { this.key('keydown', 'KeyR'); }
  releaseR() { this.key('keyup', 'KeyR'); }

  /** Press R, advance k physics steps, release. Returns observed rewind data. */
  rewindSteps(k) {
    const cue = this.cue();
    this.pressR();
    const track = cue.rewindTrack ? cue.rewindTrack.slice() : null;
    const cursorAtPress = cue.rewindCursor;
    const seen = [];
    for (let i = 0; i < k; i++) {
      this.step(1);
      seen.push({ cursor: cue.rewindCursor, x: cue.x, y: cue.y, vx: cue.vx, vy: cue.vy, pocketed: cue.pocketed });
    }
    const released = { vx: cue.vx, vy: cue.vy };
    const cursorAfter = cue.rewindCursor;
    this.releaseR();
    return { track, cursorAtPress, seen, released, releaseIndex: cursorAfter };
  }

  /** Hold R until the cursor reaches 0 (prototype auto-calls endRewind there) or maxSteps. */
  rewindToStart(maxSteps = 200000) {
    const cue = this.cue();
    this.pressR();
    let n = 0;
    while (this.state.rewinding && n < maxSteps) { this.step(1); n++; }
    this.releaseR();
    return n;
  }

  /** Run physics until every non-pocketed, non-reverse ball has velocity exactly 0. */
  settle({ maxSeconds = 30, stopOnWin = true } = {}) {
    const cap = Math.ceil(maxSeconds / this.consts.STEP);
    let n = 0;
    while (n < cap) {
      this.step(1); n++;
      if (stopOnWin && this.state.won) break;
      if (this.atRest()) break;
    }
    return { steps: n, seconds: n * this.consts.STEP, atRest: this.atRest(), timedOut: n >= cap && !this.atRest() };
  }

  atRest() {
    for (const b of this.balls) {
      if (b.pocketed) continue;
      if (b.vx !== 0 || b.vy !== 0) return false;
    }
    return true;
  }

  speeds() { return this.balls.map((b) => ({ id: b.id, s: Math.hypot(b.vx, b.vy), pocketed: b.pocketed })); }

  // ---- harness-only state injection (never used to test physics itself) ----

  /** Mark all object balls pocketed so a probe runs on an empty table. */
  clearObjectBalls() {
    for (const b of this.balls) if (b.id !== 0) { b.pocketed = true; b.vx = b.vy = 0; }
    return this;
  }

  placeBall(id, x, y, vx = 0, vy = 0) {
    const b = this.ball(id);
    b.x = x; b.y = y; b.vx = vx; b.vy = vy;
    b.pocketed = false; b.reverse = false; b.rewindTrack = null; b.rewindCursor = -1;
    b.history = [{ x, y, vx, vy }];
    return b;
  }

  /** Replace a ball history with a synthetic polyline (used for trajectory/index probes). */
  setHistory(id, points) {
    const b = this.ball(id);
    b.history = points.map((p) => ({ x: p.x, y: p.y, vx: p.vx || 0, vy: p.vy || 0 }));
    return b;
  }

  /**
   * Re-lay the object balls as a TIGHT triangle (neighbour centre distance exactly 2R),
   * keeping the prototype's apex (675,280) and id order. Same 4/3/2/1 layout as init().
   * Loose (prototype) column spacing = R*1.82 = 22.204; tight = 2R*cos30 = 21.130.
   */
  tightenRack() {
    let id = 1;
    for (let col = 0; col < 4; col++) {
      for (let row = 0; row <= col; row++) {
        const b = this.ball(id++);
        b.x = 675 + col * BALL_R * 2 * Math.cos(Math.PI / 6);
        b.y = 280 + (row - col / 2) * BALL_R * 2;
        b.vx = b.vy = 0; b.pocketed = false; b.reverse = false;
        b.rewindTrack = null; b.rewindCursor = -1;
        b.history = [{ x: b.x, y: b.y, vx: 0, vy: 0 }];
      }
    }
    return this;
  }

  // ---- snapshots for lookahead policies -----------------------------------

  save() {
    const st = this.state;
    return {
      balls: this.balls.map((b) => ({
        id: b.id, x: b.x, y: b.y, vx: b.vx, vy: b.vy, r: b.r, color: b.color,
        pocketed: b.pocketed, reverse: b.reverse, rewindCursor: b.rewindCursor,
        history: b.history.map((p) => ({ x: p.x, y: p.y, vx: p.vx, vy: p.vy })),
        rewindTrack: b.rewindTrack ? b.rewindTrack.map((p) => ({ x: p.x, y: p.y, vx: p.vx, vy: p.vy })) : null
      })),
      stats: { rewinds: st.stats.rewinds, hits: st.stats.hits },
      particles: st.particles.map((p) => Object.assign({}, p)),
      nowTime: st.nowTime, won: st.won, shotUsed: st.shotUsed, rewinding: st.rewinding, selected: st.selected,
      metrics: Object.assign({}, this.metrics), steps: this.steps
    };
  }

  restore(s) {
    const cur = this.balls;
    for (let i = 0; i < cur.length; i++) {
      const b = cur[i], d = s.balls[i];
      b.x = d.x; b.y = d.y; b.vx = d.vx; b.vy = d.vy;
      b.pocketed = d.pocketed; b.reverse = d.reverse; b.rewindCursor = d.rewindCursor;
      b.history = d.history.map((p) => ({ x: p.x, y: p.y, vx: p.vx, vy: p.vy }));
      b.rewindTrack = d.rewindTrack ? d.rewindTrack.map((p) => ({ x: p.x, y: p.y, vx: p.vx, vy: p.vy })) : null;
    }
    const st = this.state;
    st.stats.rewinds = s.stats.rewinds; st.stats.hits = s.stats.hits;
    st.particles.length = 0;
    for (const p of s.particles) st.particles.push(Object.assign({}, p));
    this.sim.set({ nowTime: s.nowTime, won: s.won, shotUsed: s.shotUsed, rewinding: s.rewinding, selected: s.selected });
    this.metrics = Object.assign({}, s.metrics);
    this.steps = s.steps;
    return this;
  }

  // ---- diagnostics --------------------------------------------------------

  /** Min distance from each ball to the cue's recorded history polyline (the rewind corridor). */
  corridorDistances({ stride = 1 } = {}) {
    const h = this.cue().history;
    const out = [];
    for (const b of this.balls) {
      if (b.id === 0) continue;
      let best = Infinity;
      if (b.pocketed) { out.push({ id: b.id, dist: -1, pocketed: true }); continue; }
      for (let i = 0; i < h.length; i += stride) {
        const d = Math.hypot(h[i].x - b.x, h[i].y - b.y);
        if (d < best) best = d;
      }
      out.push({ id: b.id, dist: best, pocketed: false });
    }
    return out;
  }

  contactableCount(stride = 1) {
    return this.corridorDistances({ stride }).filter((d) => !d.pocketed && d.dist < 2 * BALL_R).length;
  }

  /** Move the real rAF loop for one frame with a synthetic timestamp (ms). */
  frame(tsMs) {
    if (!this.rafCallback) return null;
    const before = this.sim.get().nowTime;
    this.rafCallback(tsMs);
    const after = this.sim.get().nowTime;
    const simSteps = Math.round((after - before) / this.consts.STEP);
    return { simSteps, simSeconds: simSteps * this.consts.STEP };
  }

  /** Start the rAF loop at a wall-clock timestamp (ms) and return a driver. */
  rafDriver(startMs = 100000) {
    this.virtualNow = startMs;
    this.sim.set({ lastTime: startMs });
    return (ts) => this.frame(ts);
  }

  render() {
    return this.sim.render();
  }
}

export function createGame(opts) {
  return new Game(opts);
}

// ------------------------------------------------------------ misc utilities

export function pct(sorted, p) {
  if (!sorted.length) return NaN;
  const i = Math.min(sorted.length - 1, Math.max(0, Math.round((p / 100) * (sorted.length - 1))));
  return sorted[i];
}

export function summarize(values) {
  const s = values.slice().sort((a, b) => a - b);
  const sum = s.reduce((a, b) => a + b, 0);
  return {
    n: s.length, min: s[0], max: s[s.length - 1], mean: sum / (s.length || 1),
    p10: pct(s, 10), p25: pct(s, 25), median: pct(s, 50), p75: pct(s, 75), p90: pct(s, 90),
    p99: pct(s, 99), p999: pct(s, 99.9)
  };
}

export function round(x, d = 4) {
  const f = Math.pow(10, d);
  return Math.round(x * f) / f;
}

export function relErr(a, b) {
  if (b === 0) return a === 0 ? 0 : Infinity;
  return Math.abs(a - b) / Math.abs(b);
}
