using System;
using Godot;
using Rewind.Game;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// The rail impact: the elastic deformation and the surface displacement that accompanies it.
///
/// WHY THIS IS A TEST AND NOT A SCREENSHOT: the effect is transient and, worse, a capture only shows it
/// if the capture path deliberately holds it -- so "the screenshot looks right" is a statement about the
/// capture plumbing as much as about the effect. Three separate attempts at proving this feature by
/// screenshot produced a table that looked untouched while the code was already correct.
///
/// The measurements that DID settle it were the ones that isolate the variable: identical frozen state,
/// effects on vs off, differenced. That check lives in the capture pipeline (--no-fx, --warp-push), and
/// it is what caught the two real bugs this feature had. What follows asserts the arithmetic those
/// captures depend on, which a screenshot cannot do at all.
/// </summary>
public static class RailEffectTests
{
    private static Effects Fresh() => new Effects();

    private static void Hit(Effects fx, bool boosted = true)
        => fx.RailHit(1, 336.0f, new Vector2(963.0f, 336.0f), new Vector2(-1.0f, 0.1f),
            Palette.RailBoost, boosted);

    /// <summary>E01: ONE hit is ONE effect. The old fan of duplicates is gone.</summary>
    public static void E01_OneHitCreatesOneImpact(TestRunner t)
    {
        t.Section("E01 - a rail hit creates exactly one impact");

        var fx = Fresh();
        t.Check("nothing is live before the hit", fx.Count == 0, "count=" + fx.Count);

        Hit(fx);
        t.Check("one hit creates one effect", fx.Count == 1, "count=" + fx.Count);

        // The user asked for FEWER effects with MORE force. The previous version added a ripple plus a
        // streak plus up to two offset copies, so a single bounce could put five draw calls on screen --
        // each individually too faint to see. One soft-edged deformation replaces all of them.
        for (var i = 0; i < 10; i++) Hit(fx);
        t.Check("eleven hits create eleven effects, never a fan per hit", fx.Count == 11,
            "count=" + fx.Count);
    }

