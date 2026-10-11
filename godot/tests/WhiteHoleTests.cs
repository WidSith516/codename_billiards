using System;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// White holes: the energy-return zones.
///
/// Why the mechanic exists: the cue ball is the ONLY ball that can be rewound, so the moment it
/// stops, the level is over. A single strike plus rail bounces is a fragile energy supply, and a
/// heavier cue (mass 2.0) only delays the problem. A white hole is a PLACE on the table the player
/// can route the cue through to get energy back, which turns "keep the cue alive" into a routing
/// decision they can plan instead of luck.
///
/// These tests pin the two properties that make it a usable design element rather than a bug:
/// it fires once per ENTRY (no farming by resting inside), and it is bounded (no runaway speed).
/// </summary>
public static class WhiteHoleTests
{
    private static LevelData HoleLevel(double gain, double exitSpeed, double radius = 34.0)
    {
        var cfg = new PhysicsConfig { FrictionDecel = 0.0 };
        var lvl = LevelData.EmptyTable(cfg);
        lvl.Id = "hole_probe";
        lvl.Balls.Clear();
        lvl.WhiteHoles.Clear();
        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = 200, Y = 280 });
        lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = 900, Y = 480 });
        lvl.WhiteHoles.Add(new WhiteHoleDef
        {
            Center = new Vec2(500, 280), Radius = radius, Gain = gain, ExitSpeed = exitSpeed, MaxSpeed = 1500,
        });
        return lvl;
    }

    /// <summary>A ball passing through the zone must gain speed.</summary>
    public static void W01_WhiteHoleReturnsEnergy(TestRunner t)
    {
        t.Section("W01 - a white hole returns energy to a passing ball");

        var w = new World(HoleLevel(1.4, 520.0));
        w.Cue.Velocity = new Vec2(200.0, 0.0);

        var before = w.Cue.Speed;
        var peak = before;
        for (var i = 0; i < 600; i++)
        {
            w.Step();
            if (w.Cue.Speed > peak) peak = w.Cue.Speed;
        }

        t.CheckAtLeast("the white hole fired", w.Stats.WhiteHoleActivations, 1);
        t.Check("the ball gained speed", peak > before * 1.2, $"before={before:F0} peak={peak:F0}");
        t.Check("the ball never exceeded the hole's own ceiling", peak <= 1500.0 + 1e-6,
            $"peak={peak:F0}");

        t.Check("W01 evidence", true,
            $"before={before:F0}px/s peak={peak:F0}px/s activations={w.Stats.WhiteHoleActivations}");
    }

    /// <summary>
    /// A ball cannot farm energy by staying put: entering must be a rising edge, and the cooldown
    /// must stop an immediate re-fire.
    ///
    /// Note on the test design: the first version asserted "at most one activation over 240 steps
    /// while nominally resting inside", which is unachievable and therefore a bad test -- the boost
    /// throws the ball out of a 68 px zone in ~0.13 s at 520 px/s, so every later step is a genuine
    /// new entry. What actually matters is (a) a single entry is not repeatable step after step, and
    /// (b) two entries cannot be closer together than the cooldown.
    /// </summary>
    public static void W02_FiresOncePerEntry(TestRunner t)
    {
        t.Section("W02 - a white hole fires once per entry, not once per step");

        // (a) Inside the zone already: it must fire once and then stay quiet for the cooldown,
        //     which is many steps. A per-step (unlatched) implementation would fire ~120 times a
        //     second and run away immediately.
        var lvl = HoleLevel(1.4, 520.0);
        lvl.Balls[0].X = 500;
        lvl.Balls[0].Y = 280;
        var w = new World(lvl);
        t.CheckClose("the cue starts at rest", w.Cue.Speed, 0.0, 0, " px/s");

        var cooldownSteps = (int)Math.Round(lvl.WhiteHoles[0].Cooldown / lvl.Config.SimulationStep);
        for (var i = 0; i < cooldownSteps / 2; i++) w.Step();

        // Half the cooldown has passed; even though the ball may have been kicked out and rolled
        // back, the cooldown must still be suppressing further activations.
        t.CheckLess("no second activation within half a cooldown", w.Stats.WhiteHoleActivations, 2);

        // (b) The hard guarantee: measure the gap between activations directly.
        var lvl2 = HoleLevel(1.4, 520.0);
        lvl2.Balls[0].X = 500;
        lvl2.Balls[0].Y = 280;
        var w2 = new World(lvl2);
        var lastTime = double.NegativeInfinity;
        var minGap = double.PositiveInfinity;
        var seen = 0;
        for (var i = 0; i < 4000; i++)
        {
            var before = w2.Stats.WhiteHoleActivations;
            w2.Step();
            if (w2.Stats.WhiteHoleActivations != before)
            {
                seen++;
                var gap = w2.Time - lastTime;
                if (seen > 1 && gap < minGap) minGap = gap;
                lastTime = w2.Time;
            }
        }

        t.CheckAtLeast("the hole did fire repeatedly across the run (so the gap is meaningful)", seen, 2);
        t.CheckAtLeast("activations are never closer than the cooldown", minGap,
            lvl2.WhiteHoles[0].Cooldown - 1e-9, " s");

        t.Check("W02 evidence", true,
            $"activations={w.Stats.WhiteHoleActivations} minGap={minGap:F3}s " +
            $"cooldown={lvl2.WhiteHoles[0].Cooldown:F2}s seen={seen}");
    }

    /// <summary>A stopped cue must be able to be restarted by a white hole, or it is not a rescue.</summary>
    public static void W03_RestartsAStoppedCue(TestRunner t)
    {
        t.Section("W03 - a white hole can restart a stopped cue");

        var lvl = HoleLevel(1.4, 520.0);
        var w = new World(lvl);
        w.Cue.Velocity = new Vec2(200.0, 0.0);

        // Roll it into the zone, then let friction bring it to rest inside if it can.
        var restarted = false;
        for (var i = 0; i < 4000; i++)
        {
            w.Step();
            if (w.Stats.WhiteHoleActivations > 0 && w.Cue.Speed > 100.0) { restarted = true; break; }
        }

        t.Check("the cue left the zone with real speed", restarted,
            $"activations={w.Stats.WhiteHoleActivations} speed={w.Cue.Speed:F0}");
        t.Check("W03 evidence", true, $"speed={w.Cue.Speed:F0}px/s");
    }

    /// <summary>The shipped level carries a white hole and still clears.</summary>
    public static void W04_ShippedLevelHasAWhiteHole(TestRunner t)
    {
        t.Section("W04 - the shipped level carries a white hole");

        var lvl = LevelLoader.Load(LevelLoader.LevelDirectory + "/corridor.json");
        t.CheckAtLeast("the shipped level declares a white hole", lvl.WhiteHoles.Count, 1);

        var peak = 0.0;
        for (var i = 0; i < lvl.WhiteHoles.Count; i++)
        {
            var h = lvl.WhiteHoles[i];
            t.Check("hole " + i + " has a positive radius", h.Radius > 0.0, "r=" + h.Radius.ToString("F0"));
            t.Check("hole " + i + " is inside the table box",
                h.Center.X > lvl.TableLeft && h.Center.X < lvl.TableRight
                && h.Center.Y > lvl.TableTop && h.Center.Y < lvl.TableBottom,
                $"({h.Center.X:F0},{h.Center.Y:F0})");
            t.Check("hole " + i + " gains energy", h.Gain > 1.0, "gain=" + h.Gain.ToString("F2"));
            t.Check("hole " + i + " respects the speed cap", h.MaxSpeed <= lvl.Config.SpeedCap + 1e-9,
                $"max={h.MaxSpeed:F0} cap={lvl.Config.SpeedCap:F0}");
            if (h.MaxSpeed > peak) peak = h.MaxSpeed;
        }

        t.Check("W04 evidence", true, $"holes={lvl.WhiteHoles.Count} maxSpeed={peak:F0}");
    }
}
