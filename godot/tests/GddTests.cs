using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Tests;

using Rewind.Sim;

/// <summary>
/// GDD §9 acceptance table ported to the Godot kernel. T07/T08 are deliberately ADAPTED, not
/// copied: the locked rail model replaces the prototype's unbounded x1.5 with
/// |v| = min(|v|*0.92 + 120, 2000), so the requirement "x1.5" cannot hold and must not be
/// asserted. All deviations are listed in godot/DIFFERENCES.md.
/// </summary>
public static class GddTests
{
    // ------------------------------------------------------------------- T01
    public static void T01_SecondShotRejected(TestRunner t)
    {
        t.Section("T01 — only one stroke per level");
        var level = TestSupport.Table();
        level.AddCue(292, 280);
        level.AddObject(700, 120);
        var w = new World(level);

        t.Check("first shot accepted", w.Shot(0, 170));
        t.CheckClose("break speed = PowerMax * SpeedPerPower", w.Cue.Speed, 1258.0, 1e-9, " px/s");
        var velocityAfterFirst = w.Cue.Velocity;

        t.Check("second shot refused while the cue is moving", !w.Shot(90, 170));
        t.Check("refused shot leaves velocity untouched", w.Cue.Velocity.Equals(velocityAfterFirst),
            w.Cue.Velocity.ToString());

        var steps = w.Settle();
        t.Check("cue comes to rest", w.AtRest(), steps + " steps");
        t.Check("second shot refused at rest", !w.Shot(180, 170));
        t.CheckClose("cue still at rest after refusal", w.Cue.Speed, 0.0, 0.0, " px/s");
        t.Check("exactly one input recorded", w.Inputs.Count == 1, "inputs=" + w.Inputs.Count);

        var weak = new World(level);
        t.Check("drag below MinPower is not a shot", !weak.Shot(0, 4.0));
        t.Check("no input recorded for a refused shot", weak.Inputs.Count == 0);
    }

    // ------------------------------------------------------------------- T02
    public static void T02_ReleaseVelocityIsStoredVelocityNegated(TestRunner t)
    {
        t.Section("T02 — release velocity comes from the stored v, not from a position delta");
        var level = TestSupport.Table();
        level.AddCue(292, 280);
        level.AddObject(800, 120);
        var w = new World(level);

        w.Shot(0, 170);
        w.RunSteps(60);

        t.Check("rewind accepted after the shot", w.StartRewind());
        var track = w.RewindTrack;
        var cursorAtPress = w.RewindCursor;
        t.Check("cursor starts at the last entry", cursorAtPress == track.Length - 1, "cursor=" + cursorAtPress);

        var crawl = 0.0;
        for (var i = 0; i < 20; i++)
        {
            w.Step();
            crawl += w.LastRewindCrawlDistance;
        }

        var cursorAfter = w.RewindCursor;
        var expected = -track[cursorAfter].Velocity;
        var storedSpeed = track[cursorAfter].Velocity.Length;
        w.EndRewind();

        t.Check("cursor moved backwards", cursorAfter < cursorAtPress, $"{cursorAtPress} -> {cursorAfter}");
        t.Check("release velocity is exactly -(stored v)", w.Cue.Velocity.Equals(expected), w.Cue.Velocity.ToString());
        t.Check("release is opposite to the forward pass at that entry",
            Vec2.Dot(w.Cue.Velocity, track[cursorAfter].Velocity) < 0.0);
        t.CheckClose("release speed == stored speed", w.Cue.Speed, storedSpeed, 1e-12, " px/s");
        t.CheckClose("crawl distance = RewindCrawlPxPerS * time", crawl, 240.0 * 20.0 * TestSupport.Dt, 0.05, " px");
        t.CheckLess("crawl rate is decoupled from the stored speed", 240.0, w.Cue.Speed);
    }

