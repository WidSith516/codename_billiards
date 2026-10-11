# godot/ — deliberate deviations from the frozen `h5/index.html` prototype

Authority for every value below:

| source | what it fixes |
|---|---|
| `策划案/REWIND_core_params_v1.md` (v1.1, Lead-locked) | the numeric table and the rewind semantics |
| `策划案/REWIND_port_plan_v1.md` §2.2/§2.3 (defects), §3.2 (decisions 1–9), §7.1 (locked decisions) | architecture and defect list |
| `tools/h5harness/REPORT.md` | the measured prototype behaviour being replaced |
| `tools/godotprobe/REPORT.md` | the engine constraints (integrity level, `.sln`, jitter fix, TOI) |

Reference implementation: `h5/index.html`, sha256
`98ef7c191a91f3c0b05bbcc8a91465de11fbea7069c124e8bca95dc85fefb864`, **unmodified**.

Where this document says "locked", the value is in the Lead's table and the M1 solver
(`tools/solver`) uses the same number, so "solvable in the solver" and "solvable in the game"
mean the same thing. **Do not change a locked value here without updating that file and telling
the solver owner.**

---

## 1. Deviations demanded by the locked table (each one fixes a measured defect)

| # | prototype (`h5/index.html`) | kernel (`godot/src/sim`) | why / evidence |
|---|---|---|---|
| **D1** | friction `v *= exp(-1.07*dt)`, plus a hard `< 9 px/s -> 0` cutoff | constant deceleration `v -= 640*dt`, velocity crossing zero stops the ball exactly | prototype glide on a full-power shot is **1167 px = 1.28 table lengths** and only stops because of the artificial cutoff. Locked §3; measured glide now **1231.1 px** (analytic 1236.4, −0.42%) and stop time **1.967 s** |
| **D2** | cue rail `v *= 1.5` (unbounded, both components); object balls `v *= 0.85` | reflect direction, then `|v| = min(|v|*0.92 + gain, 2000)`, gain = 120 px/s cue / 0 object, **at most once per ball per physics step** | `*1.5` is an unstable fixed point (`v* = kL/ln1.5`, only 540 px/s for a short flight, against a 1258 px/s break): 9/279 full-power breaks reached >1e5 px/s and 1 reached `Infinity`. The additive map has fixed point `120/(1−0.92) = 1500 px/s`, below the 2000 cap, so **divergence is impossible** (measured live: 100 s of frictionless bouncing peaks at **1499.999783 px/s**) |
| **D3** | the gain is applied only if the position crossed a wall; a cue resting against a cushion would therefore keep `Math.abs(vx)` and, with a *multiplicative* model, gain nothing | the rail transform (restitution + gain) is applied only when the ball's **normal velocity actually points into that wall** | with an *additive* gain the prototype's unconditional "bounce" would be a free energy source: a cue at rest touching a cushion would be injected with +120 px/s every step. Guarded explicitly; clamped-but-not-moving is not a bounce |
| **D4** | pocket = point test `hypot(ball, pocketCentre) < 24`, tested before the rails | pocket = circle test **and** the velocity must point into the pocket (`dot(v, centre − p) > 0`), still tested before the rails | prototype: a ball at rest in the capture circle is swallowed (harness R03), and the corner double-rail contact point is `hypot(12.2,12.2) = 17.25 px` from the corner centre so T08b is dead code. Locked §5 |
| **D5** | history appended "whenever displacement > 1e-5 px" | fixed **2.0 px arc-length** resampling, storing position **and** velocity, plus a flush when a ball stops and when rewind is pressed | prototype spacing had median **0.0996 px**, max **10.39 px** -> jerky playback, polyline trail cuts corners, history unbounded. Locked §6 |
| **D6** | rewind playback = exactly one history entry per physics step; entry velocity derived as `(x − x_prev)/dt` | crawl a **constant 240 px/s** along the frozen polyline (linear interpolation inside an entry) = exactly one 2.0 px entry per step; the entry's stored velocity is used **as-is** (negated) | locked §6.1: crawl and release are deliberately decoupled. Measured: 60 steps -> **exactly 120.00 px**, release **1012.7 px/s** |
| **D7** | release velocity `= (x − x_prev)/dt` (coupled to the step size and to the clamped position at a rail) | release velocity `= −(stored v)` at the current entry — bit-exact, no derivative | port plan decision 3 / D5. Measured bitwise equal in T02 and K17 |
| **D8** | rewind impulse `kick = min(1300, 1.6 * ((mover.v − target.v)·n))` | `j = max(0, (v_cue − v_target)·n) * K`, `K = RewindImpulseK = 1.0`, applied along the contact normal; the cue is never deflected; the target is also pushed out of overlap | prototype **amplifies by 1.6** and ignores contact geometry (a graze pushes as hard as a head-on hit). Measured now: head-on 1220.67 -> **1220.67 px/s** (ratio 1.000, no amplification); a `b = 23` graze gives **399.50 px/s** = 33% of head-on |
| **D9** | discrete detection once per 1/120 s step, no CCD; a `b = 23.5` graze (chord 13.13 px) is missed **41.7%** of the time at 1900 px/s | analytic time of impact for ball-ball and ball-rail (advance *to* the contact), plus chord-budgeted sub-stepping: max **1.0 px** travel and **1.0 px** relative travel per interval, measured every interval | locked §5. Measured: 16/16 phases hit at 1900/2500/3000 px/s on the `b = 23.5` graze; head-on contacts at 3000 and 5000 px/s; observed relative travel **1.0000000000 px** |
| **D10** | physics driven by the rAF delta; `physics_jitter_fix` default 0.5 | own fixed step, `dt = 1/120 s` constant, never the callback delta; `physics_jitter_fix = 0`; doubles only, fixed index order, no engine physics | port plan decision 1 + godotprobe REPORT. Two processes print the same state hash (`ac58b3fa1fa73896`) |
| **D11** | friction is applied inside the per-step integration | friction is applied **once per physics step**, before the interval loop (locked §3 literally says `v -= A_FRICTION * dt`) | applying it per sub-interval *over-applies* it, because an interval advances by its time of impact rather than by its full length; that made the analytic TOI (solved with one velocity) disagree with the movement (made with another) and a grazing contact crept towards its own solution instead of reaching it (found by K05, see §3) |

