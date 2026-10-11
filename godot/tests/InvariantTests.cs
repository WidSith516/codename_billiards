using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Tests;

using Rewind.Game;
using Rewind.Sim;

/// <summary>
/// Kernel invariants: the things the port plan says must be true and the prototype got wrong.
/// Every number in godot/REPORT.md comes from one of these checks.
/// </summary>
public static class InvariantTests
{
    // ------------------------------------------------------------------- K01
    public static void K01_ConstantDecelerationFriction(TestRunner t)
    {
        t.Section("K01 - rolling friction is constant deceleration (not exponential damping)");
        var level = TestSupport.BigTable();
        level.AddCue(500, 2000);
        level.AddObject(7000, 2000);
        var w = new World(level);

        w.Shot(0, 170);
        var v0 = w.Cue.Speed;
        var samples = new List<(double Time, double Speed)>();
        for (var i = 0; i < 400; i++)
        {
            w.Step();
            if (i == 39 || i == 79 || i == 119) samples.Add((w.Time, w.Cue.Speed));
            if (w.Cue.Speed == 0.0) break;
        }

        var glide = w.Cue.Position.X - 500.0;
        var stopTime = w.Time;
        var expectedGlide = v0 * v0 / (2.0 * 640.0);
        var expectedTime = v0 / 640.0;

        t.CheckClose("glide distance = v0^2 / (2a)", glide, expectedGlide, expectedGlide * 0.01, " px");
        t.CheckClose("stop time = v0 / a", stopTime, expectedTime, 0.01, " s");
        t.CheckClose("locked arithmetic: 1236 px", glide, 1236.4, 15.0, " px");
        t.CheckClose("locked arithmetic: 1.97 s", stopTime, 1.966, 0.02, " s");
        t.Check("stopped exactly (velocity == 0, no creep)", w.Cue.Speed == 0.0);
        t.CheckAtLeast("not the prototype's 1167 px exponential glide", Math.Abs(glide - 1167.0), 30.0, " px");

        var linear = true;
        var detail = string.Empty;
        foreach (var (time, speed) in samples)
        {
            var model = v0 - 640.0 * time;
            detail += TestRunner.N(time) + "s:" + TestRunner.N(speed) + " ";
            if (Math.Abs(speed - model) > 1.0) linear = false;
        }

        t.Check("speed falls linearly in time (|v(t) - (v0 - a t)| <= 1 px/s)", linear, detail.Trim());
        var last = w.Cue.History[w.Cue.History.Count - 1];
        t.CheckClose("history ends at the actual stop position", (last.Position - w.Cue.Position).Length, 0.0, 1e-12, " px");
    }

    // ------------------------------------------------------------------- K02
    public static void K02_HistoryIsArcLengthSampled(TestRunner t)
    {
        t.Section("K02 - history is sampled every 2.0 px of arc length, independent of the frame rate");
        var (maxGap, exactShare, count, arc) = HistoryStats(TestSupport.Dt);

        t.CheckAtMost("max gap <= HistoryStepPx", maxGap, 2.0 + 1e-9, " px");
        t.CheckAtLeast("most gaps are exactly HistoryStepPx", exactShare, 0.9);
        t.CheckClose("entry count ~= arc length / 2 px", count, arc / 2.0, arc / 2.0 * 0.05, string.Empty);

        // Same shot, half the step: a per-frame recorder would halve the spacing; arc-length
        // sampling must not.
        var (maxGapHalf, _, countHalf, _) = HistoryStats(TestSupport.Dt * 0.5);
        t.CheckAtMost("spacing is unchanged at dt/2 (not frame coupled)", maxGapHalf, 2.0 + 1e-9, " px");
        t.CheckClose("entry count is unchanged at dt/2", countHalf, count, count * 0.02, string.Empty);
        GD.Print($"        [K02] dt=1/120: entries={count} arc={TestRunner.N(arc)}px maxGap={TestRunner.N(maxGap)} " +
                 $"| dt=1/240: entries={countHalf} maxGap={TestRunner.N(maxGapHalf)}");
    }

    private static (double maxGap, double exactShare, int count, double arc) HistoryStats(double dt)
    {
        var level = TestSupport.Table(configure: c => c.SimulationStep = dt);
        level.AddCue(292, 280);
        level.AddObject(800, 120);
        var w = new World(level);
        w.Shot(0, 170);
        w.Settle(40000);

        var history = w.Cue.History;
        var maxGap = 0.0;
        var exact = 0;
        var total = 0;
        var arc = 0.0;
        for (var i = 1; i < history.Count; i++)
        {
            var gap = (history[i].Position - history[i - 1].Position).Length;
            arc += gap;
            total++;
            if (gap > maxGap) maxGap = gap;
            if (Math.Abs(gap - 2.0) < 1e-9) exact++;
        }

        return (maxGap, total == 0 ? 0.0 : (double)exact / total, history.Count, arc);
    }

