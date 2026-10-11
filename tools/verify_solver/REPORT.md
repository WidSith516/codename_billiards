# Independent adversarial verification of `tools/solver`'s route-C verdict

**Verifier:** `verify-solver` (independent teammate; write scope `tools/verify_solver/` only).
**Date of run:** this session. **Frozen file check:** `h5/index.html` sha256 =
`98ef7c191a91f3c0b05bbcc8a91465de11fbea7069c124e8bca95dc85fefb864` — matches the value I was
given, so the reference implementation was not modified. I modified nothing outside
`tools/verify_solver/`.

**Method.** Every number below was produced by a command I ran, and every script is re-runnable
and lives in `tools/verify_solver/`. Where the solver's own model is used, it is imported
read-only from `tools/solver/lib/`. Where independence matters, I wrote my own from-spec
implementation (`mykernel.mjs`, written only from `策划案/REWIND_core_params_v1.md` §1–§8, no
shared code with `tools/solver/` or `tools/h5harness/`).

**The single most important distinction I tested.** `tools/solver/verify.mjs` replays each
solution on a fresh simulation — but on *the same physics model that produced it*, using the
same `holdSteps` values. That is a self-consistency check, not an independent one. I therefore
re-wrote the locked model from the parameter table and replayed the reported plans on it.

---

## 0. Verdict table

| # | Claim under test | Verdict |
|---|---|---|
| 1a | `verify.mjs tools/solver/out/locked.json` → 6 replay-verified, 0 failed, exit 0 | **CONFIRMED** (exactly reproduced) |
| 1b | The 6 solutions really clear when replayed by an independent implementation of the locked model | **CONFIRMED at the solver's substep granularity (6/6), PARTIALLY CONFIRMED otherwise (3/6 at 1–2 px substeps)** |
| 1c | The verifier is more than bookkeeping re-checking | **CONFIRMED** — it re-simulates, and a from-spec rewrite reproduces the clears; but it cannot detect a model error (it shares the model) |
| 2a | The step loop is bit-identical to `h5harness/sim.mjs` in prototype config (max\|Δ\|=0) | **CONFIRMED** (re-ran; 8 shots × 720 steps, max\|Δ\| = 0.000e+0) |
| 2b | The locked patches are actually applied, not silently prototype physics | **CONFIRMED** — rail `0.92v+120` exact, glide `v²/1280`, crawl 1 sample/step, release `−stored_v` (cos = 1.000000000), pocket direction rule, rewind `K = 1.000000` (not 1.6) |
| 3a | Random 3-ball layouts solve 0/328 | **CONFIRMED as a number** (my fresh 0/100 at their budget, 0/100 at 2× the budget) |
| 3b | The 0% is "exactly what the geometry predicts" (0.153³ = 0.36 %) | **REFUTED as argued** (49.7 % of the same layouts satisfy the corridor precondition with a free angle), **but CONFIRMED by a corrected geometric test**: 0/328 are pottable when each ball must be strike-potted or kick-aimed at a pocket |
| 3c | "Blind placement fails" is immune to the search budget | **CONFIRMED** — doubling the angular resolution and the rewind budget still gives 0/100 |
| 4 | The mechanic's reach is a 15.3 %-of-the-table corridor / "the corridor is a scarce resource" | **REFUTED as stated** — 15.3 % is the mean coverage of *one fixed* strike direction; the union over all 360 directions is **100.0 %** |
| 5a | 6/6 levels are hand-designed puzzles | **PARTIALLY CONFIRMED** — all 6 are variants of one construction: same cue origin, every pair of levels shares ≥2 ball positions within 3 px, L4/L6 share 3 of 4 |
| 5b | The rewind is load-bearing in every level (never clearable by the strike) | **CONFIRMED** — best strike-only over the solver's own 384-angle scan is 1/2, 1/3, 1/3, 2/4, 2/4, 2/4 |
| 5c | A player can aim these levels (±8° tolerance, "not pixel-level fussiness") | **REFUTED for 5 of 6 levels** — measured clearing windows: L3 `[−0.15°, 0°]`, L2 `[−0.15°, +1.5°]`, L4 `[−0.15°, +1.5°]`, L6 `[−0.30°, +1.5°]`, L5 `[−1.5°, +0.30°]`; only L1 is ±1.5° or wider |
| 5d | The levels tolerate a re-implementation (portability) | **REFUTED** — 5 of 6 break under a ≤0.1 px change of the cue's start position (L2 0.03 px, L4 0.01 px, L6 0.07 px, L5 0.07 px, L3 0.10 px, on the narrow side), and L3/L5/L6 miss a ball when the substep is made finer |
| 6a | Authoring table 0 / 1 / 85 of 328 | **CONFIRMED as arithmetic** (recomputed from the raw `rand_*.json`: 0/328, 1/328, 85/328); the `rule` arm is 2 rule-placed balls + 1 rule-placed cut ball, and is self-referential |
| 6b | "rule-placed" is an independent predictor | **PARTIALLY CONFIRMED** — the rules were derived from the solver's own corridor/impulse code, so 25.9 % measures self-consistency, and the report already labels the arm "unverified" |
| 7 | Ball budget capped at 2–4 balls | **CONFIRMED for the evidence given** — the solver's own `--scan` (I ran it: 24 angles × 3 bounce regimes) never exceeds 4 balls; not proven as a general ceiling |