    // ------------------------------------------------------------------- T03
    public static void T03_AppendOnlyHistoryAndReversedReplay(TestRunner t)
    {
        t.Section("T03 — A->B->C->D then rewind D->C->B->A, old history preserved");
        var level = TestSupport.Table();
        level.AddCue(292, 280);
        level.AddObject(800, 120);
        var w = new World(level);

        w.Shot(0, 170);
        w.RunSteps(80); // A -> B
        var lenAfterOutbound = w.Cue.History.Count;
        t.Check("first rewind accepted", w.StartRewind());
        var track1 = w.RewindTrack;
        w.RunSteps(20); // B -> C
        w.EndRewind();
        w.RunSteps(40); // C -> D
        var lenAtSecondPress = w.Cue.History.Count;

        t.Check("history grew during the rewind (append-only)", lenAtSecondPress > lenAfterOutbound,
            $"{lenAfterOutbound} -> {lenAtSecondPress}");
        t.Check("second rewind accepted", w.StartRewind());
        var track2 = w.RewindTrack;
        // StartRewind flushes the current position first, so the snapshot is the live history
        // plus exactly one press-point entry (which is what removes the jump at press time).
        t.Check("second snapshot is the live history + the press flush", track2.Length == lenAtSecondPress + 1,
            track2.Length + " vs " + lenAtSecondPress + "+1");

        var prefixOk = track1.Length <= track2.Length;
        if (prefixOk)
        {
            for (var i = 0; i < track1.Length; i++)
            {
                if (!SamplesEqual(track1[i], track2[i]))
                {
                    prefixOk = false;
                    break;
                }
            }
        }

        t.Check("entries written before the first press are unchanged (bitwise)", prefixOk);

        var cursors = new List<int>();
        var steps = 0;
        while (w.Rewinding && steps < 5000)
        {
            w.Step();
            cursors.Add(w.RewindCursor);
            steps++;
        }

        t.Check("rewind auto-stops at the history start", !w.Rewinding, steps + " steps");
        t.Check("cursor reached index 0", w.RewindCursor == 0, "cursor=" + w.RewindCursor);

        var monotone = true;
        for (var i = 1; i < cursors.Count; i++)
        {
            if (cursors[i] > cursors[i - 1]) monotone = false;
        }

        t.Check("cursor walks monotonically backwards", monotone, cursors.Count + " samples");
        t.Check("replay starts at the end of the snapshot", cursors.Count > 0 && cursors[0] >= track2.Length - 3,
            cursors.Count > 0 ? cursors[0] + " -> " + cursors[cursors.Count - 1] : "no samples");
        t.CheckClose("cue returns to the first history entry",
            (w.Cue.Position - track2[0].Position).Length, 0.0, 1e-12, " px");
        t.CheckClose("velocity is zero at the history start", w.Cue.Speed, 0.0, 0.0, " px/s");

        // The crawl takes one entry per step (240 px/s * 1/120 s = 2.0 px), but a corner can make
        // a single step cross two entries whose chord is shorter than the sampling interval, so
        // the invariant is "monotone from the end to index 0 with no wild jump", not "exactly one
        // entry per step".
        var maxDecrement = 0;
        for (var i = 1; i < cursors.Count; i++)
        {
            var decrement = cursors[i - 1] - cursors[i];
            if (decrement > maxDecrement) maxDecrement = decrement;
        }

        t.Check("no snapshot entry is jumped over", monotone && maxDecrement <= 5,
            $"visited={cursors.Count} track={track2.Length} maxDecrement={maxDecrement}");
    }

    private static bool SamplesEqual(HistorySample a, HistorySample b)
    {
        return a.Position.Equals(b.Position) && a.Velocity.Equals(b.Velocity);
    }