    // ------------------------------------------------------------------- K03
    public static void K03_DeterminismInProcess(TestRunner t)
    {
        t.Section("K03 - determinism: same level + same inputs => identical state hash");
        var first = CanonicalRun();
        var second = CanonicalRun();
        t.Check("two runs in one process hash identically", first.Hash == second.Hash, first.Hash);
        t.Check("the canonical run really did something", first.Ticks > 500 && first.History > 100,
            $"ticks={first.Ticks} history={first.History}");
    }

    /// <summary>The canonical scenario both determinism gates (in-process and cross-process) use.</summary>
    public static (string Hash, int Ticks, int History, int Rewinds, int Pocketed, double Time) CanonicalRun()
    {
        var w = TestSupport.Rack();
        w.Shot(0, 170); // straight into the rack: collisions and pockets are part of the state
        w.RunSteps(300);
        w.StartRewind();
        w.RunSteps(60);
        w.EndRewind();
        w.RunSteps(200);
        w.StartRewind();
        w.RunSteps(90);
        w.EndRewind();
        w.RunSteps(400);
        return (StateHash.Hex(w), w.Tick, w.Cue.History.Count, w.Stats.Rewinds, w.PocketedObjectCount, w.Time);
    }

    // ------------------------------------------------------------------- K04
    public static void K04_StateIsSerializable(TestRunner t)
    {
        t.Section("K04 - whole-run state (including the input sequence) survives a JSON round trip");
        var w = TestSupport.Rack();
        w.Shot(0, 170);
        w.RunSteps(200);
        w.StartRewind();
        w.RunSteps(80);

        // Deliberately captured mid-rewind: the frozen snapshot is part of the state.
        var json = StateCodec.Encode(w);
        var restored = new World(LevelLoader.LoadById(LevelIds.H5Rack));
        StateCodec.DecodeInto(json, restored);

        t.Check("hash is identical after encode/decode", StateHash.Hex(restored) == StateHash.Hex(w),
            StateHash.Hex(w));
        t.Check("ball count preserved", restored.Balls.Length == w.Balls.Length);
        t.Check("history preserved", restored.Cue.History.Count == w.Cue.History.Count,
            restored.Cue.History.Count + " vs " + w.Cue.History.Count);
        t.Check("rewind snapshot preserved", restored.Rewinding == w.Rewinding && restored.RewindTrack != null
            && restored.RewindTrack.Length == w.RewindTrack.Length);
        t.Check("input sequence preserved", restored.Inputs.Count == w.Inputs.Count,
            restored.Inputs.Count + " vs " + w.Inputs.Count);
        t.CheckAtLeast("json is a real document", json.Length, 200, " bytes");
        t.Check("json declares the level", json.Contains("\"levelId\":\"h5_rack\""));

        for (var i = 0; i < 60; i++)
        {
            w.Step();
            restored.Step();
        }

        t.Check("both worlds keep evolving identically", StateHash.Hex(restored) == StateHash.Hex(w));
    }

    // ------------------------------------------------------------------- K05
    public static void K05_SubStepChordBudget(TestRunner t)
    {
        t.Section("K05 - sub-step budget is derived from the collision chord, and it holds");
        var cfg = new PhysicsConfig();
        var sumRadius = 2.0 * cfg.BallRadius;
        var budget = cfg.SubStepRelativeTravelBudget;
        var maxB = TestSupport.MaxDetectableImpactParameter(budget, sumRadius);
        var worstMeasuredB = 23.5;

        t.CheckAtLeast("graded detection covers the worst measured graze (b = 23.5)", maxB, worstMeasuredB,
            "bMax=" + TestRunner.N(maxB));
        t.CheckClose("guaranteed chord = 2 x budget", 2.0 * budget, 2.0, 1e-12, " px");

        var needed = TestSupport.RequiredIntervalsForChord(1900.0, TestSupport.Dt, worstMeasuredB, sumRadius);
        t.CheckAtLeast("chord logic: a 1900 px/s graze needs more than one interval", needed, 1, " intervals=" + needed);
        var needed3000 = TestSupport.RequiredIntervalsForChord(3000.0, TestSupport.Dt, worstMeasuredB, sumRadius);
        t.CheckAtLeast("chord logic: at 3000 px/s it needs more still", needed3000, needed, " intervals=" + needed3000);

        var level = TestSupport.BigTable(speedCap: 6000.0);
        level.AddCue(300, 2000);
        level.AddObject(1500, 2023.5);
        var w = new World(level);
        w.Cue.Velocity = new Vec2(3000.0, 0.0);
        TestSupport.Watch(w, 400);

        t.CheckAtMost("observed interval travel <= configured budget", w.MaxObservedIntervalTravel,
            budget * (1.0 + 1e-6), " px");
        t.CheckAtMost("observed relative interval travel <= configured budget",
            w.MaxObservedIntervalRelativeTravel, budget * (1.0 + 1e-6), " px");
        t.CheckAtLeast("integrator used at least the chord-derived interval count", w.MaxObservedIntervals, needed3000,
            " intervals=" + w.MaxObservedIntervals);
        t.Check("sub-step assertion never failed", w.AssertionFailure == null, w.AssertionFailure ?? "clean");
        t.Check("interval/event guard never tripped", w.Stats.StepGuardHits == 0,
            "guards=" + w.Stats.StepGuardHits + " diag=" + (w.GuardDiagnostic ?? "none"));
        GD.Print($"        [K05] budget={TestRunner.N(budget)}px bMax={TestRunner.N(maxB)}px " +
                 $"needed@1900={needed} needed@3000={needed3000} observedIntervals={w.MaxObservedIntervals} " +
                 $"observedTravel={w.MaxObservedIntervalTravel} guards={w.Stats.StepGuardHits}");
    }

