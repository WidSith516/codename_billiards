# `tools/solver` — REWIND level solvability solver

Decides, for a level, **is there a legal input sequence that clears every object ball, and
what is one example?** The legal input space is the frozen game's: **exactly one strike**
(angle, power) followed by **any number of REWIND operations** (press R, hold for *k*
physics steps, release, repeat). Only the cue ball ever rewinds — that is not changed here.

The solver runs on the **locked physics model** (`策划案/REWIND_core_params_v1.md` v1.1),
applied at the simulation boundary of the frozen prototype, plus the **unpatched prototype**
as a control so the effect of the physics fix is measurable.

---

## 1. Run it

```powershell
# one level
node tools/solver/solve.mjs tools/solver/levels/L1.json

# every level in tools/solver/levels, locked physics, machine-readable output
node tools/solver/solve.mjs --all --config locked --json tools/solver/out/locked.json

# the controls
node tools/solver/solve.mjs --all --config prototype            # unpatched h5/index.html physics
node tools/solver/solve.mjs --all --config locked-storedcrawl   # sensitivity: crawl follows stored v

# re-verify every SOLVED entry from scratch, and print the exact input sequence
node tools/solver/verify.mjs tools/solver/out/locked.json

# budget override (any DEFAULT_BUDGET field, comma separated)
node tools/solver/solve.mjs tools/solver/levels/L3.json --budget maxRewinds=6,beam=8,topStrikes=10
```

Exit codes: `0` at least one level SOLVED, `1` all UNSOLVED, `2` invalid level / usage.

### Evidence / development commands

```powershell
node tools/solver/dev/fidelity.mjs         # CONTROL 1: solver step loop == h5harness step loop, bit for bit
node tools/solver/dev/locked_checks.mjs    # CONTROL 2: every locked constant verified numerically
node tools/solver/dev/fan.mjs              # backstop-kick calibration table (radius x strike angle)
node tools/solver/dev/make_levels.mjs      # rebuild levels/L*.json (--write); every placement is sim-verified
node tools/solver/dev/evidence.mjs         # per-level geometry: segment-1 vs whole-path distance, kick vs pocket
node tools/solver/dev/potmap.mjs --angle 0 --power 170     # where a single ball can be potted from
node tools/solver/dev/try.mjs --level tools/solver/levels/L1.json --angle 0 --power 170 --seq 1.0
node tools/solver/dev/random_layouts.mjs --mode all --n 32 --balls 3 --seed 1 --out tools/solver/out/rand_1.json
```

---

## 2. What it guarantees, and what it does not

**Guarantees**

* A `SOLVED` verdict is **replayed on a fresh simulation** before it is reported; an
  unreplayable solution is downgraded to `UNSOLVED`. Nothing is reported as solved because a
  search score said so.
* The step loop is **bit-identical** to `tools/h5harness/sim.mjs` (which executes the frozen
  `h5/index.html` bytecode in a `node:vm`) when configured as `CFG_PROTOTYPE`
  (8 shots × 720 steps, every x/y/vx/vy compared: `max |delta| = 0.000e+0`, including the
  shots where the prototype diverges to `Infinity`). See CONTROL 1 in `REPORT.md`.
* Every locked constant is checked numerically against the derivation in the parameters
  table (CONTROL 2): friction 640 px/s², rail `|v| → 0.92|v| + 120` exact to 1e-9,
  V_CAP 2000 with no `NaN`/`Infinity` over 60 s, rewind crawl exactly 240.00 px/s, release
  velocity exactly `-stored_v`, rewind impulse ratio `measured/predicted = 1.000`.

**Does not guarantee**

* `UNSOLVED` means **"not found inside the searched budget"**, never "impossible". The game
  puts **no limit** on the number of rewinds (only `shotUsed` and a non-empty history are
  required), so the space of legal plans is unbounded; the solver bounds it by
  `maxRewinds` (depth), a release-point grid, a beam width and a strike grid.
* The physics is the **locked model approximated on top of the prototype's step loop**, not
  the Godot kernel. The fidelity list is §4 below; it is the honest limit of every number in
  `REPORT.md`.

