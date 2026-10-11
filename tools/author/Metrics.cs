using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rewind.Sim;

namespace Rewind.Author;

/// <summary>One measured solution: the strike plus the rewind the player has to perform.</summary>
public sealed class Solution
{
    public double AngleDeg;
    public double Power;
    public double ReleaseArc;
    public double Crawl;
    public PlayResult Result;

    public double MinMargin => Result == null ? double.NaN : Result.MinMargin;

    public override string ToString() =>
        $"angle={Geo.N(AngleDeg)} power={Geo.N(Power)} releaseArc={Geo.N(ReleaseArc)} " +
        $"crawl={Geo.N(Crawl)} margin={Geo.N(MinMargin)}";
}

public sealed class SolveOptions
{
    /// <summary>Every object capture must keep at least this much pocket margin (px).</summary>
    public double MarginFloor = Limits.MarginFloorPx;

    /// <summary>The release point must sit at least this far along the cue's recorded path (px).</summary>
    public double ArcFloor = Limits.CorridorArcFloorPx;

    /// <summary>How many release points of the rewind snapshot to try per power.</summary>
    public int ReleaseSamples = 14;

    /// <summary>Drag lengths (px) to try. The player picks one; the window is measured over them.</summary>
    public double[] Powers = DefaultPowers;

    public bool RequireMargin = true;

    /// <summary>Optional diagnostic hook: called for every plan tried, with the reason it failed.</summary>
    public Action<string> Trace;

    public static readonly double[] DefaultPowers =
        { 60, 75, 90, 105, 120, 135, 150, 162, 170 };

    public static readonly double[] FinePowers =
        { 80, 88, 96, 104, 112, 120, 128, 136, 144, 152, 160, 168 };

    public SolveOptions Clone() => (SolveOptions)MemberwiseClone();
}

/// <summary>A measured tolerance window plus the witnesses that prove it.</summary>
public sealed class WindowReport
{
    public bool Solvable;

    /// <summary>Contiguous clearing half-width around 0 deg, degrees (0 when nothing clears).</summary>
    public double HalfWidthDeg;

    /// <summary>Per-side clearing half-widths (the window need not be symmetric).</summary>
    public double NegativeHalfWidthDeg;
    public double PositiveHalfWidthDeg = double.NaN;

    /// <summary>Contiguous half-width where every capture also keeps margin &gt;= floor.</summary>
    public double HalfWidthMarginDeg;

    /// <summary>Farthest clearing angle found on either side (detects detached islands).</summary>
    public double IslandDeg;

    public Solution Nominal;
    public Solution NegativeEdge;
    public Solution PositiveEdge;
    /// <summary>Drag-length tolerance at the nominal angle: [min,max] that clears, px.</summary>
    public double PowerMin = double.NaN;
    public double PowerMax = double.NaN;

    /// <summary>Cue-spawn perturbation tolerance (perpendicular to the aim line), px.</summary>
    public double CueStartHalfWidth = double.NaN;

    /// <summary>Largest number of object balls any strike-only plan pocketed.</summary>
    public int StrikeOnlyBest;
    public double StrikeOnlyBestAngle = double.NaN;
    public double StrikeOnlyBestPower = double.NaN;

    /// <summary>Strike-only sweep resolution actually used (degrees, powers).</summary>
    public double StrikeOnlyStepDeg = double.NaN;
    public int StrikeOnlyPowers;

    /// <summary>Forward phase of the witness: does the cue come to rest, and how far did it travel?</summary>
    public bool Settled;
    public int SettleSteps;
    public double SettleArcPx;
    public double CorridorLengthPx;

    /// <summary>Per-variant replay results of the witness plan under different substrate settings.</summary>
    public readonly Dictionary<string, string> Portability = new Dictionary<string, string>();

    public string Note = string.Empty;

    public double PowerWindow => PowerMax - PowerMin;
}

public static class Limits
{
    /// <summary>
    /// POCKET MARGIN FLOOR = 8 px, applied to the trajectory-line margin
    /// (PocketRadius - perpendicular distance from the pocket centre to the ball's incoming line).
    ///
    /// Why 8 px:
    ///   * It leaves a 16 px=2/3-of-the-radius aim window: the ball's incoming line must pass well
    ///     inside the 24 px capture circle instead of clipping its rim. The old shipped levels ran
    ///     at 0.56-1.72 px (tools/verify_solver/REPORT.md 5c) and flipped pot/miss on a 0.05 deg
    ///     strike rotation; 8 px is 5-14x that budget.
    ///   * It is 0.66 ball radii and 1.3 px more than half the radius: at the capture boundary the
    ///     ball still has ~8 px of radial slack, which numerically survives a different sub-step
    ///     granularity (measured: Portability()).
    ///   * It is not a proxy for tolerance - it is an independent robustness floor. The playability
    ///     gate is the +-2 deg ANGLING window; this floor additionally guarantees that a pot which
    ///     is inside the window is not a rim graze.
    /// </summary>
    public const double MarginFloorPx = 8.0;

