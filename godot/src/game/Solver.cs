using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// A small greedy planner used ONLY to produce a genuine win for the M2 evidence capture: the win
/// frame should be one the kernel actually awarded after real strokes, not a painted banner.
///
/// It is not the M4 solvability solver and makes no completeness claim. It searches a coarse grid of
/// (angle, power) around the geometric pot line for each remaining ball, simulates each candidate on
/// a kernel clone, and scores the outcome by (balls potted, then distance from the cue's resting
/// place to its pre-stroke place). Between strokes the cue is assumed to return to the start of its
/// recorded path via a full rewind - which is what the kernel does when a rewind runs to the start
/// of the track - so the search deliberately does NOT model release points.
///
/// The plan is verified stroke by stroke on clones before anything touches the live world; the live
/// run then executes the same strokes and the same rewinds, and the kernel decides the outcome.
/// </summary>
public sealed class Solver
{
    public sealed class PlannedShot
    {
        public double Angle;
        public double Power;
        public int Pocketed;
        public double TimeSeconds;
        public string Note = "";
    }

    private readonly World _world;

    public Solver(World world)
    {
        _world = world;
    }

    /// <summary>Plan a sequence of strokes that clears the table, or an empty list.</summary>
    public List<PlannedShot> FindWinSequence(double timeBudgetSeconds)
    {
        var plan = new List<PlannedShot>();
        var t0 = Time.GetTicksMsec();
        var probe = _world.Clone();
        var maxStrokes = 8;

        for (var stroke = 0; stroke < maxStrokes; stroke++)
        {
            if (probe.Won) break;
            if ((Time.GetTicksMsec() - t0) / 1000.0 > timeBudgetSeconds) break;

            var best = SearchBestShot(probe, plan.Count);
            if (best == null)
            {
                GD.Print($"[solver] no candidate advanced the state at stroke {plan.Count + 1}");
                break;
            }

            plan.Add(best);
            GD.Print($"[solver] stroke {plan.Count}: angle={best.Angle:F3} power={best.Power:F1} " +
                     $"pocketedNow={best.Pocketed} total={probe.PocketedObjectCount}/{probe.ObjectBallCount} " +
                     $"t={best.TimeSeconds:F2}s  {best.Note}");

            // Model the rewind the live run will perform: the cue comes back to the path start.
            if (!probe.Won)
            {
                if (!probe.StartRewind()) break;
                var guard = 0;
                while (probe.Rewinding && guard++ < 60000) probe.Step();
            }
        }

        GD.Print($"[solver] plan strokes={plan.Count} won={probe.Won} " +
                 $"pocketed={probe.PocketedObjectCount}/{probe.ObjectBallCount} " +
                 $"elapsed={(Time.GetTicksMsec() - t0) / 1000.0:F2}s");
        return probe.Won ? plan : new List<PlannedShot>();
    }

    private PlannedShot SearchBestShot(World state, int strokeIndex)
    {
        var best = (PlannedShot)null;
        var bestScore = double.NegativeInfinity;

        // Candidate targets: each remaining object ball, aimed at each pocket.
        foreach (var target in state.Balls)
        {
            if (target.IsCue || target.Pocketed) continue;
            foreach (var pocket in state.Level.Pockets)
            {
                var aim = AimFor(state, target, pocket);
                if (!aim.HasValue) continue;
                var baseAngle = aim.Value;

                // Coarse sweep around the geometric line: the kernel's restitution and the rails mean
                // the geometric answer is a starting point, not the answer.
                for (var da = -8.0; da <= 8.01; da += 2.0)
                {
                    foreach (var power in new[] { 60.0, 110.0, 170.0 })
                    {
                        var angle = baseAngle + da;
                        var score = Evaluate(state, target.Id, angle, power, strokeIndex, out var shot);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = shot;
                        }
                    }
                }
            }
        }

        return best;
    }

    private double Evaluate(World state, int targetId, double angle, double power, int strokeIndex, out PlannedShot shot)
    {
        shot = null;
        var clone = state.Clone();
        if (!clone.Shot(angle, power)) return double.NegativeInfinity;

        var startCue = clone.Cue.Position;
        var guard = 0;
        while (!clone.Won && !clone.AtRest() && guard++ < 8000) clone.Step();

        var potted = clone.PocketedObjectCount - state.PocketedObjectCount;
        var cueDrift = (clone.Cue.Position - startCue).Length;

        // Score: potting dominates; then prefer shots that leave the cue near its strike position
        // (the cue is about to be rewound back there anyway); then shorter strokes (less budget).
        var score = potted * 1000.0 - cueDrift * 0.5 - power * 0.05;
        if (clone.Won) score += 100000.0;

        shot = new PlannedShot
        {
            Angle = angle,
            Power = power,
            Pocketed = clone.PocketedObjectCount,
            TimeSeconds = clone.Time,
            Note = $"target={targetId} potted={potted} cueDrift={cueDrift:F0}px",
        };

        return score;
    }

    /// <summary>
    /// Ghost-ball aim: the cue must arrive at the point diametrically opposite the pocket so that
    /// the contact normal points from the target ball's centre at the pocket.
    /// </summary>
    private static double? AimFor(World state, Ball target, PocketDef pocket)
    {
        var cue = state.Cue;
        var toPocket = pocket.Center - target.Position;
        if (toPocket.LengthSquared < 1e-9) return null;
        var n = toPocket.Normalized();

        // Contact happens when |cue - target| == 2r; aim at target - n*2r.
        var contact = target.Position - n * (2.0 * state.Config.BallRadius);
        var fromCue = contact - cue.Position;
        if (fromCue.LengthSquared < 1.0) return null;

        // Refuse shots whose corridor to the contact point is blocked by another ball.
        foreach (var other in state.Balls)
        {
            if (other.IsCue || other.Pocketed || ReferenceEquals(other, target)) continue;
            if (SegmentCircleHit(cue.Position, contact, other.Position, 2.0 * state.Config.BallRadius)) return null;
        }

        return Math.Atan2(fromCue.Y, fromCue.X) * 180.0 / Math.PI;
    }

    private static bool SegmentCircleHit(Vec2 a, Vec2 b, Vec2 c, double radius)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared;
        if (len2 <= 1e-9) return (c - a).Length < radius;
        var t = Math.Clamp(Vec2.Dot(c - a, ab) / len2, 0.0, 1.0);
        var closest = a + ab * t;
        return (c - closest).Length < radius;
    }
}
