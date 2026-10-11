using System;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// Per-ball-type physics and the rail-boost allowance.
///
/// These guard two things the user asked for explicitly:
///   * a ball's behaviour depends on its TYPE (mass, friction, restitution, rail gain);
///   * a rail speed bonus is CAPPED BY COUNT per ball, and the values are data-driven.
///
/// The shipped defaults must reproduce the previous global behaviour exactly, because every
/// authored level's tolerance window (godot/LEVELS.md) was measured against it. P01 is the guard
/// for that: if someone edits the default table, this fails instead of silently invalidating the
/// level verification.
/// </summary>
public static class BallTypeTests
{
    /// <summary>Defaults must equal the locked globals.</summary>
    public static void P01_DefaultsMatchLockedGlobals(TestRunner t)
    {
        t.Section("P01 - per-type defaults reproduce the locked globals");

        var cfg = new PhysicsConfig();
        var cue = cfg.For(BallType.Cue);
        var obj = cfg.For(BallType.Object);

        // The inherit sentinel must resolve to the locked constants, not to a hardcoded copy: a
        // hardcoded copy is how the first attempt broke T07/T08/K10 (0.955 instead of 0.92).
        t.Check("cue rail gain inherits CueWallGain", cue.WallGain < 0.0,
            "sentinel=" + cue.WallGain + " -> " + cfg.CueWallGain);
        t.Check("cue wall restitution inherits WallRestitution", cue.Restitution < 0.0);
        t.Check("object gets no rail bonus", Math.Abs(obj.WallGain) < 1e-12);
        t.Check("object rail boosts are disabled", obj.MaxWallBoosts == 0);
        t.Check("cue rail boosts are unlimited by default", cue.MaxWallBoosts < 0);
        t.CheckClose("object mass is 1", obj.Mass, 1.0, 1e-12, " kg");
        t.CheckClose("heavy is genuinely heavier", cfg.For(BallType.Heavy).Mass, 3.0, 1e-12, " kg");

        // The locked rail fixed point must still hold with per-type params in play.
        t.CheckClose("rail fixed point is still the locked 1500", cfg.RailFixedPoint, 1500.0, 1e-9, " px/s");
        t.Check("rail fixed point stays below the cap", cfg.RailFixedPointIsBelowCap);
    }

    /// <summary>
    /// Mass must change how a collision resolves: the same incoming cue ball leaves a heavy ball
    /// moving more slowly than a light one.
    /// </summary>
    public static void P02_MassChangesCollisionResponse(TestRunner t)
    {
        t.Section("P02 - ball type changes how a collision resolves");

        // Purpose-built two-ball table. Reusing a shipped level here meant fighting its other balls
        // for the contact, which twice produced a silent 0 px/s measurement.
        double TargetSpeedAfterImpact(double mass)
        {
            var cfg = new PhysicsConfig { FrictionDecel = 0.0 };
            cfg.Types[(int)BallType.Object] = new BallTypeParams
            {
                Mass = mass, FrictionDecel = 0.0, Restitution = -1.0, WallGain = 0.0, MaxWallBoosts = 0,
            };

            var lvl = LevelData.EmptyTable(cfg);
            lvl.Id = "mass_probe";
            lvl.Balls.Clear();
            const double x0 = 300.0;
            const double y0 = 280.0;
            const double r = 12.2;
            lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = x0, Y = y0 });
            lvl.Balls.Add(new BallSpawn
            {
                Id = 1, Type = BallType.Object, DeclaredType = "object",
                X = x0 + 2 * r + 0.5, Y = y0,
            });

