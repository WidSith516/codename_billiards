using System;
using System.Collections.Generic;
using System.Globalization;
using Rewind.Sim;

namespace Rewind.Author;

/// <summary>The cue's recorded path (measured, not predicted) plus the speed it had there.</summary>
public sealed class Corridor
{
    public readonly List<Vec2> Points = new List<Vec2>();
    public readonly List<double> Arcs = new List<double>();
    public readonly List<double> Speeds = new List<double>();

    public double Length => Arcs.Count == 0 ? 0.0 : Arcs[Arcs.Count - 1];
    public bool Empty => Points.Count < 2;

    /// <summary>
    /// Measure the corridor the cue travels with this strike, on the table as it currently stands.
    /// This is "authoring against the kernel": the path is read out of the shipping simulation
    /// (the cue's own arc-length history), never from a separate predictor.
    /// </summary>
    public static Corridor Measure(LevelData level, double angle, double power, int maxSteps = 2400)
    {
        var probe = Kernel.Clone(level);
        probe.Budget = RewindBudget.Unlimited;
        var fwd = Kernel.Forward(probe, new Kernel.Plan
        {
            AngleDeg = angle,
            Power = power,
            Rewind = false,
            MaxForwardSteps = maxSteps,
        });

        var corridor = new Corridor();
        var history = fwd.World.Cue.History;
        var arc = 0.0;
        for (var i = 0; i < history.Count; i++)
        {
            if (i > 0) arc += (history[i].Position - history[i - 1].Position).Length;
            corridor.Points.Add(history[i].Position);
            corridor.Arcs.Add(arc);
            corridor.Speeds.Add(history[i].Velocity.Length);
        }

        return corridor;
    }

    public void At(double arc, out Vec2 point, out Vec2 dir, out double speed)
    {
        point = Points[0];
        dir = new Vec2(1, 0);
        speed = 0.0;
        if (Empty) return;
        if (arc <= 0.0)
        {
            point = Points[0];
            dir = (Points[1] - Points[0]).Normalized();
            speed = Speeds[0];
            return;
        }

        for (var i = 1; i < Points.Count; i++)
        {
            if (Arcs[i] + 1e-9 < arc) continue;
            var segLen = Arcs[i] - Arcs[i - 1];
            var t = segLen > 0.0 ? (arc - Arcs[i - 1]) / segLen : 0.0;
            t = Math.Max(0.0, Math.Min(1.0, t));
            point = Vec2.Lerp(Points[i - 1], Points[i], t);
            dir = (Points[i] - Points[i - 1]).Normalized();
            speed = Speeds[i];
            return;
        }

        var last = Points.Count - 1;
        point = Points[last];
        dir = (Points[last] - Points[last - 1]).Normalized();
        speed = Speeds[last];
    }

    public double MinDistanceTo(Vec2 p)
    {
        var best = double.PositiveInfinity;
        for (var i = 1; i < Points.Count; i++)
        {
            var d = Geo.DistanceToSegment(p, Points[i - 1], Points[i]);
            if (d < best) best = d;
        }

        return best;
    }

    /// <summary>The cue's recorded speed at a given arc (the last recorded speed beyond the end).</summary>
    public double SpeedAt(double arc)
    {
        if (Empty) return 0.0;
        for (var i = 1; i < Points.Count; i++)
        {
            if (Arcs[i] + 1e-9 < arc) continue;
            return Speeds[i];
        }

        return Speeds[Points.Count - 1];
    }
}

/// <summary>An object ball planted on the measured corridor at a chosen arc length.</summary>
public sealed class ForwardSpec
{
    public int PocketIndex;
    public double Arc;
    public double Margin = Limits.MarginFloorPx;

    public static ForwardSpec Of(int pocket, double arc, double margin = Limits.MarginFloorPx) =>
        new ForwardSpec { PocketIndex = pocket, Arc = arc, Margin = margin };
}

/// <summary>
/// A level recipe: the cue spawn + the aim, one "backdoor" ball behind the cue (potted by the
/// reverse pass the rewind performs) and any number of forward balls planted on the measured
/// corridor (potted by the strike itself). Every coordinate is derived from a pocket and a
/// contact distance - nothing is hand-placed.
/// </summary>
public sealed class Recipe
{
    public string Id = "level";
    public string Name = "level";
    public string Mechanism = "";

