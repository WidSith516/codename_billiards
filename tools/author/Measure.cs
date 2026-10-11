using System;
using System.Collections.Generic;
using Rewind.Sim;

namespace Rewind.Author;

/// <summary>
/// The full acceptance measurement for a candidate level: the tolerance windows, the strike-only
/// control, the corridor/settle numbers and the portability sweep. Everything here runs the
/// shipping kernel; nothing is estimated.
/// </summary>
public static class Measure
{
    /// <summary>Angular resolution of the strike-only control sweep (degrees). Half a degree on a
    /// 34 px cue radius is far finer than the +-2 deg playability bar, so a level that the strike
    /// could clear would be caught rather than reported as clean.</summary>
    public const double StrikeSweepStepDeg = 0.5;

    public const int StrikeSweepPowers = 30;

    public static bool Full(Candidate c, Slot slot)
    {
        var opt = new SolveOptions
        {
            ReleaseSamples = 16,
            Powers = SolveOptions.FinePowers,
        };

        var level = c.Level;
        c.Window = Metrics.MeasureWindow(level, c.AimDeg, opt, coarseDeg: 0.25, maxDeg: 10.0, fineDeg: 0.02);
        if (!c.Window.Solvable) return false;

        Metrics.MeasurePowerWindow(level, c.AimDeg, opt, c.Window);
        Metrics.MeasureStrikeOnly(level, c.Window, StrikeSweepStepDeg, StrikeSweepPowers);
        c.Window.CueStartHalfWidth = Metrics.MeasureCueStartWindow(level, c.AimDeg, opt);

        // corridor / settle numbers of the winning witness
        var witness = c.Window.Nominal;
        var fwd = Kernel.Forward(level, new Kernel.Plan
        {
            AngleDeg = witness.AngleDeg,
            Power = witness.Power,
            Press = Kernel.PressMode.AtRest,
            Rewind = true,
            MaxForwardSteps = 4000,
        });
        c.Window.Settled = Kernel.AllAtRest(fwd.World);
        c.Window.SettleSteps = fwd.Steps;
        c.Window.SettleArcPx = fwd.PressArc;
        c.Window.CorridorLengthPx = c.Recipe.Corridor?.Length ?? 0.0;

        // portability: the same witness under different (legal) substrate settings
        if (witness != null)
        {
            c.Window.Portability["subStep0.5"] = TryVariant(level, witness, cfg =>
            {
                cfg.SubStepTravelBudget = 0.5;
                cfg.SubStepRelativeTravelBudget = 0.5;
            });
            c.Window.Portability["subStep2.0"] = TryVariant(level, witness, cfg =>
            {
                cfg.SubStepTravelBudget = 2.0;
                cfg.SubStepRelativeTravelBudget = 2.0;
            });
            c.Window.Portability["dt1/240"] = TryVariant(level, witness, cfg => cfg.SimulationStep = 1.0 / 240.0);
            c.Window.Portability["dt1/60"] = TryVariant(level, witness, cfg => cfg.SimulationStep = 1.0 / 60.0);
        }

        return c.Accepted;
    }

    /// <summary>
    /// Replay the witness plan under a mutated physics config. The release point is re-quantised to
    /// the nearest arc that configuration can actually reach (a different dt changes the crawl per
    /// step, hence which history entries the cursor lands on) - the player would do the same.
    /// </summary>
    private static string TryVariant(LevelData level, Solution witness, Action<PhysicsConfig> mutate)
    {
        var cfg = new PhysicsConfig();
        mutate(cfg);
        var plan = new Kernel.Plan
        {
            AngleDeg = witness.AngleDeg,
            Power = witness.Power,
            Press = Kernel.PressMode.AtRest,
            Rewind = true,
            MaxForwardSteps = 4000,
            ConfigOverride = cfg,
        };

        var fwd = Kernel.Forward(level, plan);
        if (!fwd.Strike.ShotAccepted) return "shot refused";
        var arcs = Kernel.ReachableReleaseArcs(fwd);
        if (arcs.Length == 0) return "no rewind";
        var target = witness.ReleaseArc;
        var best = double.PositiveInfinity;
        var chosen = arcs[0];
        foreach (var a in arcs)
        {
            if (a < Limits.CorridorArcFloorPx - 1e-9) continue;
            var d = Math.Abs(a - target);
            if (d < best)
            {
                best = d;
                chosen = a;
            }
        }

        if (double.IsInfinity(best)) return "no release arc above the floor";
        var res = Kernel.Finish(fwd, chosen);
        if (!res.Cleared) return $"NOT CLEAR ({res.PocketedObjects}/{res.ObjectCount})";
        var worst = double.PositiveInfinity;
        foreach (var cap in res.ObjectCaptures())
        {
            if (!cap.CleanApproach) return "capture through a bounce";
            if (cap.Margin < worst) worst = cap.Margin;
        }

        if (worst < Limits.MarginFloorPx) return $"worst margin {Geo.N(worst)} px";
        return $"OK (margin {Geo.N(worst)} px, releaseArc {Geo.N(res.ReleaseArc)}, " +
               $"release moved {Geo.N(best)} px)";
    }
}
