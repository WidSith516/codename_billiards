using System;
using System.Collections.Generic;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// Can the game actually clear MORE THAN TWO object balls?
///
/// This is the question that blocks the whole "more balls per level" feature, and it is asked directly
/// rather than assumed.
///
/// Already established by measurement: ONE strike plus ONE rewind pots about two balls at most. Adding
/// even a single plain object ball to the shipped corridor made it unsolvable, always finishing 2/2 when
/// the win needs 3/3. So ball count and rewind allowance are the SAME feature and must be decided together.
///
/// The probe places a cue plus N object balls in a simple ring, sweeps the full strike space, and walks up
/// to a given number of rewind cycles. It reports the best N that can be cleared and the witness that does
/// it, which is what the level files then get authored from.
/// </summary>
public static class BallCountProbe
{
    private static LevelData Ring(int objectCount, int maxSegments, double maxDistance)
    {
        var cfg = new PhysicsConfig();
        var lvl = LevelData.EmptyTable(cfg);
        lvl.Id = "ring_probe";
        lvl.Balls.Clear();
        lvl.AddStandardPockets();
        lvl.Budget = new RewindBudget { MaxSegments = maxSegments, MaxDistance = maxDistance };

        var midX = (lvl.TableLeft + lvl.TableRight) * 0.5;   // 520
        var midY = (lvl.TableTop + lvl.TableBottom) * 0.5;   // 280

        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = midX, Y = midY });

        // Object balls spread evenly around the cue, well clear of the pockets so the pot has to be earned.
        for (var i = 0; i < objectCount; i++)
        {
            var a = Math.Tau * i / objectCount;
            lvl.Balls.Add(new BallSpawn
            {
                Id = i + 1,
                Type = BallType.Object,
                DeclaredType = "object",
                X = midX + Math.Cos(a) * 150.0,
                Y = midY + Math.Sin(a) * 110.0,
            });
        }

        return lvl;
    }

    /// <summary>
    /// B01's ring layout was the WRONG shape for the search: with the cue in the middle and object balls
    /// spread evenly around it, no strike can drive more than one ball toward a pocket.
    ///
    /// The right shape is a QUEUE: balls stacked along one line where a single strike drives the whole line
    /// toward a pocket, so one shot can pot several at once. That is also the only shape that can work,
    /// because measurement says a single strike plus rewinds pots about two balls -- so the extra balls
    /// must come from ONE strike potting MORE than one ball, not from more strikes.
    ///
    /// This probe places the cue, a struck ball, and then extra balls in the SAME COLUMN heading for the
    /// bottom-middle pocket, and asks whether the multi-ball stack clears.
    /// </summary>
    private static LevelData Queue(int extraBalls, int maxSegments, double maxDistance)
    {
        var cfg = new PhysicsConfig();
        var lvl = LevelData.EmptyTable(cfg);
        lvl.Id = "queue_probe";
        lvl.Balls.Clear();
        lvl.AddStandardPockets();
        lvl.Budget = new RewindBudget { MaxSegments = maxSegments, MaxDistance = maxDistance };

        const double x = 520.0;

        // Cue above, first target below it, then the extra balls stacked BELOW that, in the same column,
        // each spaced more than 2R apart so they start legal and are driven as a queue.
        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = x, Y = 415 });
        lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = x, Y = 455 });

        for (var i = 0; i < extraBalls; i++)
        {
            lvl.Balls.Add(new BallSpawn
            {
                Id = i + 2,
                Type = BallType.Object,
                DeclaredType = "object",
                X = x,
                // Above the struck ball, toward the TOP-middle pocket, so the cue drives the struck ball
                // DOWN into the bottom pocket while the upper stack is a separate line. Spaced 28 px.
                Y = 385 - i * 28,
            });
        }

        return lvl;
    }

    /// <summary>Does stacking extra balls in the striking column keep the level clearable?</summary>
    public static void B03_QueueShape(TestRunner t)
    {
        t.Section("B03 - balls stacked in the striking column (combo shape)");

        foreach (var extra in new[] { 1, 2, 3 })
        {
            var lvl = Queue(extra, maxSegments: 3, maxDistance: 12000.0);

            // 1 degree steps, NOT 2. A 2 degree step over 84..96 already produced a false negative on a
            // configuration that the shipped witness clears at -93, which lies between samples: the strike
            // window on these levels is a couple of degrees wide, so sampling that coarsely finds nothing.
            var hits = SolverProbe.Solve(lvl, maxHits: 2, angleLo: 84, angleHi: 96, angleStep: 1.0,
                cycles: 3, perCycleArcStep: 70);

            var total = extra + 1;
            if (hits.Count == 0)
            {
                t.Check($"B03 queue with {total} balls clearable", false, "no solution found");
                continue;
            }

            var h = hits[0];
            t.Check($"B03 queue with {total} balls clearable", true,
                $"hits={hits.Count} angle={h.Angle:F0} power={h.Power:F0} arc={h.ReleaseArc:F0} " +
                $"strikeOnly={h.StrikeOnly}/{h.Total} final={h.Potted}/{h.Total}");
        }
    }

    /// <summary>
    /// Which extra-ball POSITIONS keep a level solvable — tested cheaply, with a narrow sweep and few
    /// cycles, because the expensive question ("what is the tolerance window") is the authoring tool's job
    /// and not this probe's.
    ///
    /// Context that makes this the right probe: a full-circle, 3-cycle search over a ring layout found no
    /// solution for 3 balls, and a queue stacked in the striking COLUMN also found none — because a ball
    /// placed between the cue and its target simply blocks the only shot. So the extra balls have to go
    /// somewhere that does not intercept the cue's line, and the probe is here to find those places
    /// instead of guessing them.
    /// </summary>
    public static void B04_WhichPositionsKeepItSolvable(TestRunner t)
    {
        t.Section("B04 - which extra-ball positions keep the level solvable");

        var baseLevel = LevelLoader.LoadById("corridor");
        var baseCount = baseLevel.Balls.Count;

        var candidates = new (double X, double Y, string Type)[]
        {
            (740, 200, "object"), (300, 200, "object"),
            (740, 400, "object"), (300, 400, "object"),
            (420, 150, "heavy"), (620, 150, "heavy"),
            (420, 420, "anchor"), (620, 420, "anchor"),
            (840, 150, "bomb"), (200, 280, "bomb"),
            (620, 240, "object"), (420, 330, "object"),
        };

        var solvable = new List<string>();

        foreach (var (x, y, type) in candidates)
        {
            var probe = LevelLoader.LoadById("corridor");
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

            // 1 degree steps over the known corridor window, 64 px of arc. Cheap and decisive.
            var hits = SolverProbe.Solve(probe, maxHits: 1, angleLo: 86, angleHi: 100, angleStep: 1.0,
                cycles: 2, perCycleArcStep: 64);

            if (hits.Count == 0) continue;

            var h = hits[0];
            solvable.Add($"{type}({x:F0},{y:F0}) a={h.Angle:F0} p={h.Power:F0} arc={h.ReleaseArc:F0} " +
                         $"final={h.Potted}/{h.Total}");
        }

        t.Check("B04 at least one extra-ball position keeps the level solvable", solvable.Count > 0,
            solvable.Count > 0 ? string.Join(" | ", solvable) : "none of the tested positions worked");
    }

    /// <summary>
    /// Does allowing a SECOND strike actually make more balls reachable? This is the claim the whole
    /// increasing-ball plan rests on, so it is measured rather than assumed.
    ///
    /// The probe takes the shipped corridor, adds one ball in a position B04 proved does not block the
    /// shot, and compares two configurations that differ ONLY in the shot allowance: 1 shot versus 2. If
    /// the extra ball is reachable with 2 shots and unreachable with 1, the change does exactly what it is
    /// for.
    /// </summary>
    public static void B05_SecondShotUnlocksMoreBalls(TestRunner t)
    {
        t.Section("B05 - a second strike is what makes extra balls reachable");

        LevelData Build(int maxShots)
        {
            var lvl = LevelLoader.LoadById("corridor");
            lvl.Config.MaxShots = maxShots;
            lvl.Budget = new RewindBudget { MaxSegments = 4, MaxDistance = 8000.0 };
            lvl.Balls.Add(new BallSpawn
            {
                Id = lvl.Balls.Count,
                Type = BallType.Object,
                DeclaredType = "object",
                X = 620.0,
                Y = 240.0,
            });
            return lvl;
        }

        var oneShot = SolverProbe.Solve(Build(1), maxHits: 1, angleLo: -100, angleHi: -80, angleStep: 1.0,
            cycles: 3, perCycleArcStep: 70);
        var twoShot = SolverProbe.Solve(Build(2), maxHits: 1, angleLo: -100, angleHi: -80, angleStep: 1.0,
            cycles: 3, perCycleArcStep: 70);

        t.Check("with ONE strike the extra ball is NOT reachable", oneShot.Count == 0,
            oneShot.Count == 0 ? "no solution, as predicted" :
            $"unexpectedly solved: final={oneShot[0].Potted}/{oneShot[0].Total}");

        t.Check("with TWO strikes the level becomes clearable", twoShot.Count > 0,
            twoShot.Count > 0
                ? $"angle={twoShot[0].Angle:F0} power={twoShot[0].Power:F0} arc={twoShot[0].ReleaseArc:F0} " +
                  $"strikeOnly={twoShot[0].StrikeOnly}/{twoShot[0].Total} final={twoShot[0].Potted}/{twoShot[0].Total}"
                : "still no solution -- the second shot does not help");

        t.Check("B05 evidence", true,
            $"1shot={oneShot.Count} solutions, 2shot={twoShot.Count} solutions, objects={Build(1).Balls.Count - 1}");
    }

    /// <summary>
    /// For each object count, report whether a multi-rewind solution exists. The budget is generous so the
    /// question being answered is "is this POTTABLE at all", not "is it affordable".
    /// </summary>
    public static void B01_HowManyBallsCanBeCleared(TestRunner t)
    {
        t.Section("B01 - how many object balls can one strike plus rewinds clear");

        foreach (var n in new[] { 2, 3, 4 })
        {
            var lvl = Ring(n, maxSegments: 4, maxDistance: 12000.0);

            // 3 cycles, each walking deeper. Coarse grid so this stays affordable inside the gate.
            var hits = SolverProbe.Solve(lvl, maxHits: 3, angleLo: 0, angleHi: 0, angleStep: 0,
                cycles: 3, perCycleArcStep: 70);

            if (hits.Count == 0)
            {
                t.Check($"B01 {n} object balls clearable", false,
                    $"no solution within 3 rewind cycles (budget 4 segments / 12000 px)");
                continue;
            }

            var h = hits[0];
            t.Check($"B01 {n} object balls clearable", true,
                $"hits={hits.Count} angle={h.Angle:F0} power={h.Power:F0} arc={h.ReleaseArc:F0} " +
                $"strikeOnly={h.StrikeOnly}/{h.Total} final={h.Potted}/{h.Total}");
        }
    }

    /// <summary>
    /// The reverse question, and the one that decides whether "more balls" is a real feature: how many
    /// rewind segments does each count actually NEED?
    /// </summary>
    public static void B02_SegmentsNeededPerCount(TestRunner t)
    {
        t.Section("B02 - minimum rewind segments needed, per object count");

        foreach (var n in new[] { 2, 3, 4 })
        {
            var found = -1;
            var evidence = "";

            for (var cycles = 1; cycles <= 4 && found < 0; cycles++)
            {
                var lvl = Ring(n, maxSegments: cycles, maxDistance: 12000.0);
                var hits = SolverProbe.Solve(lvl, maxHits: 1, angleLo: 0, angleHi: 0, angleStep: 0,
                    cycles: cycles, perCycleArcStep: 70);

                if (hits.Count > 0)
                {
                    found = cycles;
                    evidence = $"angle={hits[0].Angle:F0} power={hits[0].Power:F0} arc={hits[0].ReleaseArc:F0} " +
                               $"strikeOnly={hits[0].StrikeOnly}/{hits[0].Total} final={hits[0].Potted}/{hits[0].Total}";
                }
            }

            t.Check($"B02 {n} balls needs a known segment count", found > 0,
                found > 0 ? $"needs {found} segment(s) :: {evidence}" : "unsolvable within 4 segments");
        }
    }
}
