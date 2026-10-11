using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rewind.Sim;

namespace Rewind.Author;

/// <summary>A shipping slot: how many object balls, and the bar the level must clear.</summary>
public sealed class Slot
{
    public string Id;
    public string Name;
    public string Mechanism;
    public int ObjectBalls;
    public double MinMarginWindowDeg = 2.0;
    public double MaxMarginWindowDeg = 12.0;
    public double MinPowerWindowPx = Limits.PowerWindowFloorPx;
    public double MinCueStartPx = Limits.CueStartFloorPx;
}

/// <summary>
/// The parameterised generator. Every candidate is measured with the shipping kernel
/// (godot/src/sim compiled verbatim); a candidate that misses the bar is a rejected candidate, not
/// a shipped level.
///
/// WHY THE SEARCH IS SMALL: the tolerance law (see <see cref="Geo"/>) collapses the placement
/// space to three numbers per ball - the ball's distance from the pivot (want ~2R, where
/// |dn/dtheta| = |1 - s/(2R cos phi)| vanishes), the ball-to-pocket distance (want short), and the
/// contact thickness (want thick). The enumeration below is exactly over those, plus which pocket.
///
/// WHY EVERY LEVEL NEEDS >= 2 OBJECT BALLS: one strike can always be aimed straight at a single
/// object ball and pot it, so a one-ball level is strike-clearable by construction. The generator
/// therefore refuses to ship a level whose best strike-only plan clears it (verified by sweep).
/// </summary>
public static class Search
{
    public static bool Verbose;

    // ------------------------------------------------------------------- scan

    public static int Run()
    {
        var sw = Stopwatch.StartNew();
        var chords = Chords(Num("aimStep", 30.0), 170.0, 170.0, Num("gridStep", 70.0));
        Console.WriteLine($"# {chords.Count} corridors measured in {sw.Elapsed.TotalSeconds:0.0} s");
        var candidates = new List<Candidate>();
        var built = 0;
        foreach (var recipe in ExpandAll(chords, 1, 2))
        {
            built++;
            var level = recipe.Build();
            if (recipe.Rejects.Count > 0) continue;
            var c = new Candidate { Recipe = recipe, Level = level, AimDeg = recipe.AimDeg };
            c.Window = Metrics.MeasureWindow(level, recipe.AimDeg,
                new SolveOptions { ReleaseSamples = 8 }, coarseDeg: 0.5, maxDeg: 6.0, fineDeg: 0.05);
            if (c.Window.Solvable) candidates.Add(c);
        }

        Console.WriteLine($"# {built} recipes built, {candidates.Count} solved, {sw.Elapsed.TotalSeconds:0} s");
        var ranked = candidates.OrderByDescending(c => c.Window.HalfWidthMarginDeg).ToList();
        Console.WriteLine("cue                aim  backdoor      forward        balls corr    marginWin clearWin crawl");
        foreach (var c in ranked.Take(30))
        {
            var r = c.Recipe;
            var fwd = r.Forward.Count > 0 ? $"p{r.Forward[0].PocketIndex}/a{Geo.N(r.Forward[0].Arc)}" : "-";
            Console.WriteLine($"({Geo.N(r.CuePos.X),5},{Geo.N(r.CuePos.Y),5}) {Geo.N(r.AimDeg),5} " +
                              $"p{r.BackdoorPocket}/L{Geo.N(r.BackdoorLBack),-5} {fwd,-13} " +
                              $"{c.Level.Balls.Count,5} {Geo.N(r.Corridor.Length),6} " +
                              $"{Geo.N(c.Window.HalfWidthMarginDeg),10} {Geo.N(c.Window.HalfWidthDeg),8} " +
                              $"{Geo.N(c.Window.Nominal?.Crawl ?? -1),7}");
        }

        return 0;
    }

    // --------------------------------------------------------------- corridors

    private sealed class Chord
    {
        public Vec2 Cue;
        public double AimDeg;
        public Vec2 Direction;
        public Corridor Corridor;
    }

