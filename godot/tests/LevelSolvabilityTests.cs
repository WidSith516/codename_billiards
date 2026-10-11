using System;
using System.Collections.Generic;
using Godot;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// Does every level the level select offers actually clear under the CURRENT physics?
///
/// Why this exists: the level select now lists five levels, so "playable" is a promise the front end
/// makes to the player. Each level carries recorded witnesses in its authoring block, but those were
/// MEASURED UNDER THE OLD PHYSICS. Since then the cue's mass went 1.0 -> 2.0 -> 1.4, its restitution
/// to 0.92, object balls gained collisionBoost 1.45, and corridor gained a white hole. A recorded
/// witness is therefore a hypothesis, not a proof, and this test checks the hypothesis by replaying it.
///
/// This is deliberately a REPLAY, not a search: it must not silently "find" a solution the authoring
/// tool never verified. Either the recorded witness still works, or the level is reported broken.
/// </summary>
public static class LevelSolvabilityTests
{
    private sealed class Witness
    {
        public string Label = "";
        public double Angle;
        public double Power;
        public double ReleaseArc;
    }

    /// <summary>Reads authoring.witnesses from a level file. Returns an empty list if absent.</summary>
    private static List<Witness> ReadWitnesses(string id)
    {
        var list = new List<Witness>();
        using var f = FileAccess.Open($"{LevelLoader.LevelDirectory}/{id}.json", FileAccess.ModeFlags.Read);
        if (f == null) return list;

        var root = Rewind.Sim.Json.Parse(f.GetAsText());
        var authoring = root.Get("authoring");
        if (authoring == null) return list;
        var witnesses = authoring.Get("witnesses");
        if (witnesses == null || witnesses.Kind != JsonKind.Array) return list;

        foreach (var w in witnesses.Items)
        {
            list.Add(new Witness
            {
                Label = w.Text("label", "?"),
                Angle = w.Num("angle", 0.0),
                Power = w.Num("power", 0.0),
                ReleaseArc = w.Num("releaseArc", 0.0),
            });
        }

        return list;
    }

    /// <summary>Replays one witness end to end: strike, settle, rewind to the release arc, release, settle.</summary>
    private static bool TryWitness(LevelData level, Witness wit, out string detail)
    {
        detail = "";
        var w = new World(level);
        var total = w.ObjectBallCount;

        if (!w.Shot(wit.Angle, wit.Power))
        {
            detail = "strike rejected";
            return false;
        }

        w.Settle();
        var strikeOnly = w.PocketedObjectCount;
        if (w.Won)
        {
            detail = $"cleared by the strike alone ({strikeOnly}/{total})";
            return true;
        }

        if (!w.StartRewind())
        {
            detail = $"rewind refused after strike (strikeOnly={strikeOnly}/{total})";
            return false;
        }

        // Arc coordinate of the cursor along the recorded path, matching the meaning of `releaseArc`.
        var track = w.RewindTrack;
        var arcs = new double[track == null ? 0 : track.Length];
        for (var i = 1; i < arcs.Length; i++)
            arcs[i] = arcs[i - 1] + (track[i].Position - track[i - 1].Position).Length;

        var guard = 0;
        while (w.Rewinding && guard++ < 40000)
        {
            var cursor = w.RewindCursor;
            if (cursor >= 0 && cursor < arcs.Length && arcs[cursor] <= wit.ReleaseArc) break;
            w.Step();
        }

        var arcAtRelease = w.RewindCursor >= 0 && w.RewindCursor < arcs.Length ? arcs[w.RewindCursor] : double.NaN;
        w.EndRewind();
        w.Settle();

        detail = $"strikeOnly={strikeOnly}/{total} releaseArc={arcAtRelease:F1} final={w.PocketedObjectCount}/{total}";
        return w.Won;
    }

