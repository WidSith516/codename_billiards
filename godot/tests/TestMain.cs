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
            Run(runner, "R00", CueRetentionTests.R00_RetentionTable);
            Run(runner, "R01", CueRetentionTests.R01_CueKeepsUsefulSpeedAfterHeadOn);
            Run(runner, "R02", CueRetentionTests.R02_ShippedLevelRetainsCueSpeed);
        }

        if (suite == "all" || suite == "holes")
        {
            Run(runner, "W01", WhiteHoleTests.W01_WhiteHoleReturnsEnergy);
            Run(runner, "W02", WhiteHoleTests.W02_FiresOncePerEntry);
            Run(runner, "W03", WhiteHoleTests.W03_RestartsAStoppedCue);
            Run(runner, "W04", WhiteHoleTests.W04_ShippedLevelHasNoWhiteHole);
        }

        if (suite == "all" || suite == "menu")
        {
            Run(runner, "M01", MenuTests.M01_CatalogDiscoversPlayableLevels);
            Run(runner, "M02", MenuTests.M02_UnlockProgressionIsConsistent);
            Run(runner, "M03", MenuTests.M03_StarRuleIsBounded);
            Run(runner, "M04", MenuTests.M04_ProgressRoundTrips);
        }

        if (suite == "all" || suite == "solve")
        {
            Run(runner, "S01", LevelSolvabilityTests.S01_EveryOfferedLevelClears);
            Run(runner, "S02", LevelSolvabilityTests.S02_ShippedLevelIsForgiving);
        }

        if (suite == "queue") Run(runner, "B03", BallCountProbe.B03_QueueShape);
        if (suite == "grow2") Run(runner, "B04", BallCountProbe.B04_WhichPositionsKeepItSolvable);
        if (suite == "shots") Run(runner, "B05", BallCountProbe.B05_SecondShotUnlocksMoreBalls);

        if (suite == "balls")
        {
            Run(runner, "B01", BallCountProbe.B01_HowManyBallsCanBeCleared);
            Run(runner, "B02", BallCountProbe.B02_SegmentsNeededPerCount);
        }
        if (suite == "solve" || suite == "all") Run(runner, "Z", SolverProbe.Z_SolveAll);

        if (suite == "grow")
        {
            // Candidate extra balls for corridor: spread across the table, mixed types, avoiding the
            // cue/object column so the existing corridor is not blocked outright.
            var cands = new (double X, double Y, string Type)[]
            {
                (300, 200, "object"), (740, 200, "object"),
                (300, 400, "object"), (740, 400, "object"),
                (420, 150, "heavy"), (620, 150, "heavy"),
                (420, 420, "anchor"), (620, 420, "anchor"),
                (200, 280, "bomb"), (840, 150, "bomb"),
            };
            SolverProbe.Z_Grow(runner, "corridor", cands);
        }

        if (suite == "all" || suite == "pot")
        {
            Run(runner, "P01", PotBonusTests.P01_NoPotMeansNoBonus);
            Run(runner, "P02", PotBonusTests.P02_PotGrantsTheBonus);
            Run(runner, "P03", PotBonusTests.P03_BonusRespectsTheSpeedCap);
            Run(runner, "P04", PotBonusTests.P04_LockedRewindModelIsUntouched);
        }


        if (suite == "all" || suite == "cards")
        {
            Run(runner, "C01", CardTests.C01_PotDrawsThreeCards);
            Run(runner, "C02", CardTests.C02_CuePotDoesNotDraw);
            Run(runner, "C03", CardTests.C03_ChoosingAppliesTheCard);
            Run(runner, "C04", CardTests.C04_DrawIsDeterministic);
            Run(runner, "C05", CardTests.C05_PoolExcludesTakenCards);
            Run(runner, "C06", CardTests.C06_IceReducesFriction);
            Run(runner, "C07", CardTests.C07_TitanGrowsTheCue);
            Run(runner, "C08", CardTests.C08_TimeCocoonResetsTheCue);
            Run(runner, "C09", CardTests.C09_AntimatterAddsMomentumBack);
            Run(runner, "C10", CardTests.C10_BalloonFailsOnDeadStop);
            Run(runner, "C11", CardTests.C11_BalloonDoesNotBreakWinning);
            Run(runner, "C12", CardTests.C12_PoolIsWellFormed);
            Run(runner, "C13", CardTests.C13_RapidTapRule);
            Run(runner, "C14", CardTests.C14_DeathThroeKickIsFaster);
            Run(runner, "C15", CardTests.C15_SpeedCapIsRaisedAndHeld);
        }

        if (suite == "all" || suite == "fx")
        {
            Run(runner, "E01", RailEffectTests.E01_OneHitCreatesOneImpact);
            Run(runner, "E02", RailEffectTests.E02_ImpactReachesTheShader);
            Run(runner, "E03", RailEffectTests.E03_EffectsExpire);
            Run(runner, "E04", RailEffectTests.E04_RevealCompletes);
            Run(runner, "E05", RailEffectTests.E05_ContactWallIsRecoveredFromReflection);
            Run(runner, "E06", RailEffectTests.E06_ElasticReboundOvershootsRest);
            Run(runner, "E07", RailEffectTests.E07_DentIsLocalAndConnected);
            Run(runner, "E08", RailEffectTests.E08_BoostedAndSpentDiffer);
            Run(runner, "E09", RailEffectTests.E09_KernelCountsRailPayouts);
            Run(runner, "E10", RailEffectTests.E10_ClearingEffectsAlsoIdlesTheWarp);
        }

        if (suite == "all" || suite == "touch")
        {
            Run(runner, "T01", TouchTests.T01_GridFitsThePanel);
            Run(runner, "T02", TouchTests.T02_HitTestMatchesTheDrawnRect);
            Run(runner, "T03", TouchTests.T03_ButtonsDoNotOverlap);
            Run(runner, "T04", TouchTests.T04_AvailabilityFollowsState);
            Run(runner, "T05", TouchTests.T05_EveryActionIsLabelled);
        }

        // A dedicated "G" prefix: R01/P01/W01 are already taken by the cue-retention, ball-type and
        // white-hole suites respectively (and P01 twice), so reusing them would make the gate output
        // ambiguous about which test a failure came from.
        if (suite == "all" || suite == "play")
        {
            Run(runner, "G01", GameplayTests.R01_StrikeIsOppositeThePull);
            Run(runner, "G02", GameplayTests.R02_AimAngleIsTheReversedPull);
            Run(runner, "G03", GameplayTests.P01_CueHasAWiderMouthOnly);
            Run(runner, "G04", GameplayTests.W01_EveryBallRecordsItsOwnContact);
            Run(runner, "G05", GameplayTests.W02_ObjectBallContactsAreRecorded);
            Run(runner, "G06", GameplayTests.W03_ReadingIsIncremental);
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
