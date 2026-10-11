using System;
using Rewind.Game;
using Rewind.Sim;
using System.Globalization;

namespace Rewind.Tests;

/// <summary>
/// Measures how much speed the CUE keeps after a head-on strike, across restitution and mass
/// ratio. Written because a full-power straight hit left the cue with ~2% of its speed: with equal
/// masses and e = 0.955 the physics is correct but it kills the game, since the cue is the only
/// ball that can be rewound and a stopped cue has no corridor to walk.
///
/// This is a MEASUREMENT + tuning harness, and it also asserts a viability floor so the tuning
/// cannot silently regress.
/// </summary>
public static class CueRetentionTests
{
    /// <summary>Speed the cue retains after a head-on hit, and the speed the object gets.</summary>
    private static (double cue, double obj) HeadOn(double restitution, double objMass, double cueMass = 1.0)
    {
        var cfg = new PhysicsConfig { FrictionDecel = 0.0, BallRestitution = restitution };
        cfg.Types[(int)BallType.Cue] = new BallTypeParams
        { Mass = cueMass, FrictionDecel = 0.0, Restitution = restitution, WallGain = 0.0, MaxWallBoosts = 0 };
        cfg.Types[(int)BallType.Object] = new BallTypeParams
        { Mass = objMass, FrictionDecel = 0.0, Restitution = restitution, WallGain = 0.0, MaxWallBoosts = 0 };

        var lvl = LevelData.EmptyTable(cfg);
        lvl.Id = "retention_probe";
        lvl.Balls.Clear();
        const double x0 = 300.0, y0 = 280.0, r = 12.2;
        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = x0, Y = y0 });
        lvl.Balls.Add(new BallSpawn
        {
            Id = 1, Type = BallType.Object, DeclaredType = "object",
            X = x0 + 2 * r + 0.5, Y = y0,
        });

        var w = new World(lvl);
        w.Cue.Velocity = new Vec2(1258.0, 0.0);   // full-power break speed

        // Step until the target is moving, then read both: that is the instant after impact.
        for (var i = 0; i < 600; i++)
        {
            w.Step();
            if (w.Balls[1].Speed > 0.0) break;
        }

        return (w.Cue.Speed, w.Balls[1].Speed);
    }

    /// <summary>
    /// Prints the retention table so the numbers behind the chosen tuning are on the record.
    /// Always passes; it is a report, and F/R tests below carry the assertions.
    /// </summary>
    public static void R00_RetentionTable(TestRunner t)
    {
        t.Section("R00 - cue speed retention after a head-on hit (measurement)");

        var rows = new (string Label, double E, double M)[]
        {
            ("shipped before (e=0.955, m=1)", 0.955, 1.0),
            ("e=0.90, m=1", 0.90, 1.0),
            ("e=0.85, m=1", 0.85, 1.0),
            ("e=0.955, cue m=1.5", 0.955, 1.5),
            ("e=0.92, cue m=1.5", 0.92, 1.5),
            ("e=0.90, cue m=2.0", 0.90, 2.0),
            ("e=0.955, cue m=2.0", 0.955, 2.0),
        };

        var lines = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            var (cue, obj) = HeadOn(row.E, row.M == 1.0 ? 1.0 : 1.0, row.M);
            var pct = cue / 1258.0 * 100.0;
            lines.Append(string.Format(CultureInfo.InvariantCulture,
                "{0}: cue={1:F0} ({2:F1}%) obj={3:F0}", row.Label, cue, pct, obj)).Append("  |  ");
        }

        t.Check("R00 retention table", true, lines.ToString());
    }

    /// <summary>
    /// The cue must keep enough speed to be worth rewinding. Without this the game is unplayable:
    /// at 2% retention the cue stops dead at first contact and there is no corridor to walk back.
    /// </summary>
    public static void R01_CueKeepsUsefulSpeedAfterHeadOn(TestRunner t)
    {
        t.Section("R01 - the cue keeps useful speed after a head-on hit");

        var (cue, obj) = HeadOn(0.92, 1.0, 1.5);

        t.CheckAtLeast("the object ball is driven hard", obj, 700.0);
        t.CheckAtLeast("the cue keeps at least 15% of its speed", cue / 1258.0, 0.15);
        t.CheckAtLeast("the cue is still worth rewinding (>= 150 px/s)", cue, 150.0);
        t.Check("the cue carries less than the struck ball (it still lost energy)", cue < obj,
            $"cue={cue:F0} obj={obj:F0}");

        t.Check("R01 evidence", true,
            $"e=0.92 cueMass=1.5 -> cue={cue:F0}px/s ({cue / 1258.0 * 100.0:F1}%) obj={obj:F0}px/s");
    }

    /// <summary>The shipped level must satisfy the retention floor with its own physics.</summary>
    public static void R02_ShippedLevelRetainsCueSpeed(TestRunner t)
    {
        t.Section("R02 - the shipped level retains cue speed");

        var probe = LevelLoader.Load(LevelLoader.LevelDirectory + "/corridor.json");
        var cueParams = probe.Config.For(BallType.Cue);
        var boost = probe.Config.For(BallType.Object).CollisionBoost;

        var w = new World(LevelLoader.Load(LevelLoader.LevelDirectory + "/corridor.json"));
        t.Check("the strike is accepted", w.Shot(-90.0, 170.0));
        var peakCue = w.Cue.Speed;

        // Step until ANY object ball starts moving: that is the instant after first contact.
        // Checking only Balls[1] missed the case where the cue reaches a different ball first.
        var cueAfter = double.NaN;
        for (var i = 0; i < 4000; i++)
        {
            w.Step();
            for (var bi = 1; bi < w.Balls.Length; bi++)
            {
                if (w.Balls[bi].Speed > 0.0) { cueAfter = w.Cue.Speed; break; }
            }
            if (!double.IsNaN(cueAfter)) break;
        }

        t.Check("first contact happened", !double.IsNaN(cueAfter), "cueAfter=" + cueAfter.ToString("F1"));
        var pct = cueAfter / peakCue;
        t.CheckAtLeast("the cue retains speed at first contact in the shipped level", pct, 0.12);
        t.Check("R02 evidence", true,
            $"peak={peakCue:F0} afterFirstContact={cueAfter:F0} ({pct * 100.0:F1}%) " +
            $"cueMass={cueParams.Mass:F2} e={cueParams.Restitution:F3} objBoost={boost:F2}");
    }
}
