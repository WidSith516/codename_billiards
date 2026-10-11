using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Tests;

using Rewind.Game;
using Rewind.Sim;

/// <summary>Shared fixtures for the headless kernel gate.</summary>
public static class TestSupport
{
    public const double Dt = 1.0 / 120.0;

    /// <summary>Standard table box, six pockets, no balls yet.</summary>
    public static LevelData Table(
        double left = 65.0, double top = 63.0, double right = 975.0, double bottom = 497.0,
        bool pockets = true, Action<PhysicsConfig> configure = null)
    {
        var cfg = new PhysicsConfig
        {
            TableLeft = left,
            TableTop = top,
            TableRight = right,
            TableBottom = bottom,
        };
        configure?.Invoke(cfg);
        var level = LevelData.EmptyTable(cfg);
        level.Id = "test";
        level.Name = "test";
        level.TableLeft = left;
        level.TableTop = top;
        level.TableRight = right;
        level.TableBottom = bottom;
        level.Balls.Clear();
        if (pockets) level.AddStandardPockets();
        return level;
    }

    /// <summary>A table far larger than any shot, so friction can be measured without rails.</summary>
    public static LevelData BigTable(double speedCap = 2000.0)
    {
        return Table(0.0, 0.0, 8000.0, 4000.0, pockets: false, configure: c => c.SpeedCap = speedCap);
    }

    public static World Rack()
    {
        return new World(LevelLoader.LoadById(LevelIds.H5Rack));
    }

    /// <summary>Minimum distance from a point to a history polyline.</summary>
    public static double DistanceToTrack(Vec2 p, HistorySample[] track)
    {
        if (track == null || track.Length == 0) return double.PositiveInfinity;
        var best = double.PositiveInfinity;
        for (var i = 1; i < track.Length; i++)
        {
            var a = track[i - 1].Position;
            var b = track[i].Position;
            var ab = b - a;
            var len2 = ab.LengthSquared;
            double t = 0.0;
            if (len2 > 0.0)
            {
                t = Vec2.Dot(p - a, ab) / len2;
                if (t < 0.0) t = 0.0;
                if (t > 1.0) t = 1.0;
            }

            var d = (a + ab * t - p).Length;
            if (d < best) best = d;
        }

        return best;
    }

    /// <summary>Run steps while watching the global invariants.</summary>
    public static RunWatch Watch(World world, int steps, RunWatch into = null)
    {
        var watch = into ?? new RunWatch();
        for (var i = 0; i < steps; i++)
        {
            world.Step();
            watch.Sample(world);
            if (world.Frozen) break;
        }

        return watch;
    }

    /// <summary>Run until the predicate holds; returns the number of steps taken (-1 = timeout).</summary>
    public static int RunUntil(World world, Func<bool> predicate, int maxSteps)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (predicate()) return i;
            world.Step();
        }

        return predicate() ? maxSteps : -1;
    }

    /// <summary>Replay a recorded input sequence tick by tick.</summary>
    public static void Replay(World world, IReadOnlyList<InputCommand> events, int ticks)
    {
        var index = 0;
        while (world.Tick < ticks)
        {
            while (index < events.Count && events[index].Tick == world.Tick)
            {
                var e = events[index++];
                switch (e.Kind)
                {
                    case InputKind.Shot:
                        world.Shot(e.A, e.B);
                        break;
                    case InputKind.RewindStart:
                        world.StartRewind();
                        break;
                    case InputKind.RewindEnd:
                        world.EndRewind();
                        break;
                }
            }

            world.Step();
        }
    }

    /// <summary>
    /// Chord-based sub-step requirement: how many intervals per physics step are needed so that
    /// a collision with the given impact parameter cannot be stepped over. This is the quantity
    /// the integrator's travel budget is compared against.
    /// </summary>
    public static int RequiredIntervalsForChord(double relativeSpeed, double dt, double impactParameter, double sumRadius)
    {
        var chordSq = 4.0 * sumRadius * sumRadius - impactParameter * impactParameter;
        if (chordSq <= 0.0) return int.MaxValue; // never touches
        var chord = 2.0 * Math.Sqrt(chordSq);
        return (int)Math.Ceiling(relativeSpeed * dt / chord);
    }

    /// <summary>Largest impact parameter still guaranteed to be detected for a relative travel budget.</summary>
    public static double MaxDetectableImpactParameter(double relativeTravelBudget, double sumRadius)
    {
        var half = relativeTravelBudget * 0.5;
        var v = sumRadius * sumRadius - half * half;
        return v <= 0.0 ? 0.0 : Math.Sqrt(v);
    }
}

/// <summary>Invariant sampler used while running a world.</summary>
public sealed class RunWatch
{
    public double MaxSpeed;
    public int MaxSpeedBall = -1;
    public int MaxSpeedTick = -1;
    public double MaxCueSpeed;
    public bool NonFinite;
    public int NonFiniteTick = -1;
    public int OutOfBounds;
    public double MaxHistory;
    public string AssertionFailure;

    public void Sample(World world)
    {
        foreach (var b in world.Balls)
        {
            if (!b.Position.IsFinite || !b.Velocity.IsFinite)
            {
                NonFinite = true;
                if (NonFiniteTick < 0) NonFiniteTick = world.Tick;
                continue;
            }

            if (b.Pocketed) continue;
            var s = b.Speed;
            if (s > MaxSpeed)
            {
                MaxSpeed = s;
                MaxSpeedBall = b.Id;
                MaxSpeedTick = world.Tick;
            }

            if (b.IsCue && s > MaxCueSpeed) MaxCueSpeed = s;

            const double slop = 1e-6;
            if (b.Position.X < world.Level.TableLeft + b.Radius - slop
                || b.Position.X > world.Level.TableRight - b.Radius + slop
                || b.Position.Y < world.Level.TableTop + b.Radius - slop
                || b.Position.Y > world.Level.TableBottom - b.Radius + slop)
            {
                OutOfBounds++;
            }
        }

        if (world.Cue.History.Count > MaxHistory) MaxHistory = world.Cue.History.Count;
        if (world.AssertionFailure != null && AssertionFailure == null) AssertionFailure = world.AssertionFailure;
    }
}
