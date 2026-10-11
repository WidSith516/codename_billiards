using System;
using System.Collections.Generic;
using System.Globalization;
using Rewind.Sim;

namespace Rewind.Author;

/// <summary>
/// One pocket capture observed by <see cref="Kernel.RunPlan"/>, with the tolerance-relevant numbers.
///
/// WHY THE MARGIN IS MEASURED THIS WAY (this is the tool's definition of "pocket margin"):
///   The kernel captures a ball on the FIRST interval in which its centre is strictly inside the
///   capture circle AND its velocity points at the pocket centre (<c>World.InPocketCapture</c>).
///   Therefore the penetration depth at the capture instant is ~0 for every capture and cannot
///   discriminate a good pot from a rim graze. What does discriminate them is HOW FAR THE
///   CAPTURE CIRCLE'S CENTRE IS FROM THE BALL'S INCOMING TRAJECTORY LINE:
///
///       dPerp  = perpendicular distance from the pocket centre to that line
///       Margin = PocketRadius - dPerp
///
///   Margin is exactly the lateral shift budget of the capture: shift the incoming line by less
///   than Margin and the ball is still captured; shift it by more and it misses. Margin = 24 px
///   means the line runs through the pocket centre (a dead-centre pot); Margin = 0.56 px (the
///   number the old levels shipped, tools/verify_solver/REPORT.md 5c) means the line grazes the
///   rim and a one-pixel change of the level data flips pot into miss.
///
///   The measurement is taken from the state at the START of the step in which the capture
///   happened (position + velocity), and <see cref="CleanApproach"/> records whether the ball's
///   displacement during that step had the same direction (i.e. no bounce inside that step).
/// </summary>
public sealed class Capture
{
    public int BallId;
    public string BallType = "object";
    public int Tick;

    /// <summary>Ball centre at the moment of capture.</summary>
    public Vec2 Position;

    /// <summary>Velocity direction used for the approach line (state at the start of the capture step).</summary>
    public Vec2 Velocity;
    public double Speed;

    public int PocketIndex = -1;
    public Vec2 PocketCenter;

    /// <summary>Perpendicular distance from the pocket centre to the ball's incoming line.</summary>
    public double DPerp;

    /// <summary>PocketRadius - DPerp: the lateral shift budget of this capture, px.</summary>
    public double Margin;

    /// <summary>PocketRadius - |ball - pocketCentre| at the capture (≈0 by construction; reported for honesty).</summary>
    public double Penetration;

    /// <summary>Arc length the ball travelled from its spawn to the capture.</summary>
    public double Travel;

    /// <summary>True when the ball moved in a straight line during the capture step (no bounce inside it).</summary>
    public bool CleanApproach;

    public double MarginDeg => Travel > 1e-9 ? Math.Atan2(Margin, Travel) * 180.0 / Math.PI : 0.0;
}

/// <summary>Everything a single (angle, power, rewind) plan did.</summary>
public sealed class PlayResult
{
    public bool Won;
    public int PocketedObjects;
    public int ObjectCount;

    public readonly List<Capture> Captures = new List<Capture>();

    /// <summary>Arc length of the cue's recorded path at the rewind release point, px (player-facing: where did I let go).</summary>
    public double ReleaseArc = double.NaN;

    /// <summary>Arc length at which R was pressed.</summary>
    public double PressArc = double.NaN;

    /// <summary>Arc length the rule crawled backwards during the rewind session (kernel's RewindDistanceUsed).</summary>
    public double CrawlDistance = double.NaN;

    /// <summary>The cue's velocity handed to it by the release (diagnostics: is it really -stored v?).</summary>
    public Vec2 ReleaseVelocity;

    public int Ticks;
    public bool RewindStarted;
    public bool RewindEndedAtStart;   // crawled all the way to the history start -> velocity forced to 0
    public bool AutoReleased;         // rewind distance budget exhausted -> kernel released for the player
    public bool ShotAccepted;
    public bool Frozen;
    public int CuePocketed;
    public int BallsPocketed;

