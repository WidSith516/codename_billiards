# REWIND — M1 physics kernel report (`godot/`)

Status: **M1 complete and green.** `pwsh godot/run_gates.ps1` → **GATES PASS, exit 0, 239/239
checks**. Every number below was produced by a command in this file; nothing is inferred. What
was *not* run is listed in §8 under UNVERIFIED / BLOCKED.

Deliberate deviations from the frozen prototype are in [`DIFFERENCES.md`](DIFFERENCES.md).

---

## 1. What was built

A Godot 4.7.2 mono project whose simulation core is **engine-free C#** (`Rewind.Sim.*`: no Godot
types, no `RigidBody2D`, no engine physics, no engine math), plus a thin Godot layer for level
loading, the playable view and the headless gates.

```
godot/
  project.godot              config_version=5, features ("4.7","C#","GL Compatibility"),
                             [dotnet] project/assembly_name="RewindKernel",
                             common/physics_jitter_fix=0.0, physics_ticks_per_second=120,
                             default_texture_filter=0
  RewindKernel.csproj        hand-authored, Godot.NET.Sdk/4.7.2, TargetFramework net8.0
  RewindKernel.sln           hand-authored (a missing .sln silently drops the C# assembly on export)
  run_gates.ps1              the one-command verification loop (6 gates)
  DIFFERENCES.md             every deliberate deviation from h5/index.html
  levels/h5_rack.json        prototype-parity fixture (cue 292,280 + the 10-ball loose rack)
  levels/straight_line.json  2 object balls on the cue's corridor
  levels/corridor.json       4 object balls placed on the cue's natural path
  src/sim/Vec2.cs            double-precision 2D vector
  src/sim/Json.cs            hand-written JSON reader/writer (no reflection, deterministic order)
  src/sim/PhysicsConfig.cs   the locked parameter table + validation of its invariants
  src/sim/Ball.cs            Ball, ArcHistory (arc-length resampling), PocketDef, RewindBudget,
                             InputCommand, SimStats
  src/sim/LevelData.cs       data-driven levels + physics overrides
  src/sim/World.cs           fixed-step integrator, TOI collisions, rails, pockets, history,
                             rewind crawl + impulse, budgets, diagnostics
  src/sim/State.cs           WorldState capture/restore, JSON codec, FNV-1a 64 state hash
  src/game/LevelLoader.cs    res://levels/*.json -> LevelData
  src/game/KernelView.cs     minimal Node2D view (fixed-step drive, drag shot, R = rewind, _Draw)
  tests/TestRunner.cs        assertion runner with a real process exit code
  tests/TestSupport.cs       fixtures, RunWatch invariant sampler, chord/sub-step math
  tests/GddTests.cs          T01–T11 (GDD §9)
  tests/InvariantTests.cs    K01–K17 kernel invariants
  tests/TestMain.cs          headless gate entry point (+ `-- --negative` control)
  tests/test_main.tscn, scenes/kernel.tscn
```

### Architecture rules that the tests enforce

* **Fixed step.** `dt = 1/120 s` comes from `PhysicsConfig.SimulationStep`, never from the frame
  delta. `KernelView._PhysicsProcess` only decides *how many* steps to run.
* **Continuous collision.** Within an interval the velocities are constant, so the earliest
  ball-ball / ball-rail contact is solved analytically (quadratic TOI) and the world is advanced
  *to* the contact. Sub-stepping is bounded by a **chord budget**: at most 1.0 px of absolute
  travel and 1.0 px of relative travel per interval, measured (not estimated) every interval and
  asserted against the configured budget.
* **Friction once per step** (`v -= 640*dt`, velocity crossing zero stops the ball exactly) —
  per-interval application over-applies it because an interval advances by its TOI, not by its
  length. See DIFFERENCES D11.
* **History** is appended every 2.0 px of travelled arc length with position *and* velocity, and
  flushed at a full stop and on rewind press (so the frozen snapshot ends exactly where the cue
  is standing).
* **Rewind** freezes a read-only snapshot, crawls it at a constant 240 px/s (= exactly one 2.0 px
  entry per step), keeps appending to the live history, and releases with `-(stored v)`.
* **Whole-run state** (balls, history, snapshot, counters, input sequence) round-trips through
  JSON bit-exactly, and an FNV-1a 64 hash over every field is the determinism witness.

---

## 2. Commands

