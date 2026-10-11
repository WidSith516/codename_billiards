using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Rewind.Sim;

namespace Rewind.Author;

/// <summary>
/// Command line entry point of the tolerance-aware level authoring tool.
///
///   probe       measure ONE hand-given geometry and print every number
///   scan        search the recipe grid, rank candidates, write nothing
///   emit        build + measure the shipping level set and write godot/levels/*.json
///   verify      re-load the shipped level files from disk and re-measure them
///   verify-cut  validate the analytic tolerance law against the kernel
///   full        emit, then verify
/// </summary>
public static class Program
{
    private static readonly Dictionary<string, string> Opt = new Dictionary<string, string>();

    public static int Main(string[] argv)
    {
        foreach (var a in argv)
        {
            if (!a.StartsWith("--")) continue;
            var body = a.Substring(2);
            var eq = body.IndexOf('=');
            if (eq < 0) Opt[body] = "1";
            else Opt[body.Substring(0, eq)] = body.Substring(eq + 1);
        }

        var cmd = argv.Length > 0 && !argv[0].StartsWith("--") ? argv[0] : "verify";
        var sw = Stopwatch.StartNew();
        var rc = cmd switch
        {
            "probe" => Probe(),
            "strike" => StrikeProbe(),
            "verify-cut" => VerifyCut(),
            "scan" => Scan(),
            "emit" => Emit(),
            "verify" => Verify(),
            "full" => Full(),
            _ => Unknown(cmd),
        };
        Console.WriteLine($"# {cmd} finished in {sw.Elapsed.TotalSeconds:0.0} s, rc={rc}");
        return rc;
    }

    private static int Unknown(string cmd)
    {
        Console.WriteLine("unknown command: " + cmd);
        return 2;
    }

    // ------------------------------------------------------------------ probe

    private static Recipe RecipeFromOptions()
    {
        var r = new Recipe
        {
            Id = Str("id", "probe"),
            Name = Str("name", "probe"),
            CuePos = new Vec2(Num("cuex", 520.0), Num("cuey", 300.0)),
            AimDeg = Num("aim", -90.0),
            BackdoorPocket = Int("pocket", 4),
            BackdoorLBack = Num("lback", 35.0),
            CorridorProbePower = Num("probePower", 170.0),
        };
        var fwd = Str("forward", "");
        if (fwd.Length > 0)
        {
            foreach (var part in fwd.Split(';'))
            {
                var f = part.Split(',');
                r.Forward.Add(ForwardSpec.Of(int.Parse(f[0], CultureInfo.InvariantCulture),
                    double.Parse(f[1], CultureInfo.InvariantCulture)));
            }
        }

        return r;
    }