    private static List<Chord> Chords(double aimStepDeg, double probePower, double minCorridor, double gridStep = 60.0)
    {
        var list = new List<Chord>();
        var baseLevel = Kernel.BaseTable();
        for (var x = 110.0; x <= 930.0; x += gridStep)
        {
            for (var y = 110.0; y <= 450.0; y += gridStep)
            {
                var cue = new Vec2(x, y);
                for (var a = -180.0; a < 180.0 - 1e-9; a += aimStepDeg)
                {
                    var probe = Kernel.Clone(baseLevel);
                    probe.Balls.Clear();
                    probe.AddCue(cue.X, cue.Y);
                    var corridor = Corridor.Measure(probe, a, probePower, maxSteps: 700);
                    if (corridor.Empty || corridor.Length < minCorridor) continue;
                    list.Add(new Chord { Cue = cue, AimDeg = a, Direction = Geo.Dir(a), Corridor = corridor });
                }
            }
        }

        return list;
    }

    // ------------------------------------------------------------------ expand

    private sealed class Combo
    {
        public Chord Chord;
        public int BackdoorPocket;
        public double BackdoorLBack;
        public double BackdoorWindowDeg;
        public int ForwardPocket = -1;
        public double ForwardArc;
        public double ForwardWindowDeg = double.PositiveInfinity;

        public double PredictedWindowDeg => Math.Min(BackdoorWindowDeg, ForwardWindowDeg);
    }

    private static IEnumerable<Recipe> ExpandAll(List<Chord> chords, int forwardBalls, int maxPerChord)
    {
        foreach (var chord in chords)
        {
            foreach (var recipe in Expand(chord, forwardBalls).Take(maxPerChord)) yield return recipe;
        }
    }

    private static IEnumerable<Recipe> Expand(Chord chord, int forwardBalls)
    {
        foreach (var combo in Combos(chord, forwardBalls).Take(3))
        {
            var forward = combo.ForwardPocket < 0
                ? new List<(int, double)>()
                : new List<(int, double)> { (combo.ForwardPocket, combo.ForwardArc) };
            yield return MakeRecipe(chord.Cue, chord.AimDeg, combo.BackdoorPocket, combo.BackdoorLBack, forward);
        }
    }

    private static Recipe MakeRecipe(Vec2 cue, double aim, int backdoorPocket, double lback, List<(int, double)> forward)
    {
        var recipe = new Recipe
        {
            Id = "cand",
            Name = "candidate",
            Mechanism = "corridor + reverse pass",
            CuePos = cue,
            AimDeg = aim,
            BackdoorPocket = backdoorPocket,
            BackdoorLBack = lback,
        };
        foreach (var (pocket, arc) in forward) recipe.Forward.Add(ForwardSpec.Of(pocket, arc));
        return recipe;
    }

