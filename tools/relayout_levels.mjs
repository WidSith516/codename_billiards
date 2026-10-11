// Restructure every level into three explicit layers, and apply the difficulty changes.
//
// WHY THE SPLIT (the user's requirement: "packaging and ability layers separated, so puzzles and
// multi-solution design are cheap later"):
//
//   visual    -- what the level LOOKS like and is CALLED. Names and the body skin. Changing this must never
//                change how the level plays, so nothing here is read by the simulation.
//   abilities -- WHAT THE LEVEL CAN DO: which mechanics exist and at what strength. This is the layer a
//                puzzle designer edits to create a new puzzle or to open a second solution path. Every
//                ability is a named module with its own parameter object, so adding one is data.
//   physics   -- the RAW simulation constants (masses, friction, restitution, table geometry). Shared, and
//                the layer a puzzle should rarely need to touch.
//
// The practical win: to make a puzzle easier you raise an ability; to make it a DIFFERENT puzzle you swap an
// ability. Neither touches the other layer, and neither touches the art.
//
// ALSO APPLIED HERE (the user's numbers):
//   * white hole strengthened to gain 2.0 / exitSpeed 900 / cooldown 0.4 -- it was not visibly accelerating;
//   * rewindKick added, a floor of 35% of the launch speed so a rewind always restarts the ball;
//   * rewind budget widened from level 2 on, because levels 2+ hold more balls than one strike plus the old
//     budget could ever reach.
//
// Deliberately NO solvability search. The user asked to place the balls and verify by playing. What this
// script DOES guarantee is what a solver would not have caught anyway: no two balls closer than 2R+4,
// nothing spawning inside a pocket capture circle (which would pot it on step 1), nothing on the white hole,
// and the cue ball never moved.
import fs from 'node:fs';
import path from 'node:path';

const LEVELS = path.join('godot', 'levels');

// id, objects, maxShots, segments, distance, whiteHole, railKick, rewindKick
//
// BALL PROGRESSION 2/3/4/5/6 and MECHANIC UNLOCKS are the user's design:
//   L1 nothing yet -- base rule only, and its table is NOT touched (the only verified layout)
//   L2 WHITE HOLE appears
//   L4 RAIL KICK appears
// rewindKick is on from L2 everywhere: "the rewind must always be able to restart the ball" is a usability
// floor, not a puzzle mechanic, so it is not gated as content.
const PLAN = [
  ['corridor',            2, 1,  8, 2600, false, false, false],
  ['l1_midrail_reverse',  3, 1, 12, 4200, true,  false, true ],
  ['l2_midrail_mirrored', 4, 1, 14, 5200, true,  false, true ],
  ['l3_deeper_walk',      5, 2, 16, 6200, true,  true,  true ],
  ['l4_offset_line',      6, 2, 18, 7400, true,  true,  true ],
];

const WHITE_HOLE = {
  x: 840, y: 280, r: 38,
  gain: 2.0, exitSpeed: 900, maxSpeed: 1500, cooldown: 0.4,
  affectsObjectBalls: 0,
  note: '靠右库边的单个白洞。只影响白球，让目标球保持可预测。',
};

const RAIL_KICK = { wallGain: 120, maxBoosts: 4 };
const REWIND_KICK = { minReleaseFrac: 0.35 };

const R = 12.2;
const GAP = 2 * R + 4;

// Display names live in the VISUAL layer, so the level list can be re-themed without touching design.
const VISUAL = {
  corridor:            { name: '一杆清台 · 演示关（L1：中库回程）', skin: '默认星野' },
  l1_midrail_reverse:  { name: 'L2 · 白洞初现',                   skin: '默认星野' },
  l2_midrail_mirrored: { name: 'L3 · 镜像中库',                   skin: '默认星野' },
  l3_deeper_walk:      { name: 'L4 · 长回程（弹墙回复）',          skin: '默认星野' },
  l4_offset_line:      { name: 'L5 · 偏轴斜切',                   skin: '默认星野' },
};

function insidePocket(j, x, y) {
  for (const pk of j.pockets || []) {
    if (Math.hypot(pk.x - x, pk.y - y) < (pk.r || 24) + R + 6) return true;
  }
  return false;
}

function onWhiteHole(j, x, y) {
  for (const h of j.whiteHoles || []) {
    if (Math.hypot(h.x - x, h.y - y) < (h.r || 30) + R + 4) return true;
  }
  return false;
}