```powershell
# everything (import, build, boot, tests, negative control, cross-process determinism)
powershell -NoProfile -ExecutionPolicy Bypass -File godot\run_gates.ps1

# the individual gates
D:\codename\codename_billiards\tools\godot\godot_console.exe --headless --path godot --import
& "C:\Program Files\dotnet\dotnet.exe" build godot\RewindKernel.csproj -c Debug --nologo
D:\codename\codename_billiards\tools\godot\godot_console.exe --headless --path godot --quit
D:\codename\codename_billiards\tools\godot\godot_console.exe --headless --path godot res://tests/test_main.tscn
D:\codename\codename_billiards\tools\godot\godot_console.exe --headless --path godot res://tests/test_main.tscn -- --negative
D:\codename\codename_billiards\tools\godot\godot_console.exe --headless --path godot res://tests/test_main.tscn -- --suite=gdd
D:\codename\codename_billiards\tools\godot\godot_console.exe --headless --path godot res://tests/test_main.tscn -- --suite=kernel
```

Environment traps that are already handled inside `run_gates.ps1` (see `tools/godotprobe/REPORT.md`):
all Godot calls use `tools\godot\godot_console.exe` (the `D:\saint_ruin` copies run at Low
integrity and drop every write silently; `godot.exe` is GUI-subsystem so PowerShell neither waits
nor reads its exit code), and `TMP`/`TEMP`/`TMPDIR`/`DOTNET_CLI_HOME` are redirected into
`godot/.tmp` because MSBuild cannot use the machine temp directory here.

---

## 3. Raw gate output

`powershell -NoProfile -ExecutionPolicy Bypass -File godot\run_gates.ps1` (exit code 0):

```
=== REWIND kernel gates ===
godot  = D:\codename\codename_billiards\tools\godot\godot_console.exe
dotnet = C:\Program Files\dotnet\dotnet.exe

[1/6] headless import
  PASS  headless import exits 0  rc=0
  PASS  headless import prints no ERROR/WARNING  matches=0

[2/6] dotnet build
  PASS  C# build exits 0  rc=0
  PASS  C# build is warning-free  matches=0

[3/6] headless boot of the main scene
  PASS  boot exits 0  rc=0
  PASS  boot prints no ERROR/WARNING  matches=0
  PASS  boot loaded the kernel

[4/6] headless test gate (T01-T11 + kernel invariants)
  PASS  test gate exits 0  rc=0
  PASS  test gate reports GATE PASS  === kernel gate summary: 239/239 checks passed ===
  PASS  no FAIL lines in the gate log  failLines=0

[5/6] negative control - the gate MUST be able to fail
  PASS  forced failure exits non-zero  rc=1

[6/6] cross-process determinism (two separate processes)
  PASS  determinism run A exits 0  rc=0
  PASS  determinism run B exits 0  rc=0
  PASS  two processes print the same final-state hash  DETERMINISM_HASH=ac58b3fa1fa73896 / DETERMINISM_HASH=ac58b3fa1fa73896
  hash DETERMINISM_HASH=ac58b3fa1fa73896

GATES PASS
```

Header of the test gate itself (step 4), showing the parameters read back from the running engine:

```
=== REWIND kernel gate ===
engine: 4.7.2-stable (official)
assembly: RewindKernel
locked params: dt=0.008333333333333333 radius=12.2 power=170x7.4 friction=640 restitution=0.92
               cueGain=120 cap=2000 history=2 impulseK=1 crawl=240 pocketR=24 ballRest=0.955
project settings: physics_jitter_fix=0.0 ticksPerSecond=120 textureFilter=0
DETERMINISM_HASH=ac58b3fa1fa73896
DETERMINISM_DETAIL ticks=1050 history=952 rewinds=2 pocketed=0 time=8.75
```

Diagnostics printed by the kernel invariants:

```
[T11] steps=2360 rewinds=14 history=2078 intervals=5726 contacts=11 pocketed=0/10 wallMs=80
[K02] dt=1/120: entries=653 arc=1300.763781px maxGap=2 | dt=1/240: entries=654 maxGap=2
[K05] budget=1px bMax=24.394877px needed@1900=1 needed@3000=1 observedIntervals=25
      observedTravel=1.0000000000004559 guards=0
[K06] 3000 px/s: relTravel=1 pair=-1--1 tick=0 h=3.33927e-04 rewind=False guards=0 intervals=25
[K06-GUARD] none
[K06] 5000 px/s: relTravel=1 pair=-1--1 tick=0 h=2.002136e-04 rewind=False guards=0 intervals=42
[K06-GUARD] none
[K09] 72 runs in 7989 ms; peakSpeed=1252.666667 cuePeak=1252.666667 cap=2000 railFixedPoint=1500.0000000000007
[K10] live frictionless 100 s: transforms=167 maxCueSpeed=1499.999783 finalSpeed=1499.999783
[K11] head-on: cue=1220.666667 -> ball=1220.666667 px/s | b=23: cue=1231.333333 -> ball=399.495201 px/s
[K17] crawled=120px over 60 steps releaseSpeed=1012.666667px/s crawlRate=240px/s
```

