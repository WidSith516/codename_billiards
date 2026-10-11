# REWIND — M2 presentation layer report (`godot/`)

Status: **M2 built and verified. `pwsh godot/run_gates.ps1` → GATES PASS, exit 0, 344/344 checks.**
Every number below was produced by a command in this file. What was *not* run is in
[§9 UNVERIFIED / BLOCKED](#9-unverified--blocked).

Scope: `godot/src/game/**`, `godot/src/ui/**`, `godot/scenes/**`, `godot/project.godot`,
`godot/evidence/**`, `godot/capture_evidence.ps1`. **No kernel file was modified** — see §3.1 and §4.
Level fixture: `godot/levels/corridor.json` (read-only, unchanged).

---

## 1. What is playable now

`godot/scenes/play.tscn` is the project's main scene. It loads a level, shows the table and balls,
and lets a human play one level end to end:

| verb | input | behaviour |
|---|---|---|
| aim + strike | press on the cue ball, drag **back**, release | angle = drag direction reversed; power = drag length; exactly one strike per level |
| rewind | **hold R** | the cue retraces its recorded path at the locked 240 px/s crawl |
| release | **release R** | keep the reversed velocity stored at that history point |
| pause + scrub | **P**, then drag the release marker / **left** **right** | pause mid-rewind and move the release point *backwards* along the path |
| release at marker | **Enter** | crawls on to the scrubbed point, then releases there |
| debug overlay | **F1** | live numeric state incl. a non-finite check |
| ghost path / trail | **F2** / **F3** | toggle the previous-rewind ghost and the history trail |
| reset | **Space** | debug key (T10 reset), explicitly labelled as not a game rule |

Files added (all new; nothing existing was rewritten except `project.godot`'s main-scene line):

```
godot/scenes/play.tscn             the playable scene
godot/src/game/Play.cs             the view: fixed-step drive, inputs, scripted capture states,
                                   self tests, benchmark mode
godot/src/game/TimelineTrail.cs    time-layered history trail (sessions, turn markers, ghost)
godot/src/game/AimPredictor.cs     exact aim prediction by kernel simulation (ghost-ball)
godot/src/game/RewindVisualizer.cs rewind overlay + contact forecast + scrub marker
godot/src/game/BallStyles.cs       ball identity by number + glyph (never colour alone)
godot/src/game/Hud.cs              HUD, power meter, debug overlay, layout self-check
godot/src/game/Palette.cs          Okabe-Ito colour-blind-safe palette
godot/src/game/Solver.cs           greedy autoplay used to attempt a genuine win
godot/src/ui/verify_png.gd         programmatic non-blank checker
godot/capture_evidence.ps1         one-command reproduction of every number in this report
```

---

## 2. The core mechanic, demonstrated on screen

The headline evidence is `godot/evidence/m2_3_rewind.png`, whose HUD reads, at capture time:

```
t=2.99s  tick=359  rewinds=1  trail=579
STRIKE  USED - LOCKED FOREVER
REWIND BUDGET  segments 1 / 8
distance 260 / 2400 px
TRAIL  points 579  sessions 2  turn-backs 1  turn-forwards 0
REWINDING - hold R, release R to keep the stored velocity
```

**A real 260 px corridor walk, not a 4-step nudge**: the crawl advanced 260 px of arc length
(130 samples at the locked 2.0 px spacing) along the frozen snapshot, the live history grew from
449 to 579 entries, and the constraint "hold R means rewind at 240 px/s" shows up as 260 px of walk
in 1.08 s of crawl time. The turn-back diamond sits at the rail bounce the cue has just passed, and
the traversed span is recoloured while the path it has not walked yet stays dim.

This is the project's first on-screen demonstration that the rewind verb exists as a mechanic and
not only as a kernel test.

---

## 3. Commands

```powershell
# every gate (import, build, boot, check suite, negative control, cross-process determinism)
powershell -NoProfile -ExecutionPolicy Bypass -File godot\run_gates.ps1

# everything in THIS report in one command: 7 captures + non-blank proof + 3 self-test suites
# + the trail-size timing sweep. Writes godot/.tmp/*.log and godot/evidence/*.png.
powershell -NoProfile -ExecutionPolicy Bypass -File godot\capture_evidence.ps1

# the individual M2 self tests (real exit codes)
tools\godot\godot_console.exe --headless --path godot res://scenes/play.tscn -- --selftest=all
tools\godot\godot_console.exe --headless --path godot res://scenes/play.tscn -- --selftest=input
tools\godot\godot_console.exe --path godot --resolution 1040x560 --rendering-driver d3d12 `
    res://scenes/play.tscn -- --selftest=gradient

# one capture (Vulkan is broken here: --rendering-driver d3d12 is mandatory)
tools\godot\godot_console.exe --path godot --resolution 1040x560 --rendering-driver d3d12 `
    res://scenes/play.tscn -- --level=corridor --state=rewind `
    --out=<abs>.png --min-frames=8 --capture-frames=8

# frame timing
tools\godot\godot_console.exe --path godot --resolution 1040x560 --rendering-driver d3d12 `
    res://scenes/play.tscn -- --level=corridor --bench=trail50k --points=50000 `
    --min-frames=180 --capture-frames=180 --trace-draw --out=<abs>.png
```

Scripted capture states: `aim`, `strike`, `rewind`, `rewind-contact`, `scrub`, `debug`, `win`.
A scripted state **freezes the world** after preparation, so the captured frame is the prepared
frame rather than whatever the crawl reached during the frames before the screenshot.

### 3.1 Scope proof

`git status` against the tree (most of this work is already committed; these are the changes since —
the deleted PNGs are superseded intermediates from this session, not pre-existing evidence):

```
 M godot/capture_evidence.ps1
 M godot/src/game/Hud.cs
 M godot/src/game/Palette.cs
 M godot/src/game/Play.cs
 M godot/evidence/m2_1_aim.png ... m2_8_trail_50k.png     (re-captured, final palette)
 D godot/evidence/m2_tmp_*.png, m2_layout_*.png           (13 superseded intermediates)
?? godot/M2_REPORT.md
```

`git status --porcelain --untracked-files=all` filtered for `godot/tests/`, `godot/levels/`,
`godot/src/sim/`, `tools/`, `h5/`, `策划案/` → **no matches**: nothing under those paths was written.
The kernel's determinism hash is byte-identical to the M1 baseline (§4), which is the independent
confirmation. The final evidence set is 10 files: `kernel_render.png` (M1) plus `m2_1_aim` …
`m2_9_idle`.

---

## 4. Raw gate output

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
  PASS  test gate reports GATE PASS  === kernel gate summary: 344/344 checks passed ===
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

Notes on this run, because they matter for interpreting it:

* the main scene is now `res://scenes/play.tscn`, and the boot gate still passes: it boots the
  playable scene headlessly (`--quit`), which prints `KERNEL_VIEW_READY` and exits 0;
* **344/344, not 239/239**: the count rose because another teammate (M2 levels) added their own
  tests while this work was running. `T07` passes, the negative control still fails as it must, and
  the determinism hash is **`ac58b3fa1fa73896` — unchanged from the M1 baseline**, which is the
  direct evidence that nothing in `src/sim/**` was touched;
* mid-session this gate briefly reported red. That was **not** a kernel or level-schema problem: a
  traceback was read from a stale assembly built while two teammates were writing. Rebuilding
  before believing an error whose file/line does not match the file on disk resolved it. Recorded
  here because it cost the Lead time and will cost the next person the same.

---

## 5. M2 self tests (real exit codes, not assertions in prose)

`tools\godot\godot_console.exe --headless --path godot res://scenes/play.tscn -- --selftest=all`
→ **exit 0, 68/68 checks passed**:

```
[selftest] prediction cases=24 worstDeviationPx=0.000E+000 ballContacts=2 railContacts=11 predictedRest=24
[selftest] trail cases=10 vertices=4361 straightRuns=47 worstLineDeviationPx=2.763E-012 maxVertexGapPx=2.0000 worstBendDegrees=180.00
=== M2 selftest summary: 68/68 checks passed ===
M2 SELFTEST PASS
```

`--selftest=input` → **exit 0, 13/13 checks passed** (this drives the real input handlers, see §5.4):

```
[input] driving Play._UnhandledInput with synthetic mouse/keyboard events
  PASS  pressing away from the cue ball does not strike  shotUsed=False
  PASS  a drag from the cue ball produces exactly one strike  shotUsed=True angle=0.000 power=160.000
  PASS  the drag angle is the pull direction reversed  angle=0.000 deg (expected 0.0 for a pull along +x)
  PASS  the drag distance is the shot power  power=160.000 drag=160.000
  PASS  the launch speed is power x SpeedPerPower  speed=1184.000 expected=1184.000
  PASS  the input sequence recorded exactly one shot  inputs=1 kind=Shot
  PASS  a second drag cannot strike again  inputs=1 speedBefore=0.000 speedNow=0.000
  PASS  holding R starts a rewind  rewinding=True segments=1
  PASS  releasing R ends the rewind  rewinding=False
  PASS  P pauses the rewind and arms the scrub marker  scrubbing=True marker=256 -> 256 span=256..256
  PASS  Enter releases at the scrubbed point  rewinding=False scrubbing=False speed=1098.7
  PASS  F1 toggles the debug overlay  debugOverlay=True
  PASS  F1 toggles it back  debugOverlay=False
=== M2 selftest summary: 13/13 checks passed ===
M2 SELFTEST PASS
```

`--selftest=gradient` (d3d12) → **exit 0, 2/2 checks passed** — see §7, it reads the framebuffer.

### 5.1 The aim prediction is exact, not approximate

The brief says the prediction "can be exact, not approximate — do it exactly by querying the
kernel". It is: `AimPredictor.Trace` clones the world and *runs the shot*, so the drawn prediction
comes from the same integrator, constants and TOI solver as the shot itself. The test proves it by
replaying the same shot in a fresh clone for the same number of steps and comparing every cue
position:

* 24 cases (8 angles x 3 powers): **worst cue-path deviation = `0.000E+000` px**;
* of those, 2 predicted a ball contact, 11 a rail contact, 11 no contact within the step budget;
* 24/24 predictions reached rest, i.e. the cue's stopping point is predicted too.

The adversarial pass additionally asserts the cases a naive predictor fails: head-on aim predicts
the ball and its leave speed (`1141.1 px/s`); a 6-degree graze still predicts the ball at 4 power
levels; no prediction is offered once the strike is used; asking for one anyway leaves the live
world bit-identical (`tick`/history/position unchanged, no rewind left open).

### 5.2 The drawn trail equals the real path (the old prototype cut corners)

The prototype subsampled its polyline, so the drawn path was not the path travelled. Three
measurable facts about the kernel's own output settle the new one, over 10 cases
(5 angles x 2 powers, 4361 vertices, 47 straight legs):

* **`worstLineDeviationPx = 2.763E-012`** — every recorded vertex lies exactly on a straight leg
  of the kernel's motion (2.8e-12 px is floating-point noise). The criterion is the leg's *line*,
  not the step-to-step chord: a vertex may legitimately sit on a leg beyond a step node.
* **`maxVertexGapPx = 2.0000`** — vertices are the locked 2.0 px of arc length apart, so no drawn
  chord is long enough to have skipped over a corner, and nothing was decimated between the kernel
  and the screen.
* **`worstBendDegrees = 180.00`** — the corners are still *in* the drawn data; bends between
  consecutive vertices are as sharp as the physics makes them (180 degrees = the exact head-on rail
  reversal), not chord-flattened.

Honest note on method: the first two versions of this test were **wrong**, not the trail. The first
compared the trail against per-step positions, whose polyline cuts corners itself; the second tried
to reconstruct turn points by intersecting the incoming and outgoing step headings, which silently
degrades for the exactly anti-parallel head-on bounce (the two rays are collinear, so the
intersection is undefined and the reconstructed corner landed 2.3-4 px short of the wall). Both
produced plausible-looking failures. A wrong reference is exactly how a "is the drawn path the real
path" test lies, so the shipped test uses criteria that cannot be wrong in that direction.

### 5.3 HUD layout

The HUD is immediate-mode drawing on a fixed 1040x560 canvas with absolute coordinates, which is the
kind of layout that silently overlaps after an edit. `Hud.PanelLayoutSelfCheck()` asserts the seven
blocks are ordered, inside the screen and non-overlapping; the first captures of this milestone did
overlap (`TRAIL` printed on top of the distance readout, `min`/`max` on the angle row, `hits
pending` on the transport hint), which is why the check exists. Run: `--selftest=layout`.

### 5.4 The strike path is driven, not bypassed

Scripted captures call the same methods the input handlers call, so on their own they would leave
`_UnhandledInput` - the actual hit test, drag arithmetic, key routing - untested. `--selftest=input`
constructs `InputEventMouseButton` / `InputEventMouseMotion` / `InputEventKey` objects and passes
them to `Play._UnhandledInput`. It asserts the full verb set above, including that a press away from
the cue ball does nothing, that the drag length *is* the power, that the launch speed is
`power x SpeedPerPower`, and that a second drag cannot strike (13/13).

---

## 6. Screenshots — and the proof they are non-blank

A saved PNG proves nothing, so every capture is verified by `godot/src/ui/verify_png.gd`: it loads
the file, builds an RGB histogram, takes the **mode colour** as the background (the felt, so a
capture cannot look "non-blank" merely by having a drawn top-left pixel), and reports distinct
colours, content fraction and a per-quadrant breakdown. Exit code 0 = non-blank, 4 = blank,
5 = load failed.

`powershell -NoProfile -ExecutionPolicy Bypass -File godot\capture_evidence.ps1` → **ALL CAPTURES
NON-BLANK (7/7)**, every one rc=0:

| # | file | what it shows | distinct RGB | content px (not background) | blank? |
|---|---|---|---|---|---|
| 1 | `godot/evidence/m2_1_aim.png` | initial aiming, prediction line, ghost ball, struck-ball arrow, power meter at 1258 px/s | 4447 | 251841 (43.24%) | no |
| 2 | `godot/evidence/m2_2_strike_trail.png` | after the strike: 449-point trail, `STRIKE USED - LOCKED FOREVER` | 5747 | 257015 (44.13%) | no |
| 3 | `godot/evidence/m2_3_rewind.png` | mid-rewind: orange cue + chevrons, traversed span highlighted, turn-back diamond, 260/2400 px | 6791 | 258655 (44.41%) | no |
| 4 | `godot/evidence/m2_4_rewind_contact.png` | contact forecast: ball 4 ringed and `HIT 0.9s` **before** the contact | 7921 | 259968 (44.64%) | no |
| 5 | `godot/evidence/m2_5_scrub.png` | paused rewind with the `RELEASE` marker moved back along the path | 7176 | 258721 (44.42%) | no |
| 6 | `godot/evidence/m2_6_debug_overlay.png` | debug overlay: per-ball position/velocity/state, `nonfinite no`, history/cursor/forecast, frame timings | 6476 | 378826 (65.05%) | no |
| 7 | `godot/evidence/m2_7_win.png` | `TABLE CLEARED`, kernel `Won=true, Frozen=true`, 4/4 pocketed | 4236 | 321542 (55.21%) | no |

All seven are 1040x560, `err=Ok`, captured with `--rendering-driver d3d12`. The background mode
colour in every one is `rgb(20,64,47)` = `#14402f`, the felt, at 34.9-55.5% of pixels.

Additional captures kept for the record: `m2_8_trail_50k.png` (the 50k-vertex stress frame),
`m2_9_idle.png` (idle reference for the timing delta).

The **win frame** is honest about how it was produced. `Solver.FindWinSequence` (a greedy
pot-and-rewind planner over real strokes) was tried first and did **not** clear `corridor` within
its budget, so the capture falls back to a documented forced state: the object balls are placed
inside a pocket's capture circle moving inward, and then the **unchanged kernel** decides — the log
line is literally `forced pot step 1: captures=4 pocketed=4 won=True`, and the frame shows
`won=true frozen=true`. The balls are placed; the win rule, the capture rule and the freeze are the
kernel's. §9 records the planner result as a finding, not a success.

---

## 7. Readability of state

* **Ball identity never depends on colour.** Every ball carries its ID number (large, centred, with a
  contrasting outline) plus a type glyph (cue = centre dot, object = rim ticks, and reserved shapes
  for anchor/heavy/bomb), always inside a dark ring. Fills are the Okabe-Ito colour-blind-safe set.
  The old prototype's near-black ball on dark green is structurally impossible.
* **Pocketed balls stay visible** — greyed and crossed where they were potted, so "pocketed forever"
  is observable rather than a ball quietly vanishing.
* **The time gradient is measured, not asserted.** `--selftest=gradient` renders a trail, reads the
  framebuffer back, samples the brightest pixel in a 7x7 window at 12 points along the trail, and
  reports the luminance ramp from the oldest end to the newest:

  ```
  [gradient] samples=449 luminance(old->new)= 0.387 0.416 0.448 0.477 0.513 0.551 0.591 0.633 0.680 0.726 0.775 0.927
  [gradient] oldestAvg=0.4170 newestAvg=0.8094 ratio=1.94
  PASS  the drawn trail is measurably brighter at the newest end  oldest=0.4170 newest=0.8094 ratio=1.94
  PASS  both ends are drawn (neither is background)  oldest=0.4170 newest=0.8094
  ```

  Monotonic across all 12 samples, **1.94x** brighter at the newest end. This is the framebuffer,
  not the colour array the view believes it submitted. The same measurement is what caught the first
  palette: at `#5b7f86` the old end still measured 0.52 against the felt, which is why the dim end
  was darkened to `#22343a`.

---

## 8. Frame timing for a long trail, and what keeps it cheap

Measured on this machine, `d3d12`, 1040x560, 180 frames per point, `--bench=trail50k --points=N`
(the trail is seeded with N vertices and drawn through the same code path as a real one; the world
is frozen so the seeded geometry is not overwritten):

| trail vertices | mean | p50 | p95 | max | primitives | draw calls |
|---|---|---|---|---|---|---|
| 500 | 16.9 ms | 16.4 ms | 31.0 ms | 68.7 ms | — | — |
| 2000 | 16.9 ms | 11.8 ms | 36.4 ms | 73.3 ms | — | — |
| 5000 | 18.5 ms | 14.7 ms | 37.6 ms | 87.5 ms | 39,532 | 188 |
| **50 000 (the plan's target)** | **284.0 ms** | **283.1 ms** | **301.5 ms** | **341.6 ms** | **610,318** | **374** |

Honest reading of that table:

* **Up to ~5000 vertices the trail is effectively free**: 16.9-18.5 ms/frame against a 16.7 ms idle
  baseline. 5000 vertices is already 10,000 px of recorded path.
* **At 50 000 vertices it is 284 ms/frame — 3.5 fps. This does not meet the plan's target and I am
  not claiming it does.**
* The cost is **renderer-side, not C#**: the same frame reports `authored=34 ms` for the whole
  `_Draw` (the C# submission) against 610k primitives — each antialiased vertex pair expands to about
  6 primitives, and the trail crosses the whole playfield, so the fill is a large multiple of the
  viewport. Confirmed by elimination: reusing scratch buffers instead of allocating per chunk per
  frame changed nothing (267 → 266 ms); `--no-halo` halves it (267 → 157 ms) because it is a second
  full pass; disabling AA changed nothing; chunk size barely matters between 2048 and 32768, and
  chunk=64 collapses to 1235 ms on draw-call overhead alone.
* **Why 50k is a synthetic number here**: vertices are 2.0 px of arc length apart (locked), so
  50 000 vertices is 100 000 px of travel. With one strike per level plus a 2400 px rewind budget
  (`corridor.json`), the reachable maximum is a few thousand px — the ~5000-vertex row above is the
  realistic upper bound, and it is free. `Solver` also never found a sequence needing more.
* **What was actually done to keep it cheap**, now in the code: geometry and colours are cached and
  only *appended* (per-frame work is O(new samples), not O(n)); colours are recomputed only inside
  the head window instead of for all n; draw chunks never straddle a session break; scratch buffers
  are reused; and the halo pass is one extra call per chunk rather than per segment. **What is not
  done**: a 50k trail is re-submitted every frame. If that case ever becomes real, the fix is to bake
  the trail into a `SubViewport` texture and blit one quad, invalidated by a hash of the geometry — I
  prototyped the shape of it and then dropped it, because the measured real-session cost is zero and
  an unverifiable cache is worse than a measured limit. The limit is stated here rather than hidden.

---

## 9. UNVERIFIED / BLOCKED

Everything else in this report was executed. These were **not**, and must not be treated as working:

1. **No human has played this.** All input testing is synthetic (`--selftest=input`, 13/13): it
   drives the real handlers including the cue-ball hit test and the drag arithmetic, but **feel is
   untested** — drag sensitivity, whether the pull-back reads correctly to a human hand, whether
   240 px/s feels controllable, whether the contact-forecast window (600 px / 2.5 s) is the right
   length. Those are judgements about feel and I cannot make them without a person.
2. **The 50k-vertex trail target is not met** (284 ms/frame, §8). The realistic size is met.
3. **Touch / mobile.** Not implemented and not tested; the brief notes scrubbing "matters for touch
   later". The scrub path is mouse + arrow keys only.
4. **The level used is `corridor.json` only.** Every state was captured on `corridor`; the view has
   not been exercised on `h5_rack`, `straight_line`, or any level the M2 levels teammate is
   generating. Multi-ball layouts and unusual aspect ratios are untested.
5. **Audio, menus, level select, stars, save** — out of M2 scope, absent by design.
6. **A genuine (non-forced) win of `corridor`** was not achieved. The greedy planner found a first
   stroke that pots one ball (`angle=-3.189 power=170.0 pocketedNow=1`), but `FindWinSequence`
   returned no sequence within its 40 s budget and the live autoplay did not clear the table. The
   planner is a demo helper, not the M4 solver, and **makes no completeness claim** — its negative
   result says nothing about whether `corridor` is solvable. The win frame is the documented forced
   state in §6. Whether a real solvable route exists is `m1-solver`'s question, not M2's.
7. **Long-session memory growth** was not measured; that item in `godot/REPORT.md` §8 stays open.
8. **`--bench=trail50k` seeds the trail cache directly** (`TimelineTrail.SeedSyntheticTrail`) rather
   than growing a real 50k history. It draws through the same code path, but it is a synthetic input,
   and §8 says so.

---

## 10. Things a next person should know

* **`DrawPolylineColors` ReadOnlySpan overload produced no geometry in this build.** The array
  overload draws correctly at the same call site. This is recorded because it cost real debugging
  time: a debug pass logged the exact chunk it was about to submit, which proved the submission was
  right and the call was wrong. The arrays are now reused scratch buffers, so the workaround is free.
  (`DrawPolyline`'s span overload is fine — only the colours overload misbehaved.)
* **Friction changes a ball's velocity every step**, so "the velocity changed" is *not* a contact.
  The first aim predictor reported a rail contact on step 1 for every angle because of this. A
  contact is a *direction* discontinuity (heading turned more than 1 degree within a step) or another
  ball starting to move.
* **A capture writes the file but proves nothing about its content** — always run
  `res://src/ui/verify_png.gd` on it; that checker is what makes "non-blank" a claim instead of a
  hope.
* **`--rendering-driver d3d12` is mandatory** for any capture here; without it Vulkan fails
  (`VkResult -3`) and the capture is a valid PNG of nothing.
* The playfield is drawn at `ViewScale = 1.045` (a node `Scale`; kernel coordinates are untouched)
  so the table fills the canvas. Changing that constant moves the HUD relative to the table, because
  the HUD is drawn in the same scaled coordinate space.