function place(j, count, cue) {
  const L = j.table.left + 60, Rt = j.table.right - 60;
  const T = j.table.top + 60, B = j.table.bottom - 60;
  const spots = [];
  const cols = 6, rows = 4;
  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < cols; c++) {
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

const TYPE_ROTATION = ['object', 'heavy', 'bomb', 'anchor'];

const report = [];
for (const [id, count, shots, segs, dist, wantHole, wantRail, wantKick] of PLAN) {
  const p = path.join(LEVELS, `${id}.json`);
  const j = JSON.parse(fs.readFileSync(p, 'utf8'));

  const cue = j.balls.find(b => b.type === 'cue');
  if (!cue) { console.log(`SKIP ${id}: no cue`); continue; }

  const existing = j.balls.filter(b => b.type !== 'cue');
  // L1's positions are the only solver-verified table; NEVER re-place them.
  const keepLayout = id === 'corridor' && existing.length === count;

  if (!keepLayout) {
    const spots = place(j, count, cue);
    j.balls = [cue];
    spots.forEach(([x, y], i) => j.balls.push({
      id: i + 1,
      type: TYPE_ROTATION[i % TYPE_ROTATION.length],
      x: Math.round(x * 100) / 100,
      y: Math.round(y * 100) / 100,
    }));
  }

  // ===================== LAYER 1: VISUAL (never read by the simulation) =====================
  const vis = VISUAL[id] || { name: id, skin: '默认星野' };
  j.visual = {
    name: vis.name,
    skin: vis.skin,
    note: '包装层：只影响外观与命名，不影响任何物理或能力。换皮不会改变关卡难度。',
  };

  // ===================== LAYER 2: ABILITIES (what the level can do) =====================
  const abilities = {
    note: '能力层：决定这一关"能做什么"。加谜题或开第二解应当只改这里。',
  };
  if (wantHole) abilities.whiteHole = Object.assign({}, WHITE_HOLE);
  if (wantRail) abilities.railKick = Object.assign({}, RAIL_KICK);
  if (wantKick) abilities.rewindKick = Object.assign({}, REWIND_KICK);
  j.abilities = abilities;

  // ===================== LAYER 3: PHYSICS (raw simulation constants) =====================
  // Abilities are lowered INTO physics here so the kernel keeps reading one place. This is the only bridge
  // between the two layers, which is what keeps them separable for a designer.
  j.physics = j.physics || {};
  j.physics.maxShots = shots;
  j.physics.rewindPotBonus = 0.5;
  j.physics.rewindMinReleaseFrac = wantKick ? REWIND_KICK.minReleaseFrac : 0.0;
  j.physics.types = j.physics.types || {};
  j.physics.types.cue = Object.assign({}, j.physics.types.cue, {
    // Written explicitly even when OFF: omitting it inherits the locked CueWallGain of 120 and would hand
    // levels 1-3 a mechanic they are supposed to introduce two levels later.
    wallGain: wantRail ? RAIL_KICK.wallGain : 0,
    maxWallBoosts: wantRail ? RAIL_KICK.maxBoosts : 0,
  });

  if (wantHole) j.whiteHoles = [Object.assign({}, WHITE_HOLE)];
  else delete j.whiteHoles;

  j.budget = { maxSegments: segs, maxDistance: dist };

  j.authoring = j.authoring || {};
  j.authoring.layout = {
    placedBy: 'tools/relayout_levels.mjs',
    verified: keepLayout,
    note: keepLayout
      ? 'Original verified 2-ball table, positions untouched. Layers and mechanics applied.'
      : 'Hand-placed for the ball-count progression; NOT solver-verified. Any witness below describes an ' +
        'earlier layout and does not apply.',
  };
  if (!keepLayout && j.authoring.witnesses) j.authoring.witnessesStale = true;

  fs.writeFileSync(p, JSON.stringify(j, null, 2) + '\n');
  report.push(
    `${id.padEnd(22)} objs=${keepLayout ? existing.length : count} shots=${shots} ` +
    `budget=${segs}/${dist} hole=${wantHole ? 'Y' : 'n'} rail=${wantRail ? 'Y' : 'n'} ` +
    `kick=${wantKick ? 'Y' : 'n'} layout=${keepLayout ? 'KEPT' : 're-placed'}`);
}

console.log(report.join('\n'));
