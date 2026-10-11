using System;
using System.Collections.Generic;
using Godot;
using Rewind.Sim;

namespace Rewind.Game;

/// <summary>
/// One row of the level-select grid.
/// </summary>
public sealed class LevelEntry
{
    public string Id = "";
    public string Title = "";
    public string Subtitle = "";
    public int ObjectCount;
    public bool HasWhiteHole;

    /// <summary>1..3 difficulty dots, derived from the authoring tool's measured clearing window
    /// when it is present: a wider window is a more forgiving level. Not hand-assigned, so it
    /// cannot drift away from the measured data.</summary>
    public int Difficulty = 1;

    /// <summary>Best result recorded on this machine; 0 means never cleared.</summary>
    public int Stars;
    public bool Unlocked;
}

/// <summary>
/// The list of playable levels plus the small amount of progress the game persists.
///
/// Levels are DISCOVERED from res://levels rather than hard-coded, because five levels are already
/// on disk (corridor + l1..l4) and hard-coding them would guarantee that adding a level is a code
/// change instead of a data change. Only files that are actually playable are listed: a file must
/// load, must have a cue ball, and must have at least one object ball.
/// </summary>
public static class LevelCatalog
{
    private const string SavePath = "user://progress.cfg";
    private const string Section = "progress";

    /// <summary>Deterministic display order. Anything not listed sorts after it, alphabetically.</summary>
    private static readonly string[] PreferredOrder =
    {
        "corridor", "l1_midrail_reverse", "l2_midrail_mirrored", "l3_deeper_walk", "l4_offset_line",
    };

    /// <summary>
    /// Kernel fixtures that live in res://levels but are NOT puzzles and must never appear in the
    /// level select. They are valid level files with a cue ball and object balls, so the shape test
    /// alone cannot exclude them: h5_rack is the 10-ball rack used for regression, and straight_line
    /// is a geometric probe. Offering them would hand the player an unsolvable "level".
    /// </summary>
    private static readonly string[] Fixtures =
    {
        "h5_rack", "straight_line",
    };

    private static List<LevelEntry> _cache;

    public static List<LevelEntry> All()
    {
        if (_cache != null) return _cache;
        _cache = Build();
        return _cache;
    }

    /// <summary>Call after writing progress so the next All() re-reads it.</summary>
    public static void Invalidate() => _cache = null;

    private static List<LevelEntry> Build()
    {
        var entries = new List<LevelEntry>();
        var ids = LevelLoader.ListLevelIds();

        foreach (var id in ids)
        {
            if (Array.IndexOf(Fixtures, id) >= 0) continue;

            LevelData level;
            try
            {
                level = LevelLoader.LoadById(id);
            }
            catch (Exception)
            {
                continue;   // not a playable level file (fixtures, malformed, reserved)
            }

            var cues = 0;
            var objects = 0;
            foreach (var b in level.Balls)
            {
                if (b.Type == BallType.Cue) cues++;
                else objects++;
            }

            if (cues != 1 || objects < 1) continue;

            entries.Add(new LevelEntry
            {
                Id = id,
                Title = string.IsNullOrEmpty(level.Name) ? id : level.Name,
                Subtitle = $"{objects} 颗目标球",
                ObjectCount = objects,
                HasWhiteHole = level.WhiteHoles.Count > 0,
                Difficulty = DifficultyFrom(level, id),
            });
        }

        entries.Sort((a, b) =>
        {
            var ia = Array.IndexOf(PreferredOrder, a.Id);
            var ib = Array.IndexOf(PreferredOrder, b.Id);
            if (ia < 0) ia = int.MaxValue;
            if (ib < 0) ib = int.MaxValue;
            if (ia != ib) return ia.CompareTo(ib);
            return string.CompareOrdinal(a.Id, b.Id);
        });

        LoadProgress(entries);
        ApplyUnlockRules(entries);
        return entries;
    }

    /// <summary>
    /// Difficulty from the MEASURED clearing window the authoring tool recorded, not from opinion:
    /// a wider window means a more forgiving level. Falls back to object count for files that carry
    /// no authoring block.
    /// </summary>
    private static int DifficultyFrom(LevelData level, string id)
    {
        var window = ReadAngleHalfWidth(id);
        if (window > 0.0)
        {
            if (window >= 3.0) return 1;
            if (window >= 2.4) return 2;
            return 3;
        }

        return Math.Min(3, Math.Max(1, level.Balls.Count - 1));
    }