    public Vec2 CuePos;
    public double AimDeg;

    /// <summary>Backdoor ball: which pocket it is potted into, and how far behind the cue it sits.</summary>
    public int BackdoorPocket;
    public double BackdoorLBack;

    /// <summary>Optional explicit ball position (bypasses the backdoor solver).</summary>
    public bool BackdoorExplicit;
    public Vec2 BackdoorPos;

    public readonly List<ForwardSpec> Forward = new List<ForwardSpec>();

    public double[] Powers = SolveOptions.DefaultPowers;
    public double CorridorProbePower = 170.0;

    public Corridor Corridor;
    public readonly List<string> Rejects = new List<string>();

    public LevelData Build(bool keepRejects = true)
    {
        if (keepRejects) Rejects.Clear();
        var level = Kernel.BaseTable();
        level.Id = Id;
        level.Name = Name;
        level.AddCue(CuePos.X, CuePos.Y);
        var nextId = 1;

        Vec2 backdoorPos;
        if (BackdoorExplicit)
        {
            backdoorPos = BackdoorPos;
        }
        else
        {
            // The reverse pass travels in `frame`; the pivot is the cue's spawn, so the ball sits
            // sB = LBack IN FRONT of the pivot in that frame. Solve for the offset that aims it.
            var frame = Geo.Dir(AimDeg + 180.0);
            var cut = Geo.SolveCut(CuePos, frame, chordPocket(level, BackdoorPocket).Center, BackdoorLBack, Limits.MarginFloorPx);
            if (!cut.Ok)
            {
                Rejects.Add("backdoor cut unsolvable: " + cut.Note);
                return level;
            }

            backdoorPos = CuePos + frame * BackdoorLBack + Geo.Perp(frame) * cut.Offset;
        }

        level.AddBall(nextId++, BallType.Object, backdoorPos.X, backdoorPos.Y);
        if (!InBox(level, CuePos, 0.0)) Rejects.Add("cue spawn touches a rail");
        if (!InBox(level, backdoorPos, 0.0)) Rejects.Add("backdoor ball touches a rail");
        if ((backdoorPos - CuePos).Length < Geo.TwoR + 1.0) Rejects.Add("backdoor ball overlaps the cue at spawn");

        // the backdoor ball must actually be potted by the reverse pass: same departure line, same
        // capture rule as the kernel (a corner pocket may legitimately need a cushion first)
        if (!BackdoorExplicit)
        {
            var frame = Geo.Dir(AimDeg + 180.0);
            var potCut = Geo.SolveCut(CuePos, frame, chordPocket(level, BackdoorPocket).Center, BackdoorLBack, Limits.MarginFloorPx);
            if (potCut.Ok && !Geo.PotReachability(level, backdoorPos, potCut.Normal,
                    chordPocket(level, BackdoorPocket).Center, level.Pockets[BackdoorPocket].Radius, out var whyPot))
                Rejects.Add("backdoor pot unreachable: " + whyPot);
        }

        // the whole level (cue + backdoor ball) is on the table: measure the real corridor
        Corridor = Corridor.Measure(level, AimDeg, CorridorProbePower);
        if (Corridor.Empty) Rejects.Add("corridor is empty");
        else
        {
            if (Corridor.Length < Limits.CorridorArcFloorPx + 40.0)
                Rejects.Add($"corridor only {Geo.N(Corridor.Length)} px");
            var gap = Corridor.MinDistanceTo(backdoorPos);
            if (gap < Geo.TwoR + 1.0)
                Rejects.Add($"forward corridor passes {Geo.N(gap)} px from the backdoor ball (strike could reach it)");
        }

        foreach (var spec in Forward)
        {
            if (Corridor == null || Corridor.Empty) break;
            if (spec.Arc > Corridor.Length - 5.0)
            {
                if (keepRejects) Rejects.Add($"forward ball arc {Geo.N(spec.Arc)} beyond the corridor ({Geo.N(Corridor.Length)})");
                continue;
            }

            Corridor.At(spec.Arc, out var foot, out var legDir, out _);
            var pocket = chordPocket(level, spec.PocketIndex);
            var cut = Geo.SolveCut(foot, legDir, pocket.Center, 0.0, spec.Margin);
            if (!cut.Ok)
            {
                if (keepRejects) Rejects.Add($"forward ball at arc {Geo.N(spec.Arc)}: {cut.Note}");
                continue;
            }

            var pos = foot + Geo.Perp(legDir) * cut.Offset;
            if (!InBox(level, pos, 0.0))
            {
                if (keepRejects) Rejects.Add($"forward ball at arc {Geo.N(spec.Arc)} leaves the table");
                continue;
            }

            var clear = true;
            foreach (var b in level.Balls)
            {
                var gap = (new Vec2(b.X, b.Y) - pos).Length;
                if (gap < Geo.TwoR + 1.0)
                {
                    if (keepRejects) Rejects.Add($"forward ball at arc {Geo.N(spec.Arc)} overlaps a ball ({Geo.N(gap)} px)");
                    clear = false;
                }
            }

            if (!clear) continue;

            if (!Geo.PotReachability(level, pos, cut.Normal, pocket.Center, pocket.Radius, out var why))
            {
                if (keepRejects) Rejects.Add($"forward pot at arc {Geo.N(spec.Arc)} illegal: {why}");
                continue;
            }

            level.AddBall(nextId++, BallType.Object, pos.X, pos.Y);
            // the placed ball deflects the cue: re-measure so the NEXT planting uses the real corridor
            Corridor = Corridor.Measure(level, AimDeg, CorridorProbePower);
            if (Corridor.Empty)
            {
                if (keepRejects) Rejects.Add("corridor vanished after planting a ball");
                break;
            }
        }

        return level;
    }