    private static int Probe()
    {
        var recipe = RecipeFromOptions();
        var level = recipe.Build();
        var ball1 = level.Balls.Count > 1 ? new Vec2(level.Balls[1].X, level.Balls[1].Y) : recipe.CuePos;
        Console.WriteLine($"# recipe {recipe.Id}: cue=({Geo.F(recipe.CuePos.X)},{Geo.F(recipe.CuePos.Y)}) " +
                          $"backdoor=({Geo.F(ball1.X)},{Geo.F(ball1.Y)}) aim={Geo.N(recipe.AimDeg)} deg " +
                          $"corridor={Geo.N(recipe.Corridor?.Length ?? 0)} px balls={level.Balls.Count}");
        foreach (var rej in recipe.Rejects) Console.WriteLine("  REJECT " + rej);

        var gap = Kernel.MinSpawnGap(level, out var pair);
        Console.WriteLine($"  spawn gap {Geo.N(gap)} px ({pair}), required >= {Geo.N(Geo.TwoR)}");

        if (Num("debugPlan", 0) > 0)
        {
            var power = Num("dpPower", 170.0);
            var release = Num("dpRelease", 120.0);
            var world = new World(level);
            var plan = new Kernel.Plan
            {
                AngleDeg = recipe.AimDeg,
                Power = power,
                Press = Kernel.PressMode.AtRest,
                Rewind = true,
                MaxForwardSteps = 4000,
            };
            var fwd = Kernel.Forward(level, plan);
            var arcs = Kernel.ReachableReleaseArcs(fwd);
            var target = arcs.Length == 0 ? 0.0 : arcs[0];
            foreach (var a in arcs)
            {
                if (a >= release) { target = a; break; }
            }

            var maxSpeed = new double[fwd.World.Balls.Length];
            var res = Kernel.Finish(fwd, target);
            Console.WriteLine($"  debugPlan: pressArc={Geo.N(fwd.PressArc)} releaseArc={Geo.N(res.ReleaseArc)} " +
                              $"crawl={Geo.N(res.CrawlDistance)} pocketed={res.PocketedObjects}/{res.ObjectCount} {res.Note}");
            foreach (var c in res.Captures)
                Console.WriteLine($"    capture b{c.BallId} ({c.BallType}) pocket={c.PocketIndex} margin={Geo.N(c.Margin)} " +
                                  $"speed={Geo.N(c.Speed)} travel={Geo.N(c.Travel)} clean={c.CleanApproach}");
            Console.WriteLine($"  debugPlan: balls={fwd.World.Balls.Length}, fwd captures={fwd.Strike.Captures.Count}, " +
                              $"fwd pocketed={fwd.Strike.PocketedObjects}");

            // walk the same plan again, printing how each object ball moved
            var w2 = new World(level);
            var p2 = new Kernel.Plan
            {
                AngleDeg = recipe.AimDeg,
                Power = power,
                Press = Kernel.PressMode.AtRest,
                Rewind = true,
                ReleaseArc = target,
                MaxForwardSteps = 4000,
            };
            var f2 = Kernel.Forward(level, p2);
            var released = Kernel.Finish(f2, target);
            Console.WriteLine($"    second run: pocketed={released.PocketedObjects}/{released.ObjectCount}");
            var w3 = new World(level);
            w3.Shot(recipe.AimDeg, power);
            var peak = new double[w3.Balls.Length];
            var trace = new List<string>();
            for (var i = 0; i < 600; i++)
            {
                w3.Step();
                for (var b = 0; b < w3.Balls.Length; b++) peak[b] = Math.Max(peak[b], w3.Balls[b].Speed);
                if (w3.Balls.Length > 2 && w3.Balls[2].Speed > 1.0 && trace.Count < 4)
                    trace.Add($"t{i} b2 v={Geo.N(w3.Balls[2].Speed)} p=({Geo.F(w3.Balls[2].Position.X)},{Geo.F(w3.Balls[2].Position.Y)})");
            }

            foreach (var t in trace) Console.WriteLine("      fwd hit: " + t);
            _ = maxSpeed;
            _ = world;
        }

        if (Num("debugPot", 0) > 0)
        {
            var frame = Geo.Dir(recipe.AimDeg + 180.0);
            var pc = level.Pockets[recipe.BackdoorPocket].Center;
            var cut = Geo.SolveCut(recipe.CuePos, frame, pc, recipe.BackdoorLBack, Limits.MarginFloorPx);
            var pos = recipe.CuePos + frame * recipe.BackdoorLBack + Geo.Perp(frame) * cut.Offset;
            Console.WriteLine($"  debugPot backdoor: pocket={Geo.PocketName(recipe.BackdoorPocket)} centre=({Geo.F(pc.X)},{Geo.F(pc.Y)}) " +
                              $"ball=({Geo.F(pos.X)},{Geo.F(pos.Y)}) dir=({Geo.F(cut.Normal.X)},{Geo.F(cut.Normal.Y)}) " +
                              $"dist={Geo.N((pc - pos).Length)} cosPhi={Geo.F(cut.CosPhi)}");
            var ok = Geo.PotReachability(level, pos, cut.Normal, pc, Geo.PocketR, out var why);
            Console.WriteLine($"  debugPot reachable={ok} {why}");
        }
        Console.WriteLine($"  forward options: {(options.Count == 0 ? "none" : "")}");
        foreach (var o in options)
            Console.WriteLine($"    pocket {Geo.PocketName(o.Pocket)} arc {Geo.N(o.Arc)} predictedWindow {Geo.N(o.Window)} deg");

        var solveOptions = new SolveOptions { Powers = recipe.Powers };        if (Num("trace", 0) > 0) solveOptions.Trace = Console.WriteLine;
        var sol = Metrics.Solve(level, recipe.AimDeg, solveOptions);
        if (sol == null)
        {
            Console.WriteLine("  NO SOLUTION at the nominal angle");
        }
        else
        {
            PrintSolution("nominal", sol);
        }

        var report = Metrics.MeasureWindow(level, recipe.AimDeg, new SolveOptions { Powers = recipe.Powers },
            coarseDeg: 0.5, maxDeg: Num("maxDeg", 8.0), fineDeg: 0.05);
        PrintReport(report);
        Metrics.MeasureStrikeOnly(level, report);
        Console.WriteLine($"  strike-only best = {report.StrikeOnlyBest} object balls " +
                          $"(angle {Geo.N(report.StrikeOnlyBestAngle)}, power {Geo.N(report.StrikeOnlyBestPower)})");
        if (report.Solvable) Metrics.MeasurePowerWindow(level, recipe.AimDeg, new SolveOptions { Powers = recipe.Powers }, report);
        Console.WriteLine($"  power window = [{Geo.N(report.PowerMin)}, {Geo.N(report.PowerMax)}] px of drag");
        return 0;
    }

