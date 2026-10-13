using System;

namespace Rewind.Sim;

/// <summary>
/// Per-ball-type physical parameters. One entry per <see cref="BallType"/>.
///
/// Why a table instead of globals: the game needs "different ball types behave differently", and
/// the previous design had a single global set of constants with a two-value BallType enum, so
/// the reserved level-file vocabulary (bomb / anchor / heavy) loaded as ordinary balls. Every
/// value here is data, so a level can override them and a designer can tune them without a
/// rebuild.
///
/// DEFAULTS ARE CHOSEN SO THAT "OBJECT" EXACTLY REPRODUCES THE PREVIOUS BEHAVIOUR. Changing the
/// shipped defaults would invalidate the tolerance verification of every authored level
/// (godot/LEVELS.md), so any change here must be re-verified with tools/author.
/// </summary>
public sealed class BallTypeParams
{
    /// <summary>Relative mass. Drives how a collision splits momentum; heavier moves less.</summary>
    public double Mass = 1.0;

    /// <summary>Rolling deceleration, px/s^2. A negative value inherits Config.FrictionDecel.</summary>
    public double FrictionDecel = -1.0;

    /// <summary>Ball-ball restitution for collisions involving this type.
    /// A negative value means "inherit Config.BallRestitution".</summary>
    public double Restitution = -1.0;

    /// <summary>Speed ADDED on a rail hit (before <see cref="MaxWallBoosts"/> runs out).
    /// A negative value means "inherit the global default for this ball's role", so the locked
    /// globals stay authoritative unless a type deliberately overrides them.</summary>
    public double WallGain = -1.0;

    /// <summary>
    /// THE RAIL ENGINE. Fraction of the LAUNCH SPEED returned as momentum on each rail hit, on top of
    /// <see cref="WallGain"/>. 1.0 means a cushion hands back a full launch speed's worth.
    ///
    /// Why it is a fraction of the launch speed rather than a flat number: the user asked for rail bounces to
    /// give "100% of the initial momentum", and a fixed +120 px/s is imperceptible once the cue is moving at
    /// 1200. Scaling by the shot the player actually took means a soft shot earns a soft bounce and a full
    /// power break earns a full one, which is also what makes the mechanic feel like a property of the table
    /// instead of a hidden constant.
    ///
    /// A negative value is the "inherit the global" sentinel, matching the rest of this table. Combined with
    /// <see cref="MaxWallBoosts"/>, which is what stops a long rail from becoming an infinite energy farm.
    /// </summary>
    public double WallGainFrac = -1.0;

    /// <summary>
    /// How many times this ball may be speed-boosted by a rail before the boost stops.
    /// The reflection itself (and its restitution) always continues; only the bonus is capped.
    /// 0 disables the bonus entirely; a negative value means unlimited.
    /// </summary>
    public int MaxWallBoosts = -1;
    /// <summary>
    /// Multiplier applied to the speed a ball GAINS from a ball-ball collision. 1.0 = the collision
    /// is energy-conserving (normal billiards). Values above 1 deliberately inject energy.
    ///
    /// DEFAULT IS 1.0 ON PURPOSE. The user asked for "a collision accelerates the ball according to
    /// its type", which is energy non-conserving by construction. Shipping a default above 1.0
    /// would silently invalidate every level's tolerance window and could make authored levels
    /// unsolvable, so the MECHANISM is available and tunable but OFF until it is measured.
    /// See godot/PHYSICS_TUNING.md.
    /// </summary>
    public double CollisionBoost = 1.0;

    public BallTypeParams Clone() => (BallTypeParams)MemberwiseClone();
}

/// <summary>
/// Simulation tuning. Every value marked LOCKED is copied verbatim from
/// <c>策划案/REWIND_core_params_v1.md</c> (v1.1) and must not be changed here without
/// updating that file and <c>godot/DIFFERENCES.md</c>. The solver (m1-solver) uses the
/// same table, so implementation and solver cannot diverge.
///
/// Values NOT in the locked table are marked DERIVED and are the integrator's own
/// continuity guarantees; they are documented in godot/DIFFERENCES.md.
/// </summary>
public sealed class PhysicsConfig
{
    // ---------------------------------------------------------------- LOCKED
    /// <summary>LOCKED. Fixed simulation step. Never the frame delta.</summary>
    public double SimulationStep = 1.0 / 120.0;

    /// <summary>LOCKED. Ball radius, px.</summary>
    public double BallRadius = 12.2;