    /// <summary>Reads authoring.measured.angleHalfWidthMarginDeg straight from the level JSON.</summary>
    private static double ReadAngleHalfWidth(string id)
    {
        try
        {
            using var f = FileAccess.Open($"{LevelLoader.LevelDirectory}/{id}.json", FileAccess.ModeFlags.Read);
            if (f == null) return 0.0;
            // Fully qualified: Godot also defines a `Json` type, so the bare name is ambiguous.
            var root = Rewind.Sim.Json.Parse(f.GetAsText());
            var authoring = root.Get("authoring");
            if (authoring == null) return 0.0;
            var measured = authoring.Get("measured");
            if (measured == null) return 0.0;
            return measured.Num("angleHalfWidthMarginDeg", 0.0);
        }
        catch (Exception)
        {
            return 0.0;
        }
    }

    // ---------------------------------------------------------------- progress

    private static void LoadProgress(List<LevelEntry> entries)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SavePath) != Error.Ok) return;

        foreach (var e in entries)
        {
            e.Stars = (int)cfg.GetValue(Section, e.Id + "_stars", 0);
            if (e.Stars < 0) e.Stars = 0;
            if (e.Stars > 3) e.Stars = 3;
        }
    }

    /// <summary>
    /// The first level is always open; each later level unlocks once the previous one has been
    /// cleared. Keeping this a pure function of the entries means the rule is testable and cannot
    /// drift between the menu and the play scene.
    /// </summary>
    public static void ApplyUnlockRules(List<LevelEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].Unlocked = i == 0 || entries[i - 1].Stars > 0;
        }
    }

    /// <summary>Records a clear and returns the star rating earned for it.</summary>
    public static int RecordResult(string id, int rewindsUsed, int whiteHoleUses = 0)
    {
        var stars = RateStars(rewindsUsed, whiteHoleUses);
        var cfg = new ConfigFile();
        cfg.Load(SavePath);

        var key = id + "_stars";
        var previous = (int)cfg.GetValue(Section, key, 0);
        if (stars > previous) cfg.SetValue(Section, key, stars);
        if (rewindsUsed >= 0)
        {
            var bestKey = id + "_best_rewinds";
            var best = (int)cfg.GetValue(Section, bestKey, int.MaxValue);
            if (rewindsUsed < best) cfg.SetValue(Section, bestKey, rewindsUsed);
        }

        cfg.Save(SavePath);
        Invalidate();
        return Math.Max(stars, previous);
    }

    /// <summary>
    /// Star rule, kept in one place so the HUD, the result screen and the save file cannot disagree.
    /// Tuned for a demo slice: clearing at all is worth a star, and precision is rewarded on top.
    /// </summary>
    public static int RateStars(int rewindsUsed, int whiteHoleUses)
    {
        var stars = 1;                                  // cleared
        if (rewindsUsed <= 2) stars++;                  // efficient use of the rewind
        if (whiteHoleUses == 0) stars++;                // no energy rescue needed
        return stars;
    }

    public static int BestRewinds(string id)
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SavePath) != Error.Ok) return -1;
        return (int)cfg.GetValue(Section, id + "_best_rewinds", -1);
    }

    public static int TotalStars()
    {
        var total = 0;
        foreach (var e in All()) total += e.Stars;
        return total;
    }

    public static int MaxStars() => All().Count * 3;

    /// <summary>Index of the first level the player has not cleared, for the "continue" button.</summary>
    public static int NextUnclearedIndex()
    {
        var all = All();
        for (var i = 0; i < all.Count; i++)
            if (all[i].Stars == 0) return i;
        return Math.Max(0, all.Count - 1);
    }

    /// <summary>Test-only: wipes progress. Used by the gate so tests never depend on this machine.</summary>
    public static void ResetProgress()
    {
        var path = ProjectSettings.GlobalizePath(SavePath);
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch (Exception)
        {
            // A missing save is the desired end state; a locked file is not worth failing over.
        }

        Invalidate();
    }
}
