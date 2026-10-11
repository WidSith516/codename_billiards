# Is route C sufficient? — solver verdict

**Route C** = keep the rules exactly as they are (one strike, then rewind only the cue ball;
only the cue ball ever rewinds) and **hand-design the level geometry** so that a solution
exists. This report is the answer, from a solver that runs the real simulation.

> **Verdict (short): route C is sufficient to build a puzzle game, but only as a
> hand-authored, low-ball-count puzzle, and only if the rewind corridor is treated as the
> level's primary design object. It is NOT sufficient for a "break the rack and improvise"
> game.** Six of six hand-designed levels (2–4 balls) are cleared by one strike plus one
> rewind under the locked physics, every solution replay-verified. Blind placement of the
> same balls gives **0/328**. The mechanic's reach is a **15.3 %-of-the-table corridor**, and
> a level is solvable only when *every* ball is deliberately inside some corridor.
> Concretely: route C works, but the level designer is doing the work, and the ball budget is
> small. Details and the numbers behind every clause are below.

---

## 0. What was run, and on what physics

| | |
|---|---|
| Solver | `tools/solver/solve.mjs` (strike scan → angle refinement → rewind beam search) |
| Physics | **locked** (`策划案/REWIND_core_params_v1.md` v1.1) applied at the simulation boundary of the frozen `h5/index.html`; plus `prototype` (unpatched) and `locked-storedcrawl` controls |
| Search space | one strike (angle on a 1° grid, power on a 2–4 point grid, then 0.25° refinement) + up to 3–5 rewinds, each parameterised by its release point as a fraction of the recorded path |
| Budget per level | 384 angles × 2–4 powers ≈ 750–1100 strike sims, plus ≤ ~12 beam seeds × 8 release points × depth |
| Verification | every SOLVED entry is replayed on a fresh simulation before it is reported (`solve.mjs`), and independently re-verified by `tools/solver/verify.mjs` |

Two controls make the numbers trustworthy; both are commands, not claims:

* **CONTROL 1 — the step loop is the prototype's step loop.** `node tools/solver/dev/fidelity.mjs`
  runs 8 shots × 720 steps on both `tools/h5harness/sim.mjs` (which executes the frozen
  bytecode in a `node:vm`) and on the solver's loop configured as `prototype`, comparing
  every ball's `x, y, vx, vy, pocketed` **after every step**:
  `PASS — bit-identical on all 8 shots x 720 steps` (`max |delta| = 0.000e+0`), including the
  angles where the prototype diverges to `Infinity`. So the only differences between this
  solver and the shipped game are the locked-model patches.