    // ------------------------------------------------- per-type physical params
    /// <summary>
    /// Indexed by <see cref="BallType"/>. Object defaults reproduce the previous global behaviour
    /// exactly, so shipping levels stay valid. Levels and the HUD may override any field.
    /// </summary>
    public BallTypeParams[] Types =
    {
        //  Cue: the player's ball. All values inherit the locked globals (restitution 0.92, gain
        //  120), so shipping behaviour is unchanged; the allowance is unlimited.
        new BallTypeParams { Mass = 1.0, FrictionDecel = -1.0, Restitution = -1.0, WallGain = -1.0, MaxWallBoosts = -1 },

        // ---------------------------------------------------------------------------------------
        // THE FOUR SPACE BODIES ARE VISUAL ONLY.
        //
        // The user's call: "the bodies differ visually only, do not introduce abilities yet". An earlier
        // version gave each slot its own mass / friction / restitution, which made every level's tolerance
        // depend on WHICH SLOT a ball happened to occupy -- so re-skinning a ball silently changed the
        // puzzle. That is a bad property when levels are hand-placed, because art and difficulty become the
        // same knob.
        //
        // All four slots now inherit the locked globals and differ only in how they are DRAWN (see
        // BallStyles: a tail streak, a thick ring, radiating needles, broken arcs). Collision and momentum
        // are uniform, so a level behaves the same regardless of its art.
        //
        // The per-type table is deliberately LEFT IN PLACE: the knobs (Mass, FrictionDecel, Restitution,
        // WallGain, MaxWallBoosts, CollisionBoost) still exist and are still per-type, so the frame and
        // momentum design the user wants next is a change in a LEVEL FILE, not code here. -1 = inherit.
        new BallTypeParams { Mass = 1.0, FrictionDecel = -1.0, Restitution = -1.0, WallGain = 0.0, MaxWallBoosts = 0 },
        new BallTypeParams { Mass = 1.0, FrictionDecel = -1.0, Restitution = -1.0, WallGain = 0.0, MaxWallBoosts = 0 },
        new BallTypeParams { Mass = 1.0, FrictionDecel = -1.0, Restitution = -1.0, WallGain = 0.0, MaxWallBoosts = 0 },
    };

    /// <summary>Params for a type, falling back to the object entry if the table is short.</summary>
    public BallTypeParams For(BallType type)
    {
        var i = (int)type;
        if (Types == null || Types.Length == 0) return new BallTypeParams();
        if (i < 0 || i >= Types.Length) i = (int)BallType.Object;
        return Types[i] ?? Types[(int)BallType.Object] ?? new BallTypeParams();
    }

    /// <summary>LOCKED. Rail inner box.</summary>
    public double TableLeft = 65.0;
    public double TableRight = 975.0;
    public double TableTop = 63.0;
    public double TableBottom = 497.0;

    /// <summary>LOCKED. Drag power ceiling, px.</summary>
    public double PowerMax = 170.0;

    /// <summary>LOCKED. px/s per px of drag -> V_BREAK = 1258 px/s.</summary>
    public double SpeedPerPower = 7.4;

    /// <summary>LOCKED. Minimum drag that produces a shot, px.</summary>
    public double MinPower = 12.0;

    /// <summary>
    /// How many strikes a level allows. NOT LOCKED, and deliberately level data.
    ///
    /// Default 1 keeps the one-strike rule that defines the early levels. It exists because measurement
    /// showed the one-strike rule makes more balls PHYSICALLY UNREACHABLE: with one strike plus rewinds a
    /// level clears about two object balls, and adding an extra ball kept the corridor solvable but always
    /// finished 2/2 -- the extra ball was never potted. A three-ball layout was not merely hard, it had no
    /// solution at all. Later levels therefore raise this and grow their ball count together.
    /// </summary>
    public int MaxShots = 1;

    /// <summary>LOCKED. Constant rolling deceleration, px/s^2 (replaces exponential damping).</summary>
    public double FrictionDecel = 640.0;

    /// <summary>LOCKED. Rail restitution, all balls.</summary>
    public double WallRestitution = 0.92;

    /// <summary>LOCKED. Rail speed gain for the CUE only, px/s per rail transform.</summary>
    public double CueWallGain = 120.0;

    /// <summary>
    /// Rail engine for the CUE: fraction of the launch speed returned per rail hit. 0 = flat gain only.
    /// Levels turn this on through their abilities.railKick block; the default here is the global a type
    /// inherits when it does not specify its own.
    /// </summary>
    public double CueWallGainFrac = 0.0;