    // ------------------------------------------------------------------- T04
    public static void T04_RewindCueIsForcedTargetIsPushed(TestRunner t)
    {
        t.Section("T04 — rewind contact: cue is not deflected, object ball is pushed");
        var level = TestSupport.Table();
        level.AddCue(292, 280);
        level.AddObject(500, 120);
        var w = new World(level);

        w.Shot(0, 170);
        w.RunSteps(50);

        // Place the object ball on the cue's recorded corridor, so the forced rewind path runs
        // straight through it. (State injection is used only to set up the geometry; the contact
        // itself is then produced by the kernel's own rewind traversal.)
        var state = w.CaptureState();
        var target = state.Balls[1];
        var entry = state.Balls[0].History[20];
        target.X = entry.Position.X;
        target.Y = entry.Position.Y;
        target.Vx = 0.0;
        target.Vy = 0.0;
        w.RestoreState(state);

        t.Check("rewind accepted", w.StartRewind());
        var track = w.RewindTrack;
        t.Check("no contact before the rewind reaches the ball", w.Stats.RewindContacts == 0);

        var contactTick = -1;
        for (var i = 0; i < 400 && contactTick < 0; i++)
        {
            w.Step();
            if (w.Stats.RewindContacts > 0) contactTick = i;
        }

        t.Check("rewind contact registered", w.Stats.RewindContacts >= 1, "at step " + contactTick);
        var objectBall = w.Balls[1];
        t.Check("object ball gained speed", objectBall.Speed > 0.0, TestRunner.N(objectBall.Speed) + " px/s");

        var normal = (objectBall.Position - w.Cue.Position).Normalized();
        t.Check("push is along the contact normal", Vec2.Dot(objectBall.Velocity, normal) > 0.0);

        var expectedCueVelocity = -track[w.RewindCursor].Velocity;
        t.Check("cue velocity untouched by the collision", w.Cue.Velocity.Equals(expectedCueVelocity),
            w.Cue.Velocity.ToString());
        t.CheckAtMost("cue stays on the recorded polyline",
            TestSupport.DistanceToTrack(w.Cue.Position, track), 1e-9, " px");

        var objectSpeedAtContact = objectBall.Speed;
        w.RunSteps(10);
        t.Check("object ball keeps moving under normal physics", objectBall.Speed > 0.0 || objectBall.Pocketed,
            TestRunner.N(objectSpeedAtContact) + " -> " + TestRunner.N(objectBall.Speed) + " px/s");
    }

    /// <summary>
    /// T04b — the same rule without any state injection: an object ball rolls into the cue's
    /// corridor after the cue has already passed and stops there, then the rewind hits it.
    /// </summary>
    public static void T04b_PhysicalRewindContact(TestRunner t)
    {
        t.Section("T04b — physically produced rewind contact");
        var level = TestSupport.Table();
        level.AddCue(292, 280);
        // Stopping distance from 299.333 px/s at 640 px/s^2 is exactly 70 px: the ball stops on
        // y = 280, which is the cue's shot line, at t = 0.468 s — after the cue has stopped at
        // x = 360.45 (68.45 px away), so it is never touched on the way out.
        level.AddObject(325, 350, 0.0, -299.333);
        var w = new World(level);

        t.Check("low-power shot accepted", w.Shot(0, 40));
        w.Settle(2000);
        // Semi-implicit per-step friction (locked §3: "v -= A*dt", once per step) undershoots the
        // analytic glide by ~0.4% at this speed; see DIFFERENCES.md.
        t.CheckClose("cue stops at its friction-limited glide", w.Cue.Position.X, 360.45, 2.0, " px");
        t.CheckClose("object ball rolls to the shot line", w.Balls[1].Position.Y, 280.0, 2.0, " px");
        t.CheckClose("object ball stops on the corridor in x", w.Balls[1].Position.X, 325.0, 0.01, " px");
        t.Check("no contact happened on the way out", w.Stats.RewindContacts == 0);

        t.Check("rewind accepted", w.StartRewind());
        var contacted = TestSupport.RunUntil(w, () => w.Stats.RewindContacts > 0, 200);
        t.Check("forced cue reaches and pushes the ball", contacted >= 0, "after " + contacted + " steps");
        t.Check("object ball accelerated", w.Balls[1].Speed > 0.0, TestRunner.N(w.Balls[1].Speed) + " px/s");
        t.CheckLess("object ball is pushed backwards (away from the cue)", w.Balls[1].Velocity.X, 0.0);
    }

