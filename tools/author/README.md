# `tools/author` — tolerance-aware level authoring tool

One command, no hand-placed coordinates, and every number it prints comes out of the **shipping
kernel**: this project compiles `godot/src/sim/*.cs` **verbatim** (`AuthorTool.csproj`), so there is
no second copy of the physics that could drift from the game.

```powershell
# build + verify the shipped levels (reads godot/levels/*.json back from disk)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1

# regenerate godot/levels/l*.json from scratch (search + full measurement + emit)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1 -Command "emit"

# one geometry, every number (the design loop)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1 -Command "probe --cuex=520 --cuey=280 --aim=-90 --pocket=4 --lback=30 --forward=1,130"

# validate the analytic tolerance law against the kernel (predicted vs measured |dn/dtheta|)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1 -Command "verify-cut"

# exploration: enumerate the placement space and report the reject reasons
powershell -NoProfile -ExecutionPolicy Bypass -File tools\author\run_author.ps1 -Command "scan"
```

`run_author.ps1` redirects `TMP`/`TEMP`/`DOTNET_CLI_HOME` into `tools/author/.tmp` and keeps the
`saint_ruin` dotnet off `PATH`, for the same reasons `godot/run_gates.ps1` does.

## What it measures (first-class outputs, not a side experiment)

For every level the tool prints and embeds in the level file:

| metric | definition |
|---|---|
| **pocket margin** | `PocketRadius − perpendicular distance from the pocket centre to the ball's incoming trajectory line`, measured at the capture step. The kernel captures on first insertion into the 24 px circle, so the penetration depth at capture is ~0 for every pot and cannot discriminate a good pot from a rim graze; the line margin is the pot's lateral shift budget. |
| **clearing window** | contiguous ± window of strike angles around the nominal aim for which a clearing plan exists, **with the power and the rewind release re-chosen per angle** (`Solve` re-runs the whole search at each angle). |
| **margin window** | the same window with the extra constraint that *every* object pot keeps ≥ 8 px of pocket margin. |
| **power window** | the range of drag lengths that still clears at the nominal angle. |
| **cue-start window** | how far the cue's spawn can be moved perpendicular to the aim before clearing fails, with the player allowed to re-aim. |
| **strike-only best** | the most object balls any single-strike plan (no rewind at all) pockets over 720 angles × 30 powers. |
| **portability** | the same witness replayed at sub-step travel budgets 0.5 px and 2.0 px, and at dt = 1/240 s and 1/60 s, with the release point re-quantised to what that configuration can actually reach. |

A candidate that misses any bar is **rejected and printed as rejected**; it is never written as a
level.

## Where the tolerance comes from

`Geo.SolveCut` aims a ball's departure line through a pocket centre in closed form, and the same
derivation gives the sensitivity of that departure direction to the strike angle:

```
|dn/dtheta| = |1 - s / (2R cos(phi))|          s = along-line coordinate from the pivot, phi = cut angle
```

* it **vanishes** at `s = 2R cos(phi)`, i.e. for a ball hit at its own contact distance from the
  pivot — that is why a ball planted ~30 px from the cue with a thick cut is almost aim-insensitive;
* every pot pays `miss = D_B * |dn/dtheta| * eps` at the pocket, so a **short** ball-to-pocket
  distance and a **small** `|dn/dtheta|` are the only two levers;
* `verify-cut` checks this law against the kernel: predicted vs measured agree within 0.5 % for
  moderate contacts and within 7 % at the extreme (`s = 320 px`, `b = 23.5 px`).

## Commands

| command | purpose |
|---|---|
| `probe` | build one recipe from `--cuex/--cuey/--aim/--pocket/--lback/--forward`, then print the nominal plan, the whole window set, and (with `--debugPlan`/`--diagnose`) per-ball peak speeds |
| `scan` | enumerate the pocket-driven placement space and print the ranked candidates + reject histogram |
| `emit` | run the search, build the shipping set, fully measure it and write `godot/levels/l*.json` |
| `verify` | re-load every level file carrying an `"authoring"` block and re-check it against its own published bars |
| `verify-cut` | validate the analytic tolerance law against the kernel |
| `full` | `emit` then `verify` |

## Files

```
AuthorTool.csproj   net8.0 console app; <Compile Include="..\..\godot\src\sim\*.cs" />
run_author.ps1      one-command build+run with the environment traps handled
Geo.cs              the tolerance law, the closed-form cut solver, pot reachability
Kernel.cs           plan execution (forward phase / rewind session), capture + margin tracker, spawn-gap stats
Metrics.cs          Solve / MeasureWindow / MeasurePowerWindow / MeasureCueStartWindow / MeasureStrikeOnly + the floors
Authoring.cs        Corridor (the MEASURED cue path), Recipe (level construction), Candidate + acceptance
Measure.cs          the full acceptance run (windows, strike-only control, settle, portability)
Search.cs           the parameterised generator (pocket-driven) + the shipped-set construction
Pipeline.cs         loader / JSON emitter (with the optional "authoring" block) / table reporter
```