    /// <summary>
    /// CORRIDOR ARC FLOOR = 100 px of cue travel before the release point.
    ///
    /// Why 100 px:
    ///   * 100 px = 4.1 ball diameters = 0.42 s of holding R at the locked 240 px/s crawl. The old
    ///     levels released a cue that had travelled 10 px (6 history samples) - a nudge, not a
    ///     corridor (verify_solver REPORT 5b).
    ///   * It is a fifth of the shortest table dimension (434 px), so a "corridor" that short
    ///     already crosses a meaningful fraction of the table and forces the level to be authored
    ///     around the cue's recorded path instead of around a point-blank touch shot.
    ///   * It keeps the rewind load-bearing by construction: any solution must have walked the cue
    ///     at least that far from its spawn before letting go.
    /// NOTE: the floor is on the cue's ACCUMULATED TRAVELLED ARC at the release point. It is not a
    /// floor on the crawl (a-b) - see README "the crawl cannot be a level property".
    /// </summary>
    public const double CorridorArcFloorPx = 100.0;

    /// <summary>
    /// POWER FLOOR: the drag-length window must be at least this wide (px of drag), i.e. +-5 px.
    /// The task fixes the ANGLE bar at +-2 deg; the power bar is this tool's own playability floor.
    /// A 10 px drag window is ~23 px/s of launch speed on the locked 7.4 px/s-per-px mapping, which
    /// is far coarser than a human drag hand-off error, so it cannot be the binding constraint -
    /// but a level whose power window is 1-2 px would be unplayable and is rejected here.
    /// </summary>
    public const double PowerWindowFloorPx = 10.0;

    /// <summary>Own floor for the cue-spawn perturbation window (px), see README.</summary>
    public const double CueStartFloorPx = 3.0;
}

public static class Metrics
{
    /// <summary>
    /// Find the best solution for one strike angle: it must pocket every object ball, keep every
    /// pocket margin above the floor, and release the rewind at or beyond the corridor arc floor.
    /// The rewind is re-chosen per angle (the task's requirement - a real player re-chooses it).
    /// </summary>
    public static Solution Solve(LevelData level, double angle, SolveOptions opt)
    {
        Solution best = null;
        foreach (var power in opt.Powers)
        {
            var plan = new Kernel.Plan
            {
                AngleDeg = angle,
                Power = power,
                Press = Kernel.PressMode.AtRest,
                Rewind = true,
                ReleaseArc = double.NaN,
            };

            var fwd = Kernel.Forward(level, plan);
            if (!fwd.Strike.ShotAccepted || fwd.World.Frozen) continue;

            var arcs = Kernel.ReachableReleaseArcs(fwd);
            if (arcs.Length == 0) continue;

            var candidates = CandidateArcs(arcs, opt);
            foreach (var arc in candidates)
            {
                var res = Kernel.Finish(fwd, arc);
                var score = Score(res, opt);
                if (score == null)
                {
                    opt.Trace?.Invoke($"    power={Geo.N(power)} arc={Geo.N(arc)} -> pocketed {res.PocketedObjects}/{res.ObjectCount} " +
                                      $"releaseArc={Geo.N(res.ReleaseArc)} minMargin={Geo.N(res.MinMargin)} {res.Note} " +
                                      (res.Captures.Count > 0 ? "[" + res.CaptureSummary() + "]" : ""));
                    continue;
                }

                var sol = new Solution
                {
                    AngleDeg = angle,
                    Power = power,
                    ReleaseArc = res.ReleaseArc,
                    Crawl = res.CrawlDistance,
                    Result = res,
                };
                if (best == null || score.Value > Score(best.Result, opt).Value) best = sol;
            }
        }

        return best;
    }

    /// <summary>Solution score: cleared + all margins above the floor, ranked by the worst margin.</summary>
    private static double? Score(PlayResult res, SolveOptions opt)
    {
        if (!res.Cleared) return null;
        if (double.IsNaN(res.ReleaseArc) || res.ReleaseArc < opt.ArcFloor) return null;
        var min = double.PositiveInfinity;
        foreach (var c in res.ObjectCaptures())
        {
            if (!c.CleanApproach) return null;   // never accept a pot measured through a bounce
            if (double.IsNaN(c.Margin)) return null;
            if (c.Margin < min) min = c.Margin;
        }

        if (opt.RequireMargin && min < opt.MarginFloor) return null;
        return min;
    }