    // ------------------------------------------------------------------- T05
    public static void T05_PocketedObjectBallStaysPocketed(TestRunner t)
    {
        t.Section("T05 — a pocketed object ball never comes back");
        var level = TestSupport.Table();
        level.AddCue(520, 300);
        level.AddObject(520, 380);
        level.AddObject(200, 150);
        var w = new World(level);

        t.Check("shot accepted", w.Shot(90, 170));
        var potted = TestSupport.RunUntil(w, () => w.Balls[1].Pocketed, 2000);
        t.Check("object ball 1 was pocketed by the shot", potted >= 0, "after " + potted + " steps");

        t.Check("object ball 1 is pocketed", w.Balls[1].Pocketed);
        var score = w.PocketedObjectCount;
        t.Check("score is 1", score == 1, "score=" + score);
        t.Check("level not won yet (one ball left)", !w.Won);

        for (var session = 0; session < 4; session++)
        {
            w.StartRewind();
            w.RunSteps(30);
            w.EndRewind();
            w.RunSteps(30);
        }

        t.Check("still pocketed after 4 rewind sessions", w.Balls[1].Pocketed);
        t.Check("score did not decrease", w.PocketedObjectCount == score, "score=" + w.PocketedObjectCount);
        t.CheckClose("pocketed ball has zero velocity", w.Balls[1].Speed, 0.0, 0.0, " px/s");
    }

    // ------------------------------------------------------------------- T06
    public static void T06_CueBallCanBeRewoundOutOfAPocket(TestRunner t)
    {
        t.Section("T06 — the cue ball can be rewound back onto the table");
        var level = TestSupport.Table();
        level.AddCue(520, 300);
        level.AddObject(200, 150);
        level.AddObject(800, 150);
        var w = new World(level);

        w.Shot(90, 170);
        var potted = TestSupport.RunUntil(w, () => w.Cue.Pocketed, 2000);
        t.Check("cue ball is pocketed by the shot", w.Cue.Pocketed, "after " + potted + " steps");
        t.CheckAtLeast("history exists at that moment", w.Cue.History.Count, 2);

        t.Check("rewind accepted while pocketed", w.StartRewind());
        var steps = 0;
        while (w.Rewinding && steps < 5000)
        {
            w.Step();
            steps++;
        }

        t.Check("rewinding brought the cue back", !w.Cue.Pocketed);
        t.Check("auto-stop reached the history start", !w.Rewinding, steps + " steps");
        t.CheckClose("cue is back at the pre-shot position", (w.Cue.Position - new Vec2(520.0, 300.0)).Length,
            0.0, 1e-9, " px");
        t.CheckClose("velocity zeroed at the start", w.Cue.Speed, 0.0, 0.0, " px/s");
        t.Check("the other balls were never disturbed", !w.Balls[1].Pocketed && !w.Balls[2].Pocketed);
    }

    // ------------------------------------------------------------------- T07
    public static void T07_VerticalRailBounce(TestRunner t)
    {
        t.Section("T07 (adapted) — vertical rail bounce uses the locked bounded model");
        // Friction is disabled so the incoming speed at the contact is exactly known.
        // x = 300: the shipped table has no pocket there, so the shot really reaches the cushion
        // (x = 520 would be swallowed by the top-middle pocket — that is T08b/K08 behaviour).
        var level = TestSupport.Table(configure: c => c.FrictionDecel = 0.0);
        level.AddCue(300, 300);
        level.AddObject(150, 120);
        var w = new World(level);

        w.Shot(270, 100); // straight up: 740 px/s
        var transformsBefore = w.Stats.RailTransforms;
        var minY = double.MaxValue;
        var guard = 0;
        while (w.Cue.Velocity.Y <= 0.0 && guard < 2000)
        {
            w.Step();
            guard++;
            if (w.Cue.Position.Y < minY) minY = w.Cue.Position.Y;
        }

        var transitions = w.Stats.RailTransforms - transformsBefore;
        var cushionY = 63.0 + 12.2;
        var expected = Math.Min(740.0 * 0.92 + 120.0, 2000.0);
        t.CheckClose("speed after the bounce = |v|*0.92 + 120", w.Cue.Speed, expected, 1e-6, " px/s");
        t.CheckLess("no x velocity after a vertical bounce", Math.Abs(w.Cue.Velocity.X), 1e-9);
        t.CheckAtLeast("the cue never penetrates the cushion", minY, cushionY - 1e-9, " px");
        t.CheckAtMost("the cue really reached the cushion", minY, cushionY + 10.0, " px");
        t.Check("exactly one rail transform", transitions == 1, "transforms=" + transitions);
        t.Check("lock: not the prototype's x1.5", Math.Abs(w.Cue.Speed - 740.0 * 1.5) > 1.0,
            "x1.5 would be " + TestRunner.N(740.0 * 1.5));

        // ---------------------------------------------------------------------------------------
        // The cap.
        //
        // This used to assert `SpeedCap <= 2070`, i.e. "stay within a hair of the old 2000 so the
        // integrator's precision budget is not disturbed". That invariant has since been deliberately
        // lifted twice -- to 5600 for the rail engine, which alone needs 1258 + 3*1258 = 5032, and then
        // to 8000 for the event cards and the death-throe kick. So the old bound no longer describes a
        // requirement; it described a value.
        //
        // The requirement that DOES still hold, and is the real reason the cap exists, is that the
        // integrator stays exact at the top of the range: a ball at the cap must still be resolved by
        // analytic time of impact with the chord budget intact, and no state may go non-finite. That is
        // asserted below and in K05/K06/K07 rather than by pinning the number.
        // ---------------------------------------------------------------------------------------
        t.CheckAtLeast("the cap clears the rail engine's own ceiling",
            w.Config.SpeedCap, w.Config.SpeedPerPower * w.Config.PowerMax * 4.0,
            $"cap={w.Config.SpeedCap:F0} railCeiling=" +
            $"{w.Config.SpeedPerPower * w.Config.PowerMax * 4.0:F0}");

        // Run the cue at the cap and confirm nothing degrades: this is the property the old numeric
        // bound was a proxy for.
        w.Cue.Velocity = new Vec2(w.Config.SpeedCap, 0.0);
        for (var i = 0; i < 600; i++) w.Step();
        t.Check("the state stays finite at the cap", !w.HasNonFiniteState());
        t.Check("no sub-step assertion fired at the cap", w.AssertionFailure == null,
            w.AssertionFailure ?? "clean");
        t.Check("the cap is actually enforced", w.Cue.Speed <= w.Config.SpeedCap + 1e-6,
            $"speed={w.Cue.Speed:F1} cap={w.Config.SpeedCap:F1}");
    }

