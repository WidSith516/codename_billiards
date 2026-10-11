using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Tests;

using Rewind.Game;
using Rewind.Sim;

/// <summary>
/// L01-Lnn: tolerance assertions for the levels produced by tools/author.
///
/// WHAT THIS SUITE GUARDS (the six hard constraints of the level-authoring task):
///   1. no spawn overlap: every pair of balls starts at >= 2R = 24.4 px;
///   2. pocket margin >= 8 px on every pot of every shipped witness (margin is defined in
///      <see cref="PocketMargin"/>: PocketRadius minus the perpendicular distance from the pocket
///      centre to the ball's incoming trajectory line - i.e. the lateral shift the pot survives);
///   3. clearing angle window >= +-2 deg, verified IN THE ENGINE by replaying the edge witnesses the
///      authoring tool measured (the rewind is re-chosen per angle there, so the witness at the edge
///      is a genuine re-aimed solution, not the nominal plan held fixed);
///   4. the rewind is load-bearing: the nominal strike without any rewind must NOT clear;
///   5. the release point sits at least 100 px of cue travel along the recorded path (no 1-5 step
///      nudge: the old levels released a cue that had travelled 10 px);
///   6. portability: the same witness clears under different legal substrate settings (sub-step
///      travel budget 0.5 px and 2.0 px, and a 1/240 s step), so a level is not tuned to one
///      simulator's granularity.
///
/// The suite reads the levels' own "authoring" block for the witness plans, but it never reads the
/// level's own idea of the bar: the thresholds below are the suite's own constants.
/// </summary>
public static class LevelToleranceTests
{
    public const double BallRadius = 12.2;
    public const double TwoR = 2.0 * BallRadius;
    public const double PocketRadius = 24.0;
    public const double MarginFloorPx = 8.0;
    public const double CorridorArcFloorPx = 100.0;
    public const double AngleFloorDeg = 2.0;
    public const int ForwardCapSteps = 4000;