    private static List<Combo> Combos(Chord chord, int forwardBalls)
    {
        var frame = -chord.Direction;
        var backdoors = new List<(int Pocket, double LBack, double Window)>();
        foreach (var pocket in Enumerable.Range(0, 6))
        {
            foreach (var lback in new[] { 26.0, 30.0, 36.0, 44.0 })
            {
                var cut = Geo.SolveCut(chord.Cue, frame, ChordTable.Pockets[pocket], lback, Limits.MarginFloorPx);
                if (!cut.Ok) continue;
                var pos = chord.Cue + frame * lback + Geo.Perp(frame) * cut.Offset;
                if (!LegalPoint(pos)) continue;
                var dist = (ChordTable.Pockets[pocket] - pos).Length;
                if (dist > 300.0) continue;
                if (!Geo.PotReachability(Level0, pos, cut.Normal, ChordTable.Pockets[pocket], Geo.PocketR, out _)) continue;
                var window = (Geo.PocketR - Limits.MarginFloorPx) / (dist * cut.Sensitivity * Geo.Rad(1.0));
                if (window < 2.0) continue;
                backdoors.Add((pocket, lback, window));
            }
        }

        if (backdoors.Count == 0) return new List<Combo>();
        var bestBackdoors = backdoors.OrderByDescending(b => b.Window).Take(3).ToList();

        var firstLegDir = FirstLeg(chord.Corridor, out var firstLeg);
        var forwards = new List<(int Pocket, double Arc, double Window)>();
        if (firstLegDir.LengthSquared > 0.0)
        {
            foreach (var arc in ForwardArcs)
            {
                if (arc > firstLeg - 5.0 || arc > chord.Corridor.Length - 5.0) continue;
                var speed = chord.Corridor.SpeedAt(arc);
                foreach (var pocket in Enumerable.Range(0, 6))
                {
                    var cut = Geo.SolveCut(chord.Cue, firstLegDir, ChordTable.Pockets[pocket], arc, Limits.MarginFloorPx);
                    if (!cut.Ok) continue;
                    var pos = chord.Cue + firstLegDir * arc + Geo.Perp(firstLegDir) * cut.Offset;
                    if (!LegalPoint(pos)) continue;
                    var dist = (ChordTable.Pockets[pocket] - pos).Length;
                    if (dist > 300.0) continue;
                    if (!Geo.PotReachability(Level0, pos, cut.Normal, ChordTable.Pockets[pocket], Geo.PocketR, out _)) continue;
                    var ballSpeed = 0.9775 * speed * cut.CosPhi;
                    var needed = Math.Sqrt(2.0 * 640.0 * Math.Max(1.0, dist - Geo.PocketR)) * 1.15;
                    if (ballSpeed < needed) continue;
                    var window = (Geo.PocketR - Limits.MarginFloorPx) / (dist * cut.Sensitivity * Geo.Rad(1.0));
                    if (window < 2.2) continue;
                    forwards.Add((pocket, arc, window));
                }
            }
        }

        var rankedForward = forwards.OrderByDescending(f => f.Window).Take(4).ToList();
        var combos = new List<Combo>();
        foreach (var backdoor in bestBackdoors)
        {
            if (forwardBalls == 0 || rankedForward.Count == 0)
            {
                combos.Add(new Combo
                {
                    Chord = chord,
                    BackdoorPocket = backdoor.Pocket,
                    BackdoorLBack = backdoor.LBack,
                    BackdoorWindowDeg = backdoor.Window,
                });
                continue;
            }

            foreach (var f in rankedForward.Take(2))
            {
                combos.Add(new Combo
                {
                    Chord = chord,
                    BackdoorPocket = backdoor.Pocket,
                    BackdoorLBack = backdoor.LBack,
                    BackdoorWindowDeg = backdoor.Window,
                    ForwardPocket = f.Pocket,
                    ForwardArc = f.Arc,
                    ForwardWindowDeg = f.Window,
                });
            }
        }

        return combos.OrderByDescending(c => c.PredictedWindowDeg).ToList();
    }

    private static Vec2 FirstLeg(Corridor corridor, out double length)
    {
        length = 0.0;
        if (corridor.Empty) return Vec2.Zero;
        var dir = (corridor.Points[1] - corridor.Points[0]).Normalized();
        for (var i = 1; i < corridor.Points.Count; i++)
        {
            var seg = corridor.Points[i] - corridor.Points[i - 1];
            if (seg.LengthSquared <= 0.0) continue;
            if (Vec2.Dot(seg.Normalized(), dir) < 1.0 - 1e-9) break;
            length = corridor.Arcs[i];
        }

        return dir;
    }

    private static bool LegalPoint(Vec2 p)
    {
        const double clearance = 0.6;
        return p.X >= 65.0 + Geo.R - clearance && p.X <= 975.0 - Geo.R + clearance
               && p.Y >= 63.0 + Geo.R - clearance && p.Y <= 497.0 - Geo.R + clearance;
    }

    /// <summary>An empty standard table, used by the reachability pre-filter (no balls involved).</summary>
    private static readonly LevelData Level0 = Kernel.BaseTable();

    // -------------------------------------------------------- forward extension