    /// <summary>Rail engine for OBJECT balls. Stays 0: the cue is the player's only energy source, and an
    /// object ball that gained speed off a cushion would make a level's tolerance meaningless.</summary>
    public double ObjectBallGainFrac = 0.0;

    /// <summary>LOCKED. Rail speed gain for object balls.</summary>
    public double ObjectBallGain = 0.0;

    /// <summary>
    /// Hard speed cap for every ball, px/s. NOT LOCKED any more -- see the raised value below.
    ///
    /// Raised 2000 -> 5600 when the rail engine landed, then 5600 -> 8000 for the rapid-tap
    /// "death-throe" kick and the event cards (Titan grows the cue, Antimatter removes collision
    /// losses, several cards hand out momentum). The rail engine alone needs 1258 + 3*1258 = 5032;
    /// 8000 leaves headroom above that so a card-driven chain is never silently truncated by
    /// ClampSpeeds -- a clamp that fires is indistinguishable from "the mechanic did nothing".
    ///
    /// The cost of a higher cap is integrator work, not correctness: intervals are solved
    /// analytically by time of impact and sub-divided by travel, so a faster ball simply takes
    /// more substeps while every step still asserts the chord budget.
    /// </summary>
    public double SpeedCap = 8000.0;

    /// <summary>LOCKED. Arc-length history sampling interval, px.</summary>
    public double HistoryStepPx = 2.0;

    /// <summary>LOCKED. Rewind impulse coefficient; must stay &lt;= 1.</summary>
    public double RewindImpulseK = 1.0;

    /// <summary>
    /// NOT LOCKED (this is a deliberate addition, and it does not touch the locked model).
    ///
    /// Fraction of the LAUNCH SPEED added to the cue's release velocity PER OBJECT BALL POTTED, when
    /// the player lets go of the rewind. 0.5 means one pot grants half the launch speed again.
    ///
    /// Why it is here rather than on <see cref="RewindImpulseK"/>: that coefficient multiplies the
    /// impulse handed to an OBJECT ball in a rewind contact, and §7 of the params table locks it to
    /// &lt;= 1 precisely so the player's energy can only come from the strike and the rails. This bonus
    /// adds speed to the CUE's release -- nothing is amplified through a collision, so the locked
    /// rule is untouched -- while still paying the player back for a pot.
    ///
    /// Why it exists at all: the cue is the only ball that can be rewound, so once it runs out of
    /// speed the level is over and no amount of skill recovers it. Rewarding a pot with speed makes a
    /// good strike sustainable instead of terminal, which is the difference between a puzzle and a
    /// game.
    ///
    /// The total is still clamped by <see cref="SpeedCap"/>, so a big multi-pot break cannot push the
    /// cue past what the integrator is tuned for.
    /// </summary>
    public double RewindPotBonus = 0.5;

    /// <summary>
    /// NOT LOCKED. Floor on the rewind RELEASE speed, as a fraction of the launch speed: releasing always
    /// gives the cue at least this much speed, so a rewind can always (re)start the ball.
    ///
    /// Why it exists: without it, releasing near the start of the recorded path released the cue at almost
    /// nothing -- the stored velocity there is near zero BY DEFINITION, because that is where the ball was
    /// at rest. So "rewind most of the way home and let go", the most natural thing a new player does,
    /// produced a dead ball and the level ended. That is a trap, not a puzzle.
    ///
    /// It is a FLOOR, not an addition: releasing mid-path with plenty of stored speed is unchanged, so this
    /// cannot inflate a good release. Applied AFTER the pot bonus so the two compose in a predictable order.
    /// Still clamped by <see cref="SpeedCap"/>.
    /// </summary>
    public double RewindMinReleaseFrac = 0.35;

    /// <summary>
    /// Direction used when the release floor has to fire but there is no recorded path to derive a heading
    /// from at all. Points down the table (positive y is up in this kernel, so this aims toward the bottom
    /// rail and the bottom pockets) because that is the direction any opening strike would have travelled.
    /// Only reachable in a degenerate case; a real release always has a path.
    /// </summary>
    public Vec2 LastResortReleaseDir = new Vec2(0.0, -1.0);