* **CONTROL 2 — the locked constants are the locked constants.**
  `node tools/solver/dev/locked_checks.mjs` measures each one: glide distance matches
  `v²/1280` (worst error 3.6 %, all of it the final partial step), rail transform
  `0.92·v_in + 120` exact to `<1e-9` on every event, `V_CAP` never exceeded and no
  `NaN`/`Infinity` in 8 full-power breaks × 60 s (max observed **1252.7 px/s**, 0 cap events),
  rewind crawl **240.00 px/s** exactly, release velocity `= −stored_v` (relative error
  **0.00e+0**), rewind impulse ratio `measured/derived = 1.000–1.015` (the residual is the
  struck ball's own friction inside the same step).

Fidelity limits (full list in `README.md` §4): ball-ball collision is **sub-stepped with the
impulse resolved at the swept contact geometry**, not an exact TOI engine; history sampling
is 2 px by interpolation and its effective spacing is `max(2 px, displacement per substep)`
at the top of the speed range. Those are the only two approximations, and CONTROL 1 bounds
them to *only* the locked-model differences.

---

## 1. Per-level result (the table you asked for)

`locked` = the locked physics (design evidence). `prototype` = unpatched `h5/index.html`.
`storedcrawl` = sensitivity check where the rewind crawl follows the stored speed profile
instead of the locked flat 240 px/s.

| level | balls | **locked** (design evidence) | prototype (control) | storedcrawl (sensitivity) |
|---|---|---|---|---|
| `L0_factory` (shipped rack) | 10 | **UNSOLVED** — best 0/10, best with rewinds 0/10 | UNSOLVED — best 4/10, 6/10 with rewinds | UNSOLVED — 1/10 |
| `L1` backstop fan | 2 | **SOLVED** — 1 rewind | SOLVED — 2 rewinds | UNSOLVED |
| `L2` fan + cut | 3 | **SOLVED** — 1 rewind | SOLVED — 2 rewinds | UNSOLVED |
| `L3` rail-bounce-only cut | 3 | **SOLVED** — 1 rewind | SOLVED — 3 rewinds | SOLVED — 1 rewind |
| `L4` pre- + post-bounce cuts | 4 | **SOLVED** — 1 rewind | SOLVED — 2 rewinds | UNSOLVED (3/4) |
| `L5` four balls, two cuts | 4 | **SOLVED** — 1 rewind | UNSOLVED (3/4) | UNSOLVED |
| `L6` four balls | 4 | **SOLVED** — 1 rewind | SOLVED — 5 rewinds | UNSOLVED |

**6/6 hand-designed levels solve under the locked model**; 5/6 under the old physics
(the old physics solves *different* levels — it is more powerful because the rewind impulse
is 1.6× and the rails are unbounded, which is exactly the bug); **1/6** under the
"crawl follows the stored speed" variant. The shipped factory rack stays UNSOLVED in all
three, consistent with the h5harness measurement of 0 clears in 279 breaks.

### Example solutions (all replay-verified)

| level | strike | rewind sequence | strike pots | total |
|---|---|---|---|---|
| L1 | **0° / power 170** | hold R **650 steps (5.42 s)** → walks back to the strike origin, auto-stop, v=0 | 0/2 | 2/2 |
| L2 | 0° / 170 | hold R **60 steps (0.50 s)** → back to origin, v=0 | 1/3 | 3/3 |
| L3 | 15° / 170 | hold R **493 steps (4.11 s)** → back to origin, v=0 | 1/3 | 3/3 |
| L4 | 12° / 170 | hold R **88 steps (0.73 s)** → back to origin, v=0 | 2/4 | 4/4 |
| L5 | 349° / 170 | hold R **5 steps (0.04 s)** → back to origin, v=0 | 2/4 | 4/4 |
| L6 | 11° / 170 | hold R **4 steps (0.03 s)**, release at path index 1 at **1256.2 px/s** | 2/4 | 4/4 |

Every solution is **one strike + one rewind.** The rewind is not decoration: it supplies 1–2
of the 2–4 balls in every level. Angles searched 384 per level; runtime **0.8–1.9 s** and
751–1111 simulations per level; throughput ≈ 700–1100 sims/s per process on these levels
(the same code measures 89.7 sims/s on the 10-ball factory rack, where every shot has to
settle a full rack).

### "Requires a rail bounce" — as a number, not a claim

`node tools/solver/dev/evidence.mjs` reports, per ball, the distance to the **first** path
segment versus the distance to the **whole** recorded path:

* **L3 ball 3**: 59.2 px from segment #1, **23.8 px** from the whole path → it is outside the
  strike corridor until the cue has bounced (the level's path is 494 samples with
  **3 rail bounces**). `REACHABLE ONLY AFTER A RAIL BOUNCE`.
* **L4 ball 4**: 184.4 px from segment #1, **24.4 px** from the whole path → same verdict.
* L5/L6: their cut balls sit at 22.7/24.4 px from segment #1, i.e. **these two levels do not
  need a bounce**. They are honest 4-ball levels with different strike angles, not "harder"
  than L4. I am labelling them that way in the level files rather than claiming a ladder I
  did not build.

The generator could not place post-bounce cuts for L5/L6 because the cue's energy is gone by
then (each cut costs the cue a factor `sin(cut angle)`, and a 60° cut still leaves only 87 %).
That is itself a finding: **the cue's energy budget, not the corridor's length, caps the ball
count.**

---

## 2. How hard is it to author a solvable level? (the decisive number)

`dev/random_layouts.mjs`: **984 random 3-ball layouts**, identical solver budget for every
arm, every SOLVED replayed. Three placement policies:

| arm | what it places | layouts | SOLVED | solve rate |
|---|---|---|---|---|
| `random` | 3 balls uniformly at random on the playable area (≥ 2R+4 apart, ≥ 34 px from the cue) | 328 | **0** | **0.0 %** |
| `hybrid` | 1 backstop ball (rule A) + 2 random | 328 | **1** | **0.3 %** |
| `rule` | 2 backstop balls + cut balls on one random strike's path (rule B), **unverified** | 328 | **85** | **25.9 %** |

Across all 984 layouts, the best plan the solver could find (up to 3 rewinds) potted:

| balls potted by the best plan found | 0 | 1 | 2 | 3 (cleared) |
|---|---|---|---|---|
| layouts | 2 | **645** | 251 | 86 |

Of the 86 layouts that were solved, **72 needed a single rewind**, 13 needed two and 1 needed
three. So on a blind layout **the strike pots exactly one ball 99.3 % of the time (977/984)**,
and even with the full rewind search 645/984 never got past one ball. The rewind is not a
general-purpose escape hatch; it converts a layout you already designed for it.

**Why blind placement fails is measurable, not mysterious.** `node tools/solver/dev/coverage.mjs`
walks a 12 px grid over the playable area for each of 24 full-power strike angles and asks how
much of the table the strike's own rewind corridor covers:

| area of the playable table… | mean over 24 angles |
|---|---|
| inside the 2R rewind corridor | **15.3 %** |
| with a rewind kick > 250 px/s | 13.4 % |
| whose best kick can actually reach a pocket | **10.9 %** |

A level needs *all* its balls in that area: `0.153^n` gives **2.3 % for n=2, 0.36 % for n=3,
0.05 % for n=4** — and the stricter "kick reaches a pocket" bound gives 0.13 % for n=3. The
measured 0/328 for random 3-ball levels is exactly what the geometry predicts. The `hybrid`
arm's 1/328 shows the trap clearly: **one** ball placed by the rule is not enough; 2 of 3
balls still have to be reachable by luck.

**The rule of thumb (this is the level-design guideline):**

> **Place every object ball deliberately inside the cue's post-strike corridor, and put a
> pocket on the far side of it. A ball is only pottable if its centre is within 2R = 24.4 px
> of the cue's *recorded* path (rewind kick) or exactly 2R from a path point on the line of
> centres to a pocket (strike cut). Anything 25 px off the path is decoration.**

Two concrete placements implement it (both calibrated in `dev/fan.mjs`, both used by the
level generator):

* **Backstop kick** (potted by the rewind, costs the cue nothing):
  `B = origin + 2·d̂ + a·unit(P − origin − 2·d̂)`, `a ∈ [22.9, 24.4)` — i.e. 23.5 px from
  **history sample #1**, on the ray from that sample to the pocket. Surprising but load-bearing
  detail: history sample **#0 still carries the pre-strike velocity (0,0)**, so the rewind's
  last productive contact is sample #1, 2 px along the strike direction. Calibration: 2/2
  balls potted across a **±8° strike-angle band** for `a ∈ [22.9, 24.1]`, 0/2 at `a ≥ 24.7`.
* **Cut** (potted by the strike): `B = s_j + 2R·unit(P − s_j)` for a path sample `s_j`. The
  ball flies straight in at `0.9775·(v_cue·n)`; the cue keeps `sin(cut angle)`, which is why
  55–60° cuts chain and head-on hits (2 % retained) do not.

With those two rules plus **simulation verification of every placement**, the generator
(`dev/make_levels.mjs`) built all six levels and reported a verified clearing plan for every
one of them; three levels needed 2–4 greedy attempts per ball and two levels stopped at 4
balls. The ladder that is actually backed by the data is therefore:

**blind 0 % → rule-of-thumb 26 % (unverified) → rule-of-thumb + simulation verification 6/6
(100 %, small n)** — and the ball budget the rules support is **2–4 balls**, with 4 the most
the generator could verify.

---

## 3. The verdict, clause by clause

**What route C does give you**

1. **Solvable levels exist and are cheap to author once you know the rule.** 6/6 designed
   levels clear; the designer's cost is a geometry computation plus a verification run
   (0.8–1.9 s of solver time per level).
2. **The mechanic is legible and deterministic.** The solutions are "aim here, hold R for
   0.03–5.4 s"; the locked physics has no divergence, no tunnelling and no frame-rate
   dependence, so the same input gives the same result. L1's plan works across a ±8° band of
   strike angles, i.e. the puzzle tolerates a human hand.
3. **The rewind is load-bearing.** In all six levels the strike alone leaves 1–2 balls; the
   rewind supplies the rest. A `route C` level is not a one-strike level with decoration.

**What route C does not give you**

1. **You cannot design by placing balls and hoping.** 0/328 random layouts, 1/328 with one
   rule-placed ball. The corridor is 15.3 % of the table and a pocket-reachable corridor only
   10.9 %; the level is a corridor, not a table.
2. **The ball budget is 2–4, and the ceiling is the cue's energy, not the corridor.** Every
   cut the cue makes costs it `cos` of its speed; after 3–4 cuts (and any rail bounces) there
   is nothing left to pot with. My generator's verified maximum was **4 balls**, twice
   (L4, L5/L6) stopping there with candidates still available.
3. **The level's visual language is narrow and slightly odd.** Backstop balls must sit
   ~22 px from the cue centre — i.e. **2.4 px inside the 2R contact distance, visually
   "touching the cue ball"** — or they are not kickable at all (the kick window is
   `a ∈ [22.9, 24.4)`, and above it the sample-#1 contact is missed entirely). A designer must
   accept "balls touching the cue" as a normal opening position, or accept that the rewind can
   only pot balls the strike already reached.
4. **The solution search is shallow enough to brute-force.** The solver clears the hardest
   designed level with a 1° strike grid and a 3-point release grid in **751 simulations
   (1.8 s)**. If a bot clears it that fast on a coarse grid, a determined player will too —
   the puzzle depth has to come from level reading, not from search difficulty.
5. **It is sensitive to one parameter you just chose.** Under `locked-storedcrawl` (crawl rate
   follows the stored speed instead of flat 240 px/s) the same six levels solve **1/6**,
   because a >240 px/s crawl *skips* the samples near the origin and the backstop kick
   disappears. The locked §6.1 decision (flat 240 px/s) is therefore not a detail — it is the
   parameter that makes route C's cheapest mechanism work at all. Conversely, the price of
   that choice is real: L1's designed solution needs **5.42 s of held R** to walk the
   1300 px corridor back to the origin.
6. **The old physics "solved" these levels differently — and its solutions are bug
   artifacts.** Under the unpatched prototype, 5/6 levels solve with **2–5 rewinds** and
   releases up to 1559 px/s (above `V_BREAK`) — that is the 1.6× rewind impulse and the
   unbounded ×1.5 rail gain doing the work. Any balance conclusion drawn on the prototype is
   measuring the bug.

**So: is route C sufficient?** For a **hand-authored puzzle game with 2–4 balls per level,
where the designer places balls on the cue's corridor and verifies with a solver** — yes, and
this tool is the verifier. For anything rack-like, procedural, or "big table, many balls" —
no: the mechanic's reach is 15.3 % of the table and the cue's energy budget caps a single
strike at ~4 potted balls even when everything is placed perfectly. If the goal is more balls
per level, the leverage is **not** more rewinds; it is one of:

* a **bomb ball** (the reserved ball type in the level format): one object ball whose
  pocketing moves other balls — this multiplies the reach of a single corridor without
  changing the rules, and it is the cheapest fix consistent with route C;
* **rail-bounce routing as a designed feature** (L3/L4 already do this deliberately: a ball
  184 px from the first segment, 24.4 px from the post-bounce segment);
* **more energy per strike** (`SpeedPerPower`), which the parameter table already flags as the
  knob for "soft" feel — it would raise the cut count roughly linearly;
* **a second strike**, i.e. leaving route C. My evidence says that is not *necessary* for a
  2–4 ball puzzle game, so I do not recommend it on this data.

---

## 4. Honest limits of this result

* **UNSOLVED ≠ impossible.** The game has no rewind budget, so the legal plan space is
  unbounded; the solver bounds it by strike grid (1°, then 0.25°), release-point grid
  (3–8 fractions), `maxRewinds` (3–5) and beam width (4–6). Every UNSOLVED number here is
  "not found in this budget". For `L0_factory` I additionally ran the coarser 2° control in
  `tools/h5harness` — 0 clears in 279 breaks × 96 policies — so its UNSOLVED is
  well-supported; for the random layouts only the stated budget applies.
* **984 layouts is a 3-ball experiment.** The measured 0 % is consistent with the geometric
  prediction (≤ 0.36 %), but the arms are not independent samples of "human authoring": the
  `rule` arm is a *generator* that knows the rule and does not verify placements, and the
  6/6 result comes from a generator that verifies every placement by simulation. The honest
  reading is: **the rule is necessary and, with verification, close to sufficient for 2–4
  balls.**
* **Approximated physics.** Two items (sub-stepped TOI, 2 px history interpolation) are
  approximations of the locked table, bounded by CONTROL 1. If `m1-kernel`'s Godot
  implementation differs on those two, the pot/miss margins in the level files are the first
  thing to re-check — `node tools/solver/verify.mjs tools/solver/out/locked.json` replays
  every solution and prints the exact input sequence to compare against.
* **Not tested:** how any of this *feels*; whether a player can perceive a 24 px corridor; and
  the bomb ball, which the frozen prototype cannot express at all (the level format reserves
  the type and `validateLevel()` refuses to pretend otherwise).

---

## 5. Reproduce everything

```powershell
node tools/solver/dev/fidelity.mjs            # CONTROL 1  (bit-identical: max|delta| = 0)
node tools/solver/dev/locked_checks.mjs       # CONTROL 2  (every locked constant, measured)
node tools/solver/solve.mjs --all --config locked  --json tools/solver/out/locked.json
node tools/solver/solve.mjs --all --config prototype           # control: unpatched physics
node tools/solver/solve.mjs --all --config locked-storedcrawl  # sensitivity: crawl rate
node tools/solver/verify.mjs tools/solver/out/locked.json      # 6 replay-verified, 0 failed
node tools/solver/dev/evidence.mjs            # per-ball corridor / rail-bounce evidence
node tools/solver/dev/coverage.mjs            # 15.3 % corridor coverage of the table
node tools/solver/dev/analyze_random.mjs      # 0 / 0.3 % / 25.9 % arms + histograms
node tools/solver/dev/make_levels.mjs         # rebuild the levels (--write); every placement verified
```

Raw data: `out/locked.json`, `out/prototype.json`, `out/storedcrawl.json`,
`out/evidence.json`, `out/random_summary.json`, `out/rand_*.json` (984 layouts).