    public static void L01_AuthoredLevelTolerance(TestRunner t)
    {
        t.Section("L01 - authored levels: spawn gap, pocket margin, +-2 deg window, load-bearing rewind");
        var ids = AuthoredLevelIds();
        t.CheckAtLeast("authored levels are discovered on disk", ids.Count, 3, string.Join(",", ids));

        foreach (var id in ids)
        {
            var text = ReadLevelText(id);
            var root = Json.Parse(text);
            var level = LevelData.FromJson(root);
            var authoring = root.Get("authoring");
            var measured = authoring.Get("measured");
            var limits = authoring.Get("limits");

            // ---- 1. spawn gap
            var minGap = MinSpawnGap(level, out var pair);
            t.CheckAtLeast($"{id}: no spawn overlap (every pair >= 2R)", minGap, TwoR - 1e-9,
                $"min {TestRunner.N(minGap)} px between {pair}");

            // ---- authoring bookkeeping: the file must not claim a bar below the suite's own
            var marginBar = limits.Num("marginFloorPx", 0.0);
            var arcBar = limits.Num("corridorArcFloorPx", 0.0);
            var angleBar = limits.Num("angleHalfWidthDeg", 0.0);
            t.CheckAtLeast($"{id}: declared margin floor is not below the task floor", marginBar, MarginFloorPx - 1e-9,
                TestRunner.N(marginBar) + " px");
            t.CheckAtLeast($"{id}: declared corridor floor is not below the task floor", arcBar, CorridorArcFloorPx - 1e-9,
                TestRunner.N(arcBar) + " px");
            t.CheckAtLeast($"{id}: declared angle floor is not below the task floor", angleBar, AngleFloorDeg - 1e-9,
                TestRunner.N(angleBar) + " deg");

            // ---- 3. the measured window, and the edge witnesses that prove it
            var marginWindow = measured.Num("angleHalfWidthMarginDeg", 0.0);
            t.CheckAtLeast($"{id}: measured clearing window >= +-2 deg", marginWindow, AngleFloorDeg - 1e-9,
                "+-" + TestRunner.N(marginWindow) + " deg");

            var witnesses = authoring.Get("witnesses");
            var objectCount = (int)measured.Num("objectCount", 0);
            var sawEdge = false;
            var sawNominal = false;
            foreach (var w in witnesses.Items)
            {
                var label = w.Text("label", "?");
                var angle = w.Num("angle", double.NaN);
                var power = w.Num("power", double.NaN);
                var releaseArc = w.Num("releaseArc", double.NaN);
                var nominal = level.Balls[0].Type == BallType.Cue ? FirstCueAngle(root) : double.NaN;

                var replay = Replay(level, angle, power, releaseArc, null, out var fault);
                t.Check($"{id}/{label}: witness clears the level", replay.Cleared,
                    $"{replay.Pocketed}/{objectCount} pocketed, releaseArc={TestRunner.N(replay.ReleaseArc)}" +
                    (fault.Length > 0 ? ", " + fault : ""));
                t.CheckAtLeast($"{id}/{label}: every object pot keeps >= 8 px pocket margin",
                    replay.Cleared ? replay.MinMargin : double.NegativeInfinity, MarginFloorPx - 1e-9,
                    TestRunner.N(replay.MinMargin) + " px");
                t.Check($"{id}/{label}: every capture is a straight-line capture (measured through the incoming line)",
                    replay.AllClean, replay.CaptureSummary());
                t.CheckAtLeast($"{id}/{label}: release point is >= 100 px along the recorded path",
                    replay.ReleaseArc, CorridorArcFloorPx - 1e-9, TestRunner.N(replay.ReleaseArc) + " px");

                if (label == "nominal")
                {
                    sawNominal = true;
                    // ---- 4. the rewind is load-bearing for this very strike
                    var strikeOnly = Replay(level, angle, power, double.NaN, null, out _, rewind: false);
                    t.Check($"{id}: the nominal strike alone does NOT clear (rewind is load-bearing)",
                        !strikeOnly.Cleared,
                        $"strike-only pocketed {strikeOnly.Pocketed}/{objectCount}");

                    // ---- 6. portability: same witness, different legal substrate settings
                    var portBudget = Replay(level, angle, power, releaseArc, cfg =>
                    {
                        cfg.SubStepTravelBudget = 0.5;
                        cfg.SubStepRelativeTravelBudget = 0.5;
                    }, out var faultA);
                    t.Check($"{id}: clears at a 0.5 px sub-step budget", portBudget.Cleared && portBudget.MinMargin >= MarginFloorPx - 1e-9,
                        $"{portBudget.Pocketed}/{objectCount}, worst margin {TestRunner.N(portBudget.MinMargin)} px" +
                        (faultA.Length > 0 ? ", " + faultA : ""));

                    var portCoarse = Replay(level, angle, power, releaseArc, cfg =>
                    {
                        cfg.SubStepTravelBudget = 2.0;
                        cfg.SubStepRelativeTravelBudget = 2.0;
                    }, out var faultB);
                    t.Check($"{id}: clears at a 2.0 px sub-step budget", portCoarse.Cleared && portCoarse.MinMargin >= MarginFloorPx - 1e-9,
                        $"{portCoarse.Pocketed}/{objectCount}, worst margin {TestRunner.N(portCoarse.MinMargin)} px" +
                        (faultB.Length > 0 ? ", " + faultB : ""));

                    var portDt = Replay(level, angle, power, releaseArc, cfg => cfg.SimulationStep = 1.0 / 240.0, out var faultC,
                        recentreRelease: true);
                    t.Check($"{id}: clears at dt=1/240 (a different granularity, release re-quantised)",
                        portDt.Cleared && portDt.MinMargin >= MarginFloorPx - 1e-9,
                        $"{portDt.Pocketed}/{objectCount}, worst margin {TestRunner.N(portDt.MinMargin)} px" +
                        (faultC.Length > 0 ? ", " + faultC : ""));
                }

                if (label.StartsWith("edge"))
                {
                    sawEdge = true;
                    t.CheckAtLeast($"{id}/{label}: the edge witness really is at or beyond +-2 deg",
                        Math.Abs(RecipeNormalize(angle - FirstCueAngle(root))), AngleFloorDeg - 1e-9,
                        TestRunner.N(Math.Abs(RecipeNormalize(angle - FirstCueAngle(root)))) + " deg from the nominal aim");
                }
            }

            t.Check($"{id}: a nominal witness is present", sawNominal);
            t.CheckAtLeast($"{id}: at least the two window-edge witnesses are present", CountEdges(witnesses), 2);
            t.Check($"{id}: the edge witnesses are recorded", sawEdge);
        }
    }

