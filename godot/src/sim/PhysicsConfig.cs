using System;

namespace Rewind.Sim;

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

    /// <summary>LOCKED. Constant rolling deceleration, px/s^2 (replaces exponential damping).</summary>
    public double FrictionDecel = 640.0;

    /// <summary>LOCKED. Rail restitution, all balls.</summary>
    public double WallRestitution = 0.92;

    /// <summary>LOCKED. Rail speed gain for the CUE only, px/s per rail transform.</summary>
    public double CueWallGain = 120.0;

    /// <summary>LOCKED. Rail speed gain for object balls.</summary>
    public double ObjectBallGain = 0.0;

    /// <summary>LOCKED. Hard speed cap for every ball, px/s.</summary>
    public double SpeedCap = 2000.0;

    /// <summary>LOCKED. Arc-length history sampling interval, px.</summary>
    public double HistoryStepPx = 2.0;

    /// <summary>LOCKED. Rewind impulse coefficient; must stay &lt;= 1.</summary>
    public double RewindImpulseK = 1.0;

    /// <summary>LOCKED. Pocket capture radius, px.</summary>
    public double PocketRadius = 24.0;

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