    /// <summary>
    /// Candidate (pocket, arc) plantings for one more forward ball on the recipe's current corridor.
    /// Only first-leg arcs are offered so the tolerance law can be evaluated exactly.
    /// </summary>
    public static List<(int Pocket, double Arc, double Window)> ForwardOptions(Recipe recipe, int take = 6)
    {
        var options = new List<(int Pocket, double Arc, double Window)>();
        if (recipe.Corridor == null || recipe.Corridor.Empty) return options;
        var firstLegDir = FirstLeg(recipe.Corridor, out var firstLeg);
        if (firstLegDir.LengthSquared <= 0.0) return options;
        foreach (var arc in ForwardArcs)
        {
            if (arc > firstLeg - 5.0) continue;
            var speed = recipe.Corridor.SpeedAt(arc);
            foreach (var pocket in Enumerable.Range(0, 6))
            {
                var cut = Geo.SolveCut(recipe.CuePos, firstLegDir, ChordTable.Pockets[pocket], arc, Limits.MarginFloorPx);
                if (!cut.Ok) continue;
                var pos = recipe.CuePos + firstLegDir * arc + Geo.Perp(firstLegDir) * cut.Offset;
                if (!LegalPoint(pos)) continue;
                var dist = (ChordTable.Pockets[pocket] - pos).Length;
                if (dist > 300.0) continue;
                if (!Geo.PotReachability(Level0, pos, cut.Normal, ChordTable.Pockets[pocket], Geo.PocketR, out _)) continue;
                var ballSpeed = 0.9775 * speed * cut.CosPhi;
                var needed = Math.Sqrt(2.0 * 640.0 * Math.Max(1.0, dist - Geo.PocketR)) * 1.15;
                if (ballSpeed < needed) continue;
                var window = (Geo.PocketR - Limits.MarginFloorPx) / (dist * cut.Sensitivity * Geo.Rad(1.0));
                if (window < 2.2) continue;
                options.Add((pocket, arc, window));
            }
        }

        return options.OrderByDescending(o => o.Window).Take(take).ToList();
    }

    // ------------------------------------------------------------ shipping set

    /// <summary>
    /// Forward-ball planting distances (arc length from the cue's spawn), px.
    ///
    /// WHY SO SHORT: the tolerance law puts the forward ball's sensitivity at
    /// |1 - L/(2R cos phi)|. For a cut of phi = 40 deg the zero sits at L = 2R cos(phi) = 18.7 px, so
    /// the sweet spot for a cut ball is just above the 2R = 24.4 px contact distance. At L = 100 px
    /// the same cut is already 3.5x more sensitive, and at L = 150 px no pocket distance can keep a
    /// +-2 deg window. Short planting arcs are therefore not a style choice: they are the only
    /// region where a strike-potted cut survives a human aim error.
    /// </summary>
    private static readonly double[] ForwardArcs = { 26.0, 32.0, 40.0, 50.0 };

    public static List<Candidate> BuildShippingSet()
    {
        var sw = Stopwatch.StartNew();
        Verbose = Num("verbose", 0) > 0;
        var pool = CollectPool();
        Console.WriteLine($"# pool: {pool.Count} solvable candidates ({sw.Elapsed.TotalSeconds:0} s)");
        foreach (var c in pool.Take(6))
            Console.WriteLine($"    cue=({Geo.N(c.Recipe.CuePos.X)},{Geo.N(c.Recipe.CuePos.Y)}) aim={Geo.N(c.Recipe.AimDeg)} " +
                              $"backdoor=p{c.Recipe.BackdoorPocket}/L{Geo.N(c.Recipe.BackdoorLBack)} balls={c.Level.Balls.Count} " +
                              $"objects={Kernel.CountObjects(c.Level)} win={Geo.N(c.Window.HalfWidthMarginDeg)}");

        var chosen = new List<Candidate>();
        foreach (var slot in Slots())
        {
            var pick = Pick(pool, chosen, slot);
            if (pick == null)
            {
                Console.WriteLine($"# SLOT {slot.Id}: nothing in the pool matches (objects={slot.ObjectBalls})");
                continue;
            }

            var candidate = slot.ObjectBalls > Kernel.CountObjects(pick.Level)
                ? Extend(pick, slot, slot.ObjectBalls - Kernel.CountObjects(pick.Level))
                : pick;
            if (candidate == null)
            {
                Console.WriteLine($"# SLOT {slot.Id}: extension failed");
                continue;
            }

            candidate.Recipe.Id = slot.Id;
            candidate.Recipe.Name = slot.Name;
            candidate.Recipe.Mechanism = slot.Mechanism;
            if (!Measure.Full(candidate, slot))
            {
                Console.WriteLine($"# SLOT {slot.Id}: rejected after full measurement: {candidate.Reject}");
                continue;
            }

            chosen.Add(candidate);
            Console.WriteLine($"# SLOT {slot.Id}: ACCEPTED objects={Kernel.CountObjects(candidate.Level)} " +
                              $"marginWindow=+-{Geo.N(candidate.Window.HalfWidthMarginDeg)} deg " +
                              $"power={Geo.N(candidate.Window.PowerMin)}-{Geo.N(candidate.Window.PowerMax)} " +
                              $"strikeOnlyBest={candidate.Window.StrikeOnlyBest}");
        }

        Console.WriteLine($"# set built in {sw.Elapsed.TotalSeconds:0} s");
        return chosen;
    }

