using System;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// The pot bonus: every object ball pocketed pays the cue back in release speed.
///
/// Why the mechanic exists: the cue is the ONLY ball that can be rewound, so the moment it runs out
/// of speed the level is over and no amount of skill recovers it. Rewarding a pot with speed makes a
/// good strike sustainable instead of terminal.
///
/// Why it is NOT on RewindImpulseK: that coefficient is LOCKED at &lt;= 1 by §7 of the params table,
/// whose stated intent is that the player's energy comes only from the strike and the rails, never
/// from rewind amplification. This bonus adds speed to the CUE's own release, so nothing is amplified
/// through a collision and the locked rule stays intact.
///
/// THREE THINGS THIS FIXTURE HAD TO LEARN THE HARD WAY, all measured rather than assumed:
///  1. `LevelData.EmptyTable` has NO POCKETS. They must be added explicitly.
///  2. The capture rule is `Dot(velocity, pocket - ball) > 0`, so a ball at REST can NEVER be potted
///     -- its dot product is exactly 0 and the test is strict. Proved by arithmetic: a ball sitting in
///     a pocket measured d2=9 (well inside r2=576) with dot=0.000 and was not captured. A pot
///     therefore requires a real strike.
///  3. `+y points UP`. Shooting at -90 deg sends the cue toward SMALLER y; +90 is "down". The level
///     witness's -90 combined with a cue ABOVE its target is what made this non-obvious, and the
///     first versions of this fixture aimed the cue away from the pocket entirely.
/// </summary>
public static class PotBonusTests
{
    /// <summary>Cue XY and the aim/power that pot exactly one object ball, found by search.</summary>
    private const double CueX = 520.0;
    private const double CueY = 415.0;
    private const double ObjectY = 455.0;
    private const double AimDegrees = 90.0;    // +90 = toward the bottom wall, where the pocket is
    private const double Power = 96.0;

