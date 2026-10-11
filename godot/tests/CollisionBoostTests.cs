using System;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// The per-type collision boost: "a collision accelerates the ball according to its type".
///
/// This is deliberately energy NON-CONSERVING, which the user asked for in order to make the demo
/// slice completable. It is off by default (boost 1.0) because every authored level's tolerance
/// window was measured against conserving physics, so enabling it must be a measured decision.
/// These tests pin the mechanism so it cannot silently do nothing -- which is exactly what the
/// first implementation did.
/// </summary>
public static class CollisionBoostTests
{
    /// <summary>Struck-ball speed must scale with the struck ball's own type boost.</summary>
    public static void B01_BoostAcceleratesTheStruckBall(TestRunner t)
    {
        t.Section("B01 - the collision boost accelerates the struck ball");

        // Measure the speed the impact GIVES the ball, before anything else can act on it. The
        // first version of this test ran 240 steps and measured the ball AFTER it had hit a second
        // ball, which of course slows it down -- the boost looked like a brake.
        double StruckSpeed(double boost)
        {
            var cfg = new PhysicsConfig { FrictionDecel = 0.0 };
            cfg.Types[(int)BallType.Object] = new BallTypeParams
            {
                Mass = 1.0, FrictionDecel = 0.0, Restitution = -1.0,
                WallGain = 0.0, MaxWallBoosts = 0, CollisionBoost = boost,
            };

            // ONLY two balls, so nothing can touch the target after the impact.
            var lvl = LevelData.EmptyTable(cfg);
            lvl.Id = "boost_probe";
            lvl.Balls.Clear();
            const double x0 = 300.0, y0 = 280.0, r = 12.2;
            lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = x0, Y = y0 });
            lvl.Balls.Add(new BallSpawn
            {
                Id = 1, Type = BallType.Object, DeclaredType = "object",
                X = x0 + 2 * r + 0.5, Y = y0,
            });

            var w = new World(lvl);
            w.Cue.Velocity = new Vec2(600.0, 0.0);

            // Stop as soon as the target starts moving: that IS the speed the impact gave it.
            var best = 0.0;
            for (var i = 0; i < 600; i++)
            {
                w.Step();
                var s = w.Balls[1].Speed;
                if (s > best) best = s;
                if (s > 0.0) break;
            }

            return best;
        }

        var plain = StruckSpeed(1.0);
        var boosted = StruckSpeed(1.45);

        t.CheckAtLeast("the plain collision struck the ball", plain, 1.0);
        t.Check("the boost actually increased the struck ball's speed", boosted > plain * 1.05,
            $"plain={plain:F1} boosted={boosted:F1}");
        t.CheckClose("the boost scales the gained speed by the requested factor", boosted / plain, 1.45, 0.02, " ratio");

        t.Check("B01 evidence", true, $"plain={plain:F1}px/s boost1.45={boosted:F1}px/s ratio={boosted / plain:F3}");
    }

    /// <summary>Boost 1.0 must remain exactly the old behaviour, or every verified level is invalid.</summary>
    public static void B02_DefaultBoostIsANoOp(TestRunner t)
    {
        t.Section("B02 - boost 1.0 is a no-op (verified levels stay valid)");

        var cfg = new PhysicsConfig();
        t.CheckClose("object default boost is 1.0", cfg.For(BallType.Object).CollisionBoost, 1.0, 0, " ratio");
        t.CheckClose("cue default boost is 1.0", cfg.For(BallType.Cue).CollisionBoost, 1.0, 0, " ratio");
        t.CheckClose("heavy default boost is 1.0", cfg.For(BallType.Heavy).CollisionBoost, 1.0, 0, " ratio");

        // A level with no "physics" block must behave identically to before this feature existed.
        var lvl = LevelLoader.Load(LevelLoader.LevelDirectory + "/straight_line.json");
        t.CheckClose("a level without a physics block has boost 1.0",
            lvl.Config.For(BallType.Object).CollisionBoost, 1.0, 0, " ratio");
    }

    /// <summary>The shipped demo level must carry the boost, and still be completable.</summary>
    public static void B03_ShippedLevelCarriesTheBoost(TestRunner t)
    {
        t.Section("B03 - the shipped level carries the boost and still clears");

        var lvl = LevelLoader.Load(LevelLoader.LevelDirectory + "/corridor.json");
        var boost = lvl.Config.For(BallType.Object).CollisionBoost;
        t.Check("the shipped level enables the collision boost", boost > 1.0, "boost=" + boost.ToString("F2"));

        var w = new World(LevelLoader.Load(LevelLoader.LevelDirectory + "/corridor.json"));
        var objectCount = w.ObjectBallCount;
        t.Check("the strike is accepted", w.Shot(-90.0, 96.0));
        w.Settle();
        var afterStrike = w.PocketedObjectCount;

        t.Check("rewind is accepted once the strike has settled", w.StartRewind());
        var track = w.RewindTrack;
        var arcs = new double[track == null ? 0 : track.Length];
        for (var i = 1; i < arcs.Length; i++)
            arcs[i] = arcs[i - 1] + (track[i].Position - track[i - 1].Position).Length;
        var guard = 0;
        while (w.Rewinding && guard++ < 20000)
        {
            var cur = w.RewindCursor;
            if (cur >= 0 && cur < arcs.Length && arcs[cur] <= 102.0) break;
            w.Step();
        }
        w.EndRewind();
        w.Settle();

        t.Check("the level still clears with the boost on", w.PocketedObjectCount >= objectCount,
            $"{w.PocketedObjectCount}/{objectCount} strikeOnly={afterStrike}");
        t.Check("the rewind is still load-bearing", afterStrike < objectCount,
            $"strikeOnly={afterStrike}/{objectCount}");
        t.Check("no non-finite state", !w.HasNonFiniteState());

        t.Check("B03 evidence", true,
            $"boost={boost:F2} strikeOnly={afterStrike} final={w.PocketedObjectCount}/{objectCount} won={w.Won}");
    }
}