    /// <summary>
    /// Strike-only diagnostic: sweep the aim around the level's own reverse direction and report
    /// how many object balls a single strike pots. Used to understand why a level is or is not
    /// "strike-clearable" instead of guessing.
    /// </summary>
    private static int StrikeProbe()
    {
        var recipe = RecipeFromOptions();
        var level = recipe.Build();
        Console.WriteLine($"# level balls={level.Balls.Count} aim={Geo.N(recipe.AimDeg)}");
        foreach (var rej in recipe.Rejects) Console.WriteLine("  REJECT " + rej);
        var back = Recipe.Normalize(recipe.AimDeg + 180.0);
        Console.WriteLine("power  bestCount  bestAngle  worstAngle  pocketedAngles(<=6 shown)");
        foreach (var power in new[] { 20.0, 40.0, 60.0, 80.0, 100.0, 120.0, 140.0, 160.0, 170.0 })
        {
            var best = 0;
            var bestAngle = double.NaN;
            var hits = new List<double>();
            for (var a = back - 6.0; a <= back + 6.0 + 1e-9; a += 0.05)
            {
                var res = Kernel.RunPlan(level, new Kernel.Plan { AngleDeg = a, Power = power, Rewind = false });
                if (res.PocketedObjects > 0) hits.Add(a);
                if (res.PocketedObjects > best)
                {
                    best = res.PocketedObjects;
                    bestAngle = a;
                }
            }

            Console.WriteLine($"{Geo.N(power),5} {best,10} {Geo.N(bestAngle),10}  hits={hits.Count} " +
                              string.Join(",", hits.Take(6).Select(h => Geo.N(h))));
        }

        return 0;
    }

    private static void PrintSolution(string label, Solution s)    {
        Console.WriteLine($"  {label}: angle={Geo.N(s.AngleDeg)} power={Geo.N(s.Power)} releaseArc={Geo.N(s.ReleaseArc)} " +
                          $"crawl={Geo.N(s.Crawl)} pressArc={Geo.N(s.Result.PressArc)} ticks={s.Result.Ticks}");
        Console.WriteLine($"           captures: {s.Result.CaptureSummary()}");
    }

    private static void PrintReport(WindowReport r)
    {
        Console.WriteLine($"  clearing window   +-{Geo.N(r.HalfWidthDeg)} deg (farthest {Geo.N(r.IslandDeg)})");
        Console.WriteLine($"  margin>=8 window  +-{Geo.N(r.HalfWidthMarginDeg)} deg");
        if (r.Nominal != null) Console.WriteLine($"  nominal margin    {Geo.N(r.Nominal.MinMargin)} px");
    }