    // ------------------------------------------------------------------- K06
    public static void K06_NoTunnellingHeadOn(TestRunner t)
    {
        t.Section("K06 - no tunnelling at >= 3000 px/s (head-on)");
        foreach (var speed in new[] { 3000.0, 5000.0 })
        {
            var level = TestSupport.BigTable(speedCap: 6000.0);
            level.AddCue(300, 2000);
            level.AddObject(1500, 2000);
            var w = new World(level);
            w.Cue.Velocity = new Vec2(speed, 0.0);

            var minDistance = double.MaxValue;
            var contactTick = -1;
            for (var i = 0; i < 3000 && contactTick < 0; i++)
            {
                w.Step();
                var d = (w.Balls[1].Position - w.Balls[0].Position).Length;
                if (d < minDistance) minDistance = d;
                if (w.Stats.BallBallContacts > 0) contactTick = i;
            }

            t.Check($"head-on at {TestRunner.N(speed)} px/s registers a contact", contactTick >= 0,
                "tick=" + contactTick);
            t.CheckAtLeast($"object ball receives a near-elastic share at {TestRunner.N(speed)} px/s",
                w.Balls[1].Speed, 0.85 * speed, " px/s");
            t.CheckAtLeast($"no interpenetration at {TestRunner.N(speed)} px/s", minDistance, 24.4 - 1e-6, " px");
            t.CheckAtMost($"sub-step budget respected at {TestRunner.N(speed)} px/s",
                w.MaxObservedIntervalRelativeTravel, 1.0 + 1e-6, " px");
            GD.Print($"        [K06] {TestRunner.N(speed)} px/s: relTravel={TestRunner.N(w.MaxObservedIntervalRelativeTravel)} " +
                     $"pair={w.MaxIntervalRelativePairA}-{w.MaxIntervalRelativePairB} tick={w.MaxIntervalRelativeTick} " +
                     $"h={TestRunner.N(w.MaxIntervalRelativeDuration)} rewind={w.MaxIntervalRelativeWasRewind} " +
                     $"guards={w.Stats.StepGuardHits} intervals={w.MaxObservedIntervals}");
            GD.Print("        [K06-BUDGET] " + (w.BudgetDiagnostic ?? "none"));
            GD.Print("        [K06-GUARD] " + (w.GuardDiagnostic ?? "none"));
            t.Check("no NaN at that speed", !w.HasNonFiniteState());
        }
    }

    // ------------------------------------------------------------------- K07
    public static void K07_NoTunnellingGrazing(TestRunner t)
    {
        t.Section("K07 - no tunnelling at grazing incidence (impact parameter b = 23.5, chord 13.13 px)");
        var sumRadius = 24.4;
        var chord = 2.0 * Math.Sqrt(sumRadius * sumRadius - 23.5 * 23.5);
        t.CheckClose("chord for b = 23.5", chord, 13.13, 0.01, " px");

        foreach (var speed in new[] { 1900.0, 2500.0, 3000.0 })
        {
            var hits = 0;
            var grazeImpulse = 0.0;
            var deflected = 0;
            const int phases = 16;
            for (var k = 0; k < phases; k++)
            {
                var level = TestSupport.BigTable(speedCap: 6000.0);
                level.AddCue(300, 2000);
                level.AddObject(1500 + k * (chord / phases), 2000 + 23.5);
                var w = new World(level);
                w.Cue.Velocity = new Vec2(speed, 0.0);
                for (var i = 0; i < 4000 && w.Stats.BallBallContacts == 0; i++) w.Step();
                if (w.Stats.BallBallContacts > 0) hits++;
                if (w.Balls[1].Speed > 0.0) grazeImpulse = w.Balls[1].Speed;
                if (Math.Abs(w.Cue.Velocity.Y) > 1e-6) deflected++;
            }

            t.Check($"all {phases} phases detected at {TestRunner.N(speed)} px/s", hits == phases, hits + "/" + phases);
            t.Check($"cue is deflected in every phase at {TestRunner.N(speed)} px/s", deflected == phases,
                deflected + "/" + phases);
            t.CheckLess($"graze gives far less than head-on at {TestRunner.N(speed)} px/s",
                grazeImpulse, 0.5 * speed);
        }
    }