            var w = new World(lvl);
            // A known inward speed, set directly so both runs start from an identical state.
            w.Cue.Velocity = new Vec2(600.0, 0.0);
            for (var i = 0; i < 240; i++) w.Step();
            return w.Balls[1].Speed;
        }

        var vLight = TargetSpeedAfterImpact(1.0);
        var vHeavy = TargetSpeedAfterImpact(3.0);

        t.CheckAtLeast("the light ball was actually struck", vLight, 1.0);
        t.CheckAtLeast("the heavy ball was actually struck", vHeavy, 0.1);
        t.Check("the heavy ball leaves the collision slower", vHeavy < vLight,
            $"heavy={vHeavy:F1}px/s light={vLight:F1}px/s");

        // The factor is 2/(1+m): m=1 -> 1.0, m=3 -> the heavy ball takes half the speed.
        t.CheckClose("heavy/light speed ratio matches the equal-mass elastic model",
            vHeavy / vLight, 0.5, 0.05, " ratio");

        t.Check("P02 evidence", true, $"light={vLight:F1}px/s heavy={vHeavy:F1}px/s ratio={vHeavy / vLight:F3}");
    }

    /// <summary>The rail bonus stops after its allowance, while the reflection continues.</summary>
    public static void P03_WallBoostIsCappedByCount(TestRunner t)
    {
        t.Section("P03 - the rail speed bonus is capped by count");

        // A frictionless box so the cue can keep bouncing; 2 boosts allowed.
        var lvl = LevelLoader.Load(LevelLoader.LevelDirectory + "/straight_line.json");
        lvl.Config.FrictionDecel = 0.0;
        lvl.Config.Types[(int)BallType.Cue] = new BallTypeParams
        {
            Mass = 1.0, FrictionDecel = 0.0, Restitution = -1.0, WallGain = 120.0, MaxWallBoosts = 2,
        };
        var w = new World(lvl);

        t.Check("the strike is accepted", w.Shot(0.0, 170.0));
        var cue = w.Cue;
        t.Check("the cue starts with its full allowance", cue.WallBoostsRemaining.HasValue
            && cue.WallBoostsRemaining.Value == 2);

        var speedsAfterEachBoost = new System.Collections.Generic.List<double>();
        var lastBoosts = cue.WallBoostsUsed;
        for (var i = 0; i < 8000 && w.Stats.RailTransforms < 8; i++)
        {
            w.Step();
            if (cue.WallBoostsUsed != lastBoosts)
            {
                lastBoosts = cue.WallBoostsUsed;
                speedsAfterEachBoost.Add(cue.Speed);
            }
        }

        t.Check("exactly the allowance was spent", cue.WallBoostsUsed == 2,
            "used=" + cue.WallBoostsUsed);
        t.Check("no allowance remains", cue.WallBoostsRemaining.HasValue && cue.WallBoostsRemaining.Value == 0);
        t.Check("the ball kept bouncing after the allowance ran out", w.Stats.RailTransforms > 2,
            "transforms=" + w.Stats.RailTransforms);
        t.Check("the ball can no longer take a boost", !cue.CanTakeWallBoost);
        // Frictionless, no further bonus: speed must be non-increasing once the allowance is gone.
        t.Check("speed did not keep growing after the allowance", cue.Speed <= speedsAfterEachBoost[^1] + 1e-6,
            $"now={cue.Speed:F1} lastBoost={speedsAfterEachBoost[^1]:F1}");

        t.Check("P03 evidence", true,
            $"boosts=[{string.Join(", ", speedsAfterEachBoost.ConvertAll(v => v.ToString("F1")))}] " +
            $"final={cue.Speed:F1} transforms={w.Stats.RailTransforms}");
    }

    /// <summary>The type table is reachable from level JSON and actually takes effect.</summary>
    public static void P04_TypeOverridesComeFromLevelData(TestRunner t)
    {
        t.Section("P04 - per-type physics is data-driven from the level file");

        var lvl = LevelLoader.Load(LevelLoader.LevelDirectory + "/demo/types_demo.json");
        t.Check("the heavy ball loaded as its own type",
            lvl.Balls.Count > 2 && lvl.Balls[2].Type == BallType.Heavy);
        t.Check("the bomb ball loaded as its own type",
            lvl.Balls.Count > 3 && lvl.Balls[3].Type == BallType.Bomb);

        var heavy = lvl.Config.For(BallType.Heavy);
        t.CheckClose("physics.types.heavy.mass was applied", heavy.Mass, 3.0, 1e-12, " kg");
        t.CheckClose("physics.types.heavy.frictionDecel was applied", heavy.FrictionDecel, 900.0, 1e-12, " px/s^2");

        var cue = lvl.Config.For(BallType.Cue);
        t.Check("physics.types.cue.maxWallBoosts was applied", cue.MaxWallBoosts == 6,
            "maxWallBoosts=" + cue.MaxWallBoosts);
        t.CheckClose("physics.types.cue.wallGain was applied", cue.WallGain, 120.0, 1e-12, " px/s");

        // The ball must carry its resolved params, not just the config entry.
        var w = new World(lvl);
        t.CheckClose("the heavy ball instance carries its mass", w.Balls[2].Mass, 3.0, 1e-12, " kg");
        t.Check("the cue instance carries its boost allowance",
            w.Cue.WallBoostsRemaining.HasValue && w.Cue.WallBoostsRemaining.Value == 6);
        t.CheckClose("the cue instance has 0 boosts used", w.Cue.WallBoostsUsed, 0, 0, " boosts");
    }
}