    /// <summary>E02: the impact reaches the shader with the right contact, normal and magnitude.</summary>
    public static void E02_ImpactReachesTheShader(TestRunner t)
    {
        t.Section("E02 - the live impact is exposed to the warp shader");

        var fx = Fresh();
        t.Check("with no impact the shader is told there is nothing",
            !fx.TryGetImpact(out _), "TryGetImpact returned true on an empty set");

        Hit(fx);
        t.Check("after a hit an impact is available", fx.TryGetImpact(out var impact), "");

        t.CheckClose("contact x matches the hit", impact.Contact.X, 963.0f, 3, "");
        t.CheckClose("contact y matches the hit", impact.Contact.Y, 336.0f, 3, "");

        // Wall 1 is the right cushion, so the surface must yield to the right, i.e. outward = +x. The
        // INWARD normal is therefore -x. Getting this backwards would push the cushion into the table,
        // which is the difference between a collision and a suction.
        t.CheckClose("the inward normal of the right wall points left", impact.Normal.X, -1.0f, 4, "");
        t.CheckClose("the right wall's normal has no vertical component", impact.Normal.Y, 0.0f, 4, "");

        t.CheckClose("a boosted hit carries the boosted magnitude", impact.PeakPush,
            Effects.BoostedPushPx, 4, "");
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

        fx.Update(5.0);
        t.Check("every effect expires after a long frame", fx.Count == 0, "count=" + fx.Count);

        // Bounded under sustained fire. The bound is NOT arbitrary: one impact lives 0.55 s and a hit
        // spawns exactly one, so a ball bouncing on every frame of 60 fps legitimately holds
        // 0.55 x 60 = 33 live effects. What must never happen is growth WITHOUT bound -- the count
        // tracking hits ever made rather than hits in the last second.
        for (var round = 0; round < 200; round++)
        {
            Hit(fx);
            fx.Update(1.0 / 60.0);
        }

        var liveAfterFire = fx.Count;
        t.CheckAtMost("the live set is bounded by lifetime x spawn rate, not by total hits made",
            liveAfterFire, 60, "count=" + liveAfterFire);

        fx.Update(5.0);
        t.Check("the set drains to empty once firing stops", fx.Count == 0, "count=" + fx.Count);

        // Ten times the firing time must NOT mean a proportionally larger live set.
        for (var round = 0; round < 2000; round++)
        {
            Hit(fx);
            fx.Update(1.0 / 60.0);
        }

        t.CheckAtMost("10x the firing time does not mean 10x the live set", fx.Count, 60,
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

        // The normals must all point INTO the table, or the cushion dents the wrong way.
        t.CheckClose("left wall yields rightward", Effects.InwardNormal(0).X, 1.0f, 4, "");
        t.CheckClose("right wall yields leftward", Effects.InwardNormal(1).X, -1.0f, 4, "");
        t.CheckClose("top wall yields downward", Effects.InwardNormal(2).Y, 1.0f, 4, "");
        t.CheckClose("bottom wall yields upward", Effects.InwardNormal(3).Y, -1.0f, 4, "");

        // Every wall the heading detector can return must have a normal, or the dent silently vanishes
        // for that wall. This pairing is easy to break by reordering the wall ids.
        var paired = true;
        for (var w = 0; w < 4; w++)
        {
            if (Effects.InwardNormal(w) == Vector2.Zero) paired = false;
        }

        t.Check("every addressable wall has an inward normal", paired, "walls 0..3");

        t.CheckClose("an unknown wall yields a zero normal, so it draws nothing",
            Effects.InwardNormal(99).Length(), 0.0f, 4, "");
    }

    /// <summary>E06: the cushion actually springs back through its rest position.</summary>
    public static void E06_ElasticReboundOvershootsRest(TestRunner t)
    {
        t.Section("E06 - the impact is elastic: it compresses, springs back and overshoots");

        // This is the property that separates an IMPACT from a FLASH, and it is the reason the effect
        // was rebuilt. A plain decay curve -- exp(-k*t), the obvious choice -- can only ever be >= 0, so
        // it can only describe "something lit up and faded". It cannot describe "something was STRUCK".
        // The cos() term is what puts the envelope below zero, and that negative excursion IS the
        // cushion leaving its rest position and coming back.
        var env0 = Effects.ElasticEnvelope(0.0, Effects.Omega, Effects.Decay);
        t.CheckClose("at t=0 the cushion is fully compressed", env0, 1.0f, 3, "");

        // omega=30 rad/s, so the first return to rest is at t = pi/(2*30) ~= 0.052 s.
        var tZero = Math.PI / (2.0 * Effects.Omega);
        var beforeZero = Effects.ElasticEnvelope(tZero * 0.5, Effects.Omega, Effects.Decay);
        var afterZero = Effects.ElasticEnvelope(tZero * 1.5, Effects.Omega, Effects.Decay);

        t.Check("the cushion is still compressed before the return point", beforeZero > 0.0,
            $"env={beforeZero:F4}");
        t.Check("the cushion has sprung PAST rest after it", afterZero < 0.0,
            $"env={afterZero:F4}");

        // The overshoot must be a real excursion, not a rounding artefact near zero. With decay 7 the
        // first trough is around -0.4, which is a pronounced spring-back.
        var tTrough = Math.PI / Effects.Omega;
        var trough = Effects.ElasticEnvelope(tTrough, Effects.Omega, Effects.Decay);
        t.Check("the rebound overshoot is a real excursion, not noise", trough < -0.25f,
            $"trough={trough:F4} at t={tTrough:F3}");

        // It rings more than once: compress, spring back, compress again smaller. That second
        // compression is what makes it read as an oscillating material rather than a single bounce.
        var secondCompression = Effects.ElasticEnvelope(2.0 * tTrough, Effects.Omega, Effects.Decay);
        t.Check("it compresses a second time, smaller", secondCompression > 0.0f,
            $"env={secondCompression:F4}");
        t.Check("and that second compression is weaker than the first",
            secondCompression < Math.Abs(trough), $"{secondCompression:F4} < {Math.Abs(trough):F4}");

        // And it settles: by the end of the effect's life the envelope is negligible, so a new impact
        // never has to fight a stale one.
        var late = Math.Abs(Effects.ElasticEnvelope(0.55, Effects.Omega, Effects.Decay));
        t.Check("the oscillation is spent by the end of the effect's life", late < 0.05,
            $"|env(0.55)|={late:F4}");

        t.Check("E06 evidence", true,
            $"env(0)={env0:F3} before={beforeZero:F3} after={afterZero:F3} " +
            $"trough={trough:F3} second={secondCompression:F3} late={late:F4}");
    }

    /// <summary>E07: the dent is a connected Gaussian, strongest at the contact and centred on it.</summary>
    public static void E07_DentIsLocalAndConnected(TestRunner t)
    {
        t.Section("E07 - the dent is local and spread across the cushion, not a point");

        t.CheckClose("the dent is total at the contact", Effects.DentWeight(0.0f, Effects.DentSigma),
            1.0f, 4, "");

        // 1 sigma: exp(-1/2) = 0.6065.
        t.CheckClose("at one sigma the dent is exp(-1/2)",
            Effects.DentWeight(Effects.DentSigma, Effects.DentSigma), 0.6065f, 3, "");

        // 2 sigma: exp(-2) = 0.135, still a visible bow. That is the whole reason the Gaussian is wide:
        // a cushion is a run of connected material, not a pad that dents at one spot.
        var twoSigma = Effects.DentWeight(Effects.DentSigma * 2.0f, Effects.DentSigma);
        t.CheckClose("at two sigma the dent is exp(-2)", twoSigma, 0.1353f, 3, "");
        t.Check("two sigma is still visible, so the cushion does not dent at a point", twoSigma > 0.1f,
            $"w={twoSigma:F4}");

        // 3 sigma: under 1%, which is why the geometry stops sampling there.
        var threeSigma = Effects.DentWeight(Effects.DentSigma * 3.0f, Effects.DentSigma);
        t.Check("at three sigma the dent is negligible", threeSigma < 0.012f, $"w={threeSigma:F4}");

        // Monotonic: further away can never deform more.
        var previous = float.MaxValue;
        var monotonic = true;
        for (var d = 0.0f; d <= Effects.DentSigma * 4.0f; d += Effects.DentSigma * 0.25f)
        {
            var w = Effects.DentWeight(d, Effects.DentSigma);
            if (w > previous + 1e-6f) monotonic = false;
            previous = w;
        }

        t.Check("weight never increases with distance", monotonic, "scanned to 4 sigma");

        // Wider than a ball, which is what makes it read as material rather than as a dot on the rail.
        t.Check("the dent spans several ball radii", Effects.DentSigma > 24.0f * 3.0f,
            $"sigma={Effects.DentSigma:F1} vs 3x a 24 px ball");
    }

    /// <summary>E08: a paying hit and a spent one are visibly different.</summary>
    public static void E08_BoostedAndSpentDiffer(TestRunner t)
    {
        t.Section("E08 - a hit that paid out deforms far more than one that had nothing left");

        var paid = Fresh();
        Hit(paid, boosted: true);
        paid.TryGetImpact(out var strong);

        var spent = Fresh();
        Hit(spent, boosted: false);
        spent.TryGetImpact(out var weak);

        t.CheckClose("a paying hit uses the boosted magnitude", strong.PeakPush,
            Effects.BoostedPushPx, 4, $"got {strong.PeakPush}");
        t.CheckClose("a spent hit uses the reduced magnitude", weak.PeakPush,
            Effects.SpentPushPx, 4, $"got {weak.PeakPush}");

        // The contrast IS the readout. The rail's allowance runs out silently, so if the two looked alike
        // the player would have no way to tell that energy had stopped being returned -- and the whole
        // rail engine would present as broken rather than as spent.
        t.Check("the contrast is at least 2x, so it reads as a different event",
            strong.PeakPush > weak.PeakPush * 2.0f,
            $"{strong.PeakPush:F1} vs {weak.PeakPush:F1}");

        // A spent hit is shorter-lived as well: nothing was returned, so the disturbance should pass
        // quickly rather than ring like a real impact.
        paid.Update(0.40);
        spent.Update(0.40);
        t.Check("the spent impact is gone well before the paying one",
            spent.Count == 0 && paid.Count == 1, $"spent={spent.Count} paid={paid.Count}");
    }

    /// <summary>
    /// E09: the kernel's RailBoosts counter is what the LIVE view reads, and it must count payouts only.
    ///
    /// This exists because the live path was the one thing no test and no capture covered. Every scripted
    /// state settles the world inside PrepareState (during _Ready), which burns the stat deltas before
    /// _Process ever runs -- so PlayEventSounds, which drives the effect by DIFFERENCING those counters
    /// once per frame, saw zero delta and spawned nothing. The feature looked correct in every screenshot
    /// and was unverified in the only path a player actually uses.
    ///
    /// The bug this pins down: the view used to infer the payout from the ball's own CanTakeWallBoost
    /// AFTER stepping, but the kernel consumes that allowance in the same call that pays it out. Every
    /// hit therefore read as spent, so the strong impact was unreachable in play while all the effect
    /// arithmetic passed. Measured then: push=11.0 (the spent value) on a hit that demonstrably returned
    /// energy. RailBoosts is incremented inside the kernel, where `boosted` is still in scope.
    /// </summary>
    public static void E09_KernelCountsRailPayouts(TestRunner t)
    {
        t.Section("E09 - the kernel counts rail payouts where the allowance is still in scope");

        // The cue's allowance is UNLIMITED in the shipped table (MaxWallBoosts = -1), so with the default
        // config every rail hit pays out and RailBoosts == RailTransforms. That equality is the contract
        // for the cue, and the interesting case -- a ball whose allowance RUNS OUT -- has to be configured
        // explicitly. An earlier version of this test asserted transforms > boosts on the default table
        // and failed, because that premise is simply false for the cue; the counter is fine.
        var level = TestSupport.Table();
        level.AddCue(292, 280);
        level.AddObject(700, 120);
        var world = new World(level);

        t.Check("no payouts before anything happens", world.Stats.RailBoosts == 0,
            "boosts=" + world.Stats.RailBoosts);

        t.Check("the shot is accepted", world.Shot(7.0, 170.0), "shot refused");
        for (var i = 0; i < 4000 && !world.AtRest(); i++) world.Step();

        t.Check("the ball did reach the rails", world.Stats.RailTransforms > 0,
            "transforms=" + world.Stats.RailTransforms);
        t.Check("with an unlimited allowance every transform pays out",
            world.Stats.RailBoosts == world.Stats.RailTransforms,
            $"boosts={world.Stats.RailBoosts} transforms={world.Stats.RailTransforms}");

        // Now the case the view actually depends on: a FINITE allowance, where the two counts diverge and
        // a player can see that the cushion has stopped giving energy back. If RailBoosts and RailTransforms
        // were ever the same counter, the strong impact would be indistinguishable from the spent one and
        // the whole "is the rail still paying?" readout would be dead.
        //
        // Friction is lowered for this case so the ball actually reaches several rails. On the shipping
        // table a single 170-power shot dies after about one cushion (measured: transforms=1), so a rally
        // long enough to outlast the allowance has to be arranged rather than assumed -- the first two
        // versions of this test asserted it and failed for exactly that reason.
        //
        // POCKETS ARE DISABLED HERE, and that is a fix rather than a convenience. This fixture is about
        // rail transforms, and it never mentioned pockets; it silently relied on the cue happening not to
        // be swallowed. When the cue's capture radius was widened by request (1.3x, so a finger's aim
        // does not have to be perfect), the cue's long rally started passing close enough to a pocket to
        // drop in -- and since potting the cue ends the rally, transforms froze at 1 and this assertion
        // failed with boosts=1 transforms=1 cuePocketed=True. The observation is real and is the exact
        // cost of the wider mouth; it just does not belong in a test about rails.
        var finite = TestSupport.Table(pockets: false, configure: c =>
        {
            c.FrictionDecel = 60.0;
            c.For(BallType.Cue).MaxWallBoosts = 1;
        });
        finite.AddCue(292, 280);
        finite.AddObject(700, 120);
        var limited = new World(finite);
        limited.Shot(7.0, 170.0);
        for (var i = 0; i < 8000 && !limited.AtRest(); i++) limited.Step();

        t.Check("the cue is still on the table, so the rally is not cut short by a pot",
            !limited.Cue.Pocketed, $"pocketed={limited.Cue.Pocketed} captures={limited.Stats.PocketCaptures}");
        t.Check("a finite allowance still registers its one payout",
            limited.Stats.RailBoosts == 1, "boosts=" + limited.Stats.RailBoosts);
        t.Check("but the ball keeps bouncing long after the allowance is spent",
            limited.Stats.RailTransforms > limited.Stats.RailBoosts,
            $"boosts={limited.Stats.RailBoosts} transforms={limited.Stats.RailTransforms}");
        t.Check("payouts are always a subset of transforms, never a separate tally",
            limited.Stats.RailBoosts <= limited.Stats.RailTransforms,
            $"boosts={limited.Stats.RailBoosts} transforms={limited.Stats.RailTransforms}");
    }

    /// <summary>
    /// E10: --no-fx must silence the WARP as well as the geometry, or the "no effects" baseline carries
    /// the distortion and every measured footprint is inflated.
    ///
    /// Found by measurement, not by reading: comparing an idle-warp capture against --no-fx left 1.72% of
    /// pixels differing, and --no-fx was supposed to be the effect-free frame. The residual turned out to
    /// be the flash and bent-cushion geometry (correct, intentional), but the flag needed to guarantee the
    /// shader was idle too before that could be established -- otherwise "the effect owns 1.72%" and "the
    /// baseline is contaminated" are indistinguishable.
    /// </summary>
    public static void E10_ClearingEffectsAlsoIdlesTheWarp(TestRunner t)
    {
        t.Section("E10 - clearing the effects must also return the warp to pass-through");

        var fx = Fresh();
        Hit(fx);
        t.Check("the impact is live and would warp", fx.TryGetImpact(out var live),
            "no impact after a hit");
        t.CheckClose("the live impact carries a real magnitude", live.PeakPush,
            Effects.BoostedPushPx, 4, $"got {live.PeakPush}");

        fx.Clear();
        t.Check("after Clear there is no impact, so the shader idles",
            !fx.TryGetImpact(out _), "an impact survived Clear");
        t.Check("and the list is empty", fx.Count == 0, "count=" + fx.Count);
    }
}