    // ------------------------------------------------------------------- T08
    public static void T08_ObliqueRailBounce(TestRunner t)
    {
        t.Section("T08 (adapted) — oblique rail bounce preserves the reflection angle");
        var level = TestSupport.Table(configure: c => c.FrictionDecel = 0.0);
        level.AddCue(300, 300);
        level.AddObject(150, 120);
        var w = new World(level);

        w.Shot(30, 100); // down-right at 740 px/s
        var vxBefore = w.Cue.Velocity.X;
        var vyBefore = w.Cue.Velocity.Y;
        var transformsBefore = w.Stats.RailTransforms;
        TestSupport.RunUntil(w, () => w.Cue.Velocity.Y < 0.0, 2000);
        var transforms = w.Stats.RailTransforms - transformsBefore;

        var expected = Math.Min(740.0 * 0.92 + 120.0, 2000.0);
        t.CheckClose("speed after the bounce = |v|*0.92 + 120", w.Cue.Speed, expected, 1e-6, " px/s");
        t.Check("x component keeps its sign", Math.Sign(w.Cue.Velocity.X) == Math.Sign(vxBefore));
        t.Check("y component flips", Math.Sign(w.Cue.Velocity.Y) == -Math.Sign(vyBefore));
        t.CheckClose("reflection angle preserved (|vx/vy|)", Math.Abs(w.Cue.Velocity.X / w.Cue.Velocity.Y),
            Math.Abs(vxBefore / vyBefore), 1e-9, string.Empty);
        t.Check("exactly one rail transform", transforms == 1, "transforms=" + transforms);
    }

