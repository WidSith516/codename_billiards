using System;
using Godot;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// The rail ripple and the rebound streak.
///
/// WHY THIS IS A TEST AND NOT A SCREENSHOT: the effect is transient. A capture is taken N frames after
/// the state is prepared, and the effect lifetime is under a second, so a screenshot can only show it if
/// the capture path deliberately holds it. That makes "the screenshot looks right" a statement about the
/// capture plumbing as much as about the effect -- and indeed the first two attempts produced an empty
/// table while the code was already correct, and a third produced a ripple that was drawn at the contact
/// point only because the effect had been spawned at birth.
///
/// So the geometry is asserted directly here, and the screenshots are the illustration rather than the
/// proof.
/// </summary>
public static class RailEffectTests
{
    private static Effects Fresh() => new Effects();

    /// <summary>E01: a boosted rail hit creates exactly one ripple and one streak.</summary>
    public static void E01_BoostedHitCreatesRippleAndStreak(TestRunner t)
    {
        t.Section("E01 - a rail hit spawns a ripple and a rebound streak");

        var fx = Fresh();
        t.Check("nothing is live before the hit", fx.Count == 0, "count=" + fx.Count);

        fx.RailHit(1, 336.0f, new Vector2(963.0f, 336.0f), new Vector2(-1.0f, 0.1f),
            Palette.RailBoost, boosted: true);

        t.Check("a boosted hit creates two effects (ripple + streak)", fx.Count == 2, "count=" + fx.Count);
    }

    /// <summary>E02: an UNboosted hit shows the bounce but not the payout.</summary>
    public static void E02_UnboostedHitShowsNoPayout(TestRunner t)
    {
        t.Section("E02 - a hit with no allowance left shows a bounce but no payout light");

        var fx = Fresh();
        fx.RailHit(1, 336.0f, new Vector2(963.0f, 336.0f), new Vector2(-1.0f, 0.1f),
            Palette.RailBoost, boosted: false);

        // The ripple still appears -- the cushion DID deform -- but the streak, which is the visual
        // signature of "the rail paid you", does not. This is what lets the player see the allowance run
        // out instead of concluding the mechanic is broken.
        t.Check("an unboosted hit creates only the ripple", fx.Count == 1, "count=" + fx.Count);
    }

    /// <summary>E03: effects expire, so the list cannot grow without bound.</summary>
    public static void E03_EffectsExpire(TestRunner t)
    {
        t.Section("E03 - effects expire and never accumulate");

        var fx = Fresh();
        for (var i = 0; i < 50; i++)
        {
            fx.RailHit(i % 4, 100.0f + i, new Vector2(100.0f + i, 100.0f), new Vector2(-1.0f, 0.0f),
                Palette.RailBoost, boosted: true);
        }

        t.CheckAtLeast("fifty hits produce live effects", fx.Count, 50);

        // A long frame: everything must be gone.
        fx.Update(5.0);
        t.Check("every effect expires after a long frame", fx.Count == 0, "count=" + fx.Count);

        // And it must stay bounded under sustained fire.
        //
        // The bound is NOT an arbitrary small number: an effect lives ~0.85 s and a rail hit spawns two of
        // them, so at 60 fps a ball bouncing on every frame legitimately holds ~2 x 0.85 x 60 = 102 live
        // effects. What must never happen is growth WITHOUT bound -- i.e. the count tracking the number of
        // hits ever made rather than the number of hits in the last second. The first version of this
        // assertion used 16, which was simply the wrong model of the mechanism.
        for (var round = 0; round < 200; round++)
        {
            fx.RailHit(1, 200.0f, new Vector2(900.0f, 200.0f), new Vector2(-1.0f, 0.0f),
                Palette.RailBoost, true);
            fx.Update(1.0 / 60.0);
        }

        var liveAfterFire = fx.Count;
        t.CheckAtMost("the live set is bounded by lifetime x spawn rate, not by total hits made",
            liveAfterFire, 120, "count=" + liveAfterFire);

        // Let everything drain and confirm the count returns to zero: proof it is a rolling window.
        fx.Update(5.0);
        t.Check("the set drains to empty once firing stops", fx.Count == 0, "count=" + fx.Count);

        // Firing for ten times as long must NOT produce a proportionally larger set.
        for (var round = 0; round < 2000; round++)
        {
            fx.RailHit(1, 200.0f, new Vector2(900.0f, 200.0f), new Vector2(-1.0f, 0.0f),
                Palette.RailBoost, true);
            fx.Update(1.0 / 60.0);
        }

        t.CheckAtMost("10x the firing time does not mean 10x the live set", fx.Count, 120,
            $"after200={liveAfterFire} after2000={fx.Count}");
    }

    /// <summary>E04: the reveal flourish reports a fraction that reaches 1.</summary>
    public static void E04_RevealCompletes(TestRunner t)
    {
        t.Section("E04 - the card reveal flourish completes");

        var fx = Fresh();
        t.CheckClose("with no reveal running the fraction reads as complete", fx.CardRevealT, 1.0f, 6, "");

        fx.CardReveal();
        t.Check("the reveal starts near zero", fx.CardRevealT < 0.2f, "t=" + fx.CardRevealT);
        fx.Update(0.25);
        t.Check("the reveal advances", fx.CardRevealT > 0.2f, "t=" + fx.CardRevealT);
        fx.Update(1.0);
        t.CheckClose("the reveal finishes and reports complete", fx.CardRevealT, 1.0f, 6, "");
    }

    /// <summary>E05: the contact WALL is recovered correctly from the reflected velocity.</summary>
    public static void E05_ContactWallIsRecoveredFromReflection(TestRunner t)
    {
        t.Section("E05 - the contacted wall is recovered from the reflected heading");

        // This is the fix for a real bug: the first implementation found the wall by testing whether the
        // ball's edge was within a slop of a wall RIGHT NOW. Effects are driven from per-frame stat deltas
        // and a frame runs several physics steps, so by the time the view noticed the counter had moved
        // the ball had already left the cushion, matched no wall, and the effect silently never appeared
        // (measured: effects=0 with railTransforms=1).
        //
        // The reflected heading is what the reflection wrote, so it identifies the wall exactly however
        // many steps have passed.
        int WallFor(double vx, double vy) => Effects.WallFromHeading(new Vec2(vx, vy));

        t.Check("heading left means it came off the RIGHT wall", WallFor(-500, 0) == 1,
            "wall=" + WallFor(-500, 0));
        t.Check("heading right means it came off the LEFT wall", WallFor(500, 0) == 0,
            "wall=" + WallFor(500, 0));
        t.Check("heading down (smaller y) means it came off the BOTTOM wall", WallFor(0, -500) == 3,
            "wall=" + WallFor(0, -500));
        t.Check("heading up means it came off the TOP wall", WallFor(0, 500) == 2,
            "wall=" + WallFor(0, 500));
        t.Check("a diagonal bounce reports the dominant axis", WallFor(-900, 20) == 1,
            "wall=" + WallFor(-900, 20));

        t.Check("E05 evidence", true,
            $"right={WallFor(-500, 0)} left={WallFor(500, 0)} " +
            $"bottom={WallFor(0, -500)} top={WallFor(0, 500)}");
    }
}