    // ------------------------------------------------------------------- K08
    public static void K08_PocketCaptureAtHighSpeed(TestRunner t)
    {
        t.Section("K08 - a pocket is never skipped, even at 4000 px/s");
        foreach (var speed in new[] { 2000.0, 4000.0 })
        {
            var level = TestSupport.Table(configure: c => c.SpeedCap = 6000.0);
            level.AddCue(520, 300);
            level.AddObject(150, 120);
            var w = new World(level);
            w.Cue.Velocity = new Vec2(0.0, speed);
            TestSupport.RunUntil(w, () => w.Cue.Pocketed, 1000);
            t.Check($"cue is captured by the bottom-middle pocket at {TestRunner.N(speed)} px/s", w.Cue.Pocketed);
            t.Check("no rail transform needed for the capture", w.Stats.RailTransforms == 0,
                "transforms=" + w.Stats.RailTransforms);
        }
    }

    // ------------------------------------------------------------------- K09
    public static void K09_NoDivergence72DirectionSweep(TestRunner t)
    {
        t.Section("K09 - 72-direction full-power sweep: no divergence, no NaN, no escape");
        var cfg = new PhysicsConfig();
        var cap = cfg.SpeedCap;
        var fixedPoint = cfg.RailFixedPoint;
        var maxSpeed = 0.0;
        var maxCueSpeed = 0.0;
        var nonFinite = 0;
        var outOfBounds = 0;
        var unsettled = 0;
        var boundViolations = 0;
        var assertionFailures = 0;
        var started = Time.GetTicksMsec();

        for (var k = 0; k < 72; k++)
        {
            var angle = k * 5.0;
            var w = TestSupport.Rack();
            w.Shot(angle, 170);
            var watch = TestSupport.Watch(w, 7200); // 60 s cap
            if (!w.AtRest() && !w.Frozen) unsettled++;
            if (watch.NonFinite || w.HasNonFiniteState()) nonFinite++;
            if (watch.OutOfBounds > 0) outOfBounds++;
            if (watch.MaxSpeed > maxSpeed) maxSpeed = watch.MaxSpeed;
            if (watch.MaxCueSpeed > maxCueSpeed) maxCueSpeed = watch.MaxCueSpeed;
            if (watch.MaxSpeed > cap * (1.0 + 1e-9)) boundViolations++;
            if (watch.AssertionFailure != null || w.AssertionFailure != null) assertionFailures++;
        }

        var elapsed = Time.GetTicksMsec() - started;
        t.Check("no ball ever becomes Infinity/NaN", nonFinite == 0, "runs=" + nonFinite);
        t.Check("no ball leaves the rail box", outOfBounds == 0, "runs=" + outOfBounds);
        t.Check("no sub-step budget assertion failed", assertionFailures == 0, "runs=" + assertionFailures);
        t.Check("no ball exceeds SpeedCap", boundViolations == 0, "violations=" + boundViolations);
        t.CheckAtMost("peak speed over the sweep stays under the cap", maxSpeed, cap, " px/s");
        t.CheckAtMost("cue peak speed stays at or below the rail fixed point", maxCueSpeed, fixedPoint + 1e-6, " px/s");
        t.Check("every direction settles inside 60 s", unsettled == 0, "unsettled=" + unsettled);
        GD.Print($"        [K09] 72 runs in {elapsed} ms; peakSpeed={TestRunner.N(maxSpeed)} " +
                 $"cuePeak={TestRunner.N(maxCueSpeed)} cap={cap} railFixedPoint={fixedPoint}");
    }

