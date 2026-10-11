using System;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// The front end's data rules: which levels are playable, how difficulty and stars are derived, and
/// how unlocking progresses.
///
/// These are tested in the gate rather than left to the menu because they are the rules a player
/// feels most sharply: a level missing from the list, a star awarded wrongly, or a level that never
/// unlocks are all invisible to a headless physics test and obvious to a human.
/// </summary>
public static class MenuTests
{
    /// <summary>Every authored, playable level must be discoverable from res://levels.</summary>
    public static void M01_CatalogDiscoversPlayableLevels(TestRunner t)
    {
        t.Section("M01 - the level catalog discovers every playable level");

        LevelCatalog.ResetProgress();
        var all = LevelCatalog.All();

        t.CheckAtLeast("at least five playable levels are discovered", all.Count, 5);
        t.Check("no level has an empty id", all.TrueForAll(e => !string.IsNullOrEmpty(e.Id)));
        t.Check("no level has an empty title", all.TrueForAll(e => !string.IsNullOrEmpty(e.Title)));
        t.Check("every level has at least one object ball", all.TrueForAll(e => e.ObjectCount >= 1));

        // The shipped demo level must be present and must be first, because it is what "start" opens.
        t.Check("the shipped level is in the catalog", all.Exists(e => e.Id == "corridor"));
        t.Check("the shipped level sorts first", all.Count > 0 && all[0].Id == "corridor");

        // Fixtures that are not puzzles must NOT be offered. h5_rack is a 10-ball rack fixture and
        // straight_line is a corridor fixture; neither is an authored puzzle.
        t.Check("the 10-ball rack fixture is not offered as a level",
            !all.Exists(e => e.Id == "h5_rack"));

        var sb = new System.Text.StringBuilder();
        foreach (var e in all) sb.Append($"{e.Id}(obj={e.ObjectCount},diff={e.Difficulty},hole={(e.HasWhiteHole ? "Y" : "N")}) ");
        t.Check("M01 evidence", true, sb.ToString());
    }

    /// <summary>Unlock rules must be a simple, testable progression with the first level always open.</summary>
    public static void M02_UnlockProgressionIsConsistent(TestRunner t)
    {
        t.Section("M02 - unlocking is consistent and starts at level 1");

        var entries = new System.Collections.Generic.List<LevelEntry>
        {
            new LevelEntry { Id = "a", Stars = 0 },
            new LevelEntry { Id = "b", Stars = 0 },
            new LevelEntry { Id = "c", Stars = 0 },
        };

        LevelCatalog.ApplyUnlockRules(entries);
        t.Check("the first level is always unlocked", entries[0].Unlocked);
        t.Check("the second level starts locked", !entries[1].Unlocked);
        t.Check("the third level starts locked", !entries[2].Unlocked);

        entries[0].Stars = 1;
        LevelCatalog.ApplyUnlockRules(entries);
        t.Check("clearing level 1 unlocks level 2", entries[1].Unlocked);
        t.Check("clearing level 1 does NOT unlock level 3", !entries[2].Unlocked);

        entries[1].Stars = 2;
        LevelCatalog.ApplyUnlockRules(entries);
        t.Check("clearing level 2 unlocks level 3", entries[2].Unlocked);

        t.Check("M02 evidence", true, "first-always-open, strictly sequential unlocking");
    }

    /// <summary>Stars must follow one rule, and never exceed the maximum.</summary>
    public static void M03_StarRuleIsBounded(TestRunner t)
    {
        t.Section("M03 - the star rule is bounded and monotone");

        t.Check("clearing without precision earns one star", LevelCatalog.RateStars(9, 3) == 1);
        t.Check("few rewinds earns a second star", LevelCatalog.RateStars(2, 3) == 2);
        t.Check("no white hole needed earns the third", LevelCatalog.RateStars(2, 0) == 3);
        t.Check("a perfect run earns three", LevelCatalog.RateStars(1, 0) == 3);

        // Monotone: doing better must never award fewer stars.
        var prev = 0;
        var monotone = true;
        for (var rewinds = 6; rewinds >= 0; rewinds--)
        {
            var s = LevelCatalog.RateStars(rewinds, 0);
            if (s < prev) monotone = false;
            prev = s;
        }

        t.Check("using fewer rewinds never lowers the rating", monotone);

        // And the scale is bounded, which the level select depends on when drawing three slots.
        var bounded = true;
        for (var r = 0; r <= 20; r++)
            for (var h = 0; h <= 5; h++)
            {
                var s = LevelCatalog.RateStars(r, h);
                if (s < 0 || s > 3) bounded = false;
            }

        t.Check("stars are always within 0..3", bounded);
        t.Check("M03 evidence", true, "1 = cleared, +1 <= 2 rewinds, +1 no white hole");
    }

    /// <summary>Saving and reloading a result must round-trip, and only improve.</summary>
    public static void M04_ProgressRoundTrips(TestRunner t)
    {
        t.Section("M04 - progress persists and never regresses");

        LevelCatalog.ResetProgress();
        var all = LevelCatalog.All();
        t.CheckAtLeast("there is a level to record against", all.Count, 1);
        var id = all[0].Id;

        t.Check("a fresh profile has no stars", LevelCatalog.All()[0].Stars == 0);

        var earned = LevelCatalog.RecordResult(id, 5, 2);   // deliberately mediocre
        t.Check("a plain clear records one star", earned == 1, "earned=" + earned);
        t.Check("the star survives a reload", LevelCatalog.All().Exists(e => e.Id == id && e.Stars == 1));

        var better = LevelCatalog.RecordResult(id, 1, 0);   // perfect
        t.Check("a better run records three stars", better == 3, "earned=" + better);
        t.Check("the better score is kept", LevelCatalog.All().Exists(e => e.Id == id && e.Stars == 3));

        var worse = LevelCatalog.RecordResult(id, 9, 5);
        t.Check("a worse run does not lower the stored score", worse == 3, "earned=" + worse);

        t.Check("the best rewind count is recorded", LevelCatalog.BestRewinds(id) == 1,
            "best=" + LevelCatalog.BestRewinds(id));

        LevelCatalog.ResetProgress();   // leave the machine clean for the next run
        t.Check("reset clears the profile", LevelCatalog.All()[0].Stars == 0);

        t.Check("M04 evidence", true, "record -> reload -> improve -> no regression -> reset");
    }
}