Negative control (`-- --negative`) ends with:

```
  FAIL  negative control (forced failure)  [this run must exit non-zero]
=== kernel gate summary: 239/240 checks passed ===
GATE FAIL: negative control (forced failure)
```

---

## 4. GDD §9 acceptance table (T01–T11)

| id | kernel test | result | key measured value |
|---|---|---|---|
| T01 | second stroke produces no impulse | **PASS** | break speed exactly 1258 px/s; refusals while moving and at rest leave the velocity untouched; exactly 1 input recorded; a sub-`MinPower` drag is not a shot |
| T02 | release velocity is the stored `v` negated | **PASS** | `w.Cue.Velocity.Equals(-track[cursor].Velocity)` bitwise; crawl 40.000 px in 20 steps; release 964.0 px/s vs crawl 240 px/s |
| T03 | A→B→C→D then rewind D→C→B→A, history not deleted | **PASS** | history 349 → 494 → 495 entries, monotonically growing; the pre-press prefix is bitwise unchanged; the replay walks 493 → 0 monotonically and ends exactly at entry 0 with velocity 0 |
| T04 | rewind cue is not deflected, target is pushed | **PASS** | cue velocity stays `-(stored v)` bitwise and stays on the recorded polyline (0 px deviation); the target gains 1219.1 px/s along the contact normal |
| T04b | same rule with a *physical* setup (no state injection) | **PASS** | an object ball rolls onto the cue's corridor and stops there; the forced rewind reaches it after 6 steps and pushes it backwards at 121.7 px/s |
| T05 | a pocketed object ball never returns | **PASS** | pocketed in 15 steps; survives 4 rewind sessions; score never decreases |
| T06 | cue ball can be rewound out of a pocket | **PASS** | cue is potted, rewind is accepted while pocketed, the cue returns to its exact pre-shot position with velocity 0 and is back on the table |
| T07 | vertical rail bounce | **PASS (adapted)** | post-bounce speed exactly `740*0.92+120 = 800.8` px/s, `vx` = 0, the cue never penetrates the cushion; the locked model replaces the prototype's `×1.5` (see DIFFERENCES D2) |
| T08 | oblique rail bounce | **PASS (adapted)** | same magnitude law; `vx` sign kept, `vy` sign flipped, `|vx/vy|` preserved to 1e-9; exactly one rail transform |
| T08b | simultaneous two-rail contact applies the transform once | **PASS (split)** | on the shipped table the diagonal is **captured by the pocket** with 0 transforms (pocket priority); on a level with no corner pocket the double contact is reachable and applies **exactly 1** transform, `|v| = 300*0.92+120 = 396` px/s |
| T09 | rewind through an old bounce grants no energy | **PASS** | transforms stay at 1 across 140 rewind steps; rewind speed never exceeds the recorded forward speed |
| T10 | win on clearing, reset restores | **PASS** | win in 15 steps, state frozen and stable for 120 further steps, rewind/shot refused after the win, reset restores positions/history/score and re-enables the shot |
| T11 | long session: many rewinds, high speed | **PASS** | 2360 steps, 14 rewind sessions, 2078 history entries, 5726 intervals, 11 ball-ball contacts, 0 NaN, 0 out-of-bounds, 0 guard hits, 80 ms wall clock |

T07/T08 are **adapted on purpose**: the locked rail model is `|v'| = min(|v|*0.92 + gain, 2000)`,
so "speed × 1.5" is deliberately *not* implemented and must not be asserted. T08b is tested in two
halves because on the shipped six-pocket table the corner double-rail *reflection* is provably
unreachable (moving into both corner walls implies velocity pointing at the corner pocket centre).

## 5. Kernel invariants (K01–K17)