    // ------------------------------------------------------------------- K10
    public static void K10_RailModelIsBounded(TestRunner t)
    {
        t.Section("K10 - the rail map is bounded: fixed point 1500 px/s, below the 2000 px/s cap");
        var cfg = new PhysicsConfig();
        var fixedPoint = cfg.RailFixedPoint;
        t.CheckClose("fixed point = gain / (1 - restitution)", fixedPoint, 1500.0, 1e-9, " px/s");
        t.Check("fixed point sits below SpeedCap", fixedPoint < cfg.SpeedCap,
            TestRunner.N(fixedPoint) + " < " + TestRunner.N(cfg.SpeedCap));

        var converged = true;
        var bounded = true;
        var detail = string.Empty;
        foreach (var start in new[] { 0.0, 100.0, 500.0, 1258.0, 1999.0, 3000.0 })
        {
            var s = start;
            for (var i = 0; i < 600; i++)
            {
                s = Math.Min(s * cfg.WallRestitution + cfg.CueWallGain, cfg.SpeedCap);
                if (s > Math.Max(start, fixedPoint) + 1e-9) bounded = false;
                if (s > cfg.SpeedCap + 1e-9) bounded = false;
            }

            if (Math.Abs(s - fixedPoint) > 1e-6) converged = false;
            detail += TestRunner.N(start) + "->" + TestRunner.N(s) + " ";
        }

        t.Check("every start speed converges to the fixed point", converged, detail.Trim());
        t.Check("the rail map never exceeds max(start, fixed point)", bounded);

        // Live check with friction switched off: repeated real bounces must settle at the fixed
        // point instead of running away.
        var level = TestSupport.Table(configure: c => c.FrictionDecel = 0.0);
        level.AddCue(292, 280);
        level.AddObject(800, 120);
        var w = new World(level);
        w.Shot(0, 170);
        var maxCue = 0.0;
        for (var i = 0; i < 12000; i++)
        {
            w.Step();
            if (w.Cue.Speed > maxCue) maxCue = w.Cue.Speed;
        }

        t.CheckAtMost("12000 frictionless steps never exceed the fixed point", maxCue, fixedPoint + 1e-6, " px/s");
        t.CheckAtLeast("the cue actually kept bouncing (energy came from rails)", w.Stats.RailTransforms, 20,
            "transforms=" + w.Stats.RailTransforms);
        GD.Print($"        [K10] live frictionless 100 s: transforms={w.Stats.RailTransforms} " +
                 $"maxCueSpeed={TestRunner.N(maxCue)} finalSpeed={TestRunner.N(w.Cue.Speed)}");
    }