    private static List<Candidate> CollectPool()
    {
        var chords = Chords(Num("aimStep", 30.0), 170.0, 170.0, Num("gridStep", 60.0));
        var pool = new List<Candidate>();
        var maxPool = (int)Num("maxPool", 260);
        foreach (var recipe in ExpandAll(chords, 1, 2))
        {
            var level = recipe.Build();
            if (recipe.Rejects.Count > 0) continue;
            var c = new Candidate { Recipe = recipe, Level = level, AimDeg = recipe.AimDeg };
            c.Window = Metrics.MeasureWindow(level, recipe.AimDeg,
                new SolveOptions { ReleaseSamples = 10 }, coarseDeg: 0.5, maxDeg: 8.0, fineDeg: 0.05);
            if (!c.Window.Solvable) continue;
            if (c.Window.HalfWidthMarginDeg < 2.0) continue;
            pool.Add(c);
            if (pool.Count >= maxPool) break;
        }

        // Rank by the ball-count-adjusted window: more balls first (harder), then by tolerance.
        return pool.OrderByDescending(c => c.Level.Balls.Count)
            .ThenByDescending(c => c.Window.HalfWidthMarginDeg)
            .ToList();
    }

    /// <summary>Pick a pool candidate for a slot: enough balls, tolerance in the slot's band, and a
    /// geometry that is not a copy of an already chosen level.</summary>
    private static Candidate Pick(List<Candidate> pool, List<Candidate> chosen, Slot slot)
    {
        Candidate best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var c in pool)
        {
            if (chosen.Contains(c)) continue;
            if (Kernel.CountObjects(c.Level) > slot.ObjectBalls) continue;
            var window = c.Window.HalfWidthMarginDeg;
            if (window < slot.MinMarginWindowDeg) continue;
            if (Distinct(c, chosen) < 120.0) continue;
            // prefer a tolerance close to the slot's target band, then a longer corridor
            var target = Math.Min(slot.MaxMarginWindowDeg, Math.Max(slot.MinMarginWindowDeg, 6.0));
            var score = -Math.Abs(Math.Min(window, slot.MaxMarginWindowDeg) - target) * 10.0
                        + c.Recipe.Corridor.Length * 0.001
                        + Kernel.CountObjects(c.Level) * 0.5;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }

    private static double Distinct(Candidate c, List<Candidate> chosen)
    {
        var min = double.PositiveInfinity;
        foreach (var o in chosen)
        {
            var d = (c.Recipe.CuePos - o.Recipe.CuePos).Length;
            if (c.Recipe.BackdoorPocket != o.Recipe.BackdoorPocket) d += 150.0;
            if (Math.Abs(Recipe.Normalize(c.Recipe.AimDeg - o.Recipe.AimDeg)) > 45.0) d += 100.0;
            if (d < min) min = d;
        }

        return chosen.Count == 0 ? double.PositiveInfinity : min;
    }