| id | invariant | result | evidence |
|---|---|---|---|
| K01 | constant-deceleration friction | **PASS** | glide **1231.14 px** vs analytic 1236.38 (−0.42%, tolerance 1%); stop time **1.9667 s** vs 1.9656; speed is linear in time within 1 px/s; the ball stops exactly at 0 |
| K02 | arc-length history, frame-independent | **PASS** | max gap exactly **2.0 px**; 653 entries at dt=1/120 vs **654 at dt=1/240** — a per-frame recorder would have halved the spacing |
| K03 | determinism in-process | **PASS** | two runs of the canonical scenario hash identically: `ac58b3fa1fa73896` |
| K04 | state + input sequence serialize | **PASS** | captured *mid-rewind* (frozen snapshot included), JSON round trip preserves the hash exactly, and both worlds keep evolving identically for 60 further steps |
| K05 | sub-step budget derived from the chord | **PASS** | budget 1.0 px ⇒ guaranteed detection up to `b = 24.3949 px` (≥ the worst measured graze 23.5); observed travel 1.0000000000 px; 25 intervals; guard 0 |
| K06 | no tunnelling, head-on ≥ 3000 px/s | **PASS** | 3000 and 5000 px/s: contact registered, target receives ≥ 0.85·v, min centre distance ≥ 24.4 − 1e-6, relative travel 1.0 px |
| K07 | no tunnelling at grazing incidence | **PASS** | `b = 23.5` (chord 13.13 px) **16/16 phases** detected and deflected at 1900 / 2500 / 3000 px/s — the prototype missed 41.7% of these at 1900 px/s |
| K08 | pockets are never skipped | **PASS** | captured at 2000 and 4000 px/s |
| K09 | 72-direction full-power sweep | **PASS** | 72 runs, 0 NaN, 0 out-of-bounds, 0 cap violations, all settle inside 60 s; peak speed **1252.67 px/s**, cue peak ≤ the **1500 px/s** rail fixed point, cap 2000 |
| K10 | rail model is bounded | **PASS** | fixed point `120/(1−0.92) = 1500` (< cap 2000); the map converges to it from 0/100/500/1258/1999/3000; live frictionless bouncing for 100 s peaks at **1499.999783 px/s** |
| K11 | rewind impulse model | **PASS** | `RewindImpulseK ≤ 1` and a value > 1 is rejected by construction; head-on 1220.67 → **1220.67** (ratio 1.000, no amplification); `b = 23` graze → **399.50** = 33% of head-on |
| K12 | levels are data driven | **PASS** | `h5_rack` reproduces the prototype spawn (nearest neighbour **25.6954 px**), 6 pockets r=24, budgets from file; `corridor` loads its own 4-ball layout and 8-segment / 2400 px budget; unknown physics key and unknown ball type are rejected; `bomb` is reserved as data only |
| K13 | rewind budget enforced | **PASS** | distance budget 30 px auto-ends the rewind at ≤ 30 px and refuses more; segment budget 2 allows exactly 2 rewind sessions and refuses the third |
| K14 | replay from the serialized input sequence | **PASS** | a fresh world replayed from the JSON input log reproduces the run hash `ee0b3fedac68467a` |
| K15 | pocket rule (velocity must point in) | **PASS** | inside the capture circle moving outward → not captured; moving inward → captured |
| K16 | JSON codec | **PASS** | bit-exact double round trip, escapes, null, level round trip; malformed JSON rejected |
| K17 | crawl is one history entry per step | **PASS** | `240 * 1/120 = 2.0 = HistoryStepPx`; 60 steps crawl **exactly 120.00 px**; release speed 1012.7 px/s = the stored speed bitwise, decoupled from the 240 px/s crawl |

---

## 6. The numbers that matter (locked-table assertions the Lead asked for)

| requirement | measured | verdict |
|---|---|---|
| (a) no ball exceeds `SpeedCap = 2000`, no `Infinity`/`NaN` over a full-power run in all directions | 72 directions × up to 60 s: peak **1252.67 px/s**, 0 non-finite, 0 out-of-bounds, all settle | PASS |
| (b) full-power stop time ≈ 1.97 s, glide ≈ 1236 px | **1.9667 s**, **1231.14 px** (−0.42%: per-step semi-implicit friction) | PASS |
| (c) rail fixed point holds, never above the cap | fixed point 1500 exactly; live 100 s frictionless bouncing peaks at **1499.999783 px/s** | PASS |
| (d) holding R for N seconds rewinds ≈ 240·N px, release = −stored `v`, independent of the crawl | **exactly** 120.00 px in 60 steps; release bitwise `-(stored v)` at 1012.67 px/s | PASS |
| (e) history entries 2.0 px apart, not frame dependent | max gap **2.0 px**; identical entry count at dt=1/120 and dt=1/240 | PASS |
| no tunnelling ≥ 3000 px/s and at grazing incidence | 3000/5000 px/s head-on; `b=23.5` 16/16 phases at 1900/2500/3000 px/s | PASS |