    private static PocketDef chordPocket(LevelData level, int index) => level.Pockets[index];

    private static bool InBox(LevelData level, Vec2 p, double clearance)
    {
        return p.X >= level.TableLeft + level.BallRadius + clearance
               && p.X <= level.TableRight - level.BallRadius - clearance
               && p.Y >= level.TableTop + level.BallRadius + clearance
               && p.Y <= level.TableBottom - level.BallRadius - clearance;
    }

    public static double Normalize(double deg)
    {
        while (deg <= -180.0) deg += 360.0;
        while (deg > 180.0) deg -= 360.0;
        return deg;
    }
}

/// <summary>A built candidate: level + aim + the measurement that decides whether it ships.</summary>
public sealed class Candidate
{
    public Recipe Recipe;
    public LevelData Level;
    public double AimDeg;
    public WindowReport Window;
    public string Status = "unmeasured";

    public bool Accepted => Window != null && Window.Solvable
                            && Window.HalfWidthMarginDeg >= 2.0 - 1e-9
                            && Window.PowerWindow >= Limits.PowerWindowFloorPx - 1e-9
                            && (double.IsNaN(Window.CueStartHalfWidth) || Window.CueStartHalfWidth >= Limits.CueStartFloorPx - 1e-9)
                            && Window.StrikeOnlyBest < Window.Nominal.Result.ObjectCount
                            && Kernel.MinSpawnGap(Level) >= Geo.TwoR;

    public string Reject
    {
        get
        {
            if (Window == null) return "not measured";
            if (!Window.Solvable) return "no clearing solution at the nominal angle";
            if (Window.HalfWidthMarginDeg < 2.0) return $"angle window {Geo.N(Window.HalfWidthMarginDeg)} deg < 2 deg";
            if (Window.PowerWindow < Limits.PowerWindowFloorPx) return $"power window {Geo.N(Window.PowerWindow)} px";
            if (!double.IsNaN(Window.CueStartHalfWidth) && Window.CueStartHalfWidth < Limits.CueStartFloorPx)
                return $"cue-start window {Geo.N(Window.CueStartHalfWidth)} px";
            if (Window.StrikeOnlyBest >= Window.Nominal.Result.ObjectCount) return "clearable from the strike alone";
            if (Kernel.MinSpawnGap(Level) < Geo.TwoR) return "spawn overlap";
            return "accepted";
        }
    }
}
