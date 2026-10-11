// Verifier: confirm every level really carries the three layers, and that the mechanics are gated exactly
// where the design says. Run after tools/relayout_levels.mjs.
//
// This is a STRUCTURAL check, not a solvability check. It answers "is the data shaped the way the design
// says", which is the thing a hand-edit can silently get wrong.
import fs from 'node:fs';

const IDS = ['corridor', 'l1_midrail_reverse', 'l2_midrail_mirrored', 'l3_deeper_walk', 'l4_offset_line'];

// The agreed design. Any drift from this table is a failure.
const EXPECT = {
  corridor:            { objs: 2, hole: false, rail: false, kick: false },
  l1_midrail_reverse:  { objs: 3, hole: true,  rail: false, kick: true  },
  l2_midrail_mirrored: { objs: 4, hole: true,  rail: false, kick: true  },
  l3_deeper_walk:      { objs: 5, hole: true,  rail: true,  kick: true  },
  l4_offset_line:      { objs: 6, hole: true,  rail: true,  kick: true  },
};

let bad = 0;
for (const id of IDS) {
  const j = JSON.parse(fs.readFileSync(`godot/levels/${id}.json`, 'utf8'));
  const want = EXPECT[id];
  const errs = [];

  // --- layer presence
  if (!j.visual) errs.push('missing visual layer');
  if (!j.abilities) errs.push('missing abilities layer');
  if (!j.physics) errs.push('missing physics layer');
  if (!j.table) errs.push('missing table geometry');

  // --- visual layer must not carry simulation data
  if (j.visual) {
    for (const k of Object.keys(j.visual)) {
      if (['mass', 'friction', 'restitution', 'wallGain', 'gain', 'speed'].some(s => k.toLowerCase().includes(s))) {
        errs.push(`visual layer carries simulation key "${k}"`);
      }
    }
  }

  // --- mechanics gated as designed
  const objs = j.balls.filter(b => b.type !== 'cue').length;
  const hasHole = Array.isArray(j.whiteHoles) && j.whiteHoles.length > 0;
  const cue = (j.physics.types && j.physics.types.cue) || {};
  const hasRail = (cue.wallGain || 0) > 0;
  const hasKick = (j.physics.rewindMinReleaseFrac || 0) > 0;

  if (objs !== want.objs) errs.push(`objects ${objs} != ${want.objs}`);
  if (hasHole !== want.hole) errs.push(`whiteHole ${hasHole} != ${want.hole}`);
  if (hasRail !== want.rail) errs.push(`railKick ${hasRail} != ${want.rail}`);
  if (hasKick !== want.kick) errs.push(`rewindKick ${hasKick} != ${want.kick}`);

  // --- abilities block and whiteHoles array must agree, or a designer will edit the wrong one
  const abHole = !!(j.abilities && j.abilities.whiteHole);
  const abRail = !!(j.abilities && j.abilities.railKick);
  const abKick = !!(j.abilities && j.abilities.rewindKick);
  if (abHole !== hasHole) errs.push('abilities.whiteHole disagrees with whiteHoles array');
  if (abRail !== hasRail) errs.push('abilities.railKick disagrees with cue.wallGain');
  if (abKick !== hasKick) errs.push('abilities.rewindKick disagrees with rewindMinReleaseFrac');

  // --- geometry sanity
  for (let i = 0; i < j.balls.length; i++) {
    for (let k = i + 1; k < j.balls.length; k++) {
      const d = Math.hypot(j.balls[i].x - j.balls[k].x, j.balls[i].y - j.balls[k].y);
      if (d < 2 * 12.2 - 0.01) errs.push(`balls ${j.balls[i].id}/${j.balls[k].id} overlap (d=${d.toFixed(1)})`);
    }
  }
  for (const b of j.balls) {
    for (const pk of j.pockets || []) {
      if (Math.hypot(pk.x - b.x, pk.y - b.y) < (pk.r || 24) + 12.2 - 0.01) errs.push(`ball ${b.id} spawns in a pocket`);
    }
  }

  if (errs.length) { bad++; console.log(`FAIL ${id}\n       - ${errs.join('\n       - ')}`); }
  else {
    console.log(`ok   ${id.padEnd(22)} objs=${objs} hole=${hasHole ? 'Y' : 'n'} rail=${hasRail ? 'Y' : 'n'} ` +
                `kick=${hasKick ? 'Y' : 'n'} budget=${j.budget.maxSegments}/${j.budget.maxDistance} ` +
                `"${j.visual.name}"`);
  }
}

console.log(bad === 0 ? '\nLAYERS OK' : `\nLAYERS FAILED (${bad} level(s))`);
process.exit(bad === 0 ? 0 : 1);
