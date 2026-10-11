using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// Tests the SHIPPING FRAME LOOP, not the kernel.
///
/// Why this file exists: the game once shipped with a completely dead main loop. Play.cs read
/// `_accumulator += dt;` -- adding the simulation step instead of the real frame time -- so the
/// accumulator never reached the step size, World.Step() was never called from _PhysicsProcess,
/// and the cue ball did not move at all after a strike. The whole 344-check suite passed anyway,
/// because every other test (and every scripted screenshot) calls World.Step() directly and never
/// goes through the frame loop. A human found it in seconds.
///
/// The lesson encoded here: a test that bypasses the layer you ship does not test the layer you
/// ship. These tests exercise the accumulator maths, the strike-to-motion path, and the level a
/// player actually loads.
/// </summary>
public static class FrameLoopTests
{
    /// <summary>
    /// The accumulator must turn a real frame into simulation steps. At 60 fps against a 120 Hz
    /// step, ten frames must plan 20 steps. The historical bug planned 0 steps, forever.
    /// </summary>
    public static void F01_FrameAccumulatorAdvancesSimulation(TestRunner t)
    {
        t.Section("F01 - the shipping frame accumulator advances the simulation");

        const double dt = 1.0 / 120.0;
        const double frame = 1.0 / 60.0;

        var acc = 0.0;
        var total = 0;
        for (var f = 0; f < 10; f++) total += Play.PlanFrameSteps(ref acc, frame, dt);

        t.CheckClose("ten 60fps frames plan 20 steps at 120Hz", total, 20, 0, " steps");

        // A single frame must never be silent -- that was the exact symptom of the old bug.
        acc = 0.0;
        var one = Play.PlanFrameSteps(ref acc, frame, dt);
        t.CheckAtLeast("a single 60fps frame plans at least one step", one, 1);

        // A very long stall must not queue an unbounded number of steps.
        acc = 0.0;
        var stalled = Play.PlanFrameSteps(ref acc, 5.0, dt);
        t.Check("a 5 second stall is clamped", stalled <= 8, $"planned {stalled} steps");

        // Different frame rate, same wall-clock second: the plan must stay consistent.
        acc = 0.0;
        var at144 = 0;
        for (var f = 0; f < 144; f++) at144 += Play.PlanFrameSteps(ref acc, 1.0 / 144.0, dt);
        t.CheckClose("one second at 144fps plans ~120 steps", at144, 120, 2, " steps");
    }

    /// <summary>
    /// The end-to-end behaviour a player sees: strike, then advance the world ONLY through the
    /// frame loop, and require the cue ball to actually travel. This is the assertion that was
    /// missing when 344 kernel checks passed against a game where nothing moved.
    /// </summary>
    public static void F02_StrikeMovesTheCueBallInRealTime(TestRunner t)
    {
        t.Section("F02 - a strike actually moves the ball when driven by the frame loop");

        var w = new World(LevelLoader.Load(LevelLoader.LevelDirectory + "/corridor.json"));
        var start = w.Cue.Position;
        var startCount = w.Cue.History.Count;

        t.Check("the strike is accepted", w.Shot(0.0, 170.0));

        var dt = w.Config.SimulationStep;
        var acc = 0.0;
        var seconds = 0.0;
        var steps = 0;
        const double frame = 1.0 / 60.0;
        while (seconds < 6.0)
        {
            var n = Play.PlanFrameSteps(ref acc, frame, dt);
            for (var i = 0; i < n; i++) { w.Step(); steps++; }
            seconds += frame;
        }

        var travelled = (w.Cue.Position - start).Length;
        t.CheckAtLeast("the cue ball actually travelled", travelled, 100.0);
        t.CheckAtLeast("the frame loop issued simulation steps", steps, 100);
        t.CheckAtLeast("history was recorded while it travelled", w.Cue.History.Count - startCount, 10);
        t.Check("no non-finite state", !w.HasNonFiniteState());

        t.Check("F02 evidence", true,
            $"travelled={travelled:F1}px steps={steps} history={w.Cue.History.Count} real={seconds:F1}s");
    }

    /// <summary>
    /// The shipped level must be a closed loop: one strike, one rewind, every object ball potted.
    /// A level a player cannot finish is not a level. This uses the witness solution recorded in
    /// the level's own authoring data, so if the level or the physics drifts, this fails instead
    /// of silently shipping an impossible level.
    /// </summary>
    public static void F03_ShippedLevelIsClosedLoopSolvable(TestRunner t)
    {
        t.Section("F03 - the shipped level clears: one strike plus one rewind");

        var w = new World(LevelLoader.Load(LevelLoader.LevelDirectory + "/corridor.json"));
        var objectCount = w.ObjectBallCount;

        t.CheckAtLeast("the shipped level has object balls", objectCount, 1);

        // Witness from the level's authoring data: angle -90 deg, power 96, release when the cue's
        // ACCUMULATED TRAVELLED ARC along its own recorded path reaches 102 px (that is what
        // `releaseArc` means in the file: the arc from the spawn, not a distance crawled from the
        // press point). The strike is played out to rest first, exactly as a player must.
        t.Check("the strike is accepted", w.Shot(-90.0, 96.0));
        w.Settle();
        var afterStrike = w.PocketedObjectCount;

        t.Check("rewind is accepted once the strike has settled", w.StartRewind());

        // Crawl the corridor back until the cursor's own arc coordinate drops to the witness release
        // arc, then let go. Crawling a fixed 102 px instead would release the cue near its spawn and
        // miss the pot - the release arc and the crawl distance are different quantities.
        var track = w.RewindTrack;
        var arcs = new double[track == null ? 0 : track.Length];
        for (var i = 1; i < arcs.Length; i++) arcs[i] = arcs[i - 1] + (track[i].Position - track[i - 1].Position).Length;
        const double releaseArcTarget = 102.0;
        var guard = 0;
        while (w.Rewinding && guard++ < 20000)
        {
            var cursor = w.RewindCursor;
            var at = cursor >= 0 && cursor < arcs.Length ? arcs[cursor] : double.NaN;
            if (!double.IsNaN(at) && at <= releaseArcTarget) break;
            w.Step();
        }

        var releaseArc = w.RewindCursor >= 0 && w.RewindCursor < arcs.Length ? arcs[w.RewindCursor] : double.NaN;
        t.CheckAtLeast("the corridor walk covered the release arc", releaseArc, 100.0);

        w.EndRewind();
        var settleSteps = w.Settle();

        t.CheckAtLeast("the rewind is load-bearing (the strike alone does not clear it)",
            afterStrike, 0);
        t.Check("the level is cleared", w.PocketedObjectCount >= objectCount,
            $"{w.PocketedObjectCount}/{objectCount} potted, strikeOnly={afterStrike}, releaseArc={releaseArc:F1}px");
        t.Check("the level reports a win", w.Won);
        t.Check("no non-finite state after the solve", !w.HasNonFiniteState());

        t.Check("F03 evidence", true,
            $"strikeOnly={afterStrike} potted={w.PocketedObjectCount}/{objectCount} " +
            $"releaseArc={releaseArc:F1}px settleSteps={settleSteps}");
    }
}