    // ------------------------------------------------------------------- K11
    public static void K11_RewindImpulseModel(TestRunner t)
    {
        t.Section("K11 - rewind impulse: no amplification, geometry respected");
        t.CheckAtMost("locked coefficient is <= 1", new PhysicsConfig().RewindImpulseK, 1.0, string.Empty);

        var rejected = false;
        try
        {
            var bad = new PhysicsConfig { RewindImpulseK = 1.6 };
            bad.Validate();
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        t.Check("a coefficient above 1 is rejected at construction", rejected);

        var (headOn, headOnCueSpeed) = RewindImpulseAt(0.0);
        var (graze, grazeCueSpeed) = RewindImpulseAt(23.0);
        t.CheckAtLeast("head-on contact produces a real push", headOn, 1.0, " px/s");
        t.CheckAtLeast("graze contact produces a push too", graze, 0.001, " px/s");
        t.CheckLess("a graze imparts strictly less than a head-on hit", graze, headOn * 0.5);
        t.CheckAtMost("no amplification: object speed <= K * cue relative speed",
            headOn, headOnCueSpeed * 1.0 + 1e-9, " px/s");
        t.CheckAtMost("no amplification in the graze case", graze, grazeCueSpeed * 1.0 + 1e-9, " px/s");
        GD.Print($"        [K11] head-on: cue={TestRunner.N(headOnCueSpeed)} -> ball={TestRunner.N(headOn)} px/s " +
                 $"| b=23: cue={TestRunner.N(grazeCueSpeed)} -> ball={TestRunner.N(graze)} px/s");
    }

    private static (double objectSpeed, double cueSpeed) RewindImpulseAt(double offset)
    {
        var level = TestSupport.Table();
        level.AddCue(292, 280);
        level.AddObject(800, 120);
        var w = new World(level);
        w.Shot(0, 170);
        w.RunSteps(40);

        var state = w.CaptureState();
        var entry = state.Balls[0].History[20];
        state.Balls[1].X = entry.Position.X;
        state.Balls[1].Y = entry.Position.Y + offset;
        state.Balls[1].Vx = 0.0;
        state.Balls[1].Vy = 0.0;
        w.RestoreState(state);
        w.StartRewind();

        for (var i = 0; i < 600 && w.Stats.RewindContacts == 0; i++) w.Step();
        return (w.Balls[1].Speed, w.Cue.Speed);
    }

    // ------------------------------------------------------------------- K12
    public static void K12_LevelDataDriven(TestRunner t)
    {
        t.Section("K12 - levels are data driven (positions, pockets, budgets, physics overrides)");
        var h5 = LevelLoader.LoadById(LevelIds.H5Rack);
        t.Check("h5_rack declares 11 balls", h5.Balls.Count == 11, "n=" + h5.Balls.Count);
        t.Check("ball 0 is the cue", h5.Balls[0].Type == BallType.Cue);
        t.Check("h5_rack declares 6 pockets", h5.Pockets.Count == 6, "n=" + h5.Pockets.Count);
        var pocketRadiusOk = true;
        foreach (var p in h5.Pockets)
        {
            if (Math.Abs(p.Radius - 24.0) > 1e-12) pocketRadiusOk = false;
        }

        t.Check("all pocket radii are 24 px", pocketRadiusOk);
        t.CheckClose("cue spawn x", h5.Balls[0].X, 292.0, 0.0, " px");
        t.CheckClose("cue spawn y", h5.Balls[0].Y, 280.0, 0.0, " px");

        var minNeighbour = double.MaxValue;
        for (var i = 1; i < h5.Balls.Count; i++)
        {
            for (var j = i + 1; j < h5.Balls.Count; j++)
            {
                var d = Math.Sqrt(Math.Pow(h5.Balls[i].X - h5.Balls[j].X, 2) + Math.Pow(h5.Balls[i].Y - h5.Balls[j].Y, 2));
                if (d < minNeighbour) minNeighbour = d;
            }
        }

        t.CheckClose("prototype rack geometry reproduced (nearest neighbour 25.6954 px)", minNeighbour, 25.6954, 1e-3, " px");

        var corridor = LevelLoader.LoadById(LevelIds.Corridor);
        // corridor.json is the SHIPPED level and is now the promoted, tolerance-verified L1 puzzle:
        // 1 cue + 2 object balls. Assert the SHAPE, not a magic total, so promoting a different
        // verified level does not require editing this test.
        //
        // Counted with explicit loops rather than LINQ: this file is deliberately dependency-light
        // (no `using System.Linq`), and adding one for four counts is not worth it.
        var cueCount = 0;
        var objectCount = 0;
        var idsSeen = new HashSet<int>();
        var idsUnique = true;
        foreach (var b in corridor.Balls)
        {
            if (b.Type == BallType.Cue) cueCount++;
            if (b.Type == BallType.Object) objectCount++;
            if (!idsSeen.Add(b.Id)) idsUnique = false;
        }

        t.Check("corridor has exactly one cue ball", cueCount == 1, "cues=" + cueCount);
        t.CheckAtLeast("corridor has at least one object ball", objectCount, 1);
        t.Check("corridor ball ids are unique", idsUnique);
        // Budget is level DATA, so assert what the data-driven contract actually promises (a level
        // carries a real budget; h5_rack is unlimited) instead of hardcoding the retired fixture's
        // 8 segments / 2400 px - corridor.json is now the promoted, tolerance-verified L1 puzzle
        // (1 segment, no distance cap), and a hardcoded expectation here would make the promotion
        // look like a regression.
        t.Check("budgets come from the level file",
            corridor.Budget.MaxSegments > 0 && corridor.Budget.MaxDistance >= 0.0);
        t.Check("h5_rack has an unlimited rewind budget", h5.Budget.MaxSegments == 0 && h5.Budget.MaxDistance == 0.0);
        t.Check("the two levels are actually different", corridor.Balls[1].X != h5.Balls[1].X);

        var ids = LevelLoader.ListLevelIds();
        t.CheckAtLeast("levels are discovered from disk", ids.Length, 3, string.Join(",", ids));

        var unknownKeyRejected = false;
        try
        {
            LevelData.ApplyPhysicsOverrides(new PhysicsConfig(), Json.Parse("{\"nope\":1}"));
        }
        catch (JsonException)
        {
            unknownKeyRejected = true;
        }

        t.Check("an unknown physics override is rejected (no silent divergence)", unknownKeyRejected);

        var tuned = LevelData.FromJsonText(
            "{\"id\":\"tuned\",\"balls\":[{\"id\":0,\"type\":\"cue\",\"x\":10,\"y\":10}]," +
            "\"physics\":{\"speedCap\":6000,\"frictionDecel\":0,\"historyStepPx\":1.5}}");
        t.CheckClose("per-level speedCap override", tuned.Config.SpeedCap, 6000.0, 0.0, " px/s");
        t.CheckClose("per-level friction override", tuned.Config.FrictionDecel, 0.0, 0.0, " px/s");
        t.CheckClose("per-level history override", tuned.Config.HistoryStepPx, 1.5, 0.0, " px");

        // Special ball types are RESERVED AS DATA in M1 (the solver's bomb-ball lever): they load
        // and behave as ordinary object balls, and the kernel must not pretend otherwise.
        var reserved = LevelData.FromJsonText(
            "{\"id\":\"reserved\",\"balls\":[{\"id\":0,\"type\":\"cue\",\"x\":10,\"y\":10}," +
            "{\"id\":1,\"type\":\"bomb\",\"x\":100,\"y\":10}]}");
        t.Check("bomb type is preserved as declared data", reserved.Balls[1].DeclaredType == "bomb");
        // The reserved vocabulary is no longer inert: each name now maps to a real BallType with its
        // own tunables in PhysicsConfig.Types. A bomb still plays as an ordinary object ball
        // physically (its table entry inherits the object defaults), but it IS a distinct type,
        // which is what lets a level carry it and a designer tune it.
        t.Check("bomb loads as its own type, not a plain object", reserved.Balls[1].Type == BallType.Bomb);
        t.Check("reserved types are visible to tools", reserved.Balls[1].IsReservedSpecialType);

        var unknownTypeRejected = false;
        try
        {
            LevelData.FromJsonText("{\"id\":\"x\",\"balls\":[{\"id\":0,\"type\":\"teleporter\",\"x\":1,\"y\":1}]}");
        }
        catch (JsonException)
        {
            unknownTypeRejected = true;
        }

        t.Check("an unknown ball type is rejected", unknownTypeRejected);
    }

    // ------------------------------------------------------------------- K13
    public static void K13_RewindBudgetEnforced(TestRunner t)
    {
        t.Section("K13 - the rewind budget from level data is enforced");

        // Distance budget only.
        var distanceLevel = TestSupport.Table();
        distanceLevel.AddCue(292, 280);
        distanceLevel.AddObject(800, 120);
        distanceLevel.Budget = new RewindBudget { MaxSegments = 0, MaxDistance = 30.0 };
        var wd = new World(distanceLevel);
        wd.Shot(0, 170);
        wd.RunSteps(60);
        t.Check("distance budget: first rewind allowed", wd.StartRewind());
        wd.RunSteps(100);
        t.Check("distance budget auto-ends the rewind", !wd.Rewinding);
        t.CheckAtMost("distance used respects the budget", wd.RewindDistanceUsed, 30.0 + 1e-9, " px");
        t.Check("distance budget refuses a further rewind", !wd.StartRewind());

        // Segment budget only.
        var segmentLevel = TestSupport.Table();
        segmentLevel.AddCue(292, 280);
        segmentLevel.AddObject(800, 120);
        segmentLevel.Budget = new RewindBudget { MaxSegments = 2, MaxDistance = 0.0 };
        var w = new World(segmentLevel);
        w.Shot(0, 170);
        w.RunSteps(60);
        t.Check("segment budget: rewind 1 allowed", w.StartRewind());
        w.RunSteps(20);
        w.EndRewind();
        t.Check("segment budget: rewind 2 allowed", w.StartRewind());
        w.RunSteps(20);
        w.EndRewind();
        t.Check("segment budget: rewind 3 refused", !w.StartRewind());
        t.Check("segment counter is 2", w.RewindSegmentsUsed == 2, "segments=" + w.RewindSegmentsUsed);
        t.CheckAtLeast("rewind distance accumulated", w.RewindDistanceUsed, 70.0, " px");
    }

    // ------------------------------------------------------------------- K14
    public static void K14_ReplayFromInputSequence(TestRunner t)
    {
        t.Section("K14 - a run can be replayed from its serialized input sequence");
        var w = TestSupport.Rack();
        w.Shot(0, 170);
        w.RunSteps(200);
        w.StartRewind();
        w.RunSteps(40);
        w.EndRewind();
        w.RunSteps(150);
        w.StartRewind();
        w.RunSteps(70);
        w.EndRewind();
        w.RunSteps(100);

        var ticks = w.Tick;
        var events = new List<InputCommand>();
        foreach (var e in w.Inputs) events.Add(e.Clone());
        var expected = StateHash.Hex(w);

        var json = StateCodec.Encode(w);
        var decoded = StateCodec.Decode(json, LevelLoader.LoadById(LevelIds.H5Rack));
        var fromJson = new List<InputCommand>(decoded.Inputs);

        var fresh = TestSupport.Rack();
        TestSupport.Replay(fresh, fromJson, ticks);

        t.Check("input log survives serialization", fromJson.Count == events.Count,
            fromJson.Count + " vs " + events.Count);
        t.Check("replay from the serialized input sequence reproduces the run", StateHash.Hex(fresh) == expected,
            expected);
    }

    // ------------------------------------------------------------------- K15
    public static void K15_PocketRule(TestRunner t)
    {
        t.Section("K15 - pocket capture requires the velocity to point into the pocket");
        var inside = new Vec2(77.35, 77.35); // 17.5 px from the top-left pocket centre (r = 24)

        var outward = TestSupport.Table(configure: c => c.FrictionDecel = 0.0);
        outward.AddCue(inside.X, inside.Y);
        outward.AddObject(800, 400);
        var wo = new World(outward);
        wo.Cue.Velocity = new Vec2(212.0, 212.0); // away from the corner
        wo.Step();
        t.Check("inside the circle but moving outward is NOT captured", !wo.Cue.Pocketed);

        var inward = TestSupport.Table(configure: c => c.FrictionDecel = 0.0);
        inward.AddCue(inside.X, inside.Y);
        inward.AddObject(800, 400);
        var wi = new World(inward);
        wi.Cue.Velocity = new Vec2(-212.0, -212.0); // into the pocket
        wi.Step();
        t.Check("inside the circle and moving inward IS captured", wi.Cue.Pocketed);
    }

    // ------------------------------------------------------------------- K16
    public static void K16_JsonCodec(TestRunner t)
    {
        t.Section("K16 - the hand-written JSON codec round-trips exactly");
        var root = JsonValue.Object();
        root.Set("i", JsonValue.Of(42.0));
        root.Set("d", JsonValue.Of(0.1 + 0.2));
        root.Set("s", JsonValue.Of("quote\" backslash\\ newline\n"));
        root.Set("b", JsonValue.Of(true));
        root.Set("n", JsonValue.Null());
        var arr = JsonValue.Array();
        arr.Add(JsonValue.Of(-1.0e-7));
        arr.Add(JsonValue.Of(1.0e21));
        root.Set("a", arr);

        var text = Json.Write(root);
        var back = Json.Parse(text);
        t.CheckClose("double round trip is bit exact", back.Num("d", 0.0), 0.1 + 0.2, 0.0, string.Empty);
        t.CheckClose("negative exponent", back.Arr("a").At(0).AsDouble(), -1.0e-7, 0.0, string.Empty);
        t.CheckClose("large exponent", back.Arr("a").At(1).AsDouble(), 1.0e21, 0.0, string.Empty);
        t.Check("escaped string round trip", back.Text("s", string.Empty) == "quote\" backslash\\ newline\n");
        t.Check("null member round trip", back.Get("n").Kind == JsonKind.Null);

        var level = LevelLoader.LoadById(LevelIds.H5Rack);
        var levelAgain = LevelData.FromJsonText(level.ToJsonText());
        t.Check("level round trip preserves balls", levelAgain.Balls.Count == level.Balls.Count);
        t.CheckClose("level round trip preserves coordinates", levelAgain.Balls[5].X, level.Balls[5].X, 0.0, " px");

        var threw = false;
        try
        {
            Json.Parse("{\"a\":}");
        }
        catch (JsonException)
        {
            threw = true;
        }

        t.Check("malformed json is rejected", threw);
    }

    // ------------------------------------------------------------------- K17
    public static void K17_RewindCrawlIsDecoupled(TestRunner t)
    {
        t.Section("K17 - rewind crawl: one history entry per step, release velocity from the stored v");
        var cfg = new PhysicsConfig();
        t.CheckClose("crawl rate is the locked 240 px/s", cfg.RewindCrawlPxPerS, 240.0, 0.0, " px/s");
        t.CheckClose("crawl per step is exactly one history entry", cfg.RewindCrawlPxPerStep, cfg.HistoryStepPx,
            1e-12, " px");
        t.Check("crawl and history interval are not decoupled", cfg.CrawlIsOneHistoryEntryPerStep);

        var level = TestSupport.Table();
        level.AddCue(292, 280);
        level.AddObject(800, 120);
        var w = new World(level);
        w.Shot(0, 170);
        w.RunSteps(60);

        w.StartRewind();
        var track = w.RewindTrack;
        var cursorAtPress = w.RewindCursor;
        var exactCrawl = 0.0;
        const int steps = 60;
        for (var i = 0; i < steps; i++)
        {
            w.Step();
            exactCrawl += w.LastRewindCrawlDistance;
        }

        var cursorAfter = w.RewindCursor;
        t.CheckClose($"holding R for {steps} steps rewinds exactly {steps} x 2.0 px", exactCrawl, steps * 2.0,
            1e-9, " px");
        t.CheckAtLeast("cursor advanced by at least one entry per step", cursorAtPress - cursorAfter, steps);
        t.CheckAtMost("cursor did not run ahead of the crawl", cursorAtPress - cursorAfter, steps + 1);

        var released = w.Cue.Velocity;
        var stored = track[w.RewindCursor].Velocity;
        w.EndRewind();

        t.CheckClose("release speed equals the stored speed at that entry", released.Length, stored.Length, 1e-12, " px/s");
        t.Check("release velocity is exactly -(stored v)", w.Cue.Velocity.Equals(-stored));
        t.CheckLess("release speed is far above the crawl rate (decoupled)", 400.0, released.Length);
        GD.Print($"        [K17] crawled={TestRunner.N(exactCrawl)}px over {steps} steps " +
                 $"releaseSpeed={TestRunner.N(released.Length)}px/s crawlRate=240px/s");
    }
}