    private static int CountEdges(JsonValue witnesses)
    {
        var n = 0;
        if (witnesses == null) return 0;
        foreach (var w in witnesses.Items)
        {
            if (w.Text("label", "").StartsWith("edge")) n++;
        }

        return n;
    }

    private static double FirstCueAngle(JsonValue root)
    {
        // the nominal aim is not stored separately; the nominal witness IS the aim
        var witnesses = root.Get("authoring")?.Get("witnesses");
        if (witnesses == null) return double.NaN;
        foreach (var w in witnesses.Items)
        {
            if (w.Text("label", "") == "nominal") return w.Num("angle", double.NaN);
        }

        return double.NaN;
    }

    private static double RecipeNormalize(double deg)
    {
        while (deg <= -180.0) deg += 360.0;
        while (deg > 180.0) deg -= 360.0;
        return deg;
    }

    // ------------------------------------------------------------------ replay

    public sealed class ReplayResult
    {
        public bool Cleared;
        public int Pocketed;
        public int ObjectCount;
        public double ReleaseArc = double.NaN;
        public double Crawl = double.NaN;
        public double MinMargin = double.PositiveInfinity;
        public bool AllClean = true;
        public readonly List<string> Captures = new List<string>();

        public string CaptureSummary() => Captures.Count == 0 ? "no object capture" : string.Join("; ", Captures);
    }

    /// <summary>
    /// Pocket margin, as the suite defines it: the perpendicular distance from the pocket centre to
    /// the ball's incoming trajectory line, subtracted from the pocket radius. The kernel captures a
    /// ball on its first entry into the capture circle, so the penetration depth at that instant is
    /// ~0 for every capture; the LINE margin is what says whether the pot is aimed or is a rim graze
    /// (the old levels shipped 0.56-1.72 px here, see tools/verify_solver/REPORT.md 5c).
    /// </summary>
    private static ReplayResult Replay(LevelData level, double angle, double power, double releaseArc,
        Action<PhysicsConfig> configure, out string fault, bool rewind = true, bool recentreRelease = false)
    {
        fault = string.Empty;
        var result = new ReplayResult { ObjectCount = CountObjects(level) };
        var cfg = level.Config.Clone();
        configure?.Invoke(cfg);
        var effective = CloneLevel(level, cfg);
        var world = new World(effective);
        if (!world.Shot(angle, power))
        {
            fault = "shot refused";
            return result;
        }

        var tracker = new MarginTracker(world);
        var steps = 0;
        while (!world.Frozen && steps < ForwardCapSteps)
        {
            if (AllAtRest(world)) break;
            tracker.Before(world);
            world.Step();
            tracker.After(world, result);
            steps++;
        }

        if (rewind && !world.Frozen && world.StartRewind())
        {
            var arcs = SnapshotArcs(world);
            var target = releaseArc;
            if (recentreRelease && !double.IsNaN(target))
            {
                var best = double.PositiveInfinity;
                for (var i = 0; i < arcs.Length; i++)
                {
                    var d = Math.Abs(arcs[i] - target);
                    if (d < best)
                    {
                        best = d;
                        target = arcs[i];
                    }
                }
            }

            while (world.Rewinding && steps < ForwardCapSteps * 4)
            {
                var cursor = world.RewindCursor;
                var at = cursor >= 0 && cursor < arcs.Length ? arcs[cursor] : double.NaN;
                if (!double.IsNaN(at) && !double.IsNaN(target) && at <= target) break;
                tracker.Before(world);
                world.Step();
                tracker.After(world, result);
                steps++;
            }

            var idx = Math.Max(world.RewindCursor, 0);
            result.ReleaseArc = idx < arcs.Length ? arcs[idx] : double.NaN;
            result.Crawl = world.RewindDistanceUsed;
            world.EndRewind();
        }

        while (!world.Frozen && steps < ForwardCapSteps * 4)
        {
            if (AllAtRest(world)) break;
            tracker.Before(world);
            world.Step();
            tracker.After(world, result);
            steps++;
        }

        result.Pocketed = world.PocketedObjectCount;
        result.Cleared = result.Pocketed >= result.ObjectCount && result.ObjectCount > 0;
        result.MinMargin = tracker.MinMargin;
        result.AllClean = tracker.AllClean;
        result.Captures.AddRange(tracker.CaptureText);
        if (world.AssertionFailure != null) fault += " ASSERTION:" + world.AssertionFailure;
        if (world.HasNonFiniteState()) fault += " NONFINITE";
        return result;
    }