## 2. Engineering deviations (not forced by the table)

| # | prototype | kernel | why |
|---|---|---|---|
| **D12** | win keeps simulating | `Won` freezes the kernel (`Step()` is a no-op) | a stable terminal state is needed for the determinism hash and for a solver; trivially reversible in M3 |
| **D13** | rewind is unlimited | per-level budget **from data** (`maxSegments`, `maxDistance`), enforced at press and during the crawl; exhaustion behaves exactly like a release | port plan §4.2 "回溯预算" makes rewind a spendable resource; the field is data so M4 can tune it per level |
| **D14** | ball positions, colours, pockets and targets hardcoded in `init()` | level = JSON (`godot/levels/*.json`): table box, pocket circles, ball spawns with type/radius/mass/velocity, budget and per-level physics overrides | port plan §7.1 "关卡数据必须数据驱动"; an unknown key or ball type is a hard error so two builds cannot silently diverge |
| **D15** | `RigidBody2D`-free but still JS-flavoured mutable globals | engine-free C# kernel (`Rewind.Sim.*`): no Godot types, no engine physics, no engine math | the M4 solvability solver links the same sources; `PhysicsServer2D`/shape queries stay available for aim previews only |
| **D16** | — | whole-run state (balls, history, the frozen rewind snapshot, counters **and the input sequence**) is serializable to JSON and byte-replayable | requirement 10 / port plan decision 1 |
| **D17** | — | special ball types `bomb` / `anchor` / `heavy` are **reserved as data only**: they load, keep their declared type for tools, and behave exactly like ordinary object balls | the Lead asked for the bomb field to be reserved without behaviour in M1. `LevelData` stores `BallSpawn.DeclaredType`; an unknown type string is rejected |
| **D18** | single 384-line file mixing UI, physics and rendering | kernel-only M1: no menus, no level select, no audio, no HUD; one minimal `_Draw` view (`KernelView`) that also provides the boot/screenshot path | milestone scope |

## 3. Kernel defects found and fixed by this gate (not prototype deviations)

These were implementation bugs in `World.cs`, caught by the assertions, and are recorded because
they are exactly the failure mode the port plan warns about (a "successful" run that is wrong):

1. **Tick-spin on exact contact (variant 1).** `PairTimeOfImpact` reported a contact at exactly
   touching distance while `ResolvePair` required strictly overlapping distance; the step then
   spun on `toi = 0`, resolving nothing. 11/72 sweep directions hit the interval guard.
   Fixed by making the two tests exact complements (shared `ContactGap`).
2. **Tick-spin on exact contact (variant 2) — the same class, in the grazing case.** After the
   first fix the resolver's acceptance radius and the TOI's contact radius were *identical*, so a
   pair sitting on that boundary flipped between "event" and "not touching". Fixed by splitting
   the tolerances and validating their order: `ContactGap (1e-9) < ResolveGap (1e-6) <
   SeparationEpsilon (1e-5)`; `PhysicsConfig.Validate()` now throws if that order is broken.
3. **Friction over-application** (see D11) — the same investigation; `sumH` per step reached
   0.094 s against a `dt` of 0.0083 s.
4. **Numeric guard fallback violating the budget.** When the interval guard tripped, the
   fallback moved the whole remaining step in one go (18.4 px of relative travel against a 1 px
   budget). The fallback is now sub-stepped too, so the guarantee holds on every path.

After these fixes the guard never trips in any test (`guards=0` in K05/K06/K09/T11).

## 4. Deliberately kept identical to the prototype

* Ball radius 12.2 px, table box `left=65, right=975, top=63, bottom=497`, six pockets, capture
  radius 24 px, design resolution 1040x560.
* Shot mapping: drag power capped at 170 px, `speed = power * 7.4` -> V_BREAK = 1258 px/s,
  minimum drag 12 px, aim direction = ball-to-pointer.
* Ball-ball restitution **0.955** (equal mass, 1.0) — the prototype's `(1+0.955)` pair impulse.
* Only the cue ball records history and can be rewound (GDD §4.1), and the live history keeps
  appending during a rewind (GDD §4.2) — A→B→C→D rewinds D→C→B→A.
* `godot/levels/h5_rack.json` reproduces the prototype's spawn exactly (verified: nearest
  neighbour **25.6954 px**, the loose rack measured in `tools/h5harness/REPORT.md` D-7).

## 5. Known intent-vs-implementation mismatch (NOT changed)

* The playable area is 910x434 = **2.10:1** while a real table is 2:1. The Lead recorded this as
  deliberately unfixed (changing it would move every level coordinate). It is **not** touched here.
* On the shipped six-pocket table the corner double-rail *reflection* remains unreachable: a ball
  moving into both corner walls necessarily has velocity pointing at the corner pocket centre, so
  the locked pocket rule captures it first. T08b therefore tests both halves explicitly — capture
  on the shipped table, and the single-transform branch on a level whose corner has no pocket.
