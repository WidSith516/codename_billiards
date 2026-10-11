using Godot;

namespace Rewind.Tests;

using Rewind.Sim;

/// <summary>
/// Headless gate entry point. Run:
///   tools/godot/godot_console.exe --headless --path godot res://tests/test_main.tscn
/// Exits 0 when every check passes and 1 otherwise (a real process exit code).
/// Negative control (must exit 1):
///   ... res://tests/test_main.tscn -- --negative
/// </summary>
public partial class TestMain : Node
{
    public override void _Ready()
    {
        var runner = new TestRunner();
        var negative = false;
        var suite = "all";

        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--negative") negative = true;
            else if (arg.StartsWith("--suite=")) suite = arg.Substring("--suite=".Length);
        }

        var cfg = new PhysicsConfig();
        GD.Print("=== REWIND kernel gate ===");
        GD.Print("engine: " + (string)Engine.GetVersionInfo()["string"]);
        GD.Print("assembly: " + typeof(TestMain).Assembly.GetName().Name);
        GD.Print("locked params: " +
                 $"dt={cfg.SimulationStep:R} radius={cfg.BallRadius} power={cfg.PowerMax}x{cfg.SpeedPerPower} " +
                 $"friction={cfg.FrictionDecel} restitution={cfg.WallRestitution} cueGain={cfg.CueWallGain} " +
                 $"cap={cfg.SpeedCap} history={cfg.HistoryStepPx} impulseK={cfg.RewindImpulseK} " +
                 $"crawl={cfg.RewindCrawlPxPerS} pocketR={cfg.PocketRadius} ballRest={cfg.BallRestitution}");

        var jitter = ProjectSettings.GetSetting("physics/common/physics_jitter_fix", -1.0f);
        GD.Print($"project settings: physics_jitter_fix={jitter} " +
                 $"ticksPerSecond={ProjectSettings.GetSetting("physics/common/physics_ticks_per_second", -1)} " +
                 $"textureFilter={ProjectSettings.GetSetting("rendering/textures/canvas_textures/default_texture_filter", -1)}");
        runner.Check("physics/common/physics_jitter_fix is 0 (determinism)", (float)jitter == 0.0f, jitter.ToString());
        runner.Check("crawl advances exactly one history entry per step (locked relationship)",
            cfg.CrawlIsOneHistoryEntryPerStep,
            $"{cfg.RewindCrawlPxPerS} * {cfg.SimulationStep:R} = {cfg.RewindCrawlPxPerStep} vs history {cfg.HistoryStepPx}");

        if (cfg.RailFixedPoint >= cfg.SpeedCap)
        {
            runner.Check("rail model cannot diverge (fixed point below cap)", false,
                $"{cfg.RailFixedPoint} vs {cfg.SpeedCap}");
        }

        // The canonical determinism scenario is printed unconditionally so that two separate
        // processes can be compared by a script even when the suite is filtered.
        var canonical = InvariantTests.CanonicalRun();
        GD.Print($"DETERMINISM_HASH={canonical.Hash}");
        GD.Print($"DETERMINISM_DETAIL ticks={canonical.Ticks} history={canonical.History} " +
                 $"rewinds={canonical.Rewinds} pocketed={canonical.Pocketed} time={canonical.Time:R}");

        if (suite == "all" || suite == "gdd")
        {
            Run(runner, "T01", GddTests.T01_SecondShotRejected);
            Run(runner, "T02", GddTests.T02_ReleaseVelocityIsStoredVelocityNegated);
            Run(runner, "T03", GddTests.T03_AppendOnlyHistoryAndReversedReplay);
            Run(runner, "T04", GddTests.T04_RewindCueIsForcedTargetIsPushed);
            Run(runner, "T04b", GddTests.T04b_PhysicalRewindContact);
            Run(runner, "T05", GddTests.T05_PocketedObjectBallStaysPocketed);
            Run(runner, "T06", GddTests.T06_CueBallCanBeRewoundOutOfAPocket);
            Run(runner, "T07", GddTests.T07_VerticalRailBounce);
            Run(runner, "T08", GddTests.T08_ObliqueRailBounce);
            Run(runner, "T08b", GddTests.T08b_SimultaneousDoubleRail);
            Run(runner, "T09", GddTests.T09_RewindDoesNotRetriggerRailGain);
            Run(runner, "T10", GddTests.T10_WinAndReset);
            Run(runner, "T11", GddTests.T11_LongRunWithManyRewinds);
        }