    public string Note = string.Empty;

    public bool Cleared => ObjectCount > 0 && PocketedObjects >= ObjectCount;

    public double MinMargin
    {
        get
        {
            var m = double.PositiveInfinity;
            foreach (var c in Captures)
                if (c.BallType == "object" && c.Margin < m) m = c.Margin;
            return m;
        }
    }

    public List<Capture> ObjectCaptures()
    {
        var list = new List<Capture>();
        foreach (var c in Captures) if (c.BallType == "object") list.Add(c);
        return list;
    }

    public Capture CaptureOf(int ballId)
    {
        foreach (var c in Captures) if (c.BallId == ballId) return c;
        return null;
    }

    public string CaptureSummary()
    {
        var parts = new List<string>();
        foreach (var c in ObjectCaptures())
            parts.Add($"b{c.BallId}@p{c.PocketIndex} margin={N(c.Margin)} travel={N(c.Travel)}");
        return string.Join("; ", parts);
    }

    internal static string N(double v) => double.IsNaN(v) ? "nan" : v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Drives the shipping kernel exactly the way a player does: one shot, one rewind session
/// (press R, crawl the corridor, release), then settle. Nothing here re-implements physics - every
/// number comes out of <see cref="Rewind.Sim.World"/>, which is compiled from
/// <c>godot/src/sim/*.cs</c> verbatim (see AuthorTool.csproj).
/// </summary>
public static class Kernel
{
    public const int MaxSteps = 60000;

    /// <summary>How the player decides when to press R.</summary>
    public enum PressMode
    {
        /// <summary>Press once the whole shot has settled - the natural "walk the corridor back" pattern.</summary>
        AtRest,

        /// <summary>Press as soon as the cue's travelled arc reaches <see cref="Plan.PressArc"/>.</summary>
        AtArc,
    }

    public sealed class Plan
    {
        public double AngleDeg;
        public double Power;

        /// <summary>Where the player lets go: the cue's arc length at the release point, px.</summary>
        public double ReleaseArc;

        public PressMode Press = PressMode.AtRest;
        public double PressArc;

        /// <summary>False = strike only, no rewind at all (the "no level may clear from the strike alone" control).</summary>
        public bool Rewind = true;

        /// <summary>Optional per-run physics override (used by the portability sweep only).</summary>
        public PhysicsConfig ConfigOverride;

        /// <summary>Step budget for the forward phase (a corridor that never settles is capped, not hung).</summary>
        public int MaxForwardSteps = 2400;

        public Plan Clone() => (Plan)MemberwiseClone();
    }

    // ------------------------------------------------------------------ levels

    public static LevelData BaseTable()
    {
        var level = LevelData.EmptyTable();
        level.Id = "authoring";
        level.Name = "authoring";
        level.Balls.Clear();
        level.AddStandardPockets();
        level.Budget = new RewindBudget { MaxSegments = 1, MaxDistance = 0.0 };
        return level;
    }

    /// <summary>
    /// Deep copy of a level that does NOT re-validate (used while a level is still being assembled).
    /// Goes through the real fields, not through JSON, so the clone is exact and cheap.
    /// </summary>
    public static LevelData Clone(LevelData src)
    {
        var copy = new LevelData
        {
            Id = src.Id,
            Name = src.Name,
            TableLeft = src.TableLeft,
            TableRight = src.TableRight,
            TableTop = src.TableTop,
            TableBottom = src.TableBottom,
            BallRadius = src.BallRadius,
            Config = src.Config.Clone(),
            Budget = new RewindBudget { MaxSegments = src.Budget.MaxSegments, MaxDistance = src.Budget.MaxDistance },
        };
        foreach (var p in src.Pockets) copy.Pockets.Add(new PocketDef(p.Center, p.Radius));
        foreach (var b in src.Balls)
        {
            copy.Balls.Add(new BallSpawn
            {
                Id = b.Id,
                Type = b.Type,
                DeclaredType = b.DeclaredType,
                X = b.X,
                Y = b.Y,
                Vx = b.Vx,
                Vy = b.Vy,
                Radius = b.Radius,
                Mass = b.Mass,
            });
        }

        return copy;
    }

