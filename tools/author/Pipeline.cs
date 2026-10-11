using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Rewind.Sim;

namespace Rewind.Author;

/// <summary>Reads the authored levels back from disk (independent of the in-memory build).</summary>
public static class Loader
{
    public static string LevelsDir => Path.Combine(RepoRoot, "godot", "levels");

    public static string RepoRoot
    {
        get
        {
            var probe = new DirectoryInfo(AppContext.BaseDirectory);
            while (probe != null)
            {
                if (Directory.Exists(Path.Combine(probe.FullName, "godot", "levels"))) return probe.FullName;
                probe = probe.Parent;
            }

            throw new InvalidOperationException("cannot locate the repository root from " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Load every level file that carries an "authoring" block (i.e. every level this tool emitted).</summary>
    public static List<AuthoredLevel> LoadShipped()
    {
        var list = new List<AuthoredLevel>();
        foreach (var file in Directory.GetFiles(LevelsDir, "*.json"))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("\"authoring\"")) continue;
            list.Add(Parse(text, Path.GetFileNameWithoutExtension(file)));
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    public static AuthoredLevel Parse(string text, string fallbackId)
    {
        var root = Json.Parse(text);
        var level = LevelData.FromJson(root);
        var authored = new AuthoredLevel { Id = level.Id.Length > 0 ? level.Id : fallbackId, Level = level, Json = text };
        var authoring = root.Get("authoring");
        if (authoring == null) return authored;
        authored.Mechanism = authoring.Text("mechanism", "");
        authored.Notes = authoring.Text("notes", "");
        var limits = authoring.Get("limits");
        if (limits != null)
        {
            authored.MarginFloor = limits.Num("marginFloorPx", Limits.MarginFloorPx);
            authored.ArcFloor = limits.Num("corridorArcFloorPx", Limits.CorridorArcFloorPx);
            authored.AngleFloorDeg = limits.Num("angleHalfWidthDeg", 2.0);
            authored.PowerFloor = limits.Num("powerWindowPx", Limits.PowerWindowFloorPx);
            authored.CueStartFloor = limits.Num("cueStartHalfWidthPx", Limits.CueStartFloorPx);
        }

        var measured = authoring.Get("measured");
        if (measured != null)
        {
            authored.Measured = new Measured
            {
                ObjectCount = (int)measured.Num("objectCount", 0),
                SpawnGapPx = measured.Num("spawnGapPx", double.NaN),
                CorridorLengthPx = measured.Num("corridorLengthPx", double.NaN),
                SettleArcPx = measured.Num("settleArcPx", double.NaN),
                Settled = measured.Num("settled", 0.0) > 0.5,
                SettleSteps = (int)measured.Num("settleSteps", 0),
                AngleHalfWidthDeg = measured.Num("angleHalfWidthDeg", double.NaN),
                AngleHalfWidthMarginDeg = measured.Num("angleHalfWidthMarginDeg", double.NaN),
                NegativeHalfWidthDeg = measured.Num("negativeHalfWidthDeg", double.NaN),
                PositiveHalfWidthDeg = measured.Num("positiveHalfWidthDeg", double.NaN),
                IslandDeg = measured.Num("islandDeg", double.NaN),
                PowerMin = measured.Num("powerMin", double.NaN),
                PowerMax = measured.Num("powerMax", double.NaN),
                CueStartHalfWidthPx = measured.Num("cueStartHalfWidthPx", double.NaN),
                StrikeOnlyBest = (int)measured.Num("strikeOnlyBest", 0),
                StrikeOnlyStepDeg = measured.Num("strikeOnlyStepDeg", double.NaN),
                StrikeOnlyPowers = (int)measured.Num("strikeOnlyPowers", 0),
                NominalWorstMarginPx = measured.Num("nominalWorstMarginPx", double.NaN),
                NominalCrawlPx = measured.Num("nominalCrawlPx", double.NaN),
            };
        }

        var portability = authoring.Get("portability");
        if (portability != null)
        {
            foreach (var kv in portability.Members) authored.Portability[kv.Key] = kv.Value.Str ?? string.Empty;
        }

        var witnesses = authoring.Get("witnesses");
        if (witnesses != null)
        {
            foreach (var w in witnesses.Items)
            {
                var witness = new Witness
                {
                    Label = w.Text("label", "?"),
                    AngleDeg = w.Num("angle", double.NaN),
                    Power = w.Num("power", double.NaN),
                    ReleaseArc = w.Num("releaseArc", double.NaN),
                    Crawl = w.Num("crawl", double.NaN),
                    MinMargin = w.Num("minMargin", double.NaN),
                    ObjectCount = (int)w.Num("objectCount", 0),
                };
                var caps = w.Get("captures");
                if (caps != null)
                {
                    foreach (var cap in caps.Items)
                    {
                        witness.Captures.Add(new WitnessCapture
                        {
                            BallId = (int)cap.Num("ballId", -1),
                            PocketIndex = (int)cap.Num("pocketIndex", -1),
                            MarginPx = cap.Num("marginPx", double.NaN),
                            TravelPx = cap.Num("travelPx", double.NaN),
                        });
                    }
                }

                authored.Witnesses.Add(witness);
            }
        }

        return authored;
    }
}

public sealed class Measured
{
    public int ObjectCount;
    public double SpawnGapPx;
    public double CorridorLengthPx;
    public double SettleArcPx;
    public bool Settled;
    public int SettleSteps;
    public double AngleHalfWidthDeg;
    public double AngleHalfWidthMarginDeg;
    public double NegativeHalfWidthDeg;
    public double PositiveHalfWidthDeg;
    public double IslandDeg;
    public double PowerMin;
    public double PowerMax;
    public double CueStartHalfWidthPx;
    public int StrikeOnlyBest;
    public double StrikeOnlyStepDeg;
    public int StrikeOnlyPowers;
    public double NominalWorstMarginPx;
    public double NominalCrawlPx;

    public double PowerWindow => PowerMax - PowerMin;
}

public sealed class WitnessCapture
{
    public int BallId;
    public int PocketIndex;
    public double MarginPx;
    public double TravelPx;
}

public sealed class Witness
{
    public string Label;
    public double AngleDeg;
    public double Power;
    public double ReleaseArc;
    public double Crawl;
    public double MinMargin;
    public int ObjectCount;
    public readonly List<WitnessCapture> Captures = new List<WitnessCapture>();

    public override string ToString() =>
        $"{Label}: angle={Geo.N(AngleDeg)} power={Geo.N(Power)} releaseArc={Geo.N(ReleaseArc)} " +
        $"crawl={Geo.N(Crawl)} minMargin={Geo.N(MinMargin)}";
}

public sealed class AuthoredLevel
{
    public string Id;
    public LevelData Level;
    public string Json;
    public string Mechanism = "";
    public string Notes = "";

    public double MarginFloor = Limits.MarginFloorPx;
    public double ArcFloor = Limits.CorridorArcFloorPx;
    public double AngleFloorDeg = 2.0;
    public double PowerFloor = Limits.PowerWindowFloorPx;
    public double CueStartFloor = Limits.CueStartFloorPx;

    public Measured Measured = new Measured();
    public readonly List<Witness> Witnesses = new List<Witness>();
    public readonly Dictionary<string, string> Portability = new Dictionary<string, string>();
}

/// <summary>Writes the level files, including the measured table and the replayable witnesses.</summary>
public static class Emit
{
    public static void WriteLevels(List<Candidate> candidates)
    {
        Directory.CreateDirectory(Loader.LevelsDir);
        foreach (var c in candidates)
        {
            if (!c.Accepted)
            {
                Console.WriteLine($"# NOT EMITTED {c.Recipe.Id}: {c.Reject}");
                continue;
            }

            var path = Path.Combine(Loader.LevelsDir, c.Recipe.Id + ".json");
            File.WriteAllText(path, BuildJson(c), new UTF8Encoding(false));
            Console.WriteLine($"# wrote {Path.GetFileName(path)}: objects={Kernel.CountObjects(c.Level)} " +
                              $"marginWindow=+-{Geo.N(c.Window.HalfWidthMarginDeg)} deg " +
                              $"powerWindow={Geo.N(c.Window.PowerWindow)} px");
        }
    }

    public static string BuildJson(Candidate c)
    {
        var level = c.Level;
        var root = level.ToJson();
        var authoring = JsonValue.Object();
        authoring.Set("tool", JsonValue.Of("tools/author (tolerance-aware level authoring tool)"));
        authoring.Set("mechanism", JsonValue.Of(c.Recipe.Mechanism));
        authoring.Set("notes", JsonValue.Of(c.Recipe.Name));

        var limits = JsonValue.Object();
        limits.Set("marginFloorPx", JsonValue.Of(Limits.MarginFloorPx));
        limits.Set("corridorArcFloorPx", JsonValue.Of(Limits.CorridorArcFloorPx));
        limits.Set("angleHalfWidthDeg", JsonValue.Of(2.0));
        limits.Set("powerWindowPx", JsonValue.Of(Limits.PowerWindowFloorPx));
        limits.Set("cueStartHalfWidthPx", JsonValue.Of(Limits.CueStartFloorPx));
        authoring.Set("limits", limits);

        var w = c.Window;
        var measured = JsonValue.Object();
        measured.Set("objectCount", JsonValue.Of((double)Kernel.CountObjects(level)));
        measured.Set("spawnGapPx", JsonValue.Of(Kernel.MinSpawnGap(level)));
        measured.Set("corridorLengthPx", JsonValue.Of(R(w.CorridorLengthPx)));
        measured.Set("settleArcPx", JsonValue.Of(R(w.SettleArcPx)));
        measured.Set("settled", JsonValue.Of(w.Settled ? 1.0 : 0.0));
        measured.Set("settleSteps", JsonValue.Of((double)w.SettleSteps));
        measured.Set("angleHalfWidthDeg", JsonValue.Of(R(w.HalfWidthDeg)));
        measured.Set("angleHalfWidthMarginDeg", JsonValue.Of(R(w.HalfWidthMarginDeg)));
        measured.Set("negativeHalfWidthDeg", JsonValue.Of(R(w.NegativeHalfWidthDeg)));
        measured.Set("positiveHalfWidthDeg", JsonValue.Of(R(w.PositiveHalfWidthDeg)));
        measured.Set("islandDeg", JsonValue.Of(R(w.IslandDeg)));
        measured.Set("powerMin", JsonValue.Of(R(w.PowerMin)));
        measured.Set("powerMax", JsonValue.Of(R(w.PowerMax)));
        measured.Set("cueStartHalfWidthPx", JsonValue.Of(R(w.CueStartHalfWidth)));
        measured.Set("strikeOnlyBest", JsonValue.Of((double)w.StrikeOnlyBest));
        measured.Set("strikeOnlyBestAngle", JsonValue.Of(R(w.StrikeOnlyBestAngle)));
        measured.Set("strikeOnlyBestPower", JsonValue.Of(R(w.StrikeOnlyBestPower)));
        measured.Set("strikeOnlyStepDeg", JsonValue.Of(R(w.StrikeOnlyStepDeg)));
        measured.Set("strikeOnlyPowers", JsonValue.Of((double)w.StrikeOnlyPowers));
        if (w.Nominal != null)
        {
            measured.Set("nominalWorstMarginPx", JsonValue.Of(R(w.Nominal.MinMargin)));
            measured.Set("nominalCrawlPx", JsonValue.Of(R(w.Nominal.Crawl)));
        }

        authoring.Set("measured", measured);

        var portability = JsonValue.Object();
        foreach (var kv in w.Portability) portability.Set(kv.Key, JsonValue.Of(kv.Value));
        authoring.Set("portability", portability);

        var witnesses = JsonValue.Array();
        AddWitness(witnesses, "nominal", w.Nominal);
        AddWitness(witnesses, "edgeNegative", w.NegativeEdge);
        AddWitness(witnesses, "edgePositive", w.PositiveEdge);
        authoring.Set("witnesses", witnesses);
        root.Set("authoring", authoring);
        return Json.Write(root, true) + "\n";
    }

    private static void AddWitness(JsonValue array, string label, Solution s)
    {
        if (s == null) return;
        var o = JsonValue.Object();
        o.Set("label", JsonValue.Of(label));
        o.Set("angle", JsonValue.Of(R(s.AngleDeg)));
        o.Set("power", JsonValue.Of(R(s.Power)));
        o.Set("releaseArc", JsonValue.Of(R(s.ReleaseArc)));
        o.Set("crawl", JsonValue.Of(R(s.Crawl)));
        o.Set("minMargin", JsonValue.Of(R(s.MinMargin)));
        o.Set("objectCount", JsonValue.Of((double)s.Result.ObjectCount));
        var caps = JsonValue.Array();
        foreach (var cap in s.Result.ObjectCaptures())
        {
            var co = JsonValue.Object();
            co.Set("ballId", JsonValue.Of((double)cap.BallId));
            co.Set("pocketIndex", JsonValue.Of((double)cap.PocketIndex));
            co.Set("marginPx", JsonValue.Of(R(cap.Margin)));
            co.Set("travelPx", JsonValue.Of(R(cap.Travel)));
            caps.Add(co);
        }

        o.Set("captures", caps);
        array.Add(o);
    }

    private static double R(double v) => double.IsNaN(v) || double.IsInfinity(v)
        ? 0.0
        : Math.Round(v, 6, MidpointRounding.AwayFromZero);
}

/// <summary>Prints the measured table (also the source of godot/levels/README.md).</summary>
public static class Reporter
{
    public static bool AllAccepted(List<AuthoredLevel> levels)
    {
        var ok = levels.Count > 0;
        foreach (var l in levels)
        {
            var m = l.Measured;
            var accepted = m.AngleHalfWidthMarginDeg >= l.AngleFloorDeg - 1e-9
                           && m.PowerWindow >= l.PowerFloor - 1e-9
                           && m.SpawnGapPx >= Geo.TwoR - 1e-9
                           && m.StrikeOnlyBest < m.ObjectCount
                           && m.CorridorLengthPx >= l.ArcFloor - 1e-9
                           && m.NominalWorstMarginPx >= l.MarginFloor - 1e-9;
            if (!accepted) ok = false;
            Console.WriteLine($"  {(accepted ? "PASS" : "FAIL")}  {l.Id}: objects={m.ObjectCount} " +
                              $"marginWindow=+-{Geo.N(m.AngleHalfWidthMarginDeg)}deg (bar {Geo.N(l.AngleFloorDeg)}) " +
                              $"powerWindow={Geo.N(m.PowerWindow)}px (bar {Geo.N(l.PowerFloor)}) " +
                              $"spawnGap={Geo.N(m.SpawnGapPx)}px strikeOnlyBest={m.StrikeOnlyBest}/{m.ObjectCount} " +
                              $"nominalMargin={Geo.N(m.NominalWorstMarginPx)}px");
            foreach (var kv in l.Portability)
                Console.WriteLine($"        portability {kv.Key}: {kv.Value}");
        }

        return ok;
    }

    public static void PrintTable(List<AuthoredLevel> levels)
    {
        Console.WriteLine();
        Console.WriteLine("| level | objects | spawn gap | angle win (clear) | angle win (margin>=8) | drag window | cue-start | strike-only best | corridor | nominal margin |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var l in levels)
        {
            var m = l.Measured;
            Console.WriteLine($"| `{l.Id}` | {m.ObjectCount} | {Geo.N(m.SpawnGapPx)} px | " +
                              $"+-{Geo.N(m.AngleHalfWidthDeg)} deg | +-{Geo.N(m.AngleHalfWidthMarginDeg)} deg | " +
                              $"{Geo.N(m.PowerMin)}-{Geo.N(m.PowerMax)} px | +-{Geo.N(m.CueStartHalfWidthPx)} px | " +
                              $"{m.StrikeOnlyBest}/{m.ObjectCount} | {Geo.N(m.CorridorLengthPx)} px | {Geo.N(m.NominalWorstMarginPx)} px |");
        }
    }
}