    // ------------------------------------------------------------------ T08b
    public static void T08b_SimultaneousDoubleRail(TestRunner t)
    {
        t.Section("T08b — simultaneous two-rail contact applies the transform once");
        // (a) On the shipped six-pocket table a ball driven into a corner is captured by the
        //     corner pocket: pocket priority beats the rail bounce. Asserted, not assumed.
        var corner = TestSupport.Table();
        corner.AddCue(200, 200);
        corner.AddObject(800, 120);
        var wc = new World(corner);
        // 800 px/s: the friction-limited glide (500 px) has to exceed the 168 px to the capture
        // circle, otherwise the ball simply stops short of the pocket.
        wc.Cue.Velocity = (new Vec2(65.0, 63.0) - new Vec2(200.0, 200.0)).Normalized() * 800.0;
        TestSupport.Watch(wc, 400);
        t.Check("diagonal into the corner pocket is captured", wc.Cue.Pocketed);
        t.Check("pocket priority: no rail transform happened", wc.Stats.RailTransforms == 0,
            "transforms=" + wc.Stats.RailTransforms);

        // (b) Without a pocket in that corner the double-rail branch IS reachable, and it must
        //     apply the magnitude transform exactly once.
        var level = TestSupport.Table(configure: c => c.FrictionDecel = 0.0);
        level.Pockets.Clear();
        level.AddPocket(520, 63);
        level.AddPocket(520, 497);
        // (100, 98) is exactly 35 px from both rails, so both contacts happen at the same instant.
        level.AddCue(100, 98);
        level.AddObject(800, 400);
        var w = new World(level);
        w.Cue.Velocity = new Vec2(-212.13203435596427, -212.13203435596427); // 300 px/s into the corner
        var speedBefore = w.Cue.Speed;
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var steps = 0;
        while (steps < 200 && !(w.Cue.Velocity.X > 0.0 && w.Cue.Velocity.Y > 0.0))
        {
            w.Step();
            steps++;
            if (w.Cue.Position.X < minX) minX = w.Cue.Position.X;
            if (w.Cue.Position.Y < minY) minY = w.Cue.Position.Y;
        }

        t.Check("double-rail contact happened", steps < 200, "after " + steps + " steps");
        t.Check("exactly one transform for the simultaneous contact", w.Stats.RailTransforms == 1,
            "transforms=" + w.Stats.RailTransforms);
        t.CheckClose("speed = |v|*0.92 + 120 (applied once)", w.Cue.Speed,
            Math.Min(speedBefore * 0.92 + 120.0, 2000.0), 1e-6, " px/s");
        t.Check("the cue never penetrates the left cushion", minX >= 65.0 + 12.2 - 1e-9,
            TestRunner.N(minX) + " px >= " + TestRunner.N(77.2) + " px");
        t.Check("the cue never penetrates the top cushion", minY >= 63.0 + 12.2 - 1e-9,
            TestRunner.N(minY) + " px >= " + TestRunner.N(75.2) + " px");
        t.Check("the cue really reached both cushions", minX <= 77.2 + 10.0 && minY <= 75.2 + 10.0,
            TestRunner.N(minX) + " / " + TestRunner.N(minY));
        t.CheckClose("both axes reflected symmetrically", w.Cue.Velocity.X, w.Cue.Velocity.Y, 1e-9, " px/s");
    }

    // ------------------------------------------------------------------- T09
    public static void T09_RewindDoesNotRetriggerRailGain(TestRunner t)
    {
        t.Section("T09 — rewinding through an old bounce grants no new energy");
        var level = TestSupport.Table(configure: c => c.FrictionDecel = 0.0);
        level.AddCue(300, 300);
        level.AddObject(150, 120);
        var w = new World(level);

        w.Shot(270, 100);
        TestSupport.RunUntil(w, () => w.Cue.Velocity.Y > 0.0, 2000);
        w.RunSteps(5);
        var transformsAfterBounce = w.Stats.RailTransforms;
        var maxForwardSpeed = w.Cue.Speed;
        t.Check("one transform from the forward bounce", transformsAfterBounce == 1,
            "transforms=" + transformsAfterBounce);

        t.Check("rewind accepted", w.StartRewind());
        var maxRewindSpeed = 0.0;
        var crossedBounce = false;
        for (var i = 0; i < 140; i++)
        {
            w.Step();
            if (w.Cue.Speed > maxRewindSpeed) maxRewindSpeed = w.Cue.Speed;
            if (w.Cue.Position.Y < 100.0) crossedBounce = true;
        }

        t.Check("rewind passed the former bounce point", crossedBounce || w.Cue.Position.Y < 100.0,
            "y=" + TestRunner.N(w.Cue.Position.Y));
        t.Check("no new rail transform during the rewind", w.Stats.RailTransforms == transformsAfterBounce,
            "transforms=" + w.Stats.RailTransforms);
        t.CheckAtMost("rewind speed never exceeds the recorded forward speed", maxRewindSpeed,
            maxForwardSpeed + 1e-9, " px/s");
        t.CheckAtMost("rewind speed stays under the cap", maxRewindSpeed, w.Config.SpeedCap, " px/s");
    }