    /// <summary>
    /// Which release points to try: evenly sampled reachable arcs at or beyond the floor. Lower
    /// arcs carry more of the recorded speed, so the sample is biased towards them.
    /// </summary>
    private static List<double> CandidateArcs(double[] arcs, SolveOptions opt)
    {
        var list = new List<double>();
        var max = arcs[arcs.Length - 1];
        if (max < opt.ArcFloor) return list;
        var n = Math.Max(2, opt.ReleaseSamples);
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)(n - 1);
            // bias towards the early (high-speed) end: t^1.6
            var u = Math.Pow(t, 1.6);
            var target = opt.ArcFloor + u * (max - opt.ArcFloor);
            list.Add(NearestArcBelow(arcs, target));
        }

        list.Sort();
        var unique = new List<double>();
        foreach (var a in list)
            if (unique.Count == 0 || Math.Abs(a - unique[unique.Count - 1]) > 1e-9) unique.Add(a);
        return unique;
    }

    private static double NearestArcBelow(double[] arcs, double target)
    {
        var best = arcs[0];
        foreach (var a in arcs)
        {
            if (a <= target) best = a;
            else break;
        }

        return best;
    }

    /// <summary>
    /// Measure the strike-angle window: the contiguous set of strike angles around the base angle
    /// for which a clearing solution exists with the rewind (and power) re-chosen.
    /// </summary>
    public static WindowReport MeasureWindow(LevelData level, double baseAngle, SolveOptions opt,
        double coarseDeg = 0.25, double maxDeg = 8.0, double fineDeg = 0.025)
    {
        var report = new WindowReport();
        var nominal = Solve(level, baseAngle, opt);
        report.Nominal = nominal;
        report.Solvable = nominal != null;
        if (nominal == null)
        {
            report.HalfWidthDeg = 0.0;
            report.HalfWidthMarginDeg = 0.0;
            return report;
        }

        var neg = Contiguous(level, baseAngle, opt, coarseDeg, maxDeg, fineDeg, requireMargin: false, sign: -1.0);
        var pos = Contiguous(level, baseAngle, opt, coarseDeg, maxDeg, fineDeg, requireMargin: false, sign: +1.0);
        report.HalfWidthDeg = Math.Min(neg.Width, pos.Width);
        report.IslandDeg = Math.Max(neg.Width, pos.Width);

        var negM = Contiguous(level, baseAngle, opt, coarseDeg, maxDeg, fineDeg, requireMargin: true, sign: -1.0);
        var posM = Contiguous(level, baseAngle, opt, coarseDeg, maxDeg, fineDeg, requireMargin: true, sign: +1.0);
        report.HalfWidthMarginDeg = Math.Min(negM.Width, posM.Width);
        report.NegativeHalfWidthDeg = negM.Width;
        report.PositiveHalfWidthDeg = posM.Width;
        report.IslandDeg = Math.Max(report.IslandDeg, Math.Max(negM.Width, posM.Width));

        // Edge witnesses: re-solve exactly at the measured edge so the level file carries a plan the
        // kernel test can replay at +-window instead of trusting a number.
        report.NegativeEdge = negM.Solution;
        report.PositiveEdge = posM.Solution;
        return report;
    }

    private static (double Width, Solution Solution) Contiguous(LevelData level, double baseAngle, SolveOptions opt,
        double coarse, double max, double fine, bool requireMargin, double sign)
    {
        var opts = opt.Clone();
        opts.RequireMargin = requireMargin;

        var lo = 0.0;
        var hi = double.NaN;
        for (var d = coarse; d <= max + 1e-9; d += coarse)
        {
            if (Solve(level, baseAngle + sign * d, opts) != null) lo = d;
            else { hi = d; break; }
        }

        if (!double.IsNaN(hi))
        {
            for (var i = 0; i < 40 && hi - lo > fine; i++)
            {
                var mid = 0.5 * (lo + hi);
                if (Solve(level, baseAngle + sign * mid, opts) != null) lo = mid;
                else hi = mid;
            }
        }

        return (lo, Solve(level, baseAngle + sign * lo, opts));
    }

    /// <summary>Drag-length window at a fixed angle (release re-chosen for each power).</summary>
    public static void MeasurePowerWindow(LevelData level, double angle, SolveOptions opt, WindowReport report)
    {
        var lo = double.NaN;
        var hi = double.NaN;
        var powers = new List<double>();
        for (var p = PhysicsConfigDefaults.MinPower; p <= PhysicsConfigDefaults.PowerMax + 1e-9; p += 2.0) powers.Add(p);

        foreach (var p in powers)
        {
            var single = opt.Clone();
            single.Powers = new[] { p };
            if (Solve(level, angle, single) == null) continue;
            if (double.IsNaN(lo)) lo = p;
            hi = p;
        }

        report.PowerMin = lo;
        report.PowerMax = hi;
    }

    /// <summary>
    /// Cue-spawn window: move the cue ball's spawn perpendicular to the aim line by +-w (level data
    /// perturbation), let the player re-aim and re-rewind, and find the largest w that still clears.
    /// Reported in px.
    /// </summary>
    public static double MeasureCueStartWindow(LevelData level, double baseAngle, SolveOptions opt,
        double coarse = 1.0, double max = 24.0, double fine = 0.1)
    {
        var dir = Geo.Dir(baseAngle);
        var perp = Geo.Perp(dir);

        bool ClearsAt(double w)
        {
            var moved = Kernel.Clone(level);
            moved.Balls[0].X += perp.X * w;
            moved.Balls[0].Y += perp.Y * w;
            // the player re-aims: search a small angle band around the original aim
            for (var da = -1.5; da <= 1.5001; da += 0.15)
            {
                var single = opt.Clone();
                single.ReleaseSamples = 6;
                single.Powers = new[] { 90.0, 112.0, 135.0, 158.0, 170.0 };
                if (Solve(moved, baseAngle + da, single) != null) return true;
            }

            return false;
        }

        double Edge(double sign)
        {
            var lo = 0.0;
            var hi = double.NaN;
            for (var w = coarse; w <= max + 1e-9; w += coarse)
            {
                if (ClearsAt(sign * w)) lo = w;
                else { hi = w; break; }
            }

            if (double.IsNaN(hi)) return lo;
            for (var i = 0; i < 30 && hi - lo > fine; i++)
            {
                var mid = 0.5 * (lo + hi);
                if (ClearsAt(sign * mid)) lo = mid;
                else hi = mid;
            }

            return lo;
        }

        return Math.Min(Edge(+1.0), Edge(-1.0));
    }

    /// <summary>
    /// The control the task demands: sweep strike-only plans (no rewind at all) over a fine angle
    /// and power grid and report the largest number of object balls any of them pocketed.
    /// </summary>
    public static void MeasureStrikeOnly(LevelData level, WindowReport report, double stepDeg = 0.5, int powers = 30)
    {
        var best = 0;
        var bestAngle = double.NaN;
        var bestPower = double.NaN;
        var cfg = new PhysicsConfig();
        for (var i = 0; i < powers; i++)
        {
            var power = cfg.MinPower + (cfg.PowerMax - cfg.MinPower) * i / (double)(powers - 1);
            for (var a = -180.0; a < 180.0 - 1e-9; a += stepDeg)
            {
                var res = Kernel.RunPlan(level, new Kernel.Plan
                {
                    AngleDeg = a,
                    Power = power,
                    Rewind = false,
                });
                if (res.PocketedObjects > best)
                {
                    best = res.PocketedObjects;
                    bestAngle = a;
                    bestPower = power;
                }
            }
        }

        report.StrikeOnlyBest = best;
        report.StrikeOnlyBestAngle = bestAngle;
        report.StrikeOnlyBestPower = bestPower;
        report.StrikeOnlyStepDeg = stepDeg;
        report.StrikeOnlyPowers = powers;
    }

    /// <summary>Convenience: run the whole measurement suite for one level.</summary>
    public static WindowReport MeasureAll(LevelData level, double baseAngle, SolveOptions opt, bool cueStart = true,
        bool strikeOnly = true)
    {
        var report = MeasureWindow(level, baseAngle, opt);
        if (report.Solvable)
        {
            MeasurePowerWindow(level, baseAngle, opt, report);
            if (cueStart) report.CueStartHalfWidth = MeasureCueStartWindow(level, baseAngle, opt);
        }

        if (strikeOnly) MeasureStrikeOnly(level, report);
        return report;
    }
}

/// <summary>Locked parameter values the authoring tool has to know about (never overridden by a level).</summary>
public static class PhysicsConfigDefaults
{
    public static readonly double PowerMax = new PhysicsConfig().PowerMax;
    public static readonly double MinPower = new PhysicsConfig().MinPower;
    public static readonly double SpeedPerPower = new PhysicsConfig().SpeedPerPower;
}
