// Re-place the five playable levels with an INCREASING ball count, and give the later levels the shot
// allowance and rewind budget they need for that to be meaningful.
//
// Deliberately NO solvability search: the user asked to place the balls and verify by playing, and the
// search had already cost far more time than it was worth. What this script DOES guarantee is the things
// a solver would not have caught anyway:
//   * no two balls closer than 2R + margin, so nothing spawns already overlapping;
//   * the cue ball is not moved;
//   * nothing spawns inside a pocket's capture circle, which would pot it on step 1;
//   * nothing spawns on top of the white hole;
//   * spacing/position are deterministic, so the same script always produces the same table.
//
// Ball TYPES are assigned so the four space bodies all appear as the levels grow: 彗星 (object),
// 陨石 (heavy), 脉冲星 (bomb), 星云 (anchor).
import fs from 'node:fs';
import path from 'node:path';

const LEVELS = path.join('godot', 'levels');

// id, balls total (objects), maxShots, maxSegments, maxDistance, whiteHole?, railBoost?
//
// BALL PROGRESSION (user's call): 2 / 3 / 4 / 5 / 6.
//
// MECHANIC GATING, also the user's call, and the reason this is a table rather than a constant:
//   * WHITE HOLE appears from level 2 onward. Level 1 has none, so the player meets the base rule --
//     one strike, then rewind -- before anything else is on the table.
//   * RAIL ENERGY (the cue gaining speed off a cushion) appears only from level 4 onward. It is gated by
//     cueWallGain / maxWallBoosts on the CUE type. It must be explicitly OFF in levels 1-3 rather than
//     merely unused: an implicit free-energy source changes what a level's tolerance window means, so
//     leaving it inherited would silently alter those levels.
const PLAN = [
  ['corridor',            2, 1,  8, 2600, false, false],
  ['l1_midrail_reverse',  3, 1, 10, 3400, true,  false],
  ['l2_midrail_mirrored', 4, 1, 12, 4200, true,  false],
  ['l3_deeper_walk',      5, 2, 14, 5200, true,  true ],
  ['l4_offset_line',      6, 2, 16, 6400, true,  true ],
];

// The white hole sits off the right rail, out of the strike column, so it is a destination the player
// chooses to route through rather than something the opening shot happens to fall into. It affects the
// CUE only, so object balls stay predictable while a level is being learned.
const WHITE_HOLE = {
  x: 840, y: 280, r: 38,
  gain: 1.6, exitSpeed: 700, maxSpeed: 1500, cooldown: 0.5,
  affectsObjectBalls: 0,
  note: '靠右库边的单个白洞。只影响白球，让目标球保持可预测。',
};

// Rail energy for the cue: +120 px/s per cushion, capped at 4 boosts. The cap is what stops a level
// becoming a matter of hunting for a long rail.
const CUE_WALL_GAIN = 120;
const CUE_MAX_WALL_BOOSTS = 4;

const R = 12.2;          // ball radius from the level files
const GAP = 2 * R + 4;   // minimum centre distance between any two balls

function load(id) {
  const p = path.join(LEVELS, `${id}.json`);
  const raw = fs.readFileSync(p, 'utf8');
  return { p, raw, j: JSON.parse(raw) };
}

function insidePocket(j, x, y) {
  for (const pk of j.pockets || []) {
    const d = Math.hypot(pk.x - x, pk.y - y);
    // Must clear capture radius + ball radius, or the ball is potted the moment the world steps.
    if (d < (pk.r || 24) + R + 6) return true;
  }
  return false;
}

function onWhiteHole(j, x, y) {
  for (const h of j.whiteHoles || []) {
    if (Math.hypot(h.x - x, h.y - y) < (h.r || 30) + R + 4) return true;
  }
  return false;
}

// A deterministic spread that avoids the cue and every placed ball. Walks a lattice of candidate spots
// in a fixed order, so the layout is reproducible and reviewable rather than random.
function place(j, count, cue) {
  const L = j.table.left + 60, Rt = j.table.right - 60;
  const T = j.table.top + 60, B = j.table.bottom - 60;

  const spots = [];
  const cols = 6, rows = 4;
  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < cols; c++) {
      // Stagger alternate rows so balls do not line up in a grid that reads as artificial.
      const fx = (c + 0.5 + (r % 2 ? 0.25 : 0)) / cols;
      const fy = (r + 0.5) / rows;
      spots.push([L + (Rt - L) * fx, T + (B - T) * fy]);
    }
  }

  const chosen = [];
  for (const [x, y] of spots) {
    if (chosen.length >= count) break;
    if (Math.hypot(cue.x - x, cue.y - y) < GAP) continue;
    if (insidePocket(j, x, y)) continue;
    if (onWhiteHole(j, x, y)) continue;
    if (chosen.some(([cx, cy]) => Math.hypot(cx - x, cy - y) < GAP)) continue;
    chosen.push([x, y]);
  }
  return chosen;
}