---

## 3. Level format

```jsonc
{
  "id": "L1",
  "name": "…", "note": "…",
  "cue":  { "x": 292, "y": 280 },                    // strike origin
  "balls": [ { "id": 1, "x": 276.94, "y": 263.84, "type": "plain", "note": "…" } ],
  "pockets": [[65,63],[520,63],[975,63],[65,497],[520,497],[975,497]],   // optional, default = game's
  "table":   { "left": 65, "right": 975, "top": 63, "bottom": 497 },     // optional, default = game's
  "budget":  { "angleStep": 1, "powers": [130,170], "releaseFractions": [0.3,0.6,1.0],
               "maxRewinds": 4, "beam": 5, "topStrikes": 6 },
  "design":  { "strike": { "angleDeg": 15, "power": 170 }, "plan": "ONE full rewind" }   // metadata only
}
```

* Ball ids must be `1..10` (the prototype owns 11 balls; ids not used by the level are
  pre-pocketed sentinels, which is why a 3-ball level still ends with the game's own
  `全部清台！` win condition).
* Objects may start **in contact with the cue ball**: a backstop ball must sit within the
  2R contact radius of history sample #1 (see §5), i.e. ~22 px from the cue centre, which is
  2.4 px inside the contact distance. `levelWarnings()` reports these; `validateLevel()`
  only rejects overlaps deeper than 3 px.
* `type` is `"plain"` for everything the frozen prototype can express. `"bomb"` is
  **reserved and NOT simulated** — `h5/index.html` has no bomb ball and the locked parameter
  table does not define one, so `validateLevel()` rejects a bomb level rather than pretending.

---

## 4. Fidelity: what is applied exactly, what is approximated