    /// <summary>Greedily append forward balls to a candidate, keeping the joint window above the bar.</summary>
    private static Candidate Extend(Candidate baseCandidate, Slot slot, int extraBalls)
    {
        var current = baseCandidate;
        for (var k = 0; k < extraBalls; k++)
        {
            var options = ForwardOptions(current.Recipe);
            Candidate best = null;
            var bestWindow = -1.0;
            foreach (var option in options)
            {
                var recipe = CloneRecipe(current.Recipe);
                recipe.Forward.Add(ForwardSpec.Of(option.Pocket, option.Arc));
                recipe.Id = slot.Id;
                var level = recipe.Build();
                if (recipe.Rejects.Count > 0) continue;
                var c = new Candidate { Recipe = recipe, Level = level, AimDeg = recipe.AimDeg };
                c.Window = Metrics.MeasureWindow(level, recipe.AimDeg,
                    new SolveOptions { ReleaseSamples = 8 }, coarseDeg: 0.5, maxDeg: 6.0, fineDeg: 0.05);
                if (!c.Window.Solvable) continue;
                if (c.Window.HalfWidthMarginDeg < slot.MinMarginWindowDeg) continue;
                if (c.Window.HalfWidthMarginDeg > bestWindow)
                {
                    bestWindow = c.Window.HalfWidthMarginDeg;
                    best = c;
                }
            }

            if (best == null) return k == 0 ? null : current;
            current = best;
            Console.WriteLine($"    extended to {Kernel.CountObjects(current.Level)} objects " +
                              $"(+p{options[0].Pocket}/a{Geo.N(options[0].Arc)} style), window=+-{Geo.N(bestWindow)} deg");
        }

        return current;
    }

    private static Recipe CloneRecipe(Recipe src)
    {
        var copy = new Recipe
        {
            Id = src.Id,
            Name = src.Name,
            Mechanism = src.Mechanism,
            CuePos = src.CuePos,
            AimDeg = src.AimDeg,
            BackdoorPocket = src.BackdoorPocket,
            BackdoorLBack = src.BackdoorLBack,
            BackdoorExplicit = src.BackdoorExplicit,
            BackdoorPos = src.BackdoorPos,
            CorridorProbePower = src.CorridorProbePower,
            Powers = src.Powers,
        };
        copy.Forward.AddRange(src.Forward);
        return copy;
    }

    private static IEnumerable<Slot> Slots()
    {
        yield return new Slot
        {
            Id = "l1_backdoor_tap",
            Name = "Backdoor tap: the rewind rolls the cue back through its own spawn",
            Mechanism = "reverse pass (the strike leaves the backdoor ball untouched; only the " +
                        "rewound cue travels back past its spawn and cuts it into a near pocket)",
            ObjectBalls = 2,
            MinMarginWindowDeg = 4.0,
        };
        yield return new Slot
        {
            Id = "l2_two_pocket_corridor",
            Name = "Two-pocket corridor: strike pots the near ball, rewind pots the far one",
            Mechanism = "reverse pass + planted forward ball (a cut the strike must hit on its way out)",
            ObjectBalls = 3,
            MinMarginWindowDeg = 3.0,
        };
        yield return new Slot
        {
            Id = "l3_long_walk",
            Name = "Long walk: the corridor must be walked back before the release",
            Mechanism = "reverse pass + two planted forward balls",
            ObjectBalls = 4,
            MinMarginWindowDeg = 2.6,
        };
        yield return new Slot
        {
            Id = "l4_double_cut",
            Name = "Double cut: two object balls change the corridor before the rewind",
            Mechanism = "reverse pass + two planted forward balls on a bent corridor",
            ObjectBalls = 4,
            MinMarginWindowDeg = 2.4,
        };
        yield return new Slot
        {
            Id = "l5_five_ball",
            Name = "Five balls: four forward pots plus the rewind pot",
            Mechanism = "reverse pass + three planted forward balls",
            ObjectBalls = 5,
            MinMarginWindowDeg = 2.2,
        };
        yield return new Slot
        {
            Id = "l6_six_ball",
            Name = "Six balls: the four-ball cap re-opened",
            Mechanism = "reverse pass + four planted forward balls",
            ObjectBalls = 6,
            MinMarginWindowDeg = 2.0,
        };
    }

    private static double Num(string key, double fallback) => Program.Num(key, fallback);
}

/// <summary>The standard table's six pocket centres, in the kernel's own order.</summary>
public static class ChordTable
{
    public static readonly Vec2[] Pockets =
    {
        new Vec2(65, 63), new Vec2(520, 63), new Vec2(975, 63),
        new Vec2(65, 497), new Vec2(520, 497), new Vec2(975, 497),
    };
}