    /// <summary>
    /// DEATH-THROE KICK. The user's rule: "within 10 distance, repeatedly tapping R counts as only
    /// two" -- i.e. several panicked taps delivered at essentially the same place on the table are
    /// one deliberate act of two taps, and the SECOND of those two taps is a last-ditch burst of
    /// speed rather than another crawl.
    ///
    /// Distance, not time, is the gate. A tap is "in the same cluster" when the cue has moved less
    /// than this many px since the previous tap, which is what makes the rule about what the PLAYER
    /// did (hammering R while nothing happens) rather than about a frame rate. Time-based debouncing
    /// would fire differently on a 144 Hz machine than on a 60 Hz one.
    /// </summary>
    public double RapidTapClusterPx = 10.0;

    /// <summary>Taps allowed inside one cluster before further taps are ignored. The user's number: 2.</summary>
    public int RapidTapMaxPerCluster = 2;

    /// <summary>
    /// Fraction of the LAUNCH SPEED granted as EXTRA release speed by the second tap of a cluster --
    /// the "death throe". The first tap of a cluster is an ordinary rewind; only the second gets this,
    /// so the mechanic costs a rewind segment and cannot be spammed for free energy.
    /// </summary>
    public double RapidTapBonusFrac = 0.5;

    /// <summary>Rewind segments a death-throe kick consumes (it is a real rewind, just a desperate one).</summary>
    public double RapidTapMinReleaseFrac = 0.5;

    /// <summary>LOCKED. Pocket capture radius, px.</summary>
    public double PocketRadius = 24.0;

    /// <summary>
    /// Capture-radius multiplier applied to the CUE ball only. NOT part of the locked table -- it is a
    /// player-requested difficulty change ("白球难以入洞，略微扩大白球入洞的判定范围").
    ///
    /// CUE ONLY, ON PURPOSE. <see cref="PocketRadius"/> is locked and shared by every ball, so scaling it
    /// would also widen the capture circle the object balls need to reach -- and those define the win
    /// condition, so that would silently make every authored level easier rather than fixing the one
    /// thing the player reported. A separate multiplier keeps the change to the cue.
    ///
    /// WORTH KNOWING BEFORE RELYING ON THIS: the cue going down is NOT progress. Capture() applies the
    /// tibo refund and returns without scoring or drawing, and a level allows one shot, so potting the
    /// cue ends the level unless the player rewinds out of it. A larger capture circle therefore makes
    /// this MISTAKE MORE LIKELY. Set the value to 1.0 to revert entirely.
    /// </summary>
    public double CuePocketRadiusScale = 1.3;

    /// <summary>LOCKED. Ball-ball restitution (equal mass), carried from the prototype.</summary>
    public double BallRestitution = 0.955;

    /// <summary>
    /// LOCKED. Rewind position crawl rate, px/s. Deliberately DECOUPLED from the stored
    /// velocity: the player backs up at a constant, predictable rate while the release
    /// velocity (and the §7 impulse) uses the stored v at that entry.
    /// </summary>
    public double RewindCrawlPxPerS = 240.0;

    // --------------------------------------------------------------- DERIVED
    /// <summary>
    /// DERIVED. Continuity guarantee of the integrator: the largest distance any single
    /// ball may travel inside one substep interval, px. Collision detection inside an
    /// interval is analytic (TOI), and this budget additionally guarantees that no
    /// sample-based test (pocket capture) can be skipped and that a graze whose chord is
    /// >= 2 * this value can never be missed.
    /// </summary>
    public double SubStepTravelBudget = 1.0;

    /// <summary>DERIVED. Same, for the relative distance of a ball pair inside one interval.</summary>
    public double SubStepRelativeTravelBudget = 1.0;

    /// <summary>DERIVED. Upper bound on intervals per physics step (guards pathological states).</summary>
    public int MaxIntervalsPerStep = 256;

    /// <summary>DERIVED. Upper bound on collision events resolved inside one physics step.</summary>
    public int MaxEventsPerStep = 256;

    /// <summary>DERIVED. Rail contact is only detected past this penetration (float slop).</summary>
    public double RailSlop = 1e-9;

    /// <summary>
    /// DERIVED. Offset added to the sum of radii when solving a ball-ball time of impact: the
    /// event fires when the pair is within this of touching. This must be STRICTLY SMALLER than
    /// <see cref="ResolveGap"/>, otherwise a pair sitting exactly on the contact radius produces
    /// a zero time of impact that the resolver refuses, and the step spins without progress.
    /// </summary>
    public double ContactGap = 1e-9;