    // ------------------------------------------------------------------- play

    /// <summary>Sum of the cue's history segment lengths: the arc length it has actually travelled.</summary>
    public static double CueArc(World w)
    {
        var h = w.Cue.History;
        var arc = 0.0;
        for (var i = 1; i < h.Count; i++) arc += (h[i].Position - h[i - 1].Position).Length;
        return arc;
    }

    /// <summary>Cumulative arc length of the frozen rewind snapshot at every entry.</summary>
    public static double[] SnapshotArcs(World w)
    {
        var track = w.RewindTrack;
        if (track == null) return Array.Empty<double>();
        var arcs = new double[track.Length];
        for (var i = 1; i < track.Length; i++) arcs[i] = arcs[i - 1] + (track[i].Position - track[i - 1].Position).Length;
        return arcs;
    }

    public static bool AllAtRest(World w)
    {
        foreach (var b in w.Balls)
        {
            if (b.Pocketed) continue;
            if (b.Velocity.X != 0.0 || b.Velocity.Y != 0.0) return false;
        }

        return true;
    }

    public static int CountObjects(LevelData level)
    {
        var n = 0;
        foreach (var b in level.Balls) if (b.Type == BallType.Object) n++;
        return n;
    }

    /// <summary>
    /// The state after the forward (strike) phase, before any rewind: the world exactly as the
    /// player leaves it when they reach for R, plus the captures the strike itself produced.
    /// Cloning this state is what makes a release-arc sweep cheap: the forward phase runs once.
    /// </summary>
    public sealed class ForwardState
    {
        public World World;
        public PlayResult Strike = new PlayResult();
        public double PressArc;
        public int Steps;
    }

    /// <summary>Advance the world until the press condition holds (or the level freezes on a win).</summary>
    public static ForwardState Forward(LevelData level, Plan plan)
    {
        var effective = plan.ConfigOverride == null ? level : WithConfig(level, plan.ConfigOverride);
        var world = new World(effective);
        var tracker = new Tracker(world);
        var strike = new PlayResult { ObjectCount = CountObjects(effective) };

        var fwd = new ForwardState { World = world, Strike = strike };
        strike.ShotAccepted = world.Shot(plan.AngleDeg, plan.Power);
        if (!strike.ShotAccepted)
        {
            strike.Note = "shot refused";
            fwd.PressArc = 0.0;
            return fwd;
        }

        var steps = 0;
        var maxForward = Math.Max(1, plan.MaxForwardSteps);
        while (!world.Frozen && steps < maxForward)
        {
            if (plan.Rewind)
            {
                if (plan.Press == PressMode.AtArc && CueArc(world) >= plan.PressArc) break;
                if (plan.Press == PressMode.AtRest && AllAtRest(world)) break;
            }
            else if (AllAtRest(world)) break;

            tracker.Before(world);
            world.Step();
            tracker.After(world, strike);
            steps++;
        }

        strike.Captures.AddRange(tracker.Captures);
        strike.PocketedObjects = world.PocketedObjectCount;
        strike.Won = world.Won;
        fwd.PressArc = CueArc(world);
        fwd.Steps = steps;
        return fwd;
    }

    /// <summary>
    /// Run one plan and return everything measurable about it. Implemented as forward phase + one
    /// rewind session so that it is bit-identical to what <see cref="SweepReleases"/> does.
    /// </summary>
    public static PlayResult RunPlan(LevelData level, Plan plan)
    {
        var fwd = Forward(level, plan);
        if (!fwd.Strike.ShotAccepted) return fwd.Strike;
        if (!plan.Rewind || fwd.World.Frozen) return Finish(fwd, double.NaN, rewind: false);
        return Finish(fwd, plan.ReleaseArc);
    }