The crawl relationship is now an explicit, tested invariant: `RewindCrawlPxPerStep ==
HistoryStepPx`, `PhysicsConfig.CrawlIsOneHistoryEntryPerStep`, asserted at gate start and in K17.
The rate is **not** a free tuning knob (the M1 solver found that a faster crawl skips history
entry #1 and only 1 of its 6 levels stayed solvable); it is a locked constant and is never
overridden by any test or level.

Non-headless render (evidence that the kernel drives a real frame, not just headless math):

```powershell
tools\godot\godot_console.exe --path godot --resolution 1040x560 --rendering-driver opengl3 `
    res://scenes/kernel.tscn -- --out=<abs path>\kernel_render.png
# rc=0, OpenGL API 3.3.0 NVIDIA, PNG 1040x560, 14340 bytes
```

Programmatic non-blank check (System.Drawing, every 4th pixel): 1040x560, 100 distinct colours,
97.9% of sampled pixels differ from the background colour. **Not visually inspected** — the vision
backend is unavailable in this session (see §8). The capture is committed as
`godot/evidence/kernel_render.png` (raw copy of `godot/.tmp/kernel_render.png`, which is what the
run above produced).

---

## 7. Deviations from the prototype

Full list with reasons and evidence: [`DIFFERENCES.md`](DIFFERENCES.md). Summary of what a
verifier should expect to be *different* on purpose: friction model (constant 640 px/s² instead of
exponential damping), rail model (`×0.92 + 120`, capped, once per ball per step, instead of an
unbounded `×1.5`/`×0.85`), pocket rule (circle + inward velocity instead of a bare point test),
history sampling (2.0 px arc length instead of "any displacement > 1e-5"), rewind playback
(constant 240 px/s crawl instead of one record per step with a position-delta velocity), rewind
impulse (`K = 1.0` with the normal projection instead of `1.6 × rel`), continuous collision
(analytic TOI + chord-budgeted sub-stepping instead of a naked 1/120 step), and win = frozen.

---

## 8. UNVERIFIED / BLOCKED

Everything else in this report was executed. These were **not**, and must not be treated as
working:

1. **Export (Windows / Android).** Not attempted — that is milestone M5. The `.sln` is committed
   because a missing `.sln` makes a C# export return rc=0 while silently omitting the assembly.
2. **Visual inspection of the render.** The screenshot exists and is measurably non-blank, but the
   vision backend is unavailable in this session (`VISION_BACKEND_UNAVAILABLE`), so nobody has
   looked at it. It is *not* claimed to look correct.
3. **Human feel / playability.** No human played this. The plan's core-fun hypothesis is untested
   here and always was.
4. **Behavioural A/B against `h5/index.html`.** Port plan §3.2 decision 9 asks for an alignment run
   over the shared rule subset. **Not done.** The locked table deliberately changes exactly the
   constants that such a run would compare (friction, rails, pockets, history, impulse), so an A/B
   would only be meaningful on the subset that is *unchanged* — ball radius, table box, shot
   mapping, ball-ball restitution. Those were kept and are asserted (K12's 25.6954 px rack check is
   the one direct geometric parity measurement in this report).
5. **Solvability of any level in this kernel.** That is `m1-solver`'s result, not mine; this report
   only claims that the kernel is deterministic, bounded and replayable enough to be solved against.
6. **Performance on target hardware (mid-range Android 60 fps).** Only desktop timing was taken:
   the whole gate loop (import + build + boot + suite + negative control + 2 determinism runs)
   takes **37 s**, the test suite alone **8.9 s** (K09's 72 runs ≈ 8.0 s, T11's 2360 steps ≈ 80 ms).
7. **Long-run memory of an unbounded rewind session.** T11 bounds history at < 60k entries as an
   assertion, but no multi-minute soak was run.
8. **The determinism hash is build-specific.** `ac58b3fa1fa73896` identifies *this* build; a
   verifier must compare two runs of the same build rather than against the literal value. Any code
   change (including a comment-free logic change) legitimately changes it.