// Type rotation: the first two object balls stay plain 彗星 so the early game reads as normal billiards,
// then 陨石 / 脉冲星 / 星云 appear, each level starting the rotation a little further along so the
// later levels feel more varied.
const TYPE_ROTATION = ['object', 'heavy', 'bomb', 'anchor'];

let report = [];
for (const [id, count, shots, segs, dist, wantHole, wantRail] of PLAN) {
  const { p, j } = load(id);

  const cue = j.balls.find(b => b.type === 'cue');
  if (!cue) { console.log(`SKIP ${id}: no cue ball`); continue; }

  const existingObjects = j.balls.filter(b => b.type !== 'cue');

  // LEVEL 1 IS SPECIAL: it already has the required 2 balls, and its layout is the ONLY one verified to
  // clear (a collaborator restored it deliberately). Re-placing it would throw away the one known-good
  // table for no benefit, so it is left byte-for-byte alone apart from the mechanic gating below.
  const keepLayout = id === 'corridor' && existingObjects.length === count;

  let placed = existingObjects.length;
  if (!keepLayout) {
    const spots = place(j, count, cue);
    j.balls = [cue];
    // The original witness block is preserved rather than rewritten: it documents what WAS verified, and
    // rewording it would silently claim verification for a layout nobody measured.
    spots.forEach(([x, y], i) => {
      j.balls.push({
        id: i + 1,
        type: TYPE_ROTATION[i % TYPE_ROTATION.length],
        x: Math.round(x * 100) / 100,
        y: Math.round(y * 100) / 100,
      });
    });
    placed = spots.length;
  }

  // ---- ball identity colours stay stable per level so the player can re-identify a ball after a reset
  j.balls.forEach((b, i) => { if (b.type !== 'cue') b.id = i; });

  j.budget = { maxSegments: segs, maxDistance: dist };
  j.physics = j.physics || {};
  j.physics.maxShots = shots;

  // ---- WHITE HOLE: present from level 2 onward, absent in level 1.
  if (wantHole) j.whiteHoles = [Object.assign({}, WHITE_HOLE)];
  else delete j.whiteHoles;

  // ---- RAIL ENERGY for the cue: ON from level 4 onward, explicitly OFF before that.
  // Written explicitly even when off, because "absent" would inherit the locked global CueWallGain of 120
  // and silently hand levels 1-3 the very mechanic they are supposed to introduce later.
  j.physics.types = j.physics.types || {};
  j.physics.types.cue = Object.assign({}, j.physics.types.cue, {
    wallGain: wantRail ? CUE_WALL_GAIN : 0,
    maxWallBoosts: wantRail ? CUE_MAX_WALL_BOOSTS : 0,
  });

  // Provenance, so a reader can tell this layout was NOT solver-verified.
  j.authoring = j.authoring || {};
  j.authoring.layout = {
    placedBy: 'tools/relayout_levels.mjs',
    verified: keepLayout,
    note: keepLayout
      ? 'Layout is the original verified 2-ball table, left untouched. Mechanic gating was applied.'
      : 'Ball layout is hand-placed for the ball-count progression and has NOT been solver-verified. ' +
        'Any witness block below describes an EARLIER layout and does not apply to this one.',
  };
  if (!keepLayout && j.authoring.witnesses) j.authoring.witnessesStale = true;

  fs.writeFileSync(p, JSON.stringify(j, null, 2) + '\n');
  report.push(
    `${id.padEnd(22)} objs=${placed} shots=${shots} budget=${segs}/${dist} ` +
    `whiteHole=${wantHole ? 'YES' : 'no '} railBoost=${wantRail ? 'YES' : 'no '} ` +
    `layout=${keepLayout ? 'KEPT (verified)' : 're-placed'}`);
}

console.log(report.join('\n'));