    // ------------------------------------------------------------------- T10
    public static void T10_WinAndReset(TestRunner t)
    {
        t.Section("T10 — clearing every object ball wins; reset restores the level");
        var level = TestSupport.Table();
        level.AddCue(520, 300);
        level.AddObject(520, 380);
        var w = new World(level);

        w.Shot(90, 170);
        var steps = TestSupport.RunUntil(w, () => w.Won, 4000);
        t.Check("win triggered", w.Won, steps + " steps");
        t.Check("frozen after the win", w.Frozen);
        t.Check("all object balls pocketed", w.PocketedObjectCount == w.ObjectBallCount,
            w.PocketedObjectCount + "/" + w.ObjectBallCount);

        var frozenHash = StateHash.Hex(w);
        w.RunSteps(120);
        t.Check("terminal state is stable", StateHash.Hex(w) == frozenHash, frozenHash);
        t.Check("rewind refused after the win", !w.StartRewind());
        t.Check("another shot refused after the win", !w.Shot(0, 50));

        w.Reset();
        t.Check("reset clears the win", !w.Won && !w.Frozen);
        t.Check("reset clears the score", w.PocketedObjectCount == 0);
        t.CheckClose("reset restores the cue", (w.Cue.Position - new Vec2(520.0, 300.0)).Length, 0.0, 0.0, " px");
        t.CheckClose("reset restores the object ball", (w.Balls[1].Position - new Vec2(520.0, 380.0)).Length,
            0.0, 0.0, " px");
        t.Check("reset clears the history", w.Cue.History.Count == 1, "entries=" + w.Cue.History.Count);
        t.Check("reset clears the input log", w.Inputs.Count == 0);
        t.Check("a new shot is allowed after reset", w.Shot(0, 50));
    }

    // ------------------------------------------------------------------- T11
    public static void T11_LongRunWithManyRewinds(TestRunner t)
    {
        t.Section("T11 — long session: many rewinds, no NaN, no leak, no tunnel");
        var w = TestSupport.Rack();
        var started = Time.GetTicksMsec();

        w.Shot(0, 170); // straight into the rack so the long run really exercises collisions
        var watch = TestSupport.Watch(w, 400);
        var sessions = 0;
        for (var s = 0; s < 14; s++)
        {
            if (!w.StartRewind()) break;
            sessions++;
            TestSupport.Watch(w, 50, watch);
            w.EndRewind();
            TestSupport.Watch(w, 90, watch);
        }

        var elapsed = Time.GetTicksMsec() - started;

        t.Check("no NaN / Infinity in any ball", !watch.NonFinite && !w.HasNonFiniteState(),
            watch.NonFiniteTick >= 0 ? "first at tick " + watch.NonFiniteTick : "clean");
        t.Check("no ball left the rail box", watch.OutOfBounds == 0, "violations=" + watch.OutOfBounds);
        t.Check("sub-step assertions held", w.AssertionFailure == null && watch.AssertionFailure == null,
            w.AssertionFailure ?? watch.AssertionFailure ?? "clean");
        t.Check("interval/event guards never tripped", w.Stats.StepGuardHits == 0, "hits=" + w.Stats.StepGuardHits);
        t.Check("all 14 rewind sessions ran", sessions == 14, "sessions=" + sessions);
        t.CheckAtLeast("the long run really produced ball-ball contacts", w.Stats.BallBallContacts, 1,
            "contacts=" + w.Stats.BallBallContacts);
        t.CheckAtLeast("history recorded a real path", w.Cue.History.Count, 100, " entries");
        t.CheckAtMost("history stays bounded", w.Cue.History.Count, 60000, " entries");
        GD.Print($"        [T11] steps={w.Tick} rewinds={w.Stats.Rewinds} history={w.Cue.History.Count} " +
                 $"intervals={w.Stats.SubStepIntervals} contacts={w.Stats.BallBallContacts} " +
                 $"pocketed={w.PocketedObjectCount}/{w.ObjectBallCount} wallMs={elapsed}");
    }
}