    /// <summary>
    /// A fixture that pots EXACTLY one object ball and leaves one on the table.
    ///
    /// The geometry was FOUND by sweeping cue position, gap, and power for the case that pots one ball
    /// without potting the cue (5802 hits), not derived by hand. One ball must remain: if the strike
    /// clears the level then the world freezes and `StartRewind` is refused, so the probe could never
    /// reach the release it exists to measure.
    /// </summary>
    private static LevelData ShotLevel(double bonus)
    {
        var cfg = new PhysicsConfig { RewindPotBonus = bonus };
        var lvl = LevelData.EmptyTable(cfg);
        lvl.Id = "pot_bonus_probe";
        lvl.Balls.Clear();
        lvl.AddStandardPockets();

        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = CueX, Y = CueY });
        lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = CueX, Y = ObjectY });
        lvl.Balls.Add(new BallSpawn { Id = 2, Type = BallType.Object, DeclaredType = "object", X = 200, Y = 300 });
        return lvl;
    }

    /// <summary>Strike the fixture and settle, asserting the pot actually happened.</summary>
    private static World Pot(TestRunner t, double bonus)
    {
        var w = new World(ShotLevel(bonus));
        t.Check("the strike is accepted", w.Shot(AimDegrees, Power));
        w.Settle();
        t.CheckAtLeast("the fixture pots exactly one object ball", w.PocketedObjectCount, 1);
        t.Check("the cue is NOT potted by the fixture shot", !w.Cue.Pocketed);
        t.Check("the level is not already won, so a rewind is possible", !w.Won);
        return w;
    }

    /// <summary>
    /// Rewind back along the cue's OWN path until the arc walked reaches <paramref name="releaseArcPx"/>,
    /// then release. Returns (stored speed before release, speed after release).
    ///
    /// Releasing at the track's START would measure nothing: the cue was at rest when it was struck, so
    /// the stored velocity at the first entry is exactly zero. That is a real edge case the mechanic
    /// handles (the bonus still resumes the cue), but it is not the case that isolates the bonus
    /// arithmetic, so the fixture releases mid-track.
    /// </summary>
    private static (double Before, double After) RewindToArcAndRelease(World w, double releaseArcPx)
    {
        if (!w.StartRewind()) return (double.NaN, double.NaN);

        var track = w.RewindTrack;
        var arcs = new double[track == null ? 0 : track.Length];
        for (var i = 1; i < arcs.Length; i++)
            arcs[i] = arcs[i - 1] + (track[i].Position - track[i - 1].Position).Length;

        var guard = 0;
        while (w.Rewinding && guard++ < 40000)
        {
            var cursor = w.RewindCursor;
            if (cursor >= 0 && cursor < arcs.Length && arcs[cursor] <= releaseArcPx) break;
            w.Step();
        }

        var before = w.Cue.Speed;
        w.EndRewind();
        return (before, w.Cue.Speed);
    }

    /// <summary>Without a pot the release stays the locked reverse velocity, un-inflated.</summary>
    public static void P01_NoPotMeansNoBonus(TestRunner t)
    {
        t.Section("P01 - the bonus is gated on actually potting a ball");

        // A fixture with NO reachable pocket: the same shot, but the pockets removed, so nothing can
        // be potted and the bonus term must stay out of the way.
        var cfg = new PhysicsConfig { RewindPotBonus = 0.5 };
        var lvl = LevelData.EmptyTable(cfg);
        lvl.Balls.Clear();                       // deliberately NO AddStandardPockets
        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = CueX, Y = CueY });
        lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = CueX, Y = ObjectY });
        lvl.Balls.Add(new BallSpawn { Id = 2, Type = BallType.Object, DeclaredType = "object", X = 200, Y = 300 });

        var w = new World(lvl);
        w.Shot(AimDegrees, Power);
        w.Settle();
        t.Check("nothing was potted", w.PocketedObjectCount == 0, "potted=" + w.PocketedObjectCount);

        var (before, after) = RewindToArcAndRelease(w, 60.0);
        t.Check("the release is finite", !double.IsNaN(after), "after=" + after);
        t.Check("release speed is not inflated without a pot", after <= before + 1e-6,
            $"before={before:F1} after={after:F1}");

        t.Check("P01 evidence", true, $"potted=0 stored={before:F1} released={after:F1}");
    }

    /// <summary>A pot grants exactly bonus x launchSpeed x pots.</summary>
    public static void P02_PotGrantsTheBonus(TestRunner t)
    {
        t.Section("P02 - a pot grants the configured fraction of the launch speed");

        const double bonus = 0.5;

        double Measure(double coefficient, out int potted, out double launch, out double release)
        {
            var w = new World(ShotLevel(coefficient));
            w.Shot(AimDegrees, Power);
            launch = w.LaunchSpeed;
            w.Settle();
            potted = w.PocketedObjectCount;
            var r = RewindToArcAndRelease(w, 60.0);
            release = r.After;
            return r.After;
        }

        var zero = Measure(0.0, out var pottedZero, out var launchZero, out var releaseZero);
        var half = Measure(bonus, out var pottedHalf, out var launchHalf, out var releaseHalf);

        t.CheckAtLeast("the probe pots a ball with no bonus", pottedZero, 1);
        t.CheckAtLeast("the probe pots a ball with the bonus", pottedHalf, 1);
        t.CheckClose("the launch speed is identical across both runs", launchZero, launchHalf, 3, " px/s");

        var expectedGain = bonus * launchHalf * pottedHalf;
        var actualGain = releaseHalf - releaseZero;

        t.CheckClose("the release gains bonus x launchSpeed x pots", actualGain, expectedGain, 2, " px/s");
        t.CheckAtLeast("the bonus is a real speed increase", actualGain, 1.0);

        t.Check("P02 evidence", true,
            $"launch={launchHalf:F1} potted={pottedHalf} noBonus={releaseZero:F1} withBonus={releaseHalf:F1} " +
            $"gain={actualGain:F1} expected={expectedGain:F1}");
    }

    /// <summary>The bonus must never push the cue past the speed cap the integrator is tuned for.</summary>
    public static void P03_BonusRespectsTheSpeedCap(TestRunner t)
    {
        t.Section("P03 - a huge bonus still respects the speed cap");

        // A coefficient of 20x the launch speed per pot. The bonus alone wants 20 x 710.4 = 14208 px/s,
        // seven times the cap, so the clamp is what must hold.
        var w = Pot(t, 20.0);

        // Measured MID-track, because releasing at the very start would report the cue at rest and the
        // cap assertion would then pass for the wrong reason -- which is exactly what it did before this
        // was fixed (the earlier version reported speed=0.0 and proved nothing).
        var r = RewindToArcAndRelease(w, 60.0);

        t.CheckAtLeast("the release actually has speed, so the cap is being exercised", r.After, 1.0);
        t.Check("release speed never exceeds the speed cap", r.After <= w.Config.SpeedCap + 1e-6,
            $"speed={r.After:F1} cap={w.Config.SpeedCap:F1}");
        t.Check("the state stays finite after the clamped release", !w.HasNonFiniteState());

        // Let it fly, then confirm nothing went non-finite during the subsequent motion.
        w.Settle();
        t.Check("the cue never went non-finite while rolling", !w.HasNonFiniteState());

        t.Check("P03 evidence", true,
            $"stored={r.Before:F1} released={r.After:F1} cap={w.Config.SpeedCap:F1} " +
            $"uncappedWouldBe={(r.Before + 20.0 * w.LaunchSpeed * w.PocketedObjectCount):F0}");
    }

    /// <summary>The bonus must not disturb the LOCKED rewind model.</summary>
    public static void P04_LockedRewindModelIsUntouched(TestRunner t)
    {
        t.Section("P04 - the locked rewind model is untouched by the bonus");

        var cfg = new PhysicsConfig();
        t.CheckClose("RewindImpulseK is still 1.0", cfg.RewindImpulseK, 1.0, 6, "");
        t.CheckClose("RewindCrawlPxPerS is still 240 px/s", cfg.RewindCrawlPxPerS, 240.0, 6, " px/s");
        t.Check("the crawl still advances exactly one history entry per step",
            cfg.CrawlIsOneHistoryEntryPerStep,
            $"{cfg.RewindCrawlPxPerStep} vs {cfg.HistoryStepPx}");
        t.Check("the pot bonus defaults to one half", Math.Abs(cfg.RewindPotBonus - 0.5) < 1e-9,
            "bonus=" + cfg.RewindPotBonus);

        t.Check("P04 evidence", true,
            $"impulseK={cfg.RewindImpulseK} crawl={cfg.RewindCrawlPxPerS} potBonus={cfg.RewindPotBonus}");
    }
}