    private static LevelData CloneLevel(LevelData src, PhysicsConfig cfg)
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
            Config = cfg,
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

    private static int CountObjects(LevelData level)
    {
        var n = 0;
        foreach (var b in level.Balls)
        {
            if (b.Type == BallType.Object) n++;
        }

        return n;
    }

    private static bool AllAtRest(World world)
    {
        foreach (var b in world.Balls)
        {
            if (b.Pocketed) continue;
            if (b.Velocity.X != 0.0 || b.Velocity.Y != 0.0) return false;
        }

        return true;
    }

    private static double[] SnapshotArcs(World world)
    {
        var track = world.RewindTrack;
        if (track == null) return Array.Empty<double>();
        var arcs = new double[track.Length];
        for (var i = 1; i < track.Length; i++) arcs[i] = arcs[i - 1] + (track[i].Position - track[i - 1].Position).Length;
        return arcs;
    }

    /// <summary>Minimal per-step capture tracker (same definition of margin as tools/author).</summary>
    private sealed class MarginTracker
    {
        public double MinMargin = double.PositiveInfinity;
        public bool AllClean = true;
        public readonly List<string> CaptureText = new List<string>();

        private readonly int _n;
        private readonly Vec2[] _pos;
        private readonly Vec2[] _vel;
        private readonly bool[] _pocketed;
        private readonly double[] _travel;

        public MarginTracker(World world)
        {
            _n = world.Balls.Length;
            _pos = new Vec2[_n];
            _vel = new Vec2[_n];
            _pocketed = new bool[_n];
            _travel = new double[_n];
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

        public void After(World world, ReplayResult result)
        {
            for (var i = 0; i < _n; i++)
            {
                var b = world.Balls[i];
                var moved = b.Position - _pos[i];
                if (b.Pocketed && !_pocketed[i] && !b.IsCue)
                {
                    var clean = SameDirection(moved.Normalized(), _vel[i].Normalized());
                    var best = double.PositiveInfinity;
                    var pocketIndex = -1;
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
                            pocketIndex = p;
                        }
                    }

                    var margin = double.NaN;
                    if (pocketIndex >= 0)
                    {
                        var pocket = world.Level.Pockets[pocketIndex];
                        var dir = _vel[i].LengthSquared > 0.0 ? _vel[i].Normalized() : Vec2.Zero;
                        var dPerp = Math.Abs(Vec2.Cross(pocket.Center - _pos[i], dir));
                        margin = pocket.Radius - dPerp;
                    }

                    if (margin < MinMargin) MinMargin = margin;
                    if (!clean) AllClean = false;
                    CaptureText.Add($"b{b.Id}->p{pocketIndex} margin={TestRunner.N(margin)} travel={TestRunner.N(_travel[i])}" +
                                    (clean ? "" : " (through a bounce)"));
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

    // ------------------------------------------------------------------- files

    private static bool _scanned;
    private static readonly List<string> Authored = new List<string>();

    private static List<string> AuthoredLevelIds()
    {
        if (_scanned) return Authored;
        _scanned = true;
        foreach (var id in LevelLoader.ListLevelIds())
        {
            var path = $"{LevelLoader.LevelDirectory}/{id}.json";
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) continue;
            var text = file.GetAsText();
            if (text.Contains("\"authoring\"")) Authored.Add(id);
        }

        return Authored;
    }

    private static string ReadLevelText(string id)
    {
        using var file = FileAccess.Open($"{LevelLoader.LevelDirectory}/{id}.json", FileAccess.ModeFlags.Read);
        return file == null ? string.Empty : file.GetAsText();
    }

    private static double MinSpawnGap(LevelData level, out string pair)
    {
        var min = double.PositiveInfinity;
        pair = "-";
        for (var i = 0; i < level.Balls.Count; i++)
        {
            for (var j = i + 1; j < level.Balls.Count; j++)
            {
                var d = Math.Sqrt(Math.Pow(level.Balls[i].X - level.Balls[j].X, 2)
                                  + Math.Pow(level.Balls[i].Y - level.Balls[j].Y, 2));
                if (d < min)
                {
                    min = d;
                    pair = $"b{level.Balls[i].Id}-b{level.Balls[j].Id}";
                }
            }
        }

        return min;
    }
}
