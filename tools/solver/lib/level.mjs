// level.mjs — level data format: load, normalise, validate, and the factory baseline.
//
// Format (JSON, all lengths in px, y grows DOWN like the game's canvas):
//   id, name, note        strings
//   cue                   {x, y}                 cue-ball start (the strike origin)
//   balls[]               {id, x, y, type?}      id must be 1..10 and unique
//   pockets[]             [[x,y], ...]           optional, default = game's 6 pockets
//   table                 {left,right,top,bottom} optional, default = game's rect
//   budget                search budget hints (see DEFAULTS below)
//
// `type` is "plain" for every ball the frozen prototype can express. "bomb" is RESERVED:
// the frozen h5/index.html has no bomb ball, and 策划案/REWIND_core_params_v1.md does not
// define one, so the solver does not simulate it (a bomb level is rejected unless
// --allow-unsimulated-types is passed). See README.md "Reserved: special ball types".

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));

export const FACTORY_TABLE = { left: 65, right: 975, top: 63, bottom: 497 };
export const FACTORY_POCKETS = [[65, 63], [520, 63], [975, 63], [65, 497], [520, 497], [975, 497]];
export const FACTORY_CUE = { x: 292, y: 280 };

export const DEFAULT_BUDGET = {
  angleStart: 0, angleEnd: 360, angleStep: 2,
  powers: [40, 80, 120, 170],
  releaseFractions: [0.03, 0.08, 0.15, 0.3, 0.5, 0.7, 0.85, 1.0],
  maxRewinds: 4,
  beam: 6,
  topStrikes: 6,
  settleMaxSeconds: 25,
  refineStep: 0.5,
  refineRounds: 2,
};

/** The layout shipped inside h5/index.html init() — used as the baseline/control level. */
export function factoryLevel() {
  const R = 12.2;
  const balls = [];
  let id = 1;
  for (let col = 0; col < 4; col++) {
    for (let row = 0; row <= col; row++) {
      balls.push({
        id: id++,
        x: 675 + col * R * 1.82,
        y: 280 + (row - col / 2) * R * 2.12,
        type: 'plain',
      });
    }
  }
  return normalizeLevel({ id: 'factory', name: 'h5/index.html factory rack', cue: FACTORY_CUE, balls, budget: { ...DEFAULT_BUDGET, topStrikes: 4 } });
}

export function normalizeLevel(raw) {
  const lvl = {
    id: raw.id ?? 'unnamed',
    name: raw.name ?? raw.id ?? 'unnamed',
    note: raw.note ?? '',
    cue: { x: raw.cue?.x ?? FACTORY_CUE.x, y: raw.cue?.y ?? FACTORY_CUE.y },
    balls: (raw.balls ?? []).map((b, i) => ({
      id: b.id ?? i + 1,
      x: b.x, y: b.y,
      type: b.type ?? 'plain',
      note: b.note ?? '',
    })),
    pockets: raw.pockets ?? FACTORY_POCKETS,
    table: raw.table ?? FACTORY_TABLE,
    budget: { ...DEFAULT_BUDGET, ...(raw.budget ?? {}) },
    design: raw.design ?? null,
    source: raw.source ?? null,
  };
  return lvl;
}

export function validateLevel(lvl, { allowUnsimulatedTypes = false } = {}) {
  const errs = [];
  const ids = new Set();
  for (const b of lvl.balls) {
    if (!(b.id >= 1 && b.id <= 10)) errs.push(`ball id ${b.id} out of range 1..10`);
    if (ids.has(b.id)) errs.push(`duplicate ball id ${b.id}`);
    ids.add(b.id);
    if (!Number.isFinite(b.x) || !Number.isFinite(b.y)) errs.push(`ball ${b.id} has non-finite position`);
    if (b.type !== 'plain' && !allowUnsimulatedTypes) {
      errs.push(`ball ${b.id} type "${b.type}" is not simulated by the frozen prototype (reserved)`);
    }
  }
  if (lvl.balls.length === 0) errs.push('level has no object balls');
  if (!Number.isFinite(lvl.cue.x) || !Number.isFinite(lvl.cue.y)) errs.push('cue start is not finite');
  if (!lvl.pockets.length) errs.push('level has no pockets');
  // geometry sanity: ball centres must be inside the rails (a ball resting outside is illegal)
  const R = 12.2, T = lvl.table;
  const outside = (x, y) => x < T.left + R - 0.6 || x > T.right - R + 0.6 || y < T.top + R - 0.6 || y > T.bottom - R + 0.6;
  for (const b of lvl.balls) if (outside(b.x, b.y)) errs.push(`ball ${b.id} at (${b.x},${b.y}) is outside the rails`);
  if (outside(lvl.cue.x, lvl.cue.y)) errs.push(`cue at (${lvl.cue.x},${lvl.cue.y}) is outside the rails`);
  // Overlap sanity. Note the tolerance: a "backstop" ball (see tools/solver/README.md) MUST
  // start inside the 2R contact radius of the cue, because the rewind's only productive
  // contact is at history sample #1, 2 px along the strike direction. Such a ball is placed
  // ~22 px from the cue centre, i.e. 2.4 px inside the contact distance, which is visually
  // "the balls are touching". Anything deeper than 3 px is treated as a design error.
  const all = [...lvl.balls.map((b) => ({ id: b.id, x: b.x, y: b.y })), { id: 0, x: lvl.cue.x, y: lvl.cue.y }];
  for (let i = 0; i < all.length; i++)
    for (let j = i + 1; j < all.length; j++) {
      const d = Math.hypot(all[i].x - all[j].x, all[i].y - all[j].y);
      if (d < 2 * R - 3) errs.push(`balls ${all[i].id} and ${all[j].id} overlap by more than 3 px (d=${d.toFixed(2)})`);
    }
  return errs;
}

/** Non-fatal geometry notes (deliberate touching starts are reported, not rejected). */
export function levelWarnings(lvl) {
  const warns = [];
  const R = 12.2;
  const all = [...lvl.balls.map((b) => ({ id: b.id, x: b.x, y: b.y })), { id: 0, x: lvl.cue.x, y: lvl.cue.y }];
  for (let i = 0; i < all.length; i++)
    for (let j = i + 1; j < all.length; j++) {
      const d = Math.hypot(all[i].x - all[j].x, all[i].y - all[j].y);
      if (d < 2 * R) warns.push(`ball ${all[i].id} and ${all[j].id} start in contact (d=${d.toFixed(2)} < 2R=${2 * R})`);
    }
  return warns;
}

export function loadLevel(file) {
  // relative paths resolve against the CWD first (normal CLI behaviour), then against
  // tools/solver/ so `--all` style invocations work from anywhere.
  let p = path.isAbsolute(file) ? file : path.resolve(process.cwd(), file);
  if (!fs.existsSync(p)) p = path.resolve(HERE, '..', file);
  const raw = JSON.parse(fs.readFileSync(p, 'utf8'));
  const lvl = normalizeLevel(raw);
  lvl.source = p;
  return lvl;
}

export function listLevels() {
  const dir = path.resolve(HERE, '..', 'levels');
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort()
    .map((f) => path.join(dir, f));
}
