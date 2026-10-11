// run_tests.mjs -- GDD section 9 acceptance cases T01..T11 + defect probes D1..D13 + regressions R01..R04.
//
// Exit codes:
//   0  every T case PASS and every R case PASS
//   1  at least one T case FAILED (acceptance failure)
//   2  all T cases PASS but at least one R case FAILED (known defect still present)
//   3  the frozen prototype changed (hash guard tripped)
//
// Run:  node tools/h5harness/run_tests.mjs
//       node --expose-gc tools/h5harness/run_tests.mjs      (adds real per-entry heap measurement)
//
// NOTE: this file is deliberately pure ASCII so that no shell/text round-trip can corrupt it.
// Prose (Chinese/English) lives in REPORT.md, which is written by the agent, not by this script.

import fs from 'node:fs';
import path from 'node:path';
import v8 from 'node:v8';
import { fileURLToPath } from 'node:url';
import { createGame, sourceFingerprint, summarize, round, BALL_R } from './sim.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DT = 1 / 120;
const damp = (dt = DT) => Math.exp(-1.07 * dt);
const K_DAMP = 1.07, STOP_V = 9, MAX_POWER = 170, SPEED_PER_POWER = 7.4;

// Frozen-reference guard: run_tests refuses to produce numbers from a changed prototype.
const PINNED = {
  html: '98ef7c191a91f3c0b05bbcc8a91465de11fbea7069c124e8bca95dc85fefb864',
  scriptBody: '1e7a9f24b3c4e0c6b8c2029bf5d98e360e38f395104563a81a9b120357806436'
};

const T_CASES = [];
const PROBES = [];
const REGRESSIONS = [];

