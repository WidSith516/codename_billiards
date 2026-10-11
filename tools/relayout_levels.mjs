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

// id, balls total (objects), maxShots, maxSegments, maxDistance
const PLAN = [
  ['corridor',            3, 1,  8, 2600],
  ['l1_midrail_reverse',  4, 1, 10, 3200],
  ['l2_midrail_mirrored', 5, 2, 12, 4200],
  ['l3_deeper_walk',      6, 2, 14, 5200],
  ['l4_offset_line',      7, 3, 16, 6400],
];

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
for (const [id, count, shots, segs, dist] of PLAN) {
  const { p, raw, j } = load(id);

  const cue = j.balls.find(b => b.type === 'cue');
  if (!cue) { console.log(`SKIP ${id}: no cue ball`); continue; }

  const spots = place(j, count, cue);

  j.balls = [cue];
  // Keep the original witness block untouched: it is documentation of what WAS verified, and rewriting
  // it would silently claim verification for a layout nobody measured.
  spots.forEach(([x, y], i) => {
    j.balls.push({
      id: i + 1,
      type: TYPE_ROTATION[i % TYPE_ROTATION.length],
      x: Math.round(x * 100) / 100,
      y: Math.round(y * 100) / 100,
    });
  });

  j.budget = { maxSegments: segs, maxDistance: dist };
  j.physics = j.physics || {};
  j.physics.maxShots = shots;

  // Provenance, so a reader can tell this layout was NOT solver-verified.
  j.authoring = j.authoring || {};
  j.authoring.layout = {
    placedBy: 'tools/relayout_levels.mjs',
    verified: false,
    note: 'Ball layout is hand-placed for ball-count progression and has NOT been solver-verified. ' +
          'The witnesses below describe an EARLIER 2-ball layout and do not apply to this one.',
  };
  if (j.authoring.witnesses) j.authoring.witnessesStale = true;

  fs.writeFileSync(p, JSON.stringify(j, null, 2) + '\n');
  report.push(`${id}: ${spots.length} objects, maxShots=${shots}, budget=${segs}/${dist}`);
}

console.log(report.join('\n'));