    /// <summary>
    /// Finish a plan from a forward state: press R, crawl the corridor back to
    /// <paramref name="releaseArc"/>, release, settle. <paramref name="releaseArc"/> may be
    /// double.NaN, in which case the rewind is released immediately where it stands.
    /// </summary>
    public static PlayResult Finish(ForwardState fwd, double releaseArc, bool rewind = true)
    {
        var world = fwd.World.Clone();
        var tracker = new Tracker(world);
        var result = new PlayResult { ObjectCount = fwd.Strike.ObjectCount };
        result.ShotAccepted = fwd.Strike.ShotAccepted;
        result.PressArc = fwd.PressArc;
        result.Captures.AddRange(fwd.Strike.Captures);

        var steps = 0;
        if (rewind && !world.Frozen && world.StartRewind())
        {
            result.RewindStarted = true;
            var arcs = SnapshotArcs(world);
            while (world.Rewinding && steps++ < MaxSteps)
            {
                var cursor = world.RewindCursor;
                var at = cursor >= 0 && cursor < arcs.Length ? arcs[cursor] : double.NaN;
                if (!double.IsNaN(releaseArc) && !double.IsNaN(at) && at <= releaseArc) break;
                tracker.Before(world);
                world.Step();
                tracker.After(world, result);
                if (!world.Rewinding)
                {
                    // The kernel ended the session itself: the history start was reached (velocity
                    // forced to 0) or the distance budget ran out, which World.StepRewind treats as a
                    // release with the stored velocity.
                    if (world.RewindCursor <= 0) result.RewindEndedAtStart = true;
                    else result.AutoReleased = true;
                }
            }

            var idx = Math.Max(world.RewindCursor, 0);
            result.ReleaseArc = idx < arcs.Length ? arcs[idx] : double.NaN;
            result.CrawlDistance = world.RewindDistanceUsed;
            world.EndRewind();
            result.ReleaseVelocity = world.Cue.Velocity;
        }

        while (!world.Frozen && steps++ < MaxSteps)
        {
            if (AllAtRest(world)) break;
            tracker.Before(world);
            world.Step();
            tracker.After(world, result);
        }

        result.Ticks = world.Tick;
        result.Won = world.Won;
        result.Frozen = world.Frozen;
        result.PocketedObjects = world.PocketedObjectCount;
        result.BallsPocketed = world.Stats.PocketCaptures;
        result.CuePocketed = world.Cue.Pocketed ? 1 : 0;
        if (world.AssertionFailure != null) result.Note += " ASSERTION:" + world.AssertionFailure;
        if (world.HasNonFiniteState()) result.Note += " NONFINITE";
        result.Captures.AddRange(tracker.Captures);
        return result;
    }

    /// <summary>Candidate release points of a forward state: every reachable history entry arc, px.</summary>
    public static double[] ReachableReleaseArcs(ForwardState fwd)
    {
        var probe = fwd.World.Clone();
        if (probe.Frozen || !probe.StartRewind()) return Array.Empty<double>();
        return SnapshotArcs(probe);
    }

    private static LevelData WithConfig(LevelData level, PhysicsConfig cfg)
    {
        var copy = Clone(level);
        copy.Config = cfg;
        copy.Validate();
        return copy;
    }

    /// <summary>
    /// Per-step capture tracker. It keeps every ball's previous position/velocity so a capture can
    /// be attributed to the straight trajectory that produced it, and measures the pocket margin
    /// from that trajectory. All captures (including rail bounces that happen inside a step) are
    /// flagged with <see cref="Capture.CleanApproach"/> so a margin is never silently quoted from a
    /// bent approach.
    /// </summary>
    public sealed class Tracker
    {
        public readonly List<Capture> Captures = new List<Capture>();