| Locked item | Status | Note |
|---|---|---|
| `BallRadius 12.2`, table rect, 6 pockets, `PocketRadius 24`, `SimulationStep 1/120` | **exact** | inherited from the frozen prototype |
| `PowerMax 170`, `SpeedPerPower 7.4` → `V_BREAK 1258` | **exact** | `shoot()` reproduces the game's pointer path arithmetic (canvas subtraction included) so launch velocity is bit-identical |
| `FrictionDecel 640` constant deceleration, cross-zero → stop | **exact** | replaces `exp(-1.07 dt)`; verified against `v²/1280` (§3) |
| `WallRestitution 0.92`, `CueWallGain 120`, `ObjectBallGain 0`, direction reflected first then magnitude rescaled | **exact** | `v_out = 0.92·v_in + 120` matched to `<1e-9`; one transform per step even for a two-rail contact |
| `SpeedCap 2000` every ball every step | **exact** | 0 cap events in 8 full-power breaks × 60 s; max observed 1252.7 px/s |
| `PocketRadius 24` **+ velocity must point at the pocket centre** (§5) | **exact** | implemented as `(v · (pocket − centre)) > 0` inside the circle |
| `HistoryStepPx 2.0`, position **and** velocity per record, rewind appends to the live history | **exact-in-intent** | samples are inserted by interpolation along the travelled segment and carry the velocity **at that position** (recorded between integration and collision resolution). Effective spacing is `max(2 px, displacement per substep)` at the top of the speed range. |
| `RewindImpulseK 1.0`, `j = max(0,(v_cue−v_target)·n)·K` along n | **exact** | measured/derived ratio 1.000–1.015 (the residual is the struck ball's own friction inside the same step); plus the requested backstop clamp `|v_target| ≤ |v_cue|` |
| §6.1 fixed crawl 240 px/s, release = `−stored_v` | **exact** | 50 held steps moved the cue back exactly 100.000 px; release velocity equals `−stored_v` to 0.00e+0 |
| §5 continuous collision (TOI) for ball-ball | **approximated** | the discrete step is kept, but sub-stepped (≤4 px of travel) and the impulse is always resolved at the **swept contact geometry**, not at the end-of-step overlap. Without the sweep a cut shot's line of centres is quantised by up to ~7°, which flips pot/miss; the sweep is what makes `dev/make_levels.mjs` verified placements reproducible. Still not an exact TOI engine (rail contacts resolve at substep boundaries, position after contact is Euler, not the analytic TOI position). |
| `BallRestitution 0.955` | **exact** | locked v1.1 after this solver flagged it as missing from v1.0 |
| No ball-ball **spin**, no cushion friction, no throw | **out of scope** | not in the parameter table, not in the prototype; nothing here depends on them |
| Particle/UI/hint subsystems | **not simulated** | no effect on ball physics |

The prototype's `Infinity` divergence and pass-through tunnelling (h5harness D-1/D-2) are
**structurally impossible** in the locked config: the rail law has a stable fixed point and
the cap is asserted every step.

---

## 5. How the search works, and the two design rules it found

**Search** (`lib/search.mjs`), all of it operating on the real simulation:

1. **Strike scan** — angle × power grid, each candidate is one `shoot` + `settle`.
2. **Angle refinement** — 0.25° steps around the best coarse strikes, plus power ±15/±30.
3. **Rewind beam search** — from the top seeds, a node is "cue at rest"; an action is
   *(press R, hold k steps, release, settle)* with `k` parameterised as a **fraction of the
   cue's recorded path** (`releaseFractions`), because a fixed step count means different
   things for paths of different length. Beam × depth × fractions, scored by balls potted,
   then by the best rewind kick the current path can still deliver.

The decisive scoring term is the **rewind kick potential** `Σ max(0, (−stored_v)·n)` over
survivors within 2R of the path: it is what lets the search prefer a strike that pots
*nothing* but leaves every survivor kickable over a strike that pots one ball and strands
the rest — which is the shape route C actually wins with.

**Rule A — backstop kick (the ball is potted by the rewind, at zero energy cost):**

```
B = origin + 2·d̂ + a·unit(P − origin − 2·d̂),   a ∈ [22.9, 24.4)
```

The rewind's *last productive contact* is at history sample **#1**, because sample **#0
still stores the pre-strike velocity (0,0)** and therefore carries no impulse. Contact at
sample #1 gives the ball exactly the direction of the ray *from sample #1* to the pocket,
with `|j| = V_BREAK·cos(angle between −d̂ and that ray)`. Only pockets within ±60° of the
reverse strike direction get a usable kick. Calibration table: `dev/fan.mjs` — 2/2 potted
across a **±8° strike-angle band** for `a ∈ [22.9, 24.1]`, 0/2 at `a ≥ 24.7`.

**Rule B — cut (the ball is potted by the strike):**

```
B = s_j + 2R·unit(P − s_j)   for a path sample s_j
```

The line of centres points at the pocket, so the ball flies straight in with
`|v| = 0.9775·(v_cue·n)`, and the cue keeps `sin(cut angle)` of its speed (≈1 for a 60° cut,
2 % for a head-on hit). This is why chained cuts work and chained head-on hits do not.

The same rule works on any path segment, including post-bounce segments — that is what
makes a ball "reachable only after a rail bounce" (`dev/evidence.mjs` reports the distance
to segment #1 versus the distance to the whole path for every ball).

---

## 6. Files

| File | Role |
|---|---|
| `solve.mjs` | CLI: SOLVED/UNSOLVED per level, example solution, replay check, JSON output |
| `verify.mjs` | replays every SOLVED entry of an output file from scratch; prints the input sequence |
| `lib/locked.mjs` | the patched step loop, the two configs, snapshots, corridor/kick diagnostics |
| `lib/level.mjs` | level format, validation, the factory-rack control level |
| `lib/search.mjs` | strike scan + refinement + rewind beam search + replay |
| `levels/L1..L6.json` | the six candidate levels (`L0_factory.json` is the shipped rack as a control) |
| `out/` | solver outputs, evidence, random-layout data (regenerate with the commands above) |
| `dev/` | fidelity controls, calibration tables, the level generator, the random-layout experiment |
| `REPORT.md` | the verdict on route C |
