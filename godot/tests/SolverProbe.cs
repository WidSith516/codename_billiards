using System;
using System.Collections.Generic;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// A SEARCH based solvability probe: does there exist a strike plus one rewind that clears the level?
///
/// Why this exists, and why it is not the same as S01: S01 REPLAYS a witness the authoring tool recorded.
/// That is the right check for a level that already has one, but it cannot author new content -- and the
/// four l* levels carry an authoring budget of `1 / 0`, i.e. one rewind segment and no distance limit,
/// which is a TEST fixture value rather than anything a player could use. Growing those levels by hand
/// and hoping would have produced levels that cannot actually be cleared.
///
/// So this sweeps a human-sized grid of (angle, power, release arc) and reports the first combination that
/// wins. It is deliberately coarse: the point is to prove a level is clearable and to hand back a witness
/// good enough to seed the authoring block, not to characterise the tolerance window (the authoring tool
/// does that properly, offline).
/// </summary>
public static class SolverProbe
{
    public sealed class Hit
    {
        public double Angle;
        public double Power;
        public double ReleaseArc;
        public int Potted;
        public int Total;
        public int StrikeOnly;
    }

    /// <summary>
    /// One attempt with UP TO <paramref name="cycles"/> rewind cycles: strike, then repeatedly (settle,
    /// rewind back to an arc, release, settle) until the level is won or the cycles run out.
    ///
    /// Why cycles exist at all: ONE strike plus ONE rewind can only ever pot about two balls. That was
    /// measured, not assumed -- adding even a single plain object ball to the corridor made it unsolvable,
    /// always finishing 2/2 when the win needs 3/3. So "more balls" and "more rewinds" are the SAME
    /// feature, which is what the budget's segment count models.
    /// </summary>
    private static bool Attempt(LevelData level, double angle, double power, double releaseArc,
        int cycles, int perCycleArcStep, out Hit hit)
    {
        hit = null;
        var w = new World(level);
        var total = w.ObjectBallCount;
        if (!w.Shot(angle, power)) return false;
        w.Settle();

        var strikeOnly = w.PocketedObjectCount;

        for (var c = 0; c < cycles && !w.Won; c++)
        {
            // No budget left, or no history worth walking: stop cleanly rather than failing the whole
            // attempt, so a level that wins in fewer cycles is still reported as solvable.
            if (!w.StartRewind()) break;

            var track = w.RewindTrack;
            var arcs = new double[track == null ? 0 : track.Length];
            for (var i = 1; i < arcs.Length; i++)
                arcs[i] = arcs[i - 1] + (track[i].Position - track[i - 1].Position).Length;

            // Each cycle walks progressively deeper, so a level needing a shallow release first and a deep
            // one later is reachable.
            var target = releaseArc + c * perCycleArcStep;
            var guard = 0;
            while (w.Rewinding && guard++ < 60000)
            {
                var cursor = w.RewindCursor;
                if (cursor >= 0 && cursor < arcs.Length && arcs[cursor] <= target) break;
                w.Step();
            }

            w.EndRewind();
            w.Settle();
        }

        if (!w.Won) return false;

        hit = new Hit
        {
            Angle = angle,
            Power = power,
            ReleaseArc = releaseArc,
            Potted = w.PocketedObjectCount,
            Total = total,
            StrikeOnly = strikeOnly,
        };
        return true;
    }

    /// <summary>
    /// Sweep the grid and return every winning combination, capped so a pathological level cannot hang the
    /// gate. Coarse by design; see the class comment.
    /// </summary>
    public static List<Hit> Solve(LevelData level, int maxHits = 40,
        double angleLo = 180.0, double angleHi = 180.0, double angleStep = 0.0,
        int cycles = 2, int perCycleArcStep = 60)
    {
        var hits = new List<Hit>();

        // Full circle by default (the cue can be struck any way), 3 degree steps.
        var a0 = angleStep > 0.0 ? angleLo : -180.0;
        var a1 = angleStep > 0.0 ? angleHi : 180.0;
        var aStep = angleStep > 0.0 ? angleStep : 3.0;

        for (var angle = a0; angle <= a1 + 1e-9 && hits.Count < maxHits; angle += aStep)
        {
            for (var power = 60.0; power <= 170.0 && hits.Count < maxHits; power += 10.0)
            {
                for (var arc = 40.0; arc <= 260.0 && hits.Count < maxHits; arc += 20.0)
                {
                    if (Attempt(level, angle, power, arc, cycles, perCycleArcStep, out var hit))
                        hits.Add(hit);
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Reporting probe. Prints, for every offered level, whether a strike+rewind exists and the first
    /// witness found. Fails only when a level offered to the player is NOT solvable, which is a real
    /// defect; an unsolvable level must never reach the level select.
    /// </summary>
    public static void Z_SolveAll(TestRunner t)
    {
        t.Section("Z - search-based solvability of every offered level");

        foreach (var e in LevelCatalog.All())
        {
            LevelData level;
            try
            {
                level = LevelLoader.LoadById(e.Id);
            }
            catch (Exception ex)
            {
                t.Check($"Z {e.Id} loads", false, ex.Message);
                continue;
            }

            var hits = Solve(level);
            if (hits.Count == 0)
            {
                t.Check($"Z {e.Id} is solvable by strike + rewind", false, "no winning combination found");
                continue;
            }

            var h = hits[0];
            t.Check($"Z {e.Id} is solvable by strike + rewind", true,
                $"{hits.Count}{(hits.Count >= 40 ? "+" : "")} hits :: first angle={h.Angle:F0} power={h.Power:F0} " +
                $"releaseArc={h.ReleaseArc:F0} strikeOnly={h.StrikeOnly}/{h.Total} final={h.Potted}/{h.Total}");
        }
    }

    /// <summary>
    /// Authoring probe: takes a level, tries a list of candidate EXTRA balls, and reports for each whether
    /// the level is still solvable plus the witness that clears it.
    /// </summary>
    public static void Z_Grow(TestRunner t, string levelId, (double X, double Y, string Type)[] candidates)
    {
        t.Section($"Z grow - {levelId}");

        var baseLevel = LevelLoader.LoadById(levelId);
        var baseCount = baseLevel.Balls.Count;

        foreach (var (x, y, type) in candidates)
        {
            var probe = LevelLoader.LoadById(levelId);
            probe.Balls.Clear();
            foreach (var b in baseLevel.Balls) probe.Balls.Add(b);
            probe.Balls.Add(new BallSpawn
            {
                Id = baseCount,
                Type = type switch
                {
                    "heavy" => BallType.Heavy,
                    "bomb" => BallType.Bomb,
                    "anchor" => BallType.Anchor,
                    _ => BallType.Object,
                },
                DeclaredType = type,
                X = x,
                Y = y,
            });

            // FULL 360 sweep. A narrower window produced FALSE NEGATIVES: corridor`s own winning strike is
            // at -93 deg, outside any near-vertical window centred on +90, so restricting the range made
            // solvable candidates look impossible.
            var hits = Solve(probe, 6);
            if (hits.Count == 0)
            {
                t.Check($"Z {levelId} +{type}({x:F0},{y:F0}) still solvable", false, "not solvable");
                continue;
            }

            var h = hits[0];
            t.Check($"Z {levelId} +{type}({x:F0},{y:F0}) still solvable", true,
                $"hits={hits.Count} angle={h.Angle:F0} power={h.Power:F0} arc={h.ReleaseArc:F0} " +
                $"strikeOnly={h.StrikeOnly}/{h.Total} final={h.Potted}/{h.Total}");
        }
    }
}
