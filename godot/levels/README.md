# `godot/levels` — authored levels

`h5_rack.json`, `corridor.json` and `straight_line.json` are the kernel's own fixtures and are
**untouched**.

The four `l*.json` files are produced by [`tools/author`](../../tools/author/README.md) and carry an
optional top-level `"authoring"` object that `LevelData.FromJson` ignores. The level schema itself
(`id`, `name`, `table`, `ballRadius`, `pocketRadius`, `pockets:[{x,y,r}]`, `balls:[{id,type,x,y}]`,
`budget`) is used exactly as before — no key was renamed or restructured.

The `l*` levels are replay-verified in the kernel test suite (`godot/tests/LevelToleranceTests.cs`,
suite `L01`): every claim below is re-measured in-engine by replaying the witnesses stored in the
level file, not taken on trust from the authoring tool.

## Measured table (raw; reproduce with `run_author.ps1 -Command verify`)

| level | object balls | spawn gap | clearing window | window with margin ≥ 8 px | drag window | cue-start window | strike-only best | corridor | nominal margin |
|---|---|---|---|---|---|---|---|---|---|
| [`l1_midrail_reverse.json`](l1_midrail_reverse.json) | 2 | 30 px | ±3.484° | **±2.422°** | 96–170 px | ±2.188 px | 1/2 | 106.2 px | **24 px** |
| [`l2_midrail_mirrored.json`](l2_midrail_mirrored.json) | 2 | 30 px | ±3.484° | **±2.422°** | 96–170 px | ±2.188 px | 1/2 | 106.2 px | **24 px** |
| [`l3_deeper_walk.json`](l3_deeper_walk.json) | 2 | 36 px | ±3.969° | **±2.734°** | 96–170 px | ±2.313 px | 1/2 | 106.2 px | **24 px** |
| [`l4_offset_line.json`](l4_offset_line.json) | 2 | 32.0 px | ±3.547° | **±2.453°** | 96–170 px | ±2.188 px | 1/2 | 128.5 px | **24 px** |

Portability (same witness, different legal substrate settings — re-verified by the kernel test):

| level | sub-step 0.5 px | sub-step 2.0 px | dt = 1/240 s | dt = 1/60 s |
|---|---|---|---|---|
| l1 / l2 / l3 | clears, margin 24 px | clears, margin 24 px | clears, margin 24 px | clears, margin 24 px |
| l4 | clears, margin 24 px | clears, margin 24 px | clears, margin 24 px | **does not clear (1/2)** — reported, not hidden |

## Example solution (l1; the same shape for all four)

Ball data: cue `(520,280)`, backdoor ball `(520,310)`, strike-potted ball `(520,150)`.

1. **Strike:** aim `-90°` (straight up the table), drag `96 px` (≈ 710 px/s). The cue rolls up 106 px,
   cuts the ball at `(520,150)` — its departure line runs through the top-middle pocket centre — and
   the cue stops against it.
2. **Rewind:** press `R` (the cue is at rest at arc 105.8 px), let go at arc **102 px**. The release
   hands the cue the stored velocity reversed, and it rolls **straight back down the corridor**, past
   its own spawn, and cuts the backdoor ball at `(520,310)` into the bottom-middle pocket. Both pots
   land dead-centre: measured pocket margin **24 px** for both balls (the whole capture circle).

## Mechanisms

* **kept — the backdoor pot by the reverse pass.** The rewind hands the cue `-(stored v)` at the
  release point; on a straight corridor that launches it back along its own path, *past its spawn*,
  into a ball the strike never touched. In the reverse frame that ball sits `sB = +LBack` in front of
  the pivot, so its sensitivity is `|1 − LBack/(2R cos φ)| ≈ 0.1–0.3`: a 24 px margin that survives
  tens of degrees of aim error.
* **kept — the planted cut, with the tolerance law as the placement rule.** The second ball is planted
  `130 px` along the corridor (just beyond the 100 px release floor) and cut into the opposite
  mid-rail pocket with a thick contact. Its sensitivity is `|1 − s/(2R cos φ)| ≈ 4.3`, which is what
  caps the clearing window at ±3.5° and the margin window at ±2.4°: those two numbers are the same
  law reading out, not a tuned coincidence.
* **abandoned — the "backstop kick" (ball spawning in contact with the cue).** Triggering it needs the
  balls to start within `2R` of each other, and that is exactly what destroys tolerance: one
  simulation step is the whole interaction, so the clearing window collapses to 0.15°. Near-overlap
  and aimability are mutually exclusive in this kernel, so the mechanism is a dead end, not a tuning
  problem. No shipped level uses it: the smallest spawn gap here is 30 px (`2R = 24.4 px`).
* **abandoned — pots that arrive through a cushion bounce.** A corner pocket can catch a ball after a
  rail contact, but the *measured* approach line is then off the pocket centre: one such candidate
  landed with 1.78 px of margin even though its design line ran through the centre. Every pot in
  these levels is rail-free into the capture circle, which is why the margins read 24 px.