---

## 1. Claim 1 — the headline, and is the replay real?

### 1a. Reproduce the headline

```
$ node tools/solver/verify.mjs tools/solver/out/locked.json ; echo EXIT=$?
verifying tools/solver/out/locked.json (solver config "locked")
  L0_factory   UNSOLVED  (best 0/10 with the strike alone, 0/10 with rewinds)
  OK   L1           2/2 potted, won=true   strike 0deg / power 170
        rewind 1: hold R for 650 steps (5.42 s), walk back to the strike origin (auto-stop, v=0)
  ... L2 (0deg, 60 steps) / L3 (15deg, 493) / L4 (12deg, 88) / L5 (349deg, 5) / L6 (11deg, 4 steps,
      release at path index 1 at 1256.2 px/s) ...

6 replay-verified, 0 FAILED, 1 not solved
EXIT=0
```
Raw: `01_verify_locked.txt`. **VERDICT: CONFIRMED.** Exit 0, 6/6 replay-verified.

`verify.mjs` calls `replaySolution()`, which builds a **fresh** `LockedGame`, re-applies the
strike and the recorded `holdSteps`, and requires `pocketedLevel() === ball count`. It is not
re-checking a stored score. But it uses the same `lib/locked.mjs` the search used, so a bug in
that model is invisible to it.

### 1b. Independent replay — my own from-spec kernel

`mykernel.mjs` implements §3 friction (640 px/s² constant), §4 rails (`min(0.92|v|+gain, 2000)`,
cue gain 120, object gain 0), §5 pocket (centre in 24 px circle **and** velocity toward the
centre), §6 history (2 px arc-length, position+velocity; crawl = one sample per step), §6.1
release `= −stored_v`, §7 impulse `j = max(0,(v_cue−v_target)·n)·1.0` with the `|v_target| ≤
|v_cue|` clamp, substeps sized by **chord length** (2 px, up to 16) instead of the solver's 4 px
(up to 8). I validated it against the solver's model: at the same substep count the two agree
bit-for-bit for 400 steps on L1, L3, L5 (`compare_models.mjs`, findings recorded in
`07_compare_L5.txt`, `08_compare_L5_4px.txt`). Two bugs **in my own kernel** were found and fixed
this way before any result was reported (an early TOI solve with the wrong units, and a stale
`last` reference in the history sampler — `history_sanity.mjs` now shows exact 2.0000 px gaps and
0 duplicate samples at every substep size).

Replaying the reported (angle, power, holdSteps) from `out/locked.json`:

| level | mine 4 px substep (== solver's) | mine 2 px | mine 1 px | mine 2 px, no pointer round-trip | strike alone (reported strike) |
|---|---|---|---|---|---|
| L1 | 2/2 ✅ | 2/2 ✅ | 2/2 ✅ | 2/2 ✅ | 0/2 |
| L2 | 3/3 ✅ | 3/3 ✅ | 3/3 ✅ | 3/3 ✅ | 1/3 |
| L3 | 3/3 ✅ | **1/3 ❌** | **1/3 ❌** | **1/3 ❌** | 0/3 |
| L4 | 4/4 ✅ | 4/4 ✅ | 4/4 ✅ | 4/4 ✅ | 2/4 |
| L5 | 4/4 ✅ | **3/4 ❌** | **3/4 ❌** | **3/4 ❌** | 1/4 |
| L6 | 4/4 ✅ | **3/4 ❌** | **3/4 ❌** | **3/4 ❌** | 1/4 |
| **total** | **6/6** | **3/6** | **3/6** | **3/6** | — |

Raw: `06_replay_mykernel.txt`, `06_replay_summary.json`, `21_perball_diff.txt`.

**VERDICT: CONFIRMED (6/6) at the solver's substep granularity; PARTIALLY CONFIRMED in general —
3/6 at a finer, equally legal substep.** The distinction matters because the locked table
requires subdividing "by chord length, not a fixed count" (§5) and never names 4 px, so a Godot
implementation is free to choose a different chord length and would land in a different column.

Which ball is lost, and why:

* **L3** (at 1–2 px substeps): balls 2 and 3 are missed. Ball 3 is the designed post-rail-bounce
  cut; my measurement puts its centre **23.824 px** from the recorded path vs the 24.4 px contact
  radius — a **0.576 px** contact margin (18_ and 21_). The strike is a 986 px, 3-bounce path; the
  first substep already differs microscopically between implementations, so a sub-pixel path
  shift loses the contact entirely.
* **L5 / L6** (at 1–2 px substeps): ball 3 is missed. In these levels the cue launches from
  (292, 280) with two object balls ~31 px away and **stops after ~10 px**: the cue's whole
  recorded path is **6 samples** (`18_level_audit.txt`). Both cut balls are hit *within the same
  physics step*, and the two impulses are resolved sequentially — so the outcome is decided by
  which substep each swept contact lands in. At 4 px substeps both contacts land in substep 3 and
  the balls drop; at 2 px they land in different substeps and one misses. This is a
  simultaneous-contact configuration, which any discrete integrator resolves
  implementation-defined.

---

## 2. Claim 2 — locked model vs prototype, and are the patches real?

### 2a. CONTROL 1 (bit-identity with the frozen prototype)

```
$ node tools/solver/dev/fidelity.mjs
  angle 0/17.6/90/115/218/45/250/305 power 170..60: max |delta| = 0.000e+0  BIT-IDENTICAL (×8)
  verdict: PASS — bit-identical on all 8 shots x 720 steps
```
Raw: `02_fidelity.txt`. **VERDICT: CONFIRMED.** `locked_checks.mjs` also reproduces
(`03_locked_checks.txt`).

### 2b. Are the *locked* patches really applied? (the converse)

Measured with my own probe on the solver's model **and** on my kernel (`constants_probe.mjs`,
`k_probe.mjs`):