        private readonly int _n;
        private readonly Vec2[] _pos;
        private readonly Vec2[] _vel;
        private readonly Vec2[] _dir;
        private readonly double[] _travel;
        private readonly bool[] _pocketed;

        public Tracker(World world)
        {
            _n = world.Balls.Length;
            _pos = new Vec2[_n];
            _vel = new Vec2[_n];
            _dir = new Vec2[_n];
            _travel = new double[_n];
            _pocketed = new bool[_n];
            for (var i = 0; i < _n; i++)
            {
                _pos[i] = world.Balls[i].Position;
                _vel[i] = world.Balls[i].Velocity;
                _pocketed[i] = world.Balls[i].Pocketed;
            }
        }

        public void Before(World world)
        {
            for (var i = 0; i < _n; i++)
            {
                var b = world.Balls[i];
                _pos[i] = b.Position;
                _vel[i] = b.Velocity;
                _pocketed[i] = b.Pocketed;
            }
        }

        public void After(World world, PlayResult result)
        {
            for (var i = 0; i < _n; i++)
            {
                var b = world.Balls[i];
                var moved = b.Position - _pos[i];
                if (b.Pocketed && !_pocketed[i])
                {
                    var capture = new Capture
                    {
                        BallId = b.Id,
                        BallType = b.IsCue ? "cue" : "object",
                        Tick = world.Tick,
                        Position = b.Position,
                        Velocity = _vel[i],
                        Speed = _vel[i].Length,
                        Travel = _travel[i] + moved.Length,
                        CleanApproach = SameDirection(moved.Normalized(), _vel[i].Normalized()),
                    };

                    var best = double.PositiveInfinity;
                    for (var p = 0; p < world.Level.Pockets.Count; p++)
                    {
                        var pocket = world.Level.Pockets[p];
                        var toCenter = pocket.Center - b.Position;
                        var dist = toCenter.Length;
                        if (dist >= pocket.Radius) continue;
                        if (Vec2.Dot(_vel[i], toCenter) <= 0.0) continue;
                        if (dist < best)
                        {
                            best = dist;
                            capture.PocketIndex = p;
                            capture.PocketCenter = pocket.Center;
                            capture.Penetration = pocket.Radius - dist;
                        }
                    }

                    if (capture.PocketIndex >= 0)
                    {
                        var dir = _vel[i].LengthSquared > 0.0 ? _vel[i].Normalized() : Vec2.Zero;
                        capture.DPerp = Math.Abs(Vec2.Cross(capture.PocketCenter - _pos[i], dir));
                        capture.Margin = world.Level.Pockets[capture.PocketIndex].Radius - capture.DPerp;
                    }

                    Captures.Add(capture);
                }

                _travel[i] += moved.Length;
            }
        }

        private static bool SameDirection(Vec2 a, Vec2 b)
        {
            if (a.LengthSquared <= 0.0 || b.LengthSquared <= 0.0) return false;
            return Vec2.Dot(a, b) > 1.0 - 1e-12;
        }
    }

    // ------------------------------------------------------------- level stats

    /// <summary>Minimum centre distance between any two ball spawns (the "no spawn overlap" metric).</summary>
    public static double MinSpawnGap(LevelData level, out string pair)
    {
        var min = double.PositiveInfinity;
        pair = "-";
        for (var i = 0; i < level.Balls.Count; i++)
        {
            for (var j = i + 1; j < level.Balls.Count; j++)
            {
                var d = (new Vec2(level.Balls[i].X, level.Balls[i].Y) - new Vec2(level.Balls[j].X, level.Balls[j].Y)).Length;
                if (d < min)
                {
                    min = d;
                    pair = $"b{level.Balls[i].Id}-b{level.Balls[j].Id}";
                }
            }
        }

        return min;
    }

    public static double MinSpawnGap(LevelData level) => MinSpawnGap(level, out _);
}