        if (suite == "all" || suite == "kernel")
        {
            Run(runner, "K01", InvariantTests.K01_ConstantDecelerationFriction);
            Run(runner, "K02", InvariantTests.K02_HistoryIsArcLengthSampled);
            Run(runner, "K03", InvariantTests.K03_DeterminismInProcess);
            Run(runner, "K04", InvariantTests.K04_StateIsSerializable);
            Run(runner, "K05", InvariantTests.K05_SubStepChordBudget);
            Run(runner, "K06", InvariantTests.K06_NoTunnellingHeadOn);
            Run(runner, "K07", InvariantTests.K07_NoTunnellingGrazing);
            Run(runner, "K08", InvariantTests.K08_PocketCaptureAtHighSpeed);
            Run(runner, "K09", InvariantTests.K09_NoDivergence72DirectionSweep);
            Run(runner, "K10", InvariantTests.K10_RailModelIsBounded);
            Run(runner, "K11", InvariantTests.K11_RewindImpulseModel);
            Run(runner, "K12", InvariantTests.K12_LevelDataDriven);
            Run(runner, "K13", InvariantTests.K13_RewindBudgetEnforced);
            Run(runner, "K14", InvariantTests.K14_ReplayFromInputSequence);
            Run(runner, "K15", InvariantTests.K15_PocketRule);
            Run(runner, "K16", InvariantTests.K16_JsonCodec);
            Run(runner, "K17", InvariantTests.K17_RewindCrawlIsDecoupled);
        }

        if (suite == "all" || suite == "levels")
        {
            Run(runner, "L01", LevelToleranceTests.L01_AuthoredLevelTolerance);
        }

        // The shipping frame loop is its own layer, and it is the one a human actually touches.
        // It gets its own suite because the bug it guards against is invisible to every test that
        // drives World.Step() directly -- which is all of the above.
        if (suite == "all" || suite == "loop")
        {
            Run(runner, "F01", FrameLoopTests.F01_FrameAccumulatorAdvancesSimulation);
            Run(runner, "F02", FrameLoopTests.F02_StrikeMovesTheCueBallInRealTime);
            Run(runner, "F03", FrameLoopTests.F03_ShippedLevelIsClosedLoopSolvable);
            Run(runner, "F04", FrameLoopTests.F04_LivePlayIsNotFrozen);
        }

        if (suite == "all" || suite == "types")
        {
            Run(runner, "P01", BallTypeTests.P01_DefaultsMatchLockedGlobals);
            Run(runner, "P02", BallTypeTests.P02_MassChangesCollisionResponse);
            Run(runner, "P03", BallTypeTests.P03_WallBoostIsCappedByCount);
            Run(runner, "P04", BallTypeTests.P04_TypeOverridesComeFromLevelData);
            Run(runner, "B01", CollisionBoostTests.B01_BoostAcceleratesTheStruckBall);
            Run(runner, "B02", CollisionBoostTests.B02_DefaultBoostIsANoOp);
            Run(runner, "B03", CollisionBoostTests.B03_ShippedLevelCarriesTheBoost);
        }

        if (negative)
        {
            // An acceptance gate that cannot fail is worse than no gate.
            runner.Check("negative control (forced failure)", false, "this run must exit non-zero");
        }

        var code = runner.Summarize("kernel gate");
        GetTree().Quit(code);
    }

    /// <summary>
    /// A failing test must fail the gate, and it must never take the process down with it: an
    /// exception escaping _Ready() leaves the SceneTree running forever with no exit code.
    /// </summary>
    private static void Run(TestRunner runner, string id, System.Action<TestRunner> test)
    {
        try
        {
            test(runner);
        }
        catch (System.Exception ex)
        {
            runner.Check($"{id} suite did not throw", false, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