    /// <summary>DERIVED. The resolver treats a pair closer than (sum of radii + this) as touching.</summary>
    public double ResolveGap = 1e-6;

    /// <summary>DERIVED. Positional overlap left after separation (must not add momentum).</summary>
    public double SeparationEpsilon = 1e-5;

    /// <summary>DERIVED. Assert the sub-step budget after every step (tests keep this on).</summary>
    public bool AssertSubStepBudget = true;

    /// <summary>DERIVED. Record history for the cue ball only (GDD §4.1). Not a tuning knob for M1.</summary>
    public bool RecordCueHistoryOnly = true;

    public PhysicsConfig Clone()
    {
        return (PhysicsConfig)MemberwiseClone();
    }

    /// <summary>
    /// Validate the invariants the locked table states as musts. Called by World's constructor
    /// so a bad level/config fails loudly instead of silently bending a rule.
    /// </summary>
    public void Validate()
    {
        if (SimulationStep <= 0.0) throw new ArgumentException("SimulationStep must be > 0");
        if (BallRadius <= 0.0) throw new ArgumentException("BallRadius must be > 0");
        if (TableRight <= TableLeft || TableBottom <= TableTop) throw new ArgumentException("degenerate table");
        if (PowerMax <= 0.0 || SpeedPerPower <= 0.0) throw new ArgumentException("shot power must be positive");
        if (FrictionDecel < 0.0) throw new ArgumentException("FrictionDecel must be >= 0");
        if (WallRestitution < 0.0 || WallRestitution > 1.0) throw new ArgumentException("WallRestitution out of range");
        if (CueWallGain < 0.0 || ObjectBallGain < 0.0) throw new ArgumentException("rail gain must be >= 0");
        if (SpeedCap <= 0.0) throw new ArgumentException("SpeedCap must be > 0");
        if (HistoryStepPx <= 0.0) throw new ArgumentException("HistoryStepPx must be > 0");
        if (RewindImpulseK < 0.0 || RewindImpulseK > 1.0)
            throw new ArgumentException("RewindImpulseK must be within [0,1]: the locked model forbids amplification");
        if (PocketRadius <= 0.0) throw new ArgumentException("PocketRadius must be > 0");
        if (BallRestitution < 0.0 || BallRestitution > 1.0) throw new ArgumentException("BallRestitution out of range");
        if (RewindCrawlPxPerS <= 0.0) throw new ArgumentException("RewindCrawlPxPerS must be > 0");
        if (SubStepTravelBudget <= 0.0 || SubStepRelativeTravelBudget <= 0.0)
            throw new ArgumentException("sub-step budgets must be > 0");
        if (MaxIntervalsPerStep <= 0 || MaxEventsPerStep <= 0)
            throw new ArgumentException("interval/event guards must be > 0");
        if (!(ContactGap < ResolveGap && ResolveGap < SeparationEpsilon))
            throw new ArgumentException(
                "contact tolerances must satisfy ContactGap < ResolveGap < SeparationEpsilon " +
                "(otherwise a contact event can never be resolved and the step spins)");
    }

    /// <summary>
    /// True when the locked rail map cannot diverge: the rail fixed point
    /// <c>gain / (1 - restitution)</c> must be below the hard cap.
    /// </summary>
    public bool RailFixedPointIsBelowCap =>
        WallRestitution < 1.0 && CueWallGain / (1.0 - WallRestitution) < SpeedCap;

    /// <summary>Rail fixed point speed, px/s (1500 for the locked values).</summary>
    public double RailFixedPoint => WallRestitution < 1.0 ? CueWallGain / (1.0 - WallRestitution) : double.PositiveInfinity;

    /// <summary>
    /// Arc length the cue crawls per physics step. LOCKED RELATIONSHIP: with the locked values
    /// this is 240 * 1/120 = 2.0 px, i.e. exactly one history entry per step. The M1 solver
    /// showed that a faster crawl skips history entry #1, which is where the "backstop ball"
    /// mechanism lands, and only 1 of its 6 levels stayed solvable. Do not decouple these
    /// without re-running solvability (see DIFFERENCES.md and the locked parameter table 6.1).
    /// </summary>
    public double RewindCrawlPxPerStep => RewindCrawlPxPerS * SimulationStep;

    /// <summary>True while the crawl advances exactly one history entry per step (locked).</summary>
    public bool CrawlIsOneHistoryEntryPerStep =>
        Math.Abs(RewindCrawlPxPerStep - HistoryStepPx) < 1e-9;
}