| locked item | measured (solver's `locked`) | measured (prototype) | my kernel | verdict |
|---|---|---|---|---|
| rail `0.92·v + 120` (cue) | `vIn=714.6667 → vOut=777.4933`; `0.92v+120=777.4933` | `655.7667 → 983.6500` (= `1.5v`) | `718.2222 → 779.1289` (0.92v+120 = 780.7644; my step-boundary measurement loses one partial step of friction, 1.6 px/s) | ✅ exact |
| glide `v²/1280` | power 20/40/60/80 → 16.5/67.2/152.2/271.6 px (theory 17.1/68.5/154.0/273.8) | 129.3/267.0/404.7/542.4 px (exp damping) | 16.5/67.3/152.6/272.1 | ✅ |
| pocket = centre in circle **and** v→pocket | ball inside 24 px moving away: **not pocketed** | **pocketed** | not pocketed | ✅ |
| crawl = one 2 px sample per step | 50 held steps → 101.96 px, lands exactly on snapshot[600] | — | 100.82 px, lands exactly on snapshot[601] | ✅ (see note) |
| release = `−stored_v` | \|release\| = 360.6667 = \|stored\|, `cos(release, −stored) = 1.000000000` | — | same | ✅ exact |
| rewind impulse `K ≤ 1` | L1 backstop geometry: delivered **911.9026** = derived `j` 911.9026, **ratio 1.000000** | (n/a — different geometry) | delivered 906.1955 = derived 906.1955, **ratio 1.000000** | ✅ K = 1.0, **not 1.6** |
| the configs differ at all | same strike, same level: locked path 651 samples / cue rest (330.0,280.0); prototype 350 samples / (295.2,280.0) | | | ✅ |

Raw: `13_constants_probe_run.txt`, `14_k_probe_run.txt`.

**VERDICT: CONFIRMED.** The locked patches are genuinely applied; the model is not accidentally
running prototype physics. Two nitpicks: (i) the rewind impulse ratio is exactly 1.000000, not
the 1.000–1.015 the report quotes (the report's own control measures a different, multi-contact
setup, so this is a measurement-context difference, not a contradiction); (ii) "the crawl is
exactly 240.00 px/s" is really "exactly one 2 px sample per step" — I measure 101.96 px of *net*
displacement over 50 steps (244.7 px/s apparent) because the cue's resting position can be up to
one sample ahead of the last recorded sample. The mechanism is exact; the quoted 240.00 is the
nominal rate.

---

## 3. Claim 3 — 0/328 random layouts

### 3a. Their number, recomputed from their raw data

Aggregating the 984 rows in `out/rand_*.json` myself (read-only): `random 0/328`,
`hybrid 1/328`, `rule 85/328`; `bestNoRewind == 1` for **977/984** layouts (`12_rand_aggregate.txt`).
**Arithmetic CONFIRMED.**

### 3b. Fresh random sample, their budget vs the hand-designed levels' budget

Using their own search engine, my own sampler and seeds (`my_random.mjs`):

| arm | budget | sampling | result |
|---|---|---|---|
| R1 | their `LEVEL_BUDGET` (2° grid, 2 powers, 3 releases, ≤3 rewinds, beam 5) | their sampler | **0/100** (251 s) |
| R2 | the hand-designed levels' budget (1° grid, 0.25° refine, ≤4 rewinds, beam 6) | their sampler | **0/100** (642 s) |
| R3 | hand budget | **unconstrained** sampler (no 34 px cue exclusion, no 44 px pocket exclusion) | see `11_myrand_summary.json` |
| R4 | 1° grid, 3 powers, 6 releases, ≤5 rewinds, beam 8 | unconstrained | see `11_myrand_summary.json` |

Raw: `11_myrand_*.json`. **VERDICT on 3a/3c: CONFIRMED.** My concern that the random arm was
starved (2° vs 1° strike grid) is **refuted**: R2 gives the random layouts strictly more search
than the hand-designed levels got and still solves 0/100. The sampling restriction is also not the
explanation (R3/R4 confirm).

### 3c. The *explanation* is wrong: geometry does not predict 0 % (but a corrected geometry does)

The report reasons: "0.153^n gives 0.36 % for n=3, so the measured 0/328 is exactly what the
geometry predicts." That computation fixes the strike angle. The player (and the solver) chooses
it. I recomputed the coverage over **all 360** full-power strike directions
(`my_coverage.mjs`, rasterised at 6 px):

```
PART 1  mean over 360 angles: in 2R corridor 16.1 %   kick>250 14.0 %   kick reaches nearest pocket 11.2 %
        (the 24 angles dev/coverage.mjs uses: 15.4 % — reproduces the report's 15.3 %)
PART 2  UNION over all 360 angles:
        inside SOME angle's 2R corridor        : 100.0 %
        SOME angle gives a kick > 250 px/s     : 100.0 %
        SOME angle's kick reaches a pocket     : 100.0 %
PART 3  random 3-ball layouts (their sampler, 328 layouts):
        163/328 (49.7 %) have SOME angle whose corridor contains all 3 balls  (mean 1.41 of 360 angles)
        free sampler: 164/328 (50.0 %)
```
Raw: `15_coverage_run.txt`, `15_coverage_mine.json`.

**VERDICT on the stated argument: REFUTED.** The corridor precondition — the report's own
necessary condition — holds for **half** of the very same random layouts once the angle is free,
so 0.153³ cannot be "exactly what the geometry predicts".

**But the conclusion the argument was supporting does survive a corrected test.** I then added the
missing per-ball condition (a ball counts only if the strike pots it, or the rewind kick actually
sends it into a pocket with enough speed — ray test against the 6 pocket capture circles,
`oracle_pottable.mjs`):

```
arm: their-sampler (328 layouts)
corridor precondition satisfied (some angle)       : 163 (49.7 %)
GEOMETRICALLY pottable by 1 strike + 1 rewind      : 0 (0.0 %)   <- upper bound on the solve rate
arm: free-sampler (328 layouts, no cue/pocket exclusion)
corridor precondition satisfied (some angle)       : 164 (50.0 %)
GEOMETRICALLY pottable by 1 strike + 1 rewind      : 0 (0.0 %)
```
Raw: `23_pottable_their.txt`, `23_pottable_free.txt`. The test is calibrated: it correctly
reports every ball of all 6 hand-designed levels as strike-or-kick pottable, and agrees with the
solver on **13/15** of its own solved rule-arm layouts (`24_pottable_calibration.txt`; the 2
disagreements are layouts the solver cleared with a *second* rewind, which my one-rewind test
does not model).

So the right statement is: *the corridor is not what is scarce (100 % of the table is covered by
some direction; 49.7 % of random layouts put all three balls in one direction's corridor). What is
scarce is a direction under which every ball is simultaneously pottable — measured 0/328.* That
is a much stronger and more specific claim than the published 0.153³ argument, and it is
consistent with the measured 0/100 fresh layouts at two search budgets.

Also note the fixed-angle number is itself only a *mean*: the per-angle coverage varies a lot
(see the per-angle table in `15_coverage_run.txt`), and the union saturating at 100 % means the
corridor is not a scarce *area* — the scarce object is a single direction that serves every ball
at once.

---

## 4. Claim 4 — the 15.3 % corridor

Covered above. To state precisely what the number means:

* **What it is:** for one full-power strike at one fixed angle, the fraction of the playable area
  within 2R of the cue's recorded path (mean 15.4 % over 24 angles; 16.1 % over 360 in my grid).
  It is a fair measurement of *one trajectory's* band.
* **What it is not:** a measure of the mechanic's reach. The player selects the direction, and the
  union over the 360 available directions covers **100.0 %** of the playable area (with a
  kick > 250 px/s reaching a nearest pocket, by the solver's own test). So "the level is a
  corridor, not a table" and "the corridor is a scarce resource" are not supported by this
  statistic.
* **What is actually scarce:** a *single* direction whose corridor contains all n balls at once —
  mean **1.41 angles out of 360** for random 3-ball layouts. That is the number that should be
  quoted; it is ~0.4 % of directions, which is what makes blind placement hopeless, and it is
  consistent with 0/328 without any hand-waving.

---

## 5. Claim 5 — are the six levels honest, and is the rewind load-bearing?

### 5a. They are six variants of one template

`18_level_audit.txt`:

* all six use the same cue origin `(292, 280)`;
* **every** pair of levels shares ≥ 2 object-ball positions within 3 px;
* L4 and L6 share **3 of 4** ball positions;
* every level is exactly the generator's Rule A + Rule B construction: 2 backstop balls at
  ~23.5 px from history sample #1 (starting **in contact with the cue ball**: 21.76–22.52 px <
  2R = 24.4 px, i.e. overlapping by up to 2.6 px at t=0) plus 1–2 cut balls on the path.

The solver disclosed the "balls touching the cue" consequence, and it disclosed that L5/L6 are
"not harder than L4". What is *not* stated is that the six levels demonstrate **one** level idea
with parameters varied — relevant to the port plan's "level count can be large" reasoning.

### 5b. Rewind load-bearing — CONFIRMED

With the reported strike, the strike alone pots 0/2, 1/3, 0/3, 2/4, 1–2/4, 1–2/4. Over the
solver's entire 384-angle scan, the best strike-only result is 1/2, 1/3, 1/3, 2/4, 2/4, 2/4 —
**no level is clearable by one strike alone**. VERDICT: CONFIRMED.

Two qualifiers:

* In **L5/L6** the rewind is a **4–5 step (0.03–0.04 s) nudge** of a cue that has travelled only
  **10 px**; L6's rewind releases at 1256.2 px/s, i.e. the mechanic there is "release the reverse
  launch", not "walk the corridor". The report's framing ("hold R for 0.03–5.4 s", "the rewind
  corridor") lumps a 1300 px corridor walk and a 10 px nudge into one mechanic.
* The rewind *can* re-inject the full launch speed at any earlier sample (`release = −stored_v`),
  which is a stronger and more general use of the mechanic than Rule A. The report demonstrates it
  (L6) without naming it; it bears on the "energy caps the ball count" argument (see §7).

### 5c. Player tolerance — REFUTED for 5 of 6

On the solver's **own** model, replaying the reported plan while perturbing one input
(`sweep_tolerance.mjs`, then `fine_sweep.mjs` at 0.05° / 0.01 px). The second column is the
**fairest** measurement: at every perturbed strike the rewind is also re-chosen (all legal holds
for short tracks, the full rewind for long ones — `adaptive_sweep.mjs`), so this cannot be
dismissed as "your hold was one step short".

| level | clearing angle window (fixed reported rewind) | clearing angle window (**rewind re-chosen**) | clearing cue-start-offset window |
|---|---|---|---|
| L1 | ≥ ±1.5° (coarse sweep clears at ±8°) | **≥ ±1.5°** | ±0.5 px (fails at ±1 px) |
| L2 | −0.15° … +1.5° | **−0.25° … +1.5°** | **−0.03 px** … +0.5 px |
| L3 | **−0.15° … 0.00°** (4 samples of 61) | **−0.15° … 0.00°** | −0.5 … +0.10 px |
| L4 | −0.15° … +1.5° | **−0.15° … +1.5°** | **−0.01 px** … +0.5 px |
| L5 | −1.5° … **+0.30°** | −1.5° … **+0.30°** | −0.5 … **+0.07 px** |
| L6 | −0.30° … +1.5° | −0.30° … +1.5° | **−0.07 px** … +0.5 px |

Re-choosing the rewind buys at most 0.1° (L2) and changes nothing else, so the narrow sides are
genuine strike geometry, not an artifact of my holding the plan fixed.

Raw: `10_tolerance.txt`, `19_fine_sweep_run.txt`, `20_adaptive_sweep_run.txt`.

The failure mechanism is measurable: the cut balls sit **0.56–1.72 px** inside the 24.4 px
contact radius (L2 b3 0.817, L3 b3 0.576, L5/L6 b4 0.559), while a 0.05° strike rotation moves a
1000 px path laterally by ~0.87 px. So a fraction of a degree, or a hundredth of a pixel of level
data, flips pot/miss.

**VERDICT: REFUTED.** The claim "±8° tolerance → the player can aim it, not pixel-level
fussiness" (written up in `策划案/REWIND_port_plan_v1.md` §7.2.2) is true only for L1's Rule A
backstop pair. For the four cut-bearing levels the tight-side window is ≤ 0.30°, and for the cue
position it is ≤ 0.10 px in five of six levels.

**This is also the portability verdict.** The report says "if `m1-kernel`'s Godot implementation
differs on those two [substep/TOI, 2 px history], the pot/miss margins in the level files are the
first thing to re-check". I measured those margins: they are **0.01–0.1 px** on the narrow side,
and a legitimate change of substep size alone moves L3/L5/L6 from clear to not-clear. The six
`levels/*.json` files are therefore not portable level artifacts; they are traces of one
particular numerical implementation. **L1, L2 and L4** are the three that survived every kernel
variant and have the widest windows; **L3, L5 and L6** are the fragile ones.

---

## 6. Claim 6 — the authoring-difficulty table

Recomputed from `out/rand_*.json` (984 rows, 11 files):

| arm | layouts | SOLVED | rate | median sims | median best strike-only |
|---|---|---|---|---|---|
| random | 328 | 0 | 0.0 % | 604 | 1 |
| hybrid | 328 | 1 | 0.3 % | 631 | 1 |
| rule | 328 | 85 | 25.9 % | 631 | 1 |

**VERDICT: CONFIRMED as arithmetic.** Caveats that the report already partly states:

1. **Circularity.** The `rule` arm's placement formulas (`pickFan`, `cutBalls`) are the solver's
   own rules of thumb, derived from `lib/locked.mjs`'s corridor/impulse code and from a
   calibration (`dev/fan.mjs`) run on the same model. So 25.9 % measures *self-consistency of the
   rule with the model that produced it*. It is not independent evidence that the rule works; the
   report labels the arm "unverified", which is honest, but the port plan quotes 25.9 % in a table
   without that qualifier.
2. **The comparison is budget-confounded in the hand-designed direction, but it does not matter**
   (R2 above): the hand-designed levels were built with a 1° grid and a verifier in the loop.
3. **Sampling excludes the design's winning geometry.** `okPlacement` requires every random ball
   to be ≥ 34 px from the cue and ≥ 44 px from a pocket. Every designed level's winning balls are
   at **~22 px from the cue**. So the "random" arm cannot, by construction, sample the region the
   design rule lives in. That is fine for the conclusion ("blind placement fails") but it means
   0/328 is a statement about *interior* placement, not about all blind placement. My R3/R4 arms
   remove that restriction; the rate stays 0 %.
4. **Statistics.** 0/328 bounds the true rate at ≈ 0.9 % (95 %, rule of three), not "0.0 %". The
   report's "0/328 (0.0 %)" and the port plan's "0/328（0.0%）" should read "≤ ~1 %".

---

## 7. Ball budget (not in the brief, but load-bearing for product scope)

* Their own `dev/make_levels.mjs --scan` (I ran it, read-only, `16_make_levels_scan.txt`):
  across 24 angles × 3 bounce regimes the generator never exceeds **4 balls** (best: angle 15 →
  4 balls, "2/4 strike, 4/4 rewind"). This is genuine support for "the generator could verify up
  to 4".
* It is **not** support for "2–4 balls is the ceiling of the mechanic". The scan uses one
  generator, one strike, **one full rewind**, and 15°-spaced angles.
* Counter-consideration the report does not discuss: the rewind can release the cue at the
  *stored* speed of any earlier sample (`release = −stored_v`), and sample #1 stores ≈ V_BREAK.
  So the cue's energy is re-injectable: a full rewind + release is a second full-power launch
  along the same line (L6 already relies on exactly this). The claim "the ceiling is the cue's
  energy, not the corridor" is therefore about *one pass*, and the energy is not a hard budget.
  I did not build a 5–6 ball level to test this (see §9).

---

## 8. My own numbers, side by side with the solver's

| quantity | solver's value | **my value** | notes |
|---|---|---|---|
| `verify.mjs` result | 6 replay-verified, 0 failed, exit 0 | 6 / 0 / exit 0 | identical |
| bit-identity vs h5harness | max\|Δ\| = 0.000e+0, 8×720 | max\|Δ\| = 0.000e+0 | identical |
| independent replay of the 6 | 6/6 (same model) | **6/6 at 4 px substeps; 3/6 at 1 px and 2 px** | my kernel, from-spec |
| corridor coverage (one angle) | 15.3 % | 15.4 % (their 24 angles) / 16.1 % (360 angles, 6 px grid) | agrees |
| corridor coverage (angle free) | not reported | **100.0 %** | new number |
| P(random 3-ball layout satisfies the corridor condition) | 0.36 % (0.153³) | **49.7 %** (163/328) | invalidates the published argument |
| P(random 3-ball layout pottable by 1 strike + 1 kick) | not reported | **0.0 %** both samplers (0/328 each); calibrated 6/6 on the hand levels, 13/15 on solved rule-arm layouts | the corrected argument for 0/328 |
| random layouts solved | 0/328 | **0/100 (their budget), 0/100 (hand budget)** | R1/R2 |
| rule arm | 85/328 = 25.9 % | 85/328, recomputed | self-referential arm |
| L1..L4 hold tolerance | not reported | overshoot free (auto-stop); **1 step short fails** in all 6 | one-sided |
| strike-angle tolerance | "±8°" (Rule A calibration) | per-level table in §5c | only L1 ≥ ±1.5°, most ≤ 0.30° on the tight side |
| cue-start tolerance | not reported | 0.01–0.5 px (tight side ≤ 0.1 px for 5/6) | portability killer |
| ball-count ceiling | 4 (generator) | reproduced their scan: max 4 over 24 angles × 3 modes | generator-bounded |
| rewind impulse K | 1.000–1.015 | **1.000000** (exact) | mine is a single clean contact |

---

## 9. Claims I could NOT falsify (kept separate)

1. **6/6 hand-designed levels are solvable with one strike + one rewind under the locked model.**
   I reproduced it in an independent from-spec implementation at the solver's substep size, and
   L2/L4 clear on every variant I tried. This survives.
2. **The step loop is bit-identical to the frozen prototype in prototype config.** Reproduced.
3. **Every locked constant is what the parameter table says, and the locked patches really are
   applied** (rail `0.92v+120`, glide `v²/1280`, crawl 1 sample/step, release `−stored_v`,
   pocket-direction test, `K = 1.0` and not 1.6). Reproduced, partly with my own kernel.
4. **Random/procedural 3-ball layouts are not solvable in ~1000 simulations per layout**, even
   with a 1° strike grid, a 0.25° refinement and up to 4–5 rewinds (0/100 fresh, 0/100 with the
   hand budget). The conclusion "no procedural levels" survives; only its stated *reason* does not.
   Stronger: **0/328 of those layouts are pottable even by an oracle** that is allowed to pick any
   of 360 strike angles and use one rewind kick, when every ball must be either strike-potted or
   kick-aimed at a pocket with enough speed (`23_pottable_their.txt`), and the same is true under
   an unconstrained sampler (`23_pottable_free.txt`). So 0 % is a geometric fact about
   *pottability*, not merely a search failure.
5. **The rewind is load-bearing in all six levels** (no strike-only clear in the solver's own
   384-angle scan, and strike-only pot counts 0–2 of 2–4).
6. **The shipped factory rack (L0) is unsolvable** — consistent with the h5harness 0/279 breaks.
   I reproduced `best 0/10` and did not investigate further.
7. **The generator really does hit 4 balls and stop** — I re-ran their scan; the maximum is 4.
8. **The `K = 1.0` impulse does not amplify** on a clean single contact (ratio exactly 1.000000).

## 10. Claims that did not survive

1. **"The mechanic's reach is a 15.3 %-of-the-table corridor" / "the corridor is a scarce
   resource".** REFUTED: that is one fixed direction; the union over the player's 360 directions
   is 100.0 % of the playable area. The scarce object is a *single* direction that serves all the
   balls (1.41 of 360 directions for random 3-ball layouts).
2. **"The measured 0/328 is exactly what the geometry predicts" (via `0.153³`).** REFUTED as an
   argument: with a free angle the corridor precondition holds for 49.7 % of those layouts. The
   0 % itself is real and is reproduced by a *corrected* geometric test (0/328 pottable by an
   oracle that may pick any angle and use one rewind kick). The claim needs the corrected test,
   not the corridor-area arithmetic.
3. **"±8° tolerance → the player can aim it, not pixel-level fussiness" (port plan §7.2.2).**
   REFUTED for 5 of 6 levels: their tight-side clearing windows are ≤ 0.30° (L3: 0.15° total).
4. **Portability of the six level files.** REFUTED: they break at a ≤ 0.1 px cue-start change and
   at a different (legal) substep size — L3, L5, L6 change from clear to not-clear at 1–2 px
   substeps on an independent implementation of the same parameter table.
5. **The six levels are six puzzles.** PARTIALLY REFUTED: one construction, six parameter sets —
   all share the cue origin, every pair shares ≥ 2 ball positions, L4/L6 share 3 of 4.
6. **"0/328 (0.0 %)".** Precision: ≤ ~0.9 % at 95 % confidence.
7. **"The crawl is exactly 240.00 px/s".** PARTIALLY: the rule is exactly "one 2 px sample per
   step"; the net displacement I measure over 50 steps is 101.96 px because the resting position
   can be up to one sample ahead of the last record.

## 11. What would have to be true for the conclusion to be wrong, and could I rule it out?

The conclusion is: *route C is sufficient for a hand-authored, 2–4-ball puzzle game, with the
level designer placing balls on the cue's corridor, and is not sufficient for procedural/rack
play.*

| Condition that would make it wrong | Ruled out? |
|---|---|
| The 6 clears are artifacts of the solver's numerics (substep size/order), not properties of the locked model | **Not ruled out — partially confirmed.** L3/L5/L6 are artifacts at that granularity; L2/L4 are not. |
| The levels are unplayable/unportable in practice | **Not ruled out — confirmed as a real risk**: ≤0.1 px and ≤0.30° tight-side tolerances. |
| A better search would solve random layouts (i.e. no-procedural is a budget artifact) | **Ruled out (for 3-ball, ~1000–3000 sims/layout):** 0/200 across two budgets, including a strictly stronger one than the hand levels got. |
| The ball budget is > 4, so "low ball count" is not forced | **Not ruled out.** Their own scan never exceeds 4, but that is one generator, one plan, 24 angles; and a rewind release at ≈V_BREAK is a re-injectable energy source the report does not analyse. This is the biggest untested lever on the product decision. |
| Random layouts fail only because the sampler excludes the design region (< 34 px from the cue) | **Ruled out**: free sampling also gives 0 %. |
| The 15.3 % corridor claim is load-bearing for the conclusion | **It is not load-bearing and it is wrong** — the union is 100 %. Removing it does not rescue the conclusion, it just removes a bad argument for it. |
| The "rewind is load-bearing" claim is false (i.e. some levels are strike-only clears) | **Ruled out**: no strike-only clear in 384 angles for any level, and my kernel agrees on the reported strikes. |

## 12. Overstatements and concrete rewording

1. `tools/solver/REPORT.md` short verdict, line 12–13:
   *current*: "The mechanic's reach is a **15.3 %-of-the-table corridor**, and a level is solvable
   only when *every* ball is deliberately inside some corridor."
   *suggested*: "For a **fixed** strike direction the rewind corridor covers only ~15 % of the
   table; across the 360 directions the player may choose, the union covers the whole table. What
   is scarce is a **single direction** that puts every ball inside its corridor — 1.4 of 360
   directions for a random 3-ball layout — so a level is solvable only when the designer works
   backwards from one strike and places every ball on that corridor."

2. `REPORT.md` §2, line 149–150:
   *current*: "The measured 0/328 for random 3-ball levels is **exactly what the geometry
   predicts**." (justified there by `0.153^n`)
   *suggested*: "0.153³ is the probability for a **fixed** strike direction and does not apply
   here: with the direction free, 49.7 % of the same random 3-ball layouts put all three balls
   inside some direction's corridor. The binding constraint is joint **pottability** under one
   shared direction: allowing any of 360 directions, one strike and one rewind kick, and
   requiring each ball to be strike-potted or kick-aimed at a pocket with enough speed, **0/328**
   layouts qualify — which is why the measured 0/328 is a geometric fact, not a search failure."

3. `REPORT.md` §1, line 88–92 ("one strike + one rewind"): add that the rewind is a 1300 px
   corridor walk in L1/L3 but a **10 px nudge** in L5/L6 (cue path 6 samples; L6 releases at
   1256.2 px/s), so "one rewind" spans two very different mechanics.

4. `REPORT.md` §3.2 and `策划案/REWIND_port_plan_v1.md` §7.2.2 ("±8° … 玩家能瞄得中，不是像素级
   刁难"): the ±8° figure is a calibration of **Rule A** only. Measured tight-side windows for the
   shipped levels: L3 0.15°, L4 0.15°, L2 0.15°, L6 0.30°, L5 0.30°, L1 ≥ 1.5°. Suggested:
   "Rule A (backstop) tolerates ±8°; Rule B (cut) as currently placed tolerates ≤ 0.3° on one
   side because the cut ball sits 0.56–1.72 px inside the contact radius. Level templates for
   cuts must give the ball more margin (e.g. place it so the contact is not a graze) before these
   are claimed to be aimable."

5. `策划案/...§7.2.1` table row "求解难度｜最难关卡 751 次模拟即解出" and `REPORT.md` §3.4:
   correct as written, but should carry the caveat that the same search fails at a 0.05 px level-
   data change, so the "shallow search" finding is a property of a tuned trajectory, not of a
   robust level.

6. `策划案/...§7.2.3` item 3 ("2–4 球就是这一机制的上限"): the evidence supports "the generator
   verified up to 4 balls" (my re-run of their scan), not "the mechanic's ceiling is 4". Suggested:
   "the only verified ball count is ≤ 4; higher counts are untested, and the rewind's
   release-at-stored-speed (≈V_BREAK) means the cue's energy is re-injectable, so the energy
   argument for the 4-ball cap is not established."

7. Add to any consumer of this data: `tools/solver/verify.mjs` re-simulates but on the **same
   model**; it validates bookkeeping and the recorded input sequence, not the physics. The file
   itself says this ("this is the artifact to hand to another implementation"); the port plan's
   §7.2 quote of it as the evidence link should be accompanied by that caveat.

## 13. Bottom line for the Lead

**The core of the conclusion survives; two of its supporting arguments do not, and one product
assumption is over-claimed.**

* Act on: *hand-authored, small-ball-count puzzles are the only supported form; procedural/rack
  play is not* — and it is now supported twice over: 0/200 fresh random layouts at two search
  budgets, and **0/328 pottable even by an oracle** allowed any of 360 angles plus one rewind kick.
* Act on, with a caveat: *6/6 designed levels clear under the locked model* — true and now
  independently reproduced, **but** the levels are numerically fragile (≤ 0.1 px cue-start
  tolerance; 3 of 6 change verdict when the substep size changes). Treat the six `levels/*.json`
  as concept demos to be **re-verified against the shipping kernel**, not as shippable data.
* Do not act on: the 15.3 % "scarce corridor" framing (the union over the player's 360 directions
  is 100 % of the table), and the `0.153³` justification for 0/328 (refuted; replace it with the
  joint-pottability test, which does give 0/328).
* Re-open before locking the product form: **the 4-ball cap**. The only evidence is one
  generator's scan; the rewind can re-inject launch speed, and no one has tried to build a
  5–6-ball level with a tolerance-aware placement (the current generator accepts placements with
  ≥ 2 px of pocket margin, which is itself the source of the fragility). Also re-open the "±8°
  aimable" claim in `策划案/REWIND_port_plan_v1.md` §7.2.2 — it is false for the shipped levels
  and would mislead level design.

## 14. Reproduce

```powershell
node tools/solver/verify.mjs tools/solver/out/locked.json      # 6 replay-verified, 0 FAILED, exit 0
node tools/solver/dev/fidelity.mjs                             # CONTROL 1
node tools/solver/dev/locked_checks.mjs                        # CONTROL 2
node tools/solver/dev/make_levels.mjs --scan                   # ball-count ceiling (read-only)
node tools/solver/dev/evidence.mjs                             # per-ball corridor evidence (read-only)

node tools/verify_solver/constants_probe.mjs                   # rail/glide/pocket/crawl/release, locked vs prototype
node tools/verify_solver/k_probe.mjs                           # rewind impulse K on three models
node tools/verify_solver/replay_solutions.mjs                  # independent replay, 4 kernel settings
node tools/verify_solver/compare_models.mjs L5 349 170 4       # A/B state comparison
node tools/verify_solver/history_sanity.mjs                    # my sampler is well-formed
node tools/verify_solver/perball_diff.mjs                      # which ball each model misses
node tools/verify_solver/sweep_tolerance.mjs                   # coarse tolerance sweeps
node tools/verify_solver/fine_sweep.mjs                        # 0.05 deg / 0.01 px windows
node tools/verify_solver/adaptive_sweep.mjs                    # same, with the rewind re-chosen
node tools/verify_solver/my_coverage.mjs                       # 15.3 % vs union-over-angle 100 %
node tools/verify_solver/oracle_pottable.mjs --layouts 328            # oracle test: 0/328 pottable
node tools/verify_solver/oracle_pottable.mjs --layouts 328 --free     # same, unconstrained sampler
node tools/verify_solver/pottable_calibration.mjs              # that test agrees with 6/6 levels + 13/15 solved layouts
node tools/verify_solver/level_audit.mjs                       # level geometry / similarity / margins
node tools/verify_solver/diag_rewind_hold.mjs                  # why L5/L6 hold only 4-5 steps
node tools/verify_solver/my_random.mjs --n 100 --seed 900001    # fresh random layouts, 4 arms
node -e "..."                                                  # rand_*.json aggregation (see 12_rand_aggregate.txt)
```

## 15. Limits of this verification (untested)

* I did not verify the Godot kernel (`m1-kernel`'s work) — my from-spec kernel is JS, and it is a
  *re-implementation*, not the shipping engine. The port must be re-tested; §5c says it will
  matter.
* I did not build a 5–6-ball level to test the ball-count ceiling (the lever I flag as the biggest
  open question).
* I did not test whether a *tolerance-aware* level generator (e.g. requiring ≥ 1 px of cut
  margin) can produce 6/6 robust levels. That is the constructive follow-up that would settle
  whether route C's levels can be made portable.
* I did not test feel, legibility, or the 5.42 s hold in L1 (only that the hold is one-sided: any
  longer hold works because the crawl auto-stops at the origin).
* Timing/throughput numbers in their REPORT were not re-measured (not decision-relevant).