    // ------------------------------------------------------------- verify-cut

    /// <summary>
    /// Validate the analytic tolerance law |dn/dtheta| = sqrt((s-c)^2+(s tan phi)^2)/(2R) against the
    /// shipping kernel: put a cue and one object ball on a big empty table, strike twice with a
    /// 1 deg difference and compare the ball's measured departure direction with the prediction.
    /// This is the evidence behind the placement rule the generator reasons with.
    /// </summary>
    private static int VerifyCut()
    {
        Console.WriteLine("s(px)  b(px)   |dn/dth| predicted  measured@0.05deg  ratio   measured@1deg");
        var level = Kernel.BaseTable();
        level.Id = "cut-probe";
        level.Name = "cut-probe";
        level.Budget = RewindBudget.Unlimited;
        var cue = new Vec2(300.0, 280.0);
        level.AddCue(cue.X, cue.Y);

        var worst = 0.0;
        for (var i = 0; i < 6; i++)
        {
            var s = new[] { 40.0, 60.0, 100.0, 150.0, 220.0, 320.0 }[i];
            var b = new[] { 0.0, 5.0, 10.0, 18.0, 22.0, 23.5 }[i];
            var level2 = Kernel.Clone(level);
            level2.Balls.Clear();
            level2.AddCue(cue.X, cue.Y);
            var d = new Vec2(1, 0);
            var m = Geo.Perp(d);
            var pos = cue + d * s + m * b;
            level2.AddBall(1, BallType.Object, pos.X, pos.Y);

            var dir0 = DepartureDirection(level2, 0.0);
            var d1 = DepartureDirection(level2, 0.05);
            var d2 = DepartureDirection(level2, 1.0);
            var measured = AngleBetween(dir0, d1) / 0.05;
            var measured1 = AngleBetween(dir0, d2);
            var predicted = Geo.SensitivityAtContact(s, b);
            var ratio = predicted > 1e-12 ? measured / predicted : double.NaN;
            worst = Math.Max(worst, Math.Abs(ratio - 1.0));
            Console.WriteLine($"{Geo.N(s),5} {Geo.N(b),5}   {Geo.N(predicted),10} {Geo.N(measured),17}  {Geo.N(ratio),6}  {Geo.N(measured1),15}");
        }

        Console.WriteLine($"# worst |ratio-1| = {Geo.N(worst)}  (predicted vs measured at 0.05 deg)");
        return 0;
    }

    private static Vec2 DepartureDirection(LevelData level, double angleOffset)
    {
        var world = new World(level);
        world.Shot(angleOffset, 170.0);
        for (var i = 0; i < 600; i++)
        {
            world.Step();
            var b = world.Balls[1];
            if (b.Speed > 1.0) return b.Velocity.Normalized();
        }

        return Vec2.Zero;
    }

    private static double AngleBetween(Vec2 a, Vec2 b)
    {
        var dot = Math.Max(-1.0, Math.Min(1.0, Vec2.Dot(a, b)));
        return Math.Acos(dot) * 180.0 / Math.PI;
    }

    // -------------------------------------------------------------- scan/emit

    private static int Scan() => Search.Run();

    private static int Emit()
    {
        var table = Search.BuildShippingSet();
        Rewind.Author.Emit.WriteLevels(table);
        return 0;
    }

    private static int Verify()
    {
        var table = Loader.LoadShipped();
        Reporter.PrintTable(table);
        return Reporter.AllAccepted(table) ? 0 : 1;
    }

    private static int Full()
    {
        var rc = Emit();
        Console.WriteLine();
        return Verify() == 0 ? rc : 1;
    }

    // ---------------------------------------------------------------- options

    private static string Str(string key, string fallback) => Opt.TryGetValue(key, out var v) ? v : fallback;

    public static double Num(string key, double fallback) =>
        Opt.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : fallback;

    private static int Int(string key, int fallback) => (int)Num(key, fallback);
}