function mulberry(seed) {
  let a = seed >>> 0;
  return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
function record(id, title, status, observed, note = '') { T_CASES.push({ id, title, status, observed, note }); }
function probe(id, title, observed, note = '') { PROBES.push({ id, title, observed, note }); }
function regression(id, title, violated, observed, note = '') { REGRESSIONS.push({ id, title, status: violated ? 'FAIL' : 'PASS', observed, note }); }
function speed(b) { return Math.hypot(b.vx, b.vy); }
function ok(c) { return c ? 'PASS' : 'FAIL'; }
function finite(v) { return Number.isFinite(v); }

// =============================================================== T01
function T01() {
  const g = createGame();
  const first = g.shoot(0, 170);
  const obs = {
    launchSpeed: round(first.speed, 3), expected_power_x_7_4: round(170 * SPEED_PER_POWER, 3),
    shotUsedAfterFirst: g.state.shotUsed, statusText: g.uiText('statusText')
  };
  const a2 = g.attemptSecondShot(90, 170);            // while still travelling, no step taken
  obs.deltaWhileMoving = round(a2.speedDelta, 12);
  obs.hintAfterBlockedDrag = g.uiHtml('hint');
  const s = g.settle({ maxSeconds: 25 });
  obs.restAfterFirstStrokeSec = round(s.seconds, 4);
  obs.cueSpeedAtRest = round(speed(g.cue()), 12);
  const a3 = g.attemptSecondShot(180, 170);           // stationary
  obs.deltaWhileAtRest = round(a3.speedDelta, 12);
  const before = { vx: g.cue().vx, vy: g.cue().vy };
  g.pointer('pointerup', 0, 0);                       // bare pointerup, no pointerdown
  obs.deltaAfterBarePointerUp = round(Math.hypot(g.cue().vx - before.vx, g.cue().vy - before.vy), 12);
  obs.hintAfterThirdAttempt = g.uiHtml('hint');
  obs.hintMentionsBlockedShot = obs.hintAfterThirdAttempt.length > 10 && obs.hintAfterThirdAttempt.includes('R');
  const pass = first.speed === 1258 && obs.shotUsedAfterFirst === true && obs.deltaWhileMoving === 0 &&
    obs.deltaWhileAtRest === 0 && obs.deltaAfterBarePointerUp === 0 && obs.hintMentionsBlockedShot;
  record('T01', 'First stroke works; a second drag produces no impulse', ok(pass), obs,
    'shotUsed gate in pointerdown and releaseShot; the speed<23 gate before it is unreachable after the first stroke');
}

// =============================================================== T02
function T02() {
  const g = createGame();
  g.clearObjectBalls();
  g.placeBall(0, 300, 160, 600, 0);
  g.sim.set({ shotUsed: true });
  g.step(40);
  const rel = g.rewindSteps(10);
  const idx = rel.releaseIndex;
  const here = rel.track[idx], next = rel.track[idx + 1];
  const vr = { vx: rel.released.vx, vy: rel.released.vy };
  const sRel = Math.hypot(vr.vx, vr.vy), sNext = Math.hypot(next.vx, next.vy), sHere = Math.hypot(here.vx, here.vy);
  const obs = {
    forwardSteps: 40, historyLen: rel.track.length, rewindSteps: 10, releaseIndex: idx,
    posAtRelease: [round(rel.seen[9].x, 9), round(rel.seen[9].y, 9)],
    storedPosAtCursor: [round(here.x, 9), round(here.y, 9)],
    releasedV: [round(vr.vx, 9), round(vr.vy, 9)],
    storedV_at_cursorPlus1: [round(next.vx, 9), round(next.vy, 9)],
    storedV_at_cursor: [round(here.vx, 9), round(here.vy, 9)],
    exactNegationOfStoredNext: Math.abs(vr.vx + next.vx) < 1e-9 && Math.abs(vr.vy + next.vy) < 1e-9,
    ratio_released_over_stored_at_cursorPlus1: round(sRel / sNext, 12),
    ratio_released_over_stored_at_cursor: round(sRel / sHere, 12),
    exp_one_step_of_damping: round(1 / damp(), 9),
    dotWithStoredNext: round(vr.vx * next.vx + vr.vy * next.vy, 6)
  };
  record('T02', 'Velocity at release point C is the reverse of the forward velocity', ok(obs.exactNegationOfStoredNext), obs,
    'released v is bit-exact -(history velocity at cursor+1): one damping step (x1.00896) above the velocity stored at the return point');
}

// =============================================================== T03
function T03() {
  const g = createGame();
  g.clearObjectBalls();
  const cue = g.placeBall(0, 300, 160, 600, 0);
  g.sim.set({ shotUsed: true });
  g.step(40);
  const lenA = cue.history.length;                       // A..B
  const rw1 = g.rewindSteps(12);                         // B -> C, release
  const cIndex = rw1.releaseIndex;
  const lenAtC = cue.history.length;
  g.step(40);                                            // C -> D, appended
  const histBefore = cue.history.map((p) => ({ ...p }));
  const lenBefore = histBefore.length;
  g.pressR();
  const track2 = cue.rewindTrack.map((p) => ({ ...p }));
  const lenAtPress = cue.history.length;
  const visited = [];
  let monotone = true, lastLen = cue.history.length;
  let guard = 0;
  while (g.state.rewinding && guard++ < 5000) {
    g.step(1);
    visited.push({ x: cue.x, y: cue.y, cursor: cue.rewindCursor });
    if (cue.history.length < lastLen) monotone = false;
    lastLen = cue.history.length;
  }
  g.releaseR();
  const histAfter = cue.history.map((p) => ({ ...p }));
  const trackMatchesHistory = track2.length === lenAtPress && track2.every((p, i) =>
    p.x === histBefore[i].x && p.y === histBefore[i].y && p.vx === histBefore[i].vx && p.vy === histBefore[i].vy);
  // step 1 moves the cue to track[len-2] (track[len-1] is where it already stands), the last
  // traversal step parks it on track[0], and one extra step then zeroes the velocity in place.
  const playbackIsReversedTrack = visited.length === track2.length && visited.slice(0, track2.length - 1).every((v, k) => {
    const p = track2[track2.length - 2 - k];
    return v.x === p.x && v.y === p.y;
  });
  const terminalStepStalls = visited.length === track2.length &&
    visited[visited.length - 1].x === track2[0].x && visited[visited.length - 1].y === track2[0].y;
  const prefixPreserved = histAfter.length >= lenBefore && histBefore.every((p, i) =>
    p.x === histAfter[i].x && p.y === histAfter[i].y && p.vx === histAfter[i].vx && p.vy === histAfter[i].vy);
  const obs = {
    A_to_B_entries: lenA, B_to_C_entries: 12, C_index_in_track1: cIndex, entries_at_C: lenAtC,
    entries_after_CtoD: lenBefore, D_index_in_track2: lenAtPress - 1,
    track2_len: track2.length, historyLenAtPress: lenAtPress, trackMatchesFrozenHistory: trackMatchesHistory,
    playbackSteps: visited.length, playbackIsExactReversedTrack: playbackIsReversedTrack,
    terminalAutoStopStep: terminalStepStalls,
    playbackStartPos_D: [round(track2[track2.length - 1].x, 6), round(track2[track2.length - 1].y, 6)],
    playbackEndPos_A: [round(track2[0].x, 6), round(track2[0].y, 6)],
    indexOrder: `iA=0 < iC=${cIndex} < iB=${lenA - 1} < iD=${lenAtPress - 1}`,
    indexOrderStrict: 0 < cIndex && cIndex < lenA - 1 && lenA - 1 < lenAtPress - 1,
    historyLenAfterFullRewind: histAfter.length, appendedBySecondRewind: histAfter.length - lenBefore,
    prefixBitIdentical: prefixPreserved, historyLengthMonotone: monotone,
    note_doubleBack: 'the frozen track contains B->C once as the first traversal and again as the recorded reversal, so the physical path is A..B..C..B..C..D'
  };
  const pass = trackMatchesHistory && playbackIsReversedTrack && prefixPreserved && monotone &&
    obs.appendedBySecondRewind > 0 && obs.indexOrderStrict;
  record('T03', 'Playback order D->C->B->A, old history not deleted', ok(pass), obs,
    'playback = exact reverse of the frozen snapshot; history is strictly append-only (prefix bit-identical)');
}

// =============================================================== T04
function T04() {
  const g = createGame();
  g.clearObjectBalls();
  const cue = g.placeBall(0, 300, 280, 600, 0);
  g.sim.set({ shotUsed: true });
  g.step(40);
  const track = cue.history.map((p) => ({ ...p }));
  const mid = track[Math.floor(track.length / 2)];
  const target = g.placeBall(1, mid.x, mid.y, 0, 0);
  const p0 = { x: target.x, y: target.y };
  const hits0 = g.state.stats.hits;
  g.pressR();
  let maxDeviation = 0, kickSteps = 0, maxJump = 0, prevS = 0;
  for (let i = 0; i < 35; i++) {
    g.step(1);
    const expect = cue.rewindTrack[cue.rewindCursor];
    maxDeviation = Math.max(maxDeviation, Math.hypot(cue.x - expect.x, cue.y - expect.y));
    const s = speed(target);
    if (s > prevS + 1e-9) { kickSteps++; maxJump = Math.max(maxJump, s - prevS); }
    prevS = s;
  }
  g.releaseR();
  const st = g.settle({ maxSeconds: 20 });
  const obs = {
    corridorEntry: [round(mid.x, 6), round(mid.y, 6)], objectStart: [round(p0.x, 6), round(p0.y, 6)],
    objectEnd: [round(target.x, 6), round(target.y, 6)],
    maxCueDeviationFromHistory_px: maxDeviation,
    collisionStepsWithKick: kickSteps, maxSpeedJumpInOneStep: round(maxJump, 6),
    objectPushVector: [round(target.x - p0.x, 4), round(target.y - p0.y, 4)],
    objectRestAfterSec: round(st.seconds, 4), objectFinalSpeed: round(speed(target), 9),
    statsHitsDelta: g.state.stats.hits - hits0,
    cueSpeedAfterRelease: round(speed(cue), 6)
  };
  const pass = maxDeviation === 0 && kickSteps >= 1 && Math.hypot(target.x - p0.x, target.y - p0.y) > 1 && obs.statsHitsDelta >= 1;
  record('T04', 'Rewinding cue ball never leaves its history; the object ball is pushed and continues', ok(pass), obs,
    'cue deviation is exactly 0 px on every rewound step including overlap steps; the object ball is pushed along the contact normal');
}

// =============================================================== T05
function T05() {
  const g = createGame();
  g.placeBall(0, 300, 280, 500, 0);
  g.sim.set({ shotUsed: true });
  g.step(30);
  const pocket = g.consts.pockets[2];
  g.placeBall(1, pocket[0] - 6, pocket[1] + 6, 0, 0);
  g.step(1);
  const pottedAfter = g.ball(1).pocketed, countAfterPot = g.pocketedCount(), uiAfterPot = g.uiText('pocketed');
  const pos = { x: g.ball(1).x, y: g.ball(1).y, vx: g.ball(1).vx, vy: g.ball(1).vy };
  let minLen = Infinity;
  for (let i = 0; i < 6; i++) { g.rewindSteps(20); g.step(20); minLen = Math.min(minLen, g.cue().history.length); }
  const pos2 = { x: g.ball(1).x, y: g.ball(1).y, vx: g.ball(1).vx, vy: g.ball(1).vy };
  const obs = {
    pottedFlagAfterRails: pottedAfter, pocketedCount: countAfterPot, uiCounter: uiAfterPot,
    pocketedCountAfter6Rewinds: g.pocketedCount(), rewindsUsed: g.state.stats.rewinds,
    pottedBallMoved_px: round(Math.hypot(pos2.x - pos.x, pos2.y - pos.y), 12),
    pottedBallSpeed: round(Math.hypot(pos2.vx, pos2.vy), 12), minHistoryLenDuring: minLen
  };
  const pass = pottedAfter && countAfterPot === 1 && g.pocketedCount() === 1 &&
    obs.pottedBallMoved_px === 0 && obs.pottedBallSpeed === 0;
  record('T05', 'A potted object ball stays potted through repeated rewinds', ok(pass), obs,
    'rails() is never called for a pocketed ball; the counter/flag are monotone');
}

// =============================================================== T06
function T06() {
  const g = createGame();
  g.clearObjectBalls();
  const target = g.consts.pockets[0];
  const ang = Math.atan2(target[1] - 280, target[0] - 292) * 180 / Math.PI;
  g.shoot(ang, 100);
  let n = 0;
  while (!g.cue().pocketed && n < 2000) { g.step(1); n++; }
  const obs = {
    aimAngleDeg: round(ang, 3), power: 100, stepsToPot: n, cuePocketed: g.cue().pocketed,
    cueSpeedInPocket: round(speed(g.cue()), 12), historyLenAtPot: g.cue().history.length,
    potDistanceToPocketCentre: round(Math.hypot(g.cue().x - target[0], g.cue().y - target[1]), 6),
    uiCounterStillZero: g.uiText('pocketed')
  };
  g.pressR();
  obs.rewindAccepted = g.state.rewinding;
  g.step(1);
  obs.afterOneRewindStep = { pocketed: g.cue().pocketed, pos: [round(g.cue().x, 6), round(g.cue().y, 6)] };
  let guard = 0;
  while (g.state.rewinding && guard++ < 20000) g.step(1);
  g.releaseR();
  obs.stepsToFullReturn = guard;
  obs.finalPos = [round(g.cue().x, 6), round(g.cue().y, 6)];
  obs.historyEntry0 = [g.cue().history[0].x, g.cue().history[0].y];
  obs.finalPocketed = g.cue().pocketed;
  obs.finalSpeed = round(speed(g.cue()), 12);
  obs.finalOnTable = g.cue().x > g.consts.T.left && g.cue().x < g.consts.T.right && g.cue().y > g.consts.T.top && g.cue().y < g.consts.T.bottom;
  const pass = obs.rewindAccepted && obs.afterOneRewindStep.pocketed === false && obs.finalPocketed === false &&
    obs.finalSpeed === 0 && obs.finalOnTable && obs.finalPos[0] === 292 && obs.finalPos[1] === 280;
  record('T06', 'A pocketed cue ball returns to the table when R is pressed', ok(pass), obs,
    'every rewound step forces pocketed=false; a full rewind always parks the cue ball back on its history[0] = (292,280)');
}

// =============================================================== T07 / T08 / T09
function wallBounceProbe({ x, y, vx, vy, maxSteps = 4000 }) {
  const g = createGame();
  g.clearObjectBalls();
  const cue = g.placeBall(0, x, y, vx, vy);
  g.sim.set({ shotUsed: true });
  let prev = { vx: cue.vx, vy: cue.vy, s: speed(cue) };
  for (let i = 0; i < maxSteps; i++) {
    g.step(1);
    const s = speed(cue);
    const bouncedX = prev.vx !== 0 && Math.sign(cue.vx) !== Math.sign(prev.vx) && Math.abs(prev.vx) > 0.5;
    const bouncedY = prev.vy !== 0 && Math.sign(cue.vy) !== Math.sign(prev.vy) && Math.abs(prev.vy) > 0.5;
    if (bouncedX || bouncedY) {
      return { g, cue, i, bouncedX, bouncedY, prev, after: { x: cue.x, y: cue.y, vx: cue.vx, vy: cue.vy, s },
        arrivalSpeed: prev.s * damp(), ratio: s / (prev.s * damp()) };
    }
    prev = { vx: cue.vx, vy: cue.vy, s };
  }
  return null;
}

function T07() {
  const r = wallBounceProbe({ x: 400, y: 200, vx: 0, vy: -600 });
  const obs = {
    wall: 'top', stepsToBounce: r.i + 1,
    speedBeforeStep: round(r.prev.s, 9), speedOnArrival_afterDamping: round(r.arrivalSpeed, 9),
    speedAfterBounce: round(r.after.s, 9),
    ratio_after_vs_arrival: round(r.ratio, 12), ratio_after_vs_beforeStep: round(r.after.s / r.prev.s, 12),
    expected_ratio_vs_beforeStep: round(1.5 * damp(), 12),
    vyBefore: round(r.prev.vy, 6), vyAfter: round(r.after.vy, 6),
    restY: round(r.after.y, 9), wallContactLineY: round(63 + BALL_R, 9)
  };
  const pass = Math.abs(r.ratio - 1.5) < 1e-9 && r.after.vy > 0 && Math.abs(r.after.y - (63 + BALL_R)) < 1e-9;
  record('T07', 'Vertical rail bounce multiplies speed by exactly 1.5', ok(pass), obs,
    'amplification is exactly 1.5x on the damped arrival speed (1.48668x on the previous step speed)');
}

function T08() {
  const a = wallBounceProbe({ x: 400, y: 300, vx: 400, vy: -600 });
  const obs = {
    stepsToBounce: a.i + 1, vBefore: [round(a.prev.vx, 6), round(a.prev.vy, 6)],
    vAfter: [round(a.after.vx, 6), round(a.after.vy, 6)],
    ratio_after_vs_arrival: round(a.ratio, 12), expected_ratio: 1.5,
    tangentialRatio_vx: round(a.after.vx / (a.prev.vx * damp()), 9),
    normalRatio_vy: round(-a.after.vy / (a.prev.vy * damp()), 9),
    directionCorrect: a.after.vx > 0 && a.after.vy > 0
  };
  const passOblique = Math.abs(a.ratio - 1.5) < 1e-9 && a.after.vx > 0 && a.after.vy > 0;
  record('T08', 'Oblique rail bounce reflects correctly and scales the whole vector by 1.5', ok(passOblique), obs,
    'both the tangential and the normal component are multiplied by exactly 1.5');
}

// T08b -- the "one factor for a simultaneous two-wall frame" sub-clause is unreachable:
// inPocket() runs before the clamps and every corner pocket centre IS the corner, so any
// ball that would touch two rails is already inside the 24 px capture circle.
function T08b() {
  const g0 = createGame();
  const distToPocketCentre = Math.hypot(BALL_R, BALL_R);
  const attempted = [];
  for (const [px, py] of g0.consts.pockets) {
    const g = createGame();
    g.clearObjectBalls();
    const x0 = px + (px < 520 ? 60 : -60), y0 = py + (py < 300 ? 60 : -60);
    const gx = px - x0, gy = py - y0, L = Math.hypot(gx, gy);
    g.placeBall(0, x0, y0, (gx / L) * 600, (gy / L) * 600);
    g.sim.set({ shotUsed: true });
    let bounces = 0;
    for (let i = 0; i < 400; i++) {
      const vx = g.cue().vx, vy = g.cue().vy;
      g.step(1);
      if (vx !== 0 && Math.sign(g.cue().vx) !== Math.sign(vx)) bounces++;
      if (vy !== 0 && Math.sign(g.cue().vy) !== Math.sign(vy)) bounces++;
      if (g.cue().pocketed) break;
    }
    attempted.push({ pocket: [px, py], potted: g.cue().pocketed, signFlips: bounces });
  }
  const obs = {
    minDistanceForTwoWallClamp_px: round(distToPocketCentre, 6),
    pocketCaptureRadius_px: 24,
    twoWallClampAlwaysInsidePocketCircle: distToPocketCentre < 24,
    diagonalApproachIntoEachCorner: attempted,
    conclusion: 'a ball can never be clamped by two rails without being potted first -> the single-factor branch for simultaneous wall contact is dead code'
  };
  record('T08b', 'Simultaneous two-wall frame applies only one x1.5 (sub-clause)', 'NOT-AUTOMATABLE', obs,
    'geometrically unreachable: the two-wall contact point is hypot(R,R)=17.25 px from the corner pocket centre, inside the 24 px capture radius, and inPocket() runs before the clamps');
}

function T09() {
  const g = createGame();
  g.clearObjectBalls();
  const cue = g.placeBall(0, 400, 200, 0, -600);
  g.sim.set({ shotUsed: true });
  let bounceIdx = -1;
  for (let i = 0; i < 80; i++) {
    g.step(1);
    if (bounceIdx < 0 && cue.vy > 0) bounceIdx = cue.history.length - 1;
    if (bounceIdx >= 0 && i > 60) break;
  }
  const track = cue.history.map((p) => ({ ...p }));
  const bounceEntry = track[bounceIdx], prevEntry = track[bounceIdx - 1];
  const storedSpeed = speed(bounceEntry);
  const impliedSpeed = Math.hypot(bounceEntry.x - prevEntry.x, bounceEntry.y - prevEntry.y) / DT;
  const maxStored = Math.max(...track.map((p) => speed(p)));
  g.pressR();
  let maxRewindSpeed = 0, steps = 0;
  while (g.state.rewinding && g.cue().rewindCursor > bounceIdx - 2 && steps < 500) {
    g.step(1); steps++;
    maxRewindSpeed = Math.max(maxRewindSpeed, speed(g.cue()));
  }
  const releaseSpeed = speed(g.cue());
  const releaseIdx = g.cue().rewindCursor;
  g.releaseR();
  const obs = {
    bounceHistoryIndex: bounceIdx, forwardSteps: track.length,
    storedSpeedAtBounceEntry: round(storedSpeed, 9),
    positionImpliedSpeedAtBounceEntry: round(impliedSpeed, 9),
    ratio_implied_over_stored: round(impliedSpeed / storedSpeed, 9),
    maxStoredSpeedInTrack: round(maxStored, 9),
    maxRewindSpeedWhilePassingBounce: round(maxRewindSpeed, 9),
    boostDuringRewind: maxRewindSpeed > maxStored * 1.000001,
    releaseSpeed: round(releaseSpeed, 9), releaseCursor: releaseIdx,
    releaseSpeed_vs_storedAtBounce: round(releaseSpeed / storedSpeed, 9)
  };
  const pass = !obs.boostDuringRewind && releaseSpeed <= maxStored * 1.000001;
  record('T09', 'Rewinding across an old rail-bounce point gives no second x1.5', ok(pass), obs,
    'no rail code runs for a reverse ball; the traversal speed derived from the clamped position delta is only ~2/3 of the velocity stored at that entry');
}

// =============================================================== T10
function T10() {
  const g = createGame();
  g.sim.init();
  g.sim.set({ nowTime: 0 });
  const cue = g.cue();
  cue.vx = 600;
  g.sim.set({ shotUsed: true });
  g.step(30);
  g.pressR(); g.step(3);
  const rewindingBeforeWin = g.state.rewinding;
  let pottedCount = 0;
  for (const b of g.balls) {
    if (b.id === 0) continue;
    const p = g.consts.pockets[(b.id - 1) % 6];
    g.placeBall(b.id, p[0], p[1], 0, 0);
    g.step(1);
    if (b.pocketed) pottedCount++;
    if (g.state.won) break;
  }
  const obs = {
    objectBallsPotted: pottedCount, uiCounter: g.uiText('pocketed'), won: g.state.won, statusText: g.uiText('statusText'),
    rewindingBeforeWin, rewindingAfterWin: g.state.rewinding,
    reverseFlagAfterWin: g.cue().reverse, rewindTrackAfterWin: g.cue().rewindTrack
  };
  const rewindsNow = g.state.stats.rewinds;
  g.pressR();
  obs.rewindBlockedAfterWin = g.state.stats.rewinds === rewindsNow && !g.state.rewinding;
  g.document.getElementById('reset').dispatch('click', { preventDefault() {} });
  obs.afterReset = {
    pocketedCounter: g.uiText('pocketed'), won: g.state.won, shotUsed: g.state.shotUsed,
    rewinds: g.state.stats.rewinds, hits: g.state.stats.hits,
    cuePos: [g.cue().x, g.cue().y], cueSpeed: speed(g.cue()),
    objectBallsOnTable: g.balls.filter((b) => b.id !== 0 && !b.pocketed).length,
    statusText: g.uiText('statusText')
  };
  obs.shotAllowedAfterReset = g.shoot(0, 100).speed === 740;
  const pass = obs.won === true && obs.uiCounter === '10/10' && obs.rewindingAfterWin === false &&
    obs.rewindBlockedAfterWin && obs.afterReset.won === false && obs.afterReset.pocketedCounter === '0/10' &&
    obs.afterReset.objectBallsOnTable === 10 && obs.shotAllowedAfterReset;
  record('T10', 'All ten object balls potted shows the clear result; reset restores the initial state', ok(pass), obs,
    'win check lives in rails(); it also force-ends an in-flight rewind and blocks further rewinds');
}

// =============================================================== T11
function T11() {
  const g = createGame();
  g.shoot(0, 170);
  g.settle({ maxSeconds: 20 });
  const rnd = mulberry(7);
  let simSeconds = 0, rewinds = 0, refused = 0, iters = 0, renders = 0;
  const t0 = process.hrtime.bigint();
  while (simSeconds < 600 && iters < 4000) {
    iters++;
    const hold = 20 + Math.floor(rnd() * 220);
    g.pressR();
    if (!g.state.rewinding) {
      refused++; g.step(60); simSeconds += 0.5;
      if (refused > 20) break;
      continue;
    }
    let n = 0;
    while (g.state.rewinding && n < hold) { g.step(1); n++; simSeconds += DT; }
    g.releaseR();
    const s = g.settle({ maxSeconds: 12 });
    simSeconds += s.seconds;
    rewinds = g.state.stats.rewinds;
    if (g.state.won) break;
    if (iters % 25 === 0) { g.render(); renders++; }
  }
  const wallMs = Number(process.hrtime.bigint() - t0) / 1e6;
  const t1 = process.hrtime.bigint();
  for (let i = 0; i < 200; i++) g.render();
  const renderPerCall = Number(process.hrtime.bigint() - t1) / 1e6 / 200;
  const histNow = g.cue().history.length;
  const t2 = process.hrtime.bigint();
  for (let i = 0; i < 200; i++) { g.pressR(); g.releaseR(); }
  const slicePerPress = Number(process.hrtime.bigint() - t2) / 1e6 / 200;
  const obs = {
    simulatedSeconds: round(simSeconds, 2), iterations: iters, rewindPresses: rewinds, refusedPresses: refused,
    wallClockMs: round(wallMs, 1), simSecondsPerWallSecond: round(simSeconds / (wallMs / 1000), 2),
    stepsRun: g.metrics.steps, usPerStep: round((wallMs * 1000) / Math.max(1, g.metrics.steps), 3),
    maxSpeedAnyBall: finite(g.metrics.maxSpeed) ? round(g.metrics.maxSpeed, 3) : 'Infinity',
    maxSpeedBallId: g.metrics.maxSpeedBall, maxObjectBallSpeed: round(g.metrics.maxObjectSpeed, 3),
    maxHistoryEntries: g.metrics.maxHistory, historyNow: histNow,
    maxEntryDelta_px: round(g.metrics.maxEntryDelta, 6), maxRewindJump_px: round(g.metrics.maxRewindJump, 6),
    nanDetected: g.metrics.nan, outOfBoundsFrames: g.metrics.outOfBounds,
    renderCalls: renders + 200, renderMsPerCall: round(renderPerCall, 4),
    rewindPressMs_atHistoryLen: round(slicePerPress, 5),
    pocketed: g.pocketedCount(), won: g.state.won
  };
  const pass = !g.metrics.nan && g.metrics.outOfBounds === 0 && simSeconds >= 300 && rewinds >= 20;
  record('T11', 'Long session: no script error, no position corruption, no tunnelling', ok(pass), obs,
    '600 simulated seconds of scripted play: no NaN, no out-of-bounds frame, no exception; render stays sub-ms');
}

// =============================================================== PROBES

function D1_speed_runaway() {
  // exact bounce map: v_{n+1} = 1.5*(v_n - 1.07*L)  ->  unstable fixed point v* = 1.5*1.07*L/0.5 = 3.21*L
  const L_H = 670.8, L_V = 409.6, L_D = 1006;
  const vStarExact = (L) => 1.5 * K_DAMP * L / 0.5;
  const vStarTimeBased = (L) => K_DAMP * L / Math.log(1.5);
  const threshold = [];
  for (const v of [600, 800, 900, 1000, 1050, 1082, 1100, 1200, 1258, 1500]) {
    const g = createGame();
    g.clearObjectBalls();
    g.placeBall(0, 292, 280, 0, v);
    g.sim.set({ shotUsed: true });
    let bounces = 0, prevSign = Math.sign(v);
    const leaveSpeeds = [];
    for (let i = 0; i < 120 * 20; i++) {
      g.step(1);
      const cue = g.cue();
      if (cue.vy !== 0 && Math.sign(cue.vy) !== prevSign) {
        prevSign = Math.sign(cue.vy); bounces++;
        if (bounces <= 6) leaveSpeeds.push(round(speed(cue), 2));
        if (bounces >= 7) break;
      }
    }
    threshold.push({ v0: v, leaveSpeedPerBounce: leaveSpeeds, growing: leaveSpeeds.length >= 3 && leaveSpeeds[2] > leaveSpeeds[1] });
  }
  const cases = [];
  const divergedAngles = [];
  for (const a of [0, 20, 45, 60, 90, 120, 135, 160, 180, 200, 225, 250, 270, 300, 315, 340]) {
    const g = createGame();
    g.clearObjectBalls();
    g.shoot(a, MAX_POWER);
    for (let i = 0; i < 60 / DT; i++) g.step(1);
    const cue = g.cue();
    if (g.metrics.divergedAtStep >= 0 || !finite(g.metrics.maxSpeed)) divergedAngles.push(a);
    cases.push({
      angle: a, maxSpeed: finite(g.metrics.maxSpeed) ? round(g.metrics.maxSpeed, 2) : 'Infinity',
      divergedAtStep: g.metrics.divergedAtStep, nan: g.metrics.nan, outOfBounds: g.metrics.outOfBounds,
      finalSpeed: finite(speed(cue)) ? round(speed(cue), 2) : 'Infinity'
    });
  }
  const real = [];
  for (const a of [0, 45, 90, 135, 180, 225, 270, 315]) {
    const g = createGame();
    g.shoot(a, MAX_POWER);
    for (let i = 0; i < 60 / DT; i++) g.step(1);
    const cue = g.cue();
    real.push({
      angle: a, pocketed: g.pocketedCount(),
      maxSpeed: finite(g.metrics.maxSpeed) ? round(g.metrics.maxSpeed, 2) : 'Infinity',
      divergedAtStep: g.metrics.divergedAtStep,
      secondsToDivergence: g.metrics.divergedAtStep < 0 ? null : round(g.metrics.divergedAtStep * DT, 3),
      nan: g.metrics.nan, outOfBoundsFrames: g.metrics.outOfBounds,
      finalCue: [finite(cue.x) ? round(cue.x, 2) : 'NaN', finite(cue.y) ? round(cue.y, 2) : 'NaN',
        finite(cue.vx) ? round(cue.vx, 2) : 'Infinity', finite(cue.vy) ? round(cue.vy, 2) : 'Infinity']
    });
  }
  probe('D1', 'Rail boost x1.5 long-term stability: unstable fixed point and speed blow-up', {
    boostMapExact: 'v_{n+1} = 1.5 * (v_n - 1.07*L)   (speed loss over a flight of L px is exactly 1.07*L)',
    unstableFixedPoint_exact: round(vStarExact(L_V), 1),
    unstableFixedPoint_timeBasedApproximation: round(vStarTimeBased(L_V), 1),
    thresholds: {
      shortAxisFromSpawn_204_8: round(vStarExact(204.8), 1),
      vertical_409_6: round(vStarExact(L_V), 1),
      horizontal_670_8: round(vStarExact(L_H), 1),
      diagonal_1006: round(vStarExact(L_D), 1),
      timeBased_approximation_409_6: round(vStarTimeBased(L_V), 1)
    },
    thresholdSweep_vertical: threshold,
    emptyTable_16angles_60s: cases, emptyTable_divergedAngles: divergedAngles,
    realisticTable_8angles_60s: real,
    reachableByPlayer: 'a single legal full-power shot straight down the table (angle 90) diverges: the first flight is only 204.8 px, threshold 3.21*204.8 = 657 < 1258'
  }, 'above 3.21*L the bounce map grows exponentially; positions stay clamped but the velocity reaches Infinity and coordinates become NaN');
}

function D2_pinning() {
  const T = { left: 65, right: 975, top: 63, bottom: 497 }, R = BALL_R;
  const spots = [
    { name: 'left-wall', x: T.left + R + 0.05, y: 280, vx: -300, vy: 0 },
    { name: 'top-wall', x: 400, y: T.top + R + 0.05, vx: 0, vy: -300 },
    { name: 'topleft-corner', x: T.left + R + 0.05, y: T.top + R + 0.05, vx: -300, vy: -300 },
    { name: 'bottomright-corner', x: T.right - R - 0.05, y: T.bottom - R - 0.05, vx: 300, vy: 300 }
  ];
  const configs = [];
  let maxSpeed = 0, best = null;
  for (const s of spots) {
    for (const off of [18, 22, 24, 26]) {
      for (const dir of [[1, 0], [0, 1], [1, 1], [-1, 0], [0, -1], [-1, -1]]) {
        const g = createGame();
        g.clearObjectBalls();
        g.placeBall(0, s.x, s.y, s.vx, s.vy);
        g.placeBall(1, s.x + dir[0] * off, s.y + dir[1] * off, 0, 0);
        g.placeBall(2, s.x + dir[0] * off * 2, s.y + dir[1] * off * 2, 0, 0);
        g.sim.set({ shotUsed: true });
        g.step(2400);
        const m = g.metrics.maxSpeed;
        if (m > maxSpeed) { maxSpeed = m; best = { spot: s.name, offset: off, dir, finalSpeed: round(speed(g.cue()), 3) }; }
        configs.push(round(m, 2));
      }
    }
  }
  probe('D2', 'Can wall-pinning farm unlimited energy? 96 configurations x 20 s', {
    configurations: configs.length, maxSpeedOverAllConfigs: round(maxSpeed, 2), worstCase: best,
    medianMaxSpeed: round(summarize(configs).median, 2), p90: round(summarize(configs).p90, 2)
  }, 'no configuration beat the normal boost equilibrium; pinning does not farm energy');
}

function D3_tunneling() {
  const probeOne = (v, phaseSamples) => {
    let misses = 0;
    for (let k = 0; k < phaseSamples; k++) {
      const g = createGame();
      g.clearObjectBalls();
      const offset = (k / phaseSamples) * (v * DT);
      g.placeBall(0, 250 - offset, 280, v, 0);
      const target = g.placeBall(1, 700, 280, 0, 0);
      let hit = false;
      for (let i = 0; i < 400; i++) {
        g.step(1);
        if (speed(target) > 1e-9) { hit = true; break; }
        if (g.cue().x > 860) break;
      }
      if (!hit) misses++;
    }
    return { v, stepPx: round(v * DT, 3), phases: phaseSamples, missedContacts: misses, missRate: round(misses / phaseSamples, 3) };
  };
  const rows = [];
  for (const v of [1000, 1900, 2500, 3500, 5000, 5856, 7000, 10000, 20000, 40000]) rows.push(probeOne(v, 16));
  probe('D3', 'Discrete collision (no CCD, STEP=1/120) head-on tunnelling sweep', {
    analyticThreshold: round(4 * BALL_R / DT, 1),
    analyticNote: 'head-on: a miss needs one step to jump the whole 2R band, v > 4R/dt = 5856 px/s',
    sweep: rows,
    firstSpeedWithMisses: (rows.find((r) => r.missedContacts > 0) || {}).v ?? null,
    fullyMissedAbove: rows.filter((r) => r.missRate === 1).map((r) => r.v)
  }, 'above 5856 px/s an exact head-on contact can be skipped; near-grazes fail far earlier (see D13)');
}

function D4_gate_and_jitter() {
  const g = createGame();
  g.shoot(0, 170);
  g.settle({ maxSeconds: 20 });
  const rnd = mulberry(11);
  for (let k = 0; k < 80; k++) {
    g.pressR();
    let n = 0;
    while (g.state.rewinding && n < 60 + Math.floor(rnd() * 200)) { g.step(1); n++; }
    g.releaseR();
    g.settle({ maxSeconds: 8 });
  }
  const m = g.metrics;
  const h = g.cue().history;
  const entryDeltas = [];
  for (let i = 1; i < h.length; i++) entryDeltas.push(Math.hypot(h[i].x - h[i - 1].x, h[i].y - h[i - 1].y));
  const stats = summarize(entryDeltas);
  const slowShare = entryDeltas.filter((d) => d < 1).length / entryDeltas.length;
  const g2 = createGame();
  g2.shoot(0, 170);
  g2.settle({ maxSeconds: 25 });
  const atRestLen = g2.cue().history.length;
  const restPos = { x: g2.cue().x, y: g2.cue().y };
  g2.step(120 * 120);
  const g3 = createGame();
  g3.clearObjectBalls();
  g3.placeBall(0, 300, 160, 900, 0);
  g3.sim.set({ shotUsed: true });
  g3.step(50);
  const fast = g3.cue().history.map((p) => ({ ...p }));
  const rw = g3.rewindSteps(30);
  let maxSeenJump = 0;
  for (let i = 1; i < rw.seen.length; i++) maxSeenJump = Math.max(maxSeenJump, Math.hypot(rw.seen[i].x - rw.seen[i - 1].x, rw.seen[i].y - rw.seen[i - 1].y));
  const storedSpacings = [];
  for (let i = 1; i < fast.length; i++) storedSpacings.push(Math.hypot(fast[i].x - fast[i - 1].x, fast[i].y - fast[i - 1].y));
  probe('D4', 'record() gate / entry spacing / rewind teleport / creep at rest', {
    scriptedSession_entries: stats.n,
    entryDelta_px: { min: round(stats.min, 9), median: round(stats.median, 4), p90: round(stats.p90, 4), p99: round(stats.p99, 4), max: round(stats.max, 4) },
    shareOfEntriesBelow1px: round(slowShare, 4),
    recordGateThreshold_px: 1e-5,
    maxSpeedSeen: round(m.maxSpeed, 2), impliedMaxJumpPerStep_px: round(m.maxSpeed * DT, 4),
    fastSegmentRewind: {
      flightSpeed: 900, recordedSpacingMax_px: round(Math.max(...storedSpacings), 4),
      rewindJumpMax_px: round(maxSeenJump, 4)
    },
    restTest: {
      historyAtRest: atRestLen, historyAfter120sAtRest: g2.cue().history.length,
      entriesAddedWhileAtRest: g2.cue().history.length - atRestLen,
      cueDrift_px: round(Math.hypot(g2.cue().x - restPos.x, g2.cue().y - restPos.y), 9), stillAtRest: g2.atRest()
    },
    maxStepSpeedMultiplierSeen: round(m.maxStepMultiplier, 6), maxStepMultiplierAtStep: m.maxStepMultiplierAt,
    singleRailBoostMultiplier: round(1.5 * damp(), 6)
  }, 'a rail boost alone shows up as x1.48668 per step; anything clearly above that would be two boosts in one frame');
}

function D5_fps() {
  const rows = [];
  for (const fps of [60, 30, 20, 15, 10, 5]) {
    const g = createGame();
    g.shoot(0, 170);
    const stepMs = 1000 / fps;
    let ts = 500000, steps = 0;
    g.rafDriver(ts);
    const frames = Math.round(fps * 2);
    for (let i = 0; i < frames; i++) { ts += stepMs; steps += g.frame(ts).simSteps; }
    rows.push({
      fps, frames, realSeconds: round(frames * stepMs / 1000, 4), simulatedSeconds: round(steps * DT, 4),
      simTimePerRealTime: round((steps * DT) / (frames * stepMs / 1000), 4),
      stepsPerFrame: round(steps / frames, 4), frameCapSec: round(Math.min(0.045, stepMs / 1000), 4)
    });
  }
  const mk = () => {
    const g = createGame();
    g.clearObjectBalls();
    g.placeBall(0, 300, 160, 600, 0);
    g.sim.set({ shotUsed: true });
    g.step(120);
    return g;
  };
  const holdOneSecond = (fps) => {
    const g = mk();
    const stepMs = 1000 / fps;
    let ts = 900000, steps = 0;
    g.rafDriver(ts);
    g.pressR();
    const start = g.cue().rewindCursor;
    for (let i = 0; i < Math.round(fps * 1); i++) { ts += stepMs; steps += g.frame(ts).simSteps; }
    const consumed = start - g.cue().rewindCursor;
    const v = { vx: g.cue().vx, vy: g.cue().vy };
    g.releaseR();
    return { fps, realSeconds: 1, simSteps: steps, entriesConsumed: consumed, releasedV: [round(v.vx, 12), round(v.vy, 12)], releasedSpeed: round(Math.hypot(v.vx, v.vy), 12) };
  };
  const sample = (fps) => {
    const g = mk();
    const stepMs = 1000 / fps;
    let ts = 700000;
    g.rafDriver(ts);
    g.pressR();
    const out = [];
    for (let i = 0; i < Math.round(fps * 2); i++) {
      ts += stepMs; g.frame(ts);
      out.push({ cursor: g.cue().rewindCursor, vx: round(g.cue().vx, 9), vy: round(g.cue().vy, 9) });
      if (g.cue().rewindCursor <= 40) break;
    }
    g.releaseR();
    return out;
  };
  const s60 = sample(60), s10 = sample(10);
  const cmp = [];
  for (const a of s60) {
    const b = s10.find((x) => x.cursor === a.cursor);
    if (b) cmp.push({ cursor: a.cursor, v60: a.vx, v10: b.vx, identical: a.vx === b.vx && a.vy === b.vy });
  }
  const a = holdOneSecond(60), b = holdOneSecond(10);
  probe('D5', 'FPS coupling: frame cap / slow motion / is the release velocity frame-rate dependent?', {
    fpsTimeDilation: rows,
    holdOneRealSecond: { at60fps: a, at10fps: b, entriesRatio_60_over_10: round(a.entriesConsumed / b.entriesConsumed, 4) },
    releaseVelocityIsPureFunctionOfCursorIndex: {
      comparedIndices: cmp.length, allIdentical: cmp.length > 0 && cmp.every((c) => c.identical), samples: cmp.slice(0, 6)
    },
    frameCapSeconds: 0.045, sixStepCapSeconds: round(6 * DT, 6), sixStepCapCanBind: 6 * DT > 0.045,
    note: 'the 6-step cap (0.05 s) is larger than the 0.045 s frame cap, so the binding limiter is the frame clamp -> slow motion below 22.2 FPS'
  });
}

function D6_memory() {
  const g = createGame();
  g.shoot(0, 170);
  g.settle({ maxSeconds: 25 });
  const l0 = g.cue().history.length, s0 = g.steps;
  for (let k = 0; k < 40; k++) {
    g.pressR();
    let n = 0;
    while (g.state.rewinding && n < 120) { g.step(1); n++; }
    g.releaseR();
    g.settle({ maxSeconds: 10 });
  }
  const entries = g.cue().history.length, stepsRun = g.steps - s0;
  const perStep = (entries - l0) / stepsRun;
  const h = g.cue().history;
  const serialized = v8.serialize(h).length;
  let heapPerEntry = null;
  if (global.gc) {
    const keep = [];
    global.gc();
    const before = process.memoryUsage().heapUsed;
    for (let r = 0; r < 8; r++) keep.push(h.map((p) => ({ x: p.x, y: p.y, vx: p.vx, vy: p.vy })));
    global.gc();
    const after = process.memoryUsage().heapUsed;
    heapPerEntry = (after - before) / (8 * h.length);
    globalThis.__keep = keep;
  }
  probe('D6', 'Cue-ball history memory growth and 10-minute projection', {
    historyEntries: entries, physicsSteps: stepsRun, entriesPerPhysicsStep: round(perStep, 6),
    entriesPerSecondWhileMoving: round(perStep * 120, 3),
    serializedBytesPerEntry: round(serialized / h.length, 3),
    heapBytesPerEntry: heapPerEntry === null ? 'run with --expose-gc' : round(heapPerEntry, 2),
    projection_10min_continuousMotion: {
      entries: 120 * 600,
      serializedMB: round((serialized / h.length) * 120 * 600 / 1048576, 4),
      heapMB: heapPerEntry === null ? null : round(heapPerEntry * 120 * 600 / 1048576, 4)
    }
  });
}

function D7_pockets_and_softlock() {
  const g0 = createGame();
  const T = g0.consts.T, pockets = g0.consts.pockets;
  const railY = T.top + BALL_R;
  const captureHalfWidth = Math.sqrt(Math.max(0, 24 * 24 - (railY - 63) ** 2));
  const g = createGame();
  g.clearObjectBalls();
  const b1 = g.placeBall(1, 520, 63 + 23, 0, 0);
  const insideBefore = g.sim.inPocket(b1);
  g.step(1);
  const pottedNextStep = b1.pocketed;
  let minHistAtPot = Infinity, pottedShots = 0, refusedAfterPot = 0;
  for (const p of pockets) {
    for (const power of [60, 120, 170]) {
      const gg = createGame();
      gg.clearObjectBalls();
      gg.shoot(Math.atan2(p[1] - 280, p[0] - 292) * 180 / Math.PI, power);
      let n = 0;
      while (!gg.cue().pocketed && n < 800) { gg.step(1); n++; }
      if (gg.cue().pocketed) {
        pottedShots++;
        minHistAtPot = Math.min(minHistAtPot, gg.cue().history.length);
        gg.pressR();
        if (!gg.state.rewinding) refusedAfterPot++;
        gg.releaseR();
      }
    }
  }
  const gf = createGame();
  gf.clearObjectBalls();
  const cf = gf.cue();
  cf.x = 65; cf.y = 86; cf.vx = 0; cf.vy = 0;
  cf.history = [{ x: 65, y: 86, vx: 0, vy: 0 }];
  gf.sim.set({ shotUsed: true });
  gf.step(1);
  const lockedPotted = cf.pocketed, lockedLen = cf.history.length;
  gf.pressR();
  const rewindAccepted = gf.state.rewinding;
  gf.releaseR();
  gf.step(300);
  const stillStuck = cf.pocketed && speed(cf) === 0;
  const maxStepPx = MAX_POWER * SPEED_PER_POWER * DT;
  const nearestPocket = Math.min(...pockets.map(([x, y]) => Math.hypot(x - 292, y - 280)));
  probe('D7', 'Pocket capture radius / can a ball rest inside a pocket / softlock construction', {
    captureRadius: 24, drawnPocketRadiusOuter: 26, drawnPocketRadiusInner: 17,
    sidePocket_railRestingCaptureHalfWidth_px: round(captureHalfWidth, 4),
    sidePocket_railRestingCaptureXRange: [round(520 - captureHalfWidth, 3), round(520 + captureHalfWidth, 3)],
    ballInsideCaptureCircleOneStepBeforePot: { inPocketBeforeStep: insideBefore, pottedOnNextStep: pottedNextStep },
    legalPotSweep: { shotsFired: pockets.length * 3, shotsThatPottedTheCueBall: pottedShots, minHistoryLenAtPotMoment: minHistAtPot, rewindRefusedAfterPot: refusedAfterPot },
    theoreticalSoftlock_injectedState: { pottedWithHistoryLen: lockedLen, pottedFlag: lockedPotted, startRewindAccepted: rewindAccepted, stillStuckAfter300Steps: stillStuck },
    reachabilityArgument: {
      maxShotSpeed_px_s: MAX_POWER * SPEED_PER_POWER, maxStepPx: round(maxStepPx, 4),
      nearestPocketCentreFromCueStart_px: round(nearestPocket, 3), pocketCaptureDiameter_px: 48,
      note: 'a legal pot needs the ball centre to travel into a 24 px capture circle; at 10.5 px/step the cue ball always records history first'
    }
  });
}

function D8_kick_and_farming() {
  const rows = [];
  for (const v of [100, 200, 400, 600, 800, 1000, 1200, 2000]) {
    const g = createGame();
    g.clearObjectBalls();
    const cue = g.placeBall(0, 300, 280, v, 0);
    g.sim.set({ shotUsed: true });
    g.step(40);
    const track = cue.history.map((p) => ({ ...p }));
    const mid = track[Math.floor(track.length * 0.6)];
    const target = g.placeBall(1, mid.x, mid.y, 0, 0);
    const rewindSpeed = speed(track[track.length - 1]);
    g.pressR();
    let contacts = 0, prevS = 0;
    for (let i = 0; i < 30; i++) {
      g.step(1);
      const s = speed(target);
      if (s > prevS + 1e-9) contacts++;
      prevS = s;
    }
    g.releaseR();
    rows.push({
      cueRewindSpeed: round(rewindSpeed, 3), kickContacts: contacts, objectSpeedAfter: round(prevS, 3),
      theory_kick_min1300_1p6rel: round(Math.min(1300, 1.6 * rewindSpeed), 3),
      ratio_objectSpeed_over_cueSpeed: round(prevS / rewindSpeed, 3)
    });
  }
  const gp = createGame();
  gp.clearObjectBalls();
  const c2 = gp.placeBall(0, 300, 280, 500, 0);
  gp.sim.set({ shotUsed: true });
  gp.step(40);
  const t2 = c2.history.map((p) => ({ ...p }));
  const p2 = t2[Math.floor(t2.length * 0.6)];
  const tg = gp.placeBall(1, p2.x, p2.y, 0, 0);
  const cyclic = [];
  for (let k = 0; k < 12; k++) {
    gp.pressR();
    let n = 0;
    while (gp.state.rewinding && n < 8) { gp.step(1); n++; }
    gp.releaseR();
    gp.step(8);
    cyclic.push(round(speed(tg), 3));
  }
  probe('D8', 'Rewind kick = min(1300, 1.6*rel): momentum injection and energy farming', {
    speedSweep: rows,
    repeatedRewindIntoSameBall_objectSpeedPerCycle: cyclic,
    peakObjectSpeed: Math.max(...cyclic),
    conservation: 'not conserving by design (GDD 4.4): the pushed ball gains up to 1.6x the cue ball rewind speed, capped at 1300 px/s'
  });
}

function D9_reach_and_rest() {
  const timeToRest = (v) => Math.log(v / STOP_V) / K_DAMP;
  const distanceToRest = (v) => (v - STOP_V) / K_DAMP;
  const analytic = [1258, 1300, 2400, 600, 300, 88.8].map((v) => ({
    v0: v, secondsToRest: round(timeToRest(v), 3), distanceBudget_px: round(distanceToRest(v), 1)
  }));
  const ge = createGame();
  ge.clearObjectBalls();
  ge.placeBall(0, 300, 280, 1258, 0);
  ge.sim.set({ shotUsed: true });
  let n = 0;
  while (!ge.atRest() && n < 20 / DT) { ge.step(1); n++; }
  const measured = { secondsToRest: round(n * DT, 4), travelledNet_px: round(ge.cue().x - 300, 3), finalX: round(ge.cue().x, 3) };
  const g = createGame();
  const T = g.consts.T;
  const cueToRack = 675 - 292;
  const minPowerRack = (STOP_V + K_DAMP * cueToRack) / SPEED_PER_POWER;
  const farthest = Math.max(Math.hypot(T.left - 292, T.top - 280), Math.hypot(T.right - 292, T.top - 280),
    Math.hypot(T.left - 292, T.bottom - 280), Math.hypot(T.right - 292, T.bottom - 280));
  const minPowerFarthest = (STOP_V + K_DAMP * farthest) / SPEED_PER_POWER;
  const gb = createGame();
  gb.shoot(0, 170);
  const breakSettle = gb.settle({ maxSeconds: 30 });
  const gb2 = createGame();
  gb2.shoot(0, minPowerRack);
  const thr = gb2.settle({ maxSeconds: 30 });
  probe('D9', 'Friction / time to rest / energy budget / reachability', {
    damping: 'exp(-1.07*dt)', stopThreshold_px_per_s: STOP_V, analytic,
    measured_drySingleShot: measured,
    reach: {
      cueStart: [292, 280], rackHeadBall: [675, 280], cueToRack_px: round(cueToRack, 3),
      minLaunchSpeedToReachRack: round(STOP_V + K_DAMP * cueToRack, 2),
      minPowerToReachRack_of170: round(minPowerRack, 2), minPowerFraction: round(minPowerRack / MAX_POWER, 3),
      farthestTablePoint_px: round(farthest, 2), minPowerToReachFarthest: round(minPowerFarthest, 2),
      speedArrivingAtRackAtFullPower: round(MAX_POWER * SPEED_PER_POWER - K_DAMP * cueToRack, 1),
      energyBudgetAtFullPower_px: round(distanceToRest(MAX_POWER * SPEED_PER_POWER), 1)
    },
    fullPowerStraightBreak: {
      pocketed: gb.pocketedCount(), restSeconds: round(breakSettle.seconds, 3),
      maxObjectSpeed: round(gb.metrics.maxObjectSpeed, 2), maxCueSpeed: round(gb.metrics.maxSpeed, 2),
      cueSpeedWhenAllRest: round(speed(gb.cue()), 3)
    },
    thresholdPowerBreak: { power: round(minPowerRack, 2), pocketed: gb2.pocketedCount(), restSeconds: round(thr.seconds, 3) }
  });
}

function D10_divergence_blast_radius() {
  const g = createGame();
  g.shoot(90, MAX_POWER);
  let infAt = -1, nanAt = -1, firstOOB = -1;
  const firstBadByBall = {};
  for (let i = 0; i < 120 * 30; i++) {
    g.step(1);
    const cue = g.cue();
    if (infAt < 0 && !finite(speed(cue))) infAt = i;
    if (nanAt < 0 && (!finite(cue.x) || !finite(cue.y))) nanAt = i;
    if (firstOOB < 0 && g.metrics.outOfBounds > 0) firstOOB = i;
    for (const b of g.balls) {
      if (firstBadByBall[b.id]) continue;
      if (!finite(b.x) || !finite(b.y) || !finite(b.vx) || !finite(b.vy)) {
        firstBadByBall[b.id] = { ballId: b.id, step: i + 1, tSec: round((i + 1) * DT, 4), x: String(b.x), y: String(b.y), vx: String(b.vx), vy: String(b.vy), pocketed: b.pocketed };
      }
    }
    if (infAt >= 0 && nanAt >= 0 && i > nanAt + 600) break;
  }
  const cue = g.cue();
  const stateAt = {
    steps: g.metrics.steps, seconds: round(g.metrics.steps * DT, 3),
    speedFirstInfiniteAtStep: infAt, secondsToInfinity: infAt < 0 ? null : round(infAt * DT, 3),
    coordsFirstNonFiniteAtStep: nanAt, secondsToFirstNonFiniteCoord: nanAt < 0 ? null : round(nanAt * DT, 3),
    cue: { x: String(cue.x), y: String(cue.y), vx: String(cue.vx), vy: String(cue.vy), pocketed: cue.pocketed, historyLen: cue.history.length },
    maxSpeed: finite(g.metrics.maxSpeed) ? round(g.metrics.maxSpeed, 1) : 'Infinity',
    nanFlag: g.metrics.nan, outOfBoundsFrames: g.metrics.outOfBounds,
    objectBallsPocketed: g.pocketedCount(),
    firstNonFinitePerBall: firstBadByBall, nonFiniteBallCount: Object.keys(firstBadByBall).length
  };
  g.pressR();
  const rewindAccepted = g.state.rewinding;
  let recoveredAt = -1, n = 0;
  while (g.state.rewinding && n < 20000) {
    g.step(1); n++;
    if (recoveredAt < 0 && g.balls.every((b) => finite(b.x) && finite(b.y) && finite(b.vx) && finite(b.vy))) recoveredAt = n;
  }
  g.releaseR();
  const afterRewind = {
    accepted: rewindAccepted, steps: n, allFiniteAgainAtRewindStep: recoveredAt,
    cue: { x: String(g.cue().x), y: String(g.cue().y), speed: String(speed(g.cue())) },
    pocketed: g.cue().pocketed, historyLen: g.cue().history.length
  };
  let renderThrows = null;
  try { g.render(); } catch (e) { renderThrows = String((e && e.message) || e); }
  const nonFiniteHistory = g.cue().history.filter((p) => !finite(p.x) || !finite(p.y)).length;
  const gp = createGame();
  const victim = gp.placeBall(1, 292, 400, 0, 0);
  gp.shoot(90, MAX_POWER);
  let victimBadAt = -1;
  for (let i = 0; i < 120 * 20; i++) {
    gp.step(1);
    if (victimBadAt < 0 && (!finite(victim.x) || !finite(victim.y) || !finite(victim.vx) || !finite(victim.vy))) victimBadAt = i + 1;
    if (victimBadAt >= 0 && i > victimBadAt + 120) break;
  }
  const countBad = gp.balls.filter((b) => !finite(b.x) || !finite(b.y) || !finite(b.vx) || !finite(b.vy)).length;
  let winStillPossible = null;
  if (countBad > 0) {
    for (const b of gp.balls) {
      if (b.id === 0 || b.pocketed) continue;
      const p = gp.consts.pockets[(b.id - 1) % 6];
      gp.placeBall(b.id, p[0], p[1], 0, 0);
      gp.step(1);
    }
    winStillPossible = gp.state.won;
  }
  probe('D10', 'Blast radius of the speed blow-up after one legal max-power shot', {
    trigger: 'g.shoot(90, 170) -- straight down the table from (292,280), nothing in the way',
    stateAt, afterRewind, renderThrows, nonFiniteHistoryEntries: nonFiniteHistory,
    nanPropagation: {
      setup: 'object ball #1 parked at (292,400) on the diverged cue ball corridor',
      victimFirstNonFiniteAtStep: victimBadAt, nonFiniteBallCountAfter: countBad, winCheckStillFires: winStillPossible
    },
    verdict: 'after ~17 s the cue ball velocity is Infinity (coordinates stay clamped and finite); one rewound step restores a finite state, so the failure is recoverable only by using the rewind verb'
  }, 'the divergence path is a single legal input with no rewind involved');
}

function D11_friction_and_path() {
  const dry = [];
  for (const power of [40, 60, 80]) {
    const v0 = power * SPEED_PER_POWER;
    const g = createGame();
    g.clearObjectBalls();
    g.placeBall(0, 292, 280, v0, 0);
    g.sim.set({ shotUsed: true });
    let prev = { x: 292, y: 280 }, arc = 0, n = 0, railHits = 0;
    while (!g.atRest() && n < 30 / DT) {
      const sBefore = speed(g.cue());
      g.step(1); n++;
      arc += Math.hypot(g.cue().x - prev.x, g.cue().y - prev.y);
      prev = { x: g.cue().x, y: g.cue().y };
      if (speed(g.cue()) > sBefore * 1.01) railHits++;
    }
    dry.push({
      power, v0, measuredSeconds: round(n * DT, 4), measuredArc_px: round(arc, 2),
      analyticSeconds: round(Math.log(v0 / STOP_V) / K_DAMP, 4),
      analyticBudget_px: round((v0 - STOP_V) / K_DAMP, 2),
      railHits, endX: round(g.cue().x, 2), stopsBeforeRightRail: g.cue().x < 962.8
    });
  }
  const gb = createGame();
  gb.clearObjectBalls();
  gb.shoot(0, MAX_POWER);
  const startX = gb.cue().x;
  let arc = 0, prev = { x: gb.cue().x, y: gb.cue().y }, n = 0;
  const bounces = [];
  let prevSign = Math.sign(gb.cue().vx);
  while (!gb.atRest() && n < 40 / DT) {
    gb.step(1); n++;
    arc += Math.hypot(gb.cue().x - prev.x, gb.cue().y - prev.y);
    prev = { x: gb.cue().x, y: gb.cue().y };
    if (gb.cue().vx !== 0 && Math.sign(gb.cue().vx) !== prevSign) {
      prevSign = Math.sign(gb.cue().vx);
      bounces.push({ index: bounces.length + 1, tSec: round(n * DT, 3), leaveSpeed: round(speed(gb.cue()), 1) });
    }
  }
  const full = {
    launchSpeed: MAX_POWER * SPEED_PER_POWER, measuredSeconds: round(n * DT, 4),
    measuredArc_px: round(arc, 2), analyticSingleFlightBudget_px: round((MAX_POWER * SPEED_PER_POWER - STOP_V) / K_DAMP, 2),
    bounceCount: bounces.length, bounces, endX: round(gb.cue().x, 2), travelledNet_px: round(gb.cue().x - startX, 2),
    tableLength_px: gb.consts.T.right - gb.consts.T.left
  };
  const T = gb.consts.T;
  const rows = [];
  for (let a = 0; a < 360; a += 15) {
    const rad = (a * Math.PI) / 180, dx = Math.cos(rad), dy = Math.sin(rad);
    const cands = [];
    if (dx > 1e-9) cands.push((T.right - BALL_R - 292) / dx);
    if (dx < -1e-9) cands.push((T.left + BALL_R - 292) / dx);
    if (dy > 1e-9) cands.push((T.bottom - BALL_R - 280) / dy);
    if (dy < -1e-9) cands.push((T.top + BALL_R - 280) / dy);
    rows.push({ angle: a, firstRailDistance_px: round(Math.min(...cands.filter((t) => t > 0)), 2) });
  }
  const budget = (MAX_POWER * SPEED_PER_POWER - STOP_V) / K_DAMP;
  probe('D11', 'Friction / time to rest / actual path length (lead item 1)', {
    damping: 'exp(-1.07*dt)', stopThreshold: STOP_V,
    dryShots: dry, maxPowerEmptyTable: full,
    railAlwaysReached: {
      frictionBudget_px: round(budget, 2),
      minFirstRailDistance_px: round(Math.min(...rows.map((r) => r.firstRailDistance_px)), 2),
      maxFirstRailDistance_px: round(Math.max(...rows.map((r) => r.firstRailDistance_px)), 2),
      allAnglesReachARail: rows.every((r) => r.firstRailDistance_px < budget)
    }
  }, 'the 1167 px friction budget is longer than the table, so at full power the cue ball always hits a rail and the boost re-injects energy');
}

function D12_rack_geometry() {
  const g = createGame();
  const ob = g.balls.filter((b) => b.id !== 0);
  let minPair = Infinity, minNames = null, overlapping = 0;
  const gaps = [];
  for (let i = 0; i < ob.length; i++) {
    for (let j = i + 1; j < ob.length; j++) {
      const d = Math.hypot(ob[i].x - ob[j].x, ob[i].y - ob[j].y);
      if (d < 2 * BALL_R) overlapping++;
      gaps.push({ a: ob[i].id, b: ob[j].id, d: round(d, 4) });
      if (d < minPair) { minPair = d; minNames = [ob[i].id, ob[j].id]; }
    }
  }
  gaps.sort((x, y) => x.d - y.d);
  const gt = createGame();
  gt.tightenRack();
  const tob = gt.balls.filter((b) => b.id !== 0);
  let tMin = Infinity, tOverlap = 0;
  for (let i = 0; i < tob.length; i++) {
    for (let j = i + 1; j < tob.length; j++) {
      const d = Math.hypot(tob[i].x - tob[j].x, tob[i].y - tob[j].y);
      if (d < 2 * BALL_R - 1e-9) tOverlap++;
      if (d < tMin) tMin = d;
    }
  }
  const breaks = [];
  for (const ang of [-6, -3, 0, 3, 6]) {
    for (const power of [100, 140, 170]) {
      const run = (tight) => {
        const gg = createGame();
        if (tight) gg.tightenRack();
        const spawn = gg.balls.filter((b) => b.id !== 0).map((b) => ({ x: b.x, y: b.y }));
        gg.shoot(ang, power);
        const st = gg.settle({ maxSeconds: 30 });
        const now = gg.balls.filter((b) => b.id !== 0);
        const disp = now.map((b, i) => Math.hypot(b.x - spawn[i].x, b.y - spawn[i].y));
        let maxPair = 0;
        for (let i = 0; i < now.length; i++) for (let j = i + 1; j < now.length; j++) {
          if (now[i].pocketed || now[j].pocketed) continue;
          maxPair = Math.max(maxPair, Math.hypot(now[i].x - now[j].x, now[i].y - now[j].y));
        }
        return {
          pocketed: gg.pocketedCount(), meanDisplacement: round(disp.reduce((a, b) => a + b, 0) / disp.length, 2),
          maxPairDistance: round(maxPair, 2), maxDisplacement: round(Math.max(...disp), 2),
          maxObjectSpeed: round(gg.metrics.maxObjectSpeed, 1), restSeconds: round(st.seconds, 3)
        };
      };
      const loose = run(false), tight = run(true);
      breaks.push({ angle: ang, power, loose, tight, pocketedDelta: tight.pocketed - loose.pocketed });
    }
  }
  const sum = (k, which) => breaks.reduce((a, b) => a + b[which][k], 0);
  probe('D12', 'Rack geometry: spacing, overlap, tight vs loose break A/B (lead item 3)', {
    spawnedRack: {
      nearestNeighbourCenterDistance_px: round(minPair, 4), pairIds: minNames,
      touchingDistance_2R: 24.4, surfaceGap_px: round(minPair - 2 * BALL_R, 4),
      overlappingPairs: overlapping,
      columnSpacing_used: round(BALL_R * 1.82, 4), columnSpacing_tight: round(2 * BALL_R * Math.cos(Math.PI / 6), 4),
      rowSpacing_used: round(BALL_R * 2.12, 4), rowSpacing_tight: 2 * BALL_R,
      fiveSmallestPairDistances: gaps.slice(0, 5),
      leadClaimedNeighbourDistance: 28.146, leadClaimedGap: 3.746,
      note: 'hypot(R*1.82/2, R*2.12) is not an actual pair; the real neighbours are hypot(R*1.82, R*2.12/2) = 25.695'
    },
    tightRackAfterReposition: { minNeighbourDistance_px: round(tMin, 6), overlappingPairs: tOverlap },
    breakAB: breaks,
    totals: {
      loosePocketed: sum('pocketed', 'loose'), tightPocketed: sum('pocketed', 'tight'),
      looseMeanDisplacement: round(sum('meanDisplacement', 'loose') / breaks.length, 2),
      tightMeanDisplacement: round(sum('meanDisplacement', 'tight') / breaks.length, 2),
      looseMaxObjectSpeed: round(Math.max(...breaks.map((b) => b.loose.maxObjectSpeed)), 1),
      tightMaxObjectSpeed: round(Math.max(...breaks.map((b) => b.tight.maxObjectSpeed)), 1)
    }
  });
}

function distPointSeg(px, py, a, b) {
  const dx = b.x - a.x, dy = b.y - a.y;
  const len2 = dx * dx + dy * dy;
  let t = len2 > 0 ? ((px - a.x) * dx + (py - a.y) * dy) / len2 : 0;
  t = Math.max(0, Math.min(1, t));
  return Math.hypot(px - (a.x + t * dx), py - (a.y + t * dy));
}

// Constructed pass-through repro: sample the empty-table trajectory, park an object ball at a
// perpendicular offset `imp` from a fast passage point, and check whether the prototype registers
// the contact at all even though the continuous trajectory overlaps the ball.
function findPassThrough(shots) {
  for (const shot of shots) {
    const { angle, power } = shot;
    const g0 = createGame();
    g0.clearObjectBalls();
    g0.shoot(angle, power);
    const path = [];
    for (let i = 0; i < 2000; i++) {
      g0.step(1);
      const c = g0.cue();
      const s = speed(c);
      if (!finite(s) || s > 1e7) break;
      path.push({ x: c.x, y: c.y, s, i, vx: c.vx, vy: c.vy });
    }
    const T = g0.consts.T;
    for (const imp of [0, 12, 20, 22, 23.5]) {
      let tried = 0;
      for (let k = path.length - 1; k >= 40 && tried < 12; k -= 11) {
        const p = path[k];
        if (p.s < 1900) break;
        const vm = p.s, nx = -p.vy / vm, ny = p.vx / vm;
        const bx = p.x + nx * imp, by = p.y + ny * imp;
        if (bx < T.left + BALL_R || bx > T.right - BALL_R || by < T.top + BALL_R || by > T.bottom - BALL_R) continue;
        // the ball must NOT be reachable by any earlier (slower) part of the trajectory,
        // otherwise a slow pass would register the contact first and mask the fast one.
        let blockedEarlier = false;
        for (let i = 1; i <= k - 2; i++) {
          if (distPointSeg(bx, by, path[i - 1], path[i]) < 2 * BALL_R + 1) { blockedEarlier = true; break; }
        }
        if (blockedEarlier) continue;
        tried++;
        const g = createGame();
        g.clearObjectBalls();
        const ball = g.placeBall(1, bx, by, 0, 0);
        g.shoot(angle, power);
        let hit = false, minSampled = Infinity;
        for (let i = 0; i < 2000; i++) {
          g.step(1);
          const c = g.cue();
          const s = speed(c);
          if (!finite(s)) break;
          const d = Math.hypot(c.x - ball.x, c.y - ball.y);
          if (d < minSampled) minSampled = d;
          if (speed(ball) > 1e-9 || Math.hypot(ball.x - bx, ball.y - by) > 1e-6) { hit = true; break; }
        }
        let contMin = Infinity;
        for (let i = 1; i < path.length; i++) {
          const d = distPointSeg(bx, by, path[i - 1], path[i]);
          if (d < contMin) contMin = d;
        }
        const rec = {
          angle, power, impactParam: imp, ballAt: [round(bx, 2), round(by, 2)],
          passageStep: p.i, cueSpeedAtPassage: round(p.s, 1), cueStepPx: round(p.s * DT, 2),
          contactDetected: hit, minSampledDistance: round(minSampled, 3),
          continuousMinDistanceToTrajectory: round(contMin, 3),
          trajectoryOverlapsBall: contMin < 2 * BALL_R,
          passThrough: !hit && contMin < 2 * BALL_R - 0.5
        };
        if (rec.passThrough) return rec;
      }
    }
  }
  return null;
}

function D13_tunneling_repro() {
  const profile = [];
  {
    const g = createGame();
    g.clearObjectBalls();
    g.shoot(90, MAX_POWER);
    let prevSign = Math.sign(g.cue().vy), b = 0;
    for (let i = 0; i < 120 * 20; i++) {
      g.step(1);
      const c = g.cue();
      const s = speed(c);
      if (!finite(s)) { profile.push({ bounce: b + 1, leaveSpeed: 'Infinity', stepPx: 'Infinity', tSec: round(i * DT, 3) }); break; }
      if (c.vy !== 0 && Math.sign(c.vy) !== prevSign) {
        prevSign = Math.sign(c.vy); b++;
        profile.push({ bounce: b, leaveSpeed: round(s, 1), stepPx: round(s * DT, 2), tSec: round(i * DT, 3) });
        if (b >= 14) break;
      }
    }
  }
  const search = findPassThrough([
    { angle: 90, power: 170 }, { angle: 270, power: 170 },
    { angle: 45, power: 170 }, { angle: 135, power: 170 },
    { angle: 200, power: 170 }, { angle: 250, power: 170 },
    { angle: 70, power: 170 }, { angle: 110, power: 170 }
  ]);
  const map = [];
  for (const v of [1000, 1900, 2500, 3500, 4500, 5856, 7000, 10000]) {
    for (const imp of [0, 5, 10, 15, 20, 22, 23.5]) {
      let misses = 0;
      const phases = 12;
      for (let k = 0; k < phases; k++) {
        const g = createGame();
        g.clearObjectBalls();
        g.placeBall(0, 250 - (k / phases) * (v * DT), 280, v, 0);
        const t = g.placeBall(1, 700, 280 + imp, 0, 0);
        let hit = false;
        for (let i = 0; i < 400; i++) {
          g.step(1);
          if (speed(t) > 1e-9) { hit = true; break; }
          if (g.cue().x > 880) break;
        }
        if (!hit) misses++;
      }
      map.push({ v, stepPx: round(v * DT, 2), impactParam: imp, chordLength_px: round(2 * Math.sqrt(Math.max(0, (2 * BALL_R) ** 2 - imp ** 2)), 2), missRate: round(misses / phases, 3) });
    }
  }
  probe('D13', 'Concrete tunnelling failure after rail boosts (lead item 2 repro)', {
    bounceProfile_power170_angle90: profile,
    naiveHeadOnThreshold_px_s: round(4 * BALL_R / DT, 1),
    passThroughRepro: search,
    passThroughSearchNote: 'object ball parked at a perpendicular offset from a fast passage of the empty-table trajectory; contactDetected=false while the continuous trajectory overlaps the ball',
    phaseImpactMissMap: map
  }, 'a ball parked near the trajectory (impact parameter b) is passed through once the per-step jump exceeds the chord 2*sqrt((2R)^2-b^2)');
}

// =============================================================== REGRESSION CASES
function R01() {
  const g = createGame();
  g.shoot(90, MAX_POWER);
  let firstBad = null, firstInfAt = -1, nanAt = -1, firstRunawayAt = -1, peak = 0;
  const perBall = {};
  const wallStart = process.hrtime.bigint();
  for (let i = 0; i < 120 * 20; i++) {
    g.step(1);
    for (const b of g.balls) {
      const s = Math.hypot(b.vx, b.vy);
      if (finite(s) && s > peak) peak = s;
      if (firstInfAt < 0 && finite(b.x) && finite(b.y) && !finite(s)) firstInfAt = i + 1;
      if (nanAt < 0 && (!finite(b.x) || !finite(b.y))) nanAt = i + 1;
      if (firstRunawayAt < 0 && finite(s) && s > 1e5) firstRunawayAt = i + 1;
      const bad = !finite(b.x) || !finite(b.y) || !finite(b.vx) || !finite(b.vy) || (finite(s) && s > 1e5);
      if (bad && !perBall[b.id]) {
        perBall[b.id] = { ballId: b.id, step: i + 1, tSec: round((i + 1) * DT, 4), x: String(b.x), y: String(b.y), vx: String(b.vx), vy: String(b.vy) };
        if (!firstBad) firstBad = perBall[b.id];
      }
    }
  }
  const wallMs = Number(process.hrtime.bigint() - wallStart) / 1e6;
  regression('R01', 'One legal max-power shot must not blow up ball state (finite, below 1e5 px/s)', firstBad !== null, {
    reproduction: { call: 'g.shoot(90, 170) on a fresh table', angleDeg: 90, power: MAX_POWER, launchSpeed: MAX_POWER * SPEED_PER_POWER },
    simulatedSeconds: 20, wallClockMs: round(wallMs, 1),
    firstViolatingBall: firstBad, violatingBallCount: Object.keys(perBall).length,
    firstSpeedAbove1e5AtStep: firstRunawayAt, secondsTo1e5: firstRunawayAt < 0 ? null : round(firstRunawayAt * DT, 3),
    firstInfiniteSpeedAtStep: firstInfAt, secondsToInfinity: firstInfAt < 0 ? null : round(firstInfAt * DT, 3),
    firstNonFiniteCoordinateAtStep: nanAt,
    peakFiniteSpeed: round(peak, 1),
    finalCue: { x: String(g.cue().x), y: String(g.cue().y), vx: String(g.cue().vx), vy: String(g.cue().vy) },
    invariant: 'every ball coordinate and speed stays finite and below 1e5 px/s for 20 s after any legal input',
    launchSpeedForScale: MAX_POWER * SPEED_PER_POWER
  }, 'the x1.5 rail boost has an unstable fixed point (v > 3.21*L): the short-axis flight grows without bound');
}

function R02() {
  const rows = [];
  for (const fps of [60, 30, 20, 10, 5]) {
    const gg = createGame();
    gg.shoot(0, 170);
    const stepMs = 1000 / fps;
    let ts = 200000, steps = 0;
    gg.rafDriver(ts);
    const frames = Math.round(fps * 3);
    for (let i = 0; i < frames; i++) { ts += stepMs; steps += gg.frame(ts).simSteps; }
    const simSeconds = steps * DT, realSeconds = (frames * stepMs) / 1000;
    rows.push({ fps, simSeconds: round(simSeconds, 4), realSeconds: round(realSeconds, 4), ratio: round(simSeconds / realSeconds, 4) });
  }
  const worst = rows.reduce((a, b) => (b.ratio < a.ratio ? b : a));
  regression('R02', 'Simulated time must never exceed real time (low FPS = slow motion, not catch-up)', worst.ratio > 1.0001, {
    rows, worstCase: worst, frameCapSeconds: 0.045, sixStepCapSeconds: round(6 * DT, 6)
  }, 'low FPS produces slow motion (sim/real < 1); the port must keep that behaviour deliberately');
}

function R03() {
  const g = createGame();
  g.clearObjectBalls();
  const b = g.placeBall(1, 520, 63 + 23, 0, 0);
  const inside = g.sim.inPocket(b);
  g.step(1);
  regression('R03', 'A ball can never rest inside a pocket capture circle without being potted', inside && !b.pocketed, {
    placedInsideCaptureCircle: inside, pottedOnNextStep: b.pocketed, captureRadius: 24, drawnOuterRadius: 26
  }, 'rails() runs before the pocket freeze in every step, so capture is immediate');
}

function R04() {
  const g = createGame();
  const target = g.consts.pockets[0];
  g.shoot(Math.atan2(target[1] - 280, target[0] - 292) * 180 / Math.PI, 100);
  let n = 0;
  while (!g.cue().pocketed && n < 2000) { g.step(1); n++; }
  const potted = g.cue().pocketed, histAtPot = g.cue().history.length;
  g.pressR();
  const accepted = g.state.rewinding;
  let k = 0;
  while (g.state.rewinding && k < 20000) { g.step(1); k++; }
  g.releaseR();
  const onTable = !g.cue().pocketed && g.cue().x === 292 && g.cue().y === 280;
  regression('R04', 'A pocketed cue ball can always be fully rewound to the spawn point (292,280)', potted && !(accepted && onTable), {
    cuePotted: potted, historyLenAtPot: histAtPot, rewindAccepted: accepted,
    finalPos: [g.cue().x, g.cue().y], finalPocketed: g.cue().pocketed, stepsUsed: k
  }, 'history[0] is always the spawn point, so a full rewind always restores the cue ball');
}

// =============================================================== main
const t0 = process.hrtime.bigint();
const fp = sourceFingerprint();
console.log('REWIND h5 headless acceptance + probes + regressions');
console.log('fingerprint html        :', fp.htmlSha256);
console.log('fingerprint script body :', fp.scriptBodySha256);
console.log('fingerprint instrumented:', fp.instrumentedSourceSha256);
console.log('slice rule              :', fp.sliceRule);
console.log('body range              :', fp.bodyStartChar, '..', fp.bodyStartChar + fp.bodyLengthChars, 'of', fp.fileBytes, 'bytes');
console.log('gc-exposed:', !!global.gc);
let hashMismatch = false;
if (fp.htmlSha256 !== PINNED.html) { console.log('FATAL: h5/index.html changed (pinned ' + PINNED.html + ')'); hashMismatch = true; }
if (fp.scriptBodySha256 !== PINNED.scriptBody) { console.log('FATAL: script body changed (pinned ' + PINNED.scriptBody + ')'); hashMismatch = true; }
if (hashMismatch) {
  console.log('The frozen prototype moved: every number below would be from a different artifact.');
  console.log('Update PINNED only after re-reviewing the diff.');
  process.exit(3);
}
console.log('');

for (const fn of [T01, T02, T03, T04, T05, T06, T07, T08, T08b, T09, T10, T11]) {
  try { fn(); } catch (e) { record(fn.name, fn.name, 'FAIL', { error: String((e && e.stack) || e) }, 'uncaught exception in test'); }
}
for (const fn of [D1_speed_runaway, D2_pinning, D3_tunneling, D4_gate_and_jitter, D5_fps, D6_memory, D7_pockets_and_softlock, D8_kick_and_farming, D9_reach_and_rest, D10_divergence_blast_radius, D11_friction_and_path, D12_rack_geometry, D13_tunneling_repro]) {
  try { fn(); } catch (e) { probe(fn.name, fn.name, { error: String((e && e.stack) || e) }, 'uncaught exception in probe'); }
}
for (const fn of [R01, R02, R03, R04]) {
  try { fn(); } catch (e) { regression(fn.name, fn.name, true, { error: String((e && e.stack) || e) }, 'uncaught exception in regression'); }
}

console.log('===== GDD section 9 acceptance cases =====');
for (const r of T_CASES) {
  console.log(`${r.status.padEnd(15)} ${r.id}  ${r.title}`);
  console.log(`     observed: ${JSON.stringify(r.observed)}`);
  if (r.note) console.log(`     note: ${r.note}`);
}
console.log('\n===== regression cases =====');
for (const r of REGRESSIONS) {
  console.log(`${r.status.padEnd(15)} ${r.id}  ${r.title}`);
  console.log(`     observed: ${JSON.stringify(r.observed)}`);
  if (r.note) console.log(`     note: ${r.note}`);
}
console.log('\n===== defect probes =====');
for (const p of PROBES) {
  console.log(`-- ${p.id}  ${p.title}`);
  console.log(`   ${JSON.stringify(p.observed)}`);
  if (p.note) console.log(`   note: ${p.note}`);
}
const failed = T_CASES.filter((r) => r.status === 'FAIL');
const na = T_CASES.filter((r) => r.status === 'NOT-AUTOMATABLE');
const regFailed = REGRESSIONS.filter((r) => r.status === 'FAIL');
const wallMs = Number(process.hrtime.bigint() - t0) / 1e6;
console.log(`\nSUMMARY: T ${T_CASES.length - failed.length - na.length}/${T_CASES.length} PASS, ${failed.length} FAIL, ${na.length} NOT-AUTOMATABLE | R ${REGRESSIONS.length - regFailed.length}/${REGRESSIONS.length} PASS, ${regFailed.length} FAIL`);
if (failed.length) console.log('FAILED T: ' + failed.map((f) => f.id).join(', '));
if (regFailed.length) console.log('FAILED R: ' + regFailed.map((f) => f.id).join(', '));
fs.writeFileSync(path.join(HERE, 'results.json'), JSON.stringify({
  generatedAt: new Date().toISOString(), fingerprint: fp, gc: !!global.gc,
  cases: T_CASES, probes: PROBES, regressions: REGRESSIONS, wallMs
}, null, 2));
console.log('wrote results.json  (' + round(wallMs / 1000, 1) + ' s)');
if (failed.length) process.exit(1);
if (regFailed.length) process.exit(2);
process.exit(0);