    /// <summary>
    /// Every level offered in the level select must clear under the current physics. A level with no
    /// recorded witness is reported as UNPROVEN rather than silently passed.
    /// </summary>
    public static void S01_EveryOfferedLevelClears(TestRunner t)
    {
        t.Section("S01 - every level in the level select clears under the current physics");

        var entries = LevelCatalog.All();
        t.CheckAtLeast("the level select offers levels", entries.Count, 1);

        var proved = new List<string>();
        var broken = new List<string>();
        var unproven = new List<string>();

        foreach (var e in entries)
        {
            var witnesses = ReadWitnesses(e.Id);
            if (witnesses.Count == 0)
            {
                unproven.Add(e.Id);
                continue;
            }

            LevelData level;
            try
            {
                level = LevelLoader.LoadById(e.Id);
            }
            catch (Exception ex)
            {
                broken.Add($"{e.Id}(load failed: {ex.Message})");
                continue;
            }

            var worked = false;
            var evidence = "";
            foreach (var wit in witnesses)
            {
                if (TryWitness(level, wit, out var detail))
                {
                    worked = true;
                    evidence = $"{wit.Label}: {detail}";
                    break;
                }

                if (evidence.Length == 0) evidence = $"{wit.Label}: {detail}";
            }

            if (worked) proved.Add($"{e.Id} [{evidence}]");
            else broken.Add($"{e.Id} [{evidence}]");
        }

        t.Check("no level fails to clear", broken.Count == 0,
            broken.Count == 0 ? "all clear" : string.Join(" | ", broken));

        t.Check("every level has a recorded witness (nothing is merely assumed solvable)",
            unproven.Count == 0,
            unproven.Count == 0 ? "all covered" : "unproven: " + string.Join(", ", unproven));

        t.Check("S01 evidence", true,
            $"cleared={proved.Count}/{entries.Count} :: " + string.Join(" ; ", proved));
    }

    /// <summary>
    /// The shipped demo level must be completable with a HUMAN-REALISTIC input, not just an exact
    /// recorded one: a player cannot hit -90.000 degrees. This sweeps a small window around the
    /// witness and requires a healthy fraction to clear, so the level is forgiving rather than a
    /// precision puzzle.
    /// </summary>
    public static void S02_ShippedLevelIsForgiving(TestRunner t)
    {
        t.Section("S02 - the shipped level is forgiving to a human hand");

        var level = LevelLoader.LoadById("corridor");
        const double nominalAngle = -90.0;
        const double nominalPower = 96.0;

        // +/- 1.0 degree in 0.25 degree steps and +/- 8 power in 4 power steps: roughly the accuracy
        // an unhurried player achieves with a mouse drag.
        var tried = 0;
        var cleared = 0;
        var bestDetail = "";

        foreach (var dAngle in new[] { 0.0, 0.5, -0.5, 1.0, -1.0, 0.25, -0.25 })
        {
            foreach (var dPower in new[] { 0.0, 4.0, -4.0, 8.0, -8.0 })
            {
                foreach (var releaseArc in new[] { 96.0, 102.0, 108.0, 114.0, 120.0 })
                {
                    tried++;
                    var w = new World(level);
                    var total = w.ObjectBallCount;
                    if (!w.Shot(nominalAngle + dAngle, nominalPower + dPower)) continue;
                    w.Settle();
                    if (!w.Won)
                    {
                        if (!w.StartRewind()) continue;

                        var track = w.RewindTrack;
                        var arcs = new double[track == null ? 0 : track.Length];
                        for (var i = 1; i < arcs.Length; i++)
                            arcs[i] = arcs[i - 1] + (track[i].Position - track[i - 1].Position).Length;

                        var guard = 0;
                        while (w.Rewinding && guard++ < 40000)
                        {
                            var cursor = w.RewindCursor;
                            if (cursor >= 0 && cursor < arcs.Length && arcs[cursor] <= releaseArc) break;
                            w.Step();
                        }

                        w.EndRewind();
                        w.Settle();
                    }

                    if (w.Won)
                    {
                        cleared++;
                        if (bestDetail.Length == 0)
                            bestDetail = $"first clear at dAngle={dAngle:+0.00;-0.00;0} dPower={dPower:+0;-0;0} releaseArc={releaseArc:F0}";
                    }
                }
            }
        }

        t.CheckAtLeast("a reasonable number of human-like inputs are tried", tried, 100);
        var rate = (double)cleared / tried;
        t.CheckAtLeast("at least 20% of human-like inputs clear the demo level", rate, 0.20);
        t.Check("S02 evidence", true, $"cleared {cleared}/{tried} = {rate * 100.0:F1}% :: {bestDetail}");
    }
}
