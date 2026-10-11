using System;
using System.Collections.Generic;

namespace Rewind.Sim;

/// <summary>
/// The deterministic billiards kernel. It owns the whole simulation: fixed-step integration,
/// friction, rails, pockets, continuous collisions, the cue ball's append-only history and the
/// rewind verb. It uses no Godot types and no engine physics at all - a caller (the Godot view,
/// the headless test harness or the M4 solvability solver) only calls <see cref="Step"/> and the
/// input methods.
///
/// Locked rules implemented here (see the locked parameter table REWIND_core_params_v1.md and
/// godot/DIFFERENCES.md):
///  * fixed dt of 1/120 s, never the frame delta;
///  * constant rolling deceleration 640 px/s^2, velocity crossing zero stops exactly;
///  * rails reflect then rescale magnitude ONCE per ball per step: |v| = min(|v|*0.92 + gain, 2000);
///  * pocket capture is a circle test AND the velocity must point into the pocket;
///  * normal physics resolves ball-ball and ball-rail contacts by analytic time of impact;
///  * history is sampled every 2.0 px of arc length, storing position AND velocity;
///  * rewind crawls at a constant 240 px/s over a frozen snapshot while the live history keeps
///    appending, and release takes the stored velocity negated (decoupled from the crawl).
/// </summary>
public sealed class World
{
    private const double TimeEpsilon = 1e-12;
    private const double SegmentEpsilon = 1e-12;

    /// <summary>
    /// Distance below which a ball counts as "at the wall". Large enough to survive the
    /// floating-point residue left by analytic time-of-impact advancement, small enough that
    /// it cannot mask a real gap.
    /// </summary>
    private const double ContactSlop = 1e-7;

    private readonly int[] _railTransformUsedThisStep;

    /// <summary>[ball, hole] membership from the previous step, used to fire a white hole only on the
    /// rising edge so a ball cannot farm energy while resting inside one.</summary>
    private readonly bool[,] _whiteHoleInside;

    /// <summary>
    /// [ball, hole] simulation time of the last activation, enforcing the hole's cooldown. The rising
    /// edge alone is NOT enough: a strong boost can throw the ball out of the zone and friction can
    /// bring it straight back, which re-fired the hole four times in a row and drove a ball that had
    /// started at rest to the speed ceiling (caught by W02).
    /// </summary>
    private readonly double[,] _whiteHoleLastFired;
    private readonly Vec2[] _intervalStartPosition;
    private double _intervalDuration;
    private bool _intervalWasRewind;
    private int _intervalMaxRelativeA = -1;
    private int _intervalMaxRelativeB = -1;
    private int _intervalMaxTravelIndex = -1;
    private double _maxRelativeDuration;
    private bool _maxRelativeWasRewind;
    private double _guardRemaining;
    private int _traceNoEvent;
    private int _traceZeroToi;
    private int _tracePosToi;
    private double _traceSumH;

    private HistorySample[] _track;
    private int _cursor;
    private double _u;

    public World(LevelData level)
    {
        Level = level ?? throw new ArgumentNullException(nameof(level));
        level.Validate();
        Config = level.Config;
        Balls = new Ball[level.Balls.Count];
        _railTransformUsedThisStep = new int[Balls.Length];
        _whiteHoleInside = new bool[Balls.Length, Math.Max(1, level.WhiteHoles.Count)];
        _whiteHoleLastFired = new double[Balls.Length, Math.Max(1, level.WhiteHoles.Count)];
        for (var i = 0; i < Balls.Length; i++)
            for (var h = 0; h < _whiteHoleLastFired.GetLength(1); h++)
                _whiteHoleLastFired[i, h] = double.NegativeInfinity;
        _intervalStartPosition = new Vec2[Balls.Length];
        BuildBalls();
        Budget = level.Budget ?? RewindBudget.Unlimited;
    }

    private World(World other)
    {
        Level = other.Level;
        Config = other.Config;
        Balls = new Ball[other.Balls.Length];
        _railTransformUsedThisStep = new int[Balls.Length];
        _whiteHoleInside = new bool[Balls.Length, Math.Max(1, other.Level.WhiteHoles.Count)];
        _whiteHoleLastFired = new double[Balls.Length, Math.Max(1, other.Level.WhiteHoles.Count)];
        _intervalStartPosition = new Vec2[Balls.Length];
        Array.Copy(other._whiteHoleInside, _whiteHoleInside, other._whiteHoleInside.Length);
        Array.Copy(other._whiteHoleLastFired, _whiteHoleLastFired, other._whiteHoleLastFired.Length);
        for (var i = 0; i < Balls.Length; i++) Balls[i] = other.Balls[i].Clone();

        Time = other.Time;
        Tick = other.Tick;
        ShotsUsed = other.ShotsUsed;
        Won = other.Won;
        Frozen = other.Frozen;
        Rewinding = other.Rewinding;
        RewindSegmentsUsed = other.RewindSegmentsUsed;
        RewindDistanceUsed = other.RewindDistanceUsed;
        Budget = other.Budget;
        _cursor = other._cursor;
        _u = other._u;
        _track = other._track == null ? null : (HistorySample[])other._track.Clone();

        Stats.Rewinds = other.Stats.Rewinds;
        Stats.RewindContacts = other.Stats.RewindContacts;
        Stats.RailTransforms = other.Stats.RailTransforms;
        Stats.BallBallContacts = other.Stats.BallBallContacts;
        Stats.RailContacts = other.Stats.RailContacts;
        Stats.PocketCaptures = other.Stats.PocketCaptures;
        Stats.SubStepIntervals = other.Stats.SubStepIntervals;
        Stats.StepGuardHits = other.Stats.StepGuardHits;
        Inputs.AddRange(other.Inputs);
    }

    // ------------------------------------------------------------------ state

    public LevelData Level { get; }

    public PhysicsConfig Config { get; }

    public Ball[] Balls { get; }

    public RewindBudget Budget { get; }

    public SimStats Stats { get; } = new SimStats();

    public List<InputCommand> Inputs { get; } = new List<InputCommand>();

    /// <summary>Simulated time in seconds at the start of the current step.</summary>
    public double Time { get; private set; }

    /// <summary>Number of completed fixed steps.</summary>
    public int Tick { get; private set; }

    /// <summary>
    /// Strikes taken so far in this level.
    ///
    /// Added because the one-strike rule made more balls PHYSICALLY UNREACHABLE. Measured: with a single
    /// strike plus rewinds, a level clears at most about two object balls -- adding one extra ball to the
    /// corridor kept the level solvable but always finished 2/2, i.e. the extra ball was never potted, and a
    /// full-circle three-rewind search over a three-ball layout found no solution at all. The extra balls
    /// were not hard to pot, they were IMPOSSIBLE to reach, because after the one permitted strike the only
    /// verb left is rewinding along a path that already exists.
    ///
    /// So the shot allowance became level data (<see cref="PhysicsConfig.MaxShots"/>), which keeps one-strike
    /// as the default and the identity of the early levels, and lets later levels grow a ball count.
    /// </summary>
    public int ShotsUsed { get; private set; }

    /// <summary>True once the level's strike allowance is spent. Existing view code keys off this, so the
    /// one-shot levels behave exactly as before.</summary>
    public bool ShotUsed => ShotsUsed >= Config.MaxShots;

    public bool Won { get; private set; }

    /// <summary>Set when the level is won: the kernel stops advancing (stable terminal state).</summary>
    public bool Frozen { get; private set; }

    public bool Rewinding { get; private set; }

    public int RewindSegmentsUsed { get; private set; }

    public double RewindDistanceUsed { get; private set; }

    public Ball Cue => Balls[0];

    // ------------------------------------------------------------- event cards

    /// <summary>Cards this run has taken plus their live state (friction scale, balloon, ...).</summary>
    public CardState Cards { get; private set; } = new CardState();

    /// <summary>Deterministic draw source. Lives in the world so a capture/restore round trip
    /// reproduces the same three cards, which the solver depends on.</summary>
    private uint _cardRng = 0xC0FFEEu;

    /// <summary>
    /// The pending draw, or null. Non-null means the simulation is WAITING for the player to pick a
    /// card: the user asked for the draw to pause the game and require a choice, so this is a real
    /// input-gated state, not a notification.
    /// </summary>
    public List<CardId> PendingDraw { get; private set; }

    /// <summary>True while a draw is waiting for the player.</summary>
    public bool AwaitingCardChoice => PendingDraw != null && PendingDraw.Count > 0;

    /// <summary>How many draws have been offered this run (for the HUD / stats).</summary>
    public int CardDraws { get; private set; }

    /// <summary>Seed the draw. Only used by tests and by a level that wants a fixed hand.</summary>
    public void SeedCards(uint seed) => _cardRng = seed == 0u ? 0xC0FFEEu : seed;

    /// <summary>
    /// Grant a card directly, without a draw, for tests that need to isolate ONE card's effect.
    ///
    /// It deliberately routes through the same <see cref="ApplyCard"/> the player's choice uses, so a
    /// test can never pass against a parallel implementation of the card that the game does not run.
    /// Refuses if the card has already been taken, matching the pool's own rule.
    /// </summary>
    public bool GrantCardForTest(CardId id)
    {
        if (Cards.Has(id)) return false;
        var def = CardPool.Get(id);
        if (def == null) return false;
        Cards.Taken.Add(id);
        ApplyCard(def);
        return true;
    }

    // ------------------------------------------------------------ diagnostics

    /// <summary>Intervals processed during the last step (sub-step budget observable).</summary>
    public int LastIntervals { get; private set; }

    public double LastMaxIntervalTravel { get; private set; }

    public double LastMaxIntervalRelativeTravel { get; private set; }

    public double MaxObservedIntervalTravel { get; private set; }

    public double MaxObservedIntervalRelativeTravel { get; private set; }

    public int MaxObservedIntervals { get; private set; }

    public double MaxObservedSpeed { get; private set; }

    /// <summary>Arc length crawled during the last step while rewinding, px.</summary>
    public double LastRewindCrawlDistance { get; private set; }

    /// <summary>Null while every invariant holds; otherwise the first violated invariant.</summary>
    public string AssertionFailure { get; private set; }

    /// <summary>Diagnostics for the worst measured interval (which pair, when, how long).</summary>
    public int MaxIntervalRelativePairA { get; private set; } = -1;

    public int MaxIntervalRelativePairB { get; private set; } = -1;

    public int MaxIntervalRelativeTick { get; private set; } = -1;

    public double MaxIntervalRelativeDuration { get; private set; }

    public double MaxIntervalTravelDuration { get; private set; }

    public bool MaxIntervalRelativeWasRewind { get; private set; }

    /// <summary>One-shot diagnostic describing the first interval that broke the travel budget.</summary>
    public string BudgetDiagnostic { get; private set; }

    /// <summary>One-shot diagnostic describing the first time the interval/event guard tripped.</summary>
    public string GuardDiagnostic { get; private set; }

    private string DescribeState()
    {
        var text = $"tick={Tick} remaining={_guardRemaining:R} nextH={MaxIntervalDuration():R} rewind={Rewinding}" +
                   $" trace[noEvent={_traceNoEvent} zeroToi={_traceZeroToi} posToi={_tracePosToi} sumH={_traceSumH:R}]";
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            text += $" | b{b.Id} p={b.Position} v={b.Velocity}";
        }

        return text;
    }

    private void NoteBudgetViolation(int intervalIndex, double h, double remaining)
    {
        if (BudgetDiagnostic != null) return;
        var text = $"tick={Tick} interval={intervalIndex} h={h:R} remaining={remaining:R} rewind={Rewinding}";
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            text += $" | b{b.Id} v={b.Velocity} start={_intervalStartPosition[i]} now={b.Position}";
        }

        BudgetDiagnostic = text;
    }

    public HistorySample[] RewindTrack => _track;

    public int RewindCursor => _cursor;

    public double RewindFraction => _u;

    /// <summary>
    /// Scoring balls on the table: everything that is NOT the cue.
    ///
    /// Counted by EXCLUSION, not by <c>Type == BallType.Object</c>. The type test was a real bug once levels
    /// started using the space bodies: a heavy / bomb / anchor ball was not counted, so it was also not
    /// required, and a level containing one could never be completed -- the win fires only when
    /// <see cref="PocketedObjectCount"/> reaches this, and the target was silently short. The live selftest
    /// showed it plainly: a level with three placed balls reported "0/1".
    /// </summary>
    public int ObjectBallCount
    {
        get
        {
            var n = 0;
            foreach (var b in Balls)
            {
                if (!b.IsCue) n++;
            }

            return n;
        }
    }

    /// <summary>Scoring balls already pocketed. Same exclusion rule as <see cref="ObjectBallCount"/>, so the
    /// two can never disagree about what counts.</summary>
    public int PocketedObjectCount
    {
        get
        {
            var n = 0;
            foreach (var b in Balls)
            {
                if (!b.IsCue && b.Pocketed) n++;
            }

            return n;
        }
    }

    // ------------------------------------------------------------------ build

    private void BuildBalls()
    {
        for (var i = 0; i < Level.Balls.Count; i++)
        {
            var spawn = Level.Balls[i];
            var radius = spawn.Radius > 0.0 ? spawn.Radius : Config.BallRadius;
            var records = spawn.Type == BallType.Cue;

            // Resolve this ball's physical character: the per-type table is the default, and an
            // explicit value in the level file still wins. This is what makes "different ball types
            // behave differently" real instead of the previous inert strings.
            var typeParams = Config.For(spawn.Type).Clone();
            if (spawn.Mass > 0.0) typeParams.Mass = spawn.Mass;
            var mass = typeParams.Mass <= 0.0 ? 1.0 : typeParams.Mass;

            var ball = new Ball(spawn.Id, spawn.Type, new Vec2(spawn.X, spawn.Y), radius,
                mass, records, Config.HistoryStepPx)
            {
                Velocity = new Vec2(spawn.Vx, spawn.Vy),
                Pocketed = false,
                Params = typeParams,
            };
            ball.History.Reset(ball.Position, Vec2.Zero, 0.0);
            Balls[i] = ball;
        }
    }

    public void Reset()
    {
        BuildBalls();
        Time = 0.0;
        Tick = 0;
        ShotsUsed = 0;
        Won = false;
        Frozen = false;
        Rewinding = false;
        RewindSegmentsUsed = 0;
        RewindDistanceUsed = 0.0;
        LaunchSpeed = 0.0;
        _track = null;
        _cursor = 0;
        _u = 0.0;
        _tapCount = 0;
        _lastTapPosition = Vec2.Zero;
        DeathThroeArmed = false;
        PendingDraw = null;
        CardDraws = 0;
        Cards = new CardState();
        Inputs.Clear();
        Stats.Rewinds = 0;
        Stats.RewindContacts = 0;
        Stats.RailTransforms = 0;
        Stats.BallBallContacts = 0;
        Stats.RailContacts = 0;
        Stats.PocketCaptures = 0;
        Stats.SubStepIntervals = 0;
        Stats.StepGuardHits = 0;
        Stats.CardsTaken = 0;
        Stats.TiboRefunds = 0;
        Stats.AntimatterBoosts = 0;
        Stats.DeathThroeKicks = 0;
        Stats.RapidTapsSuppressed = 0;
        Stats.BalloonFailures = 0;
        LastIntervals = 0;
        LastMaxIntervalTravel = 0.0;
        LastMaxIntervalRelativeTravel = 0.0;
        MaxObservedIntervalTravel = 0.0;
        MaxObservedIntervalRelativeTravel = 0.0;
        MaxObservedIntervals = 0;
        MaxObservedSpeed = 0.0;
        LastRewindCrawlDistance = 0.0;
        AssertionFailure = null;
    }

    public World Clone() => new World(this);

    /// <summary>
    /// Restore everything except the ball bodies (those are handled by <see cref="StateCodec"/>).
    /// Used by deserialization and by snapshot/rollback for solver lookahead.
    /// </summary>
    internal void LoadStateScalars(WorldState s)
    {
        Time = s.Time;
        Tick = s.Tick;
        ShotsUsed = s.ShotsUsed;
        Won = s.Won;
        Frozen = s.Frozen;
        Rewinding = s.Rewinding;
        RewindSegmentsUsed = s.RewindSegmentsUsed;
        RewindDistanceUsed = s.RewindDistanceUsed;
        Stats.CopyFrom(s.Stats);
        Inputs.Clear();
        for (var i = 0; i < s.Inputs.Length; i++) Inputs.Add(s.Inputs[i].Clone());
        _track = s.RewindTrack == null ? null : (HistorySample[])s.RewindTrack.Clone();
        _cursor = s.RewindCursor;
        _u = s.RewindU;
    }

    /// <summary>Full state capture (balls + history + rewind snapshot + input sequence).</summary>
    public WorldState CaptureState() => StateCodec.Capture(this);

    /// <summary>Restore a previously captured state into this world (level geometry must match).</summary>
    public void RestoreState(WorldState state) => StateCodec.Restore(this, state);

    // ------------------------------------------------------------------ input

    /// <summary>
    /// The one and only stroke allowed per level. Returns false when the shot is refused,
    /// which is how T01 (no second shot) is expressed in the kernel.
    /// </summary>
    public bool Shot(double angleDegrees, double power)
    {
        if (Frozen || Won || ShotUsed || Rewinding) return false;
        if (power < Config.MinPower) return false;
        var clamped = Math.Min(power, Config.PowerMax);
        var speed = Math.Min(clamped * Config.SpeedPerPower, Config.SpeedCap);
        Cue.Velocity = Vec2.FromAngleDegrees(angleDegrees) * speed;
        Cue.Pocketed = false;
        ShotsUsed++;
        LaunchSpeed = speed;         // basis for the pot bonus on rewind release
        Inputs.Add(new InputCommand { Tick = Tick, Kind = InputKind.Shot, A = angleDegrees, B = power });
        return true;
    }

    /// <summary>
    /// The speed the cue was launched with by <see cref="Shot"/>. The pot bonus on rewind release is
    /// a fraction of this, so the reward scales with how hard the player hit rather than being a
    /// flat grant.
    /// </summary>
    public double LaunchSpeed { get; private set; }

    /// <summary>
    /// Begin rewinding the cue ball: freeze a read-only snapshot of the history as it is now and
    /// put the cursor at its end. The live history keeps appending during the rewind (GDD §4.2),
    /// which is what makes A→B→C→D rewind as D→C→B→A.
    ///
    /// Returns true when a rewind started. <paramref name="deathThroe"/> is set when this start was
    /// the SECOND tap of a rapid cluster, which the release will honour as a burst of speed.
    /// </summary>
    public bool StartRewind() => StartRewind(out _);

    /// <inheritdoc cref="StartRewind()"/>
    public bool StartRewind(out bool deathThroe)
    {
        deathThroe = false;
        if (Frozen || Won || Rewinding || !ShotUsed) return false;
        if (Budget.MaxSegments > 0 && RewindSegmentsUsed >= Budget.MaxSegments) return false;
        if (Budget.MaxDistance > 0.0 && RewindDistanceUsed >= Budget.MaxDistance) return false;

        var cue = Cue;
        if (cue.History.Count < 2) return false;

        // ---- RAPID TAP RULE ----------------------------------------------------------------
        //
        // The user's rule: taps delivered within 10 px of each other count as at most TWO, and the
        // second of them is a death-throe acceleration rather than another ordinary rewind.
        //
        // Measured by DISPLACEMENT since the previous tap, not by elapsed time: a time window would
        // behave differently at 60 Hz and 144 Hz, and the rule is about the player hammering R while
        // the ball is not going anywhere. A tap counts as the same cluster when the cue has moved
        // less than RapidTapClusterPx since the last tap. Once the cue travels further than that --
        // because the rewind actually took it somewhere -- the cluster resets to zero and the player
        // gets two fresh taps.
        //
        // The consequence the rule must have: mashing R cannot buy unlimited rewinds, because the
        // third and later taps in a cluster are refused outright rather than silently costing budget.
        var moved = (cue.Position - _lastTapPosition).Length;
        if (_tapCount == 0 || moved > Config.RapidTapClusterPx)
        {
            _tapCount = 0;
        }

        if (_tapCount >= Config.RapidTapMaxPerCluster)
        {
            Stats.RapidTapsSuppressed++;
            _lastTapPosition = cue.Position;
            return false;
        }

        if (_tapCount == 1) deathThroe = true;
        _tapCount++;
        _lastTapPosition = cue.Position;

        // Flush the current position so the snapshot ends exactly where the ball is standing:
        // without this the rewind would jump back by up to one sampling interval at press time.
        cue.History.Flush(cue.Position, cue.Velocity, Time);

        _track = cue.History.Snapshot();
        _cursor = _track.Length - 1;
        _u = 1.0;
        if (_cursor < 1)
        {
            _track = null;
            _cursor = 0;
            _u = 0.0;
            return false;
        }

        Rewinding = true;
        DeathThroeArmed = deathThroe;
        RewindSegmentsUsed++;
        Stats.Rewinds++;
        Inputs.Add(new InputCommand { Tick = Tick, Kind = InputKind.RewindStart });
        return true;
    }

    /// <summary>
    /// Taps delivered in the current rapid cluster (0..<see cref="PhysicsConfig.RapidTapMaxPerCluster"/>).
    /// Exposed so the HUD can tell the player "this tap is your last one".
    /// </summary>
    public int RapidTapCount => _tapCount;

    /// <summary>True while the current rewind was started by the second tap of a cluster.</summary>
    public bool DeathThroeArmed { get; private set; }

    private int _tapCount;
    private Vec2 _lastTapPosition;

    /// <summary>
    /// Release: the cue keeps the reverse of the stored velocity at the current history entry
    /// (locked: release velocity is the stored v negated, decoupled from the 240 px/s crawl).
    ///
    /// PLUS the pot bonus: every object ball pocketed grants an extra
    /// <see cref="PhysicsConfig.RewindPotBonus"/> of the launch speed, added along the release
    /// direction. This is the answer to the core frustration -- the cue is the only ball that can be
    /// rewound, so once it runs out of speed the level is over and no amount of skill helps. Rewarding
    /// a pot with speed makes potting pay for the next attempt instead of ending the run.
    ///
    /// Deliberately NOT put on RewindImpulseK: that coefficient multiplies the impulse given to an
    /// OBJECT ball during a rewind contact and is locked at &lt;= 1 by §7 of the params table, whose
    /// stated intent is that the player's energy comes from the strike and the rails, never from
    /// rewind amplification. Adding a release-speed bonus keeps that rule intact -- nothing is
    /// amplified through a collision -- while still paying the player back for a pot.
    /// </summary>
    public void EndRewind()
    {
        if (!Rewinding) return;
        Cue.Velocity = ReverseReleaseVelocityWithBonus();
        EndRewindInternal(zeroVelocity: false);
        Inputs.Add(new InputCommand { Tick = Tick, Kind = InputKind.RewindEnd });
    }

    /// <summary>
    /// The cue's velocity when a rewind is released: the stored velocity reversed, then the pot bonus, then
    /// the release floor.
    ///
    /// ORDER MATTERS and is deliberate: the bonus is an ADDITION on top of what the path stored, and the
    /// floor is a MINIMUM afterwards. Doing it the other way round (floor then bonus) would let a pot
    /// inflate a release that the floor had already flattened, so two balls potted could not be told apart
    /// from a stronger floor.
    /// </summary>
    private Vec2 ReverseReleaseVelocityWithBonus()
    {
        var baseVel = ReverseReleaseVelocity();
        var speed = baseVel.Length;

        // Resolve DIRECTION first. Every branch below needs one, including the floor case, and the old
        // shape returned early whenever there was no pot -- which silently skipped the floor entirely.
        Vec2 dir;
        if (speed > 1e-6)
        {
            dir = baseVel / speed;
        }
        else if (_track != null && _track.Length >= 2 && _cursor <= 0)
        {
            // Released at the very START of the track, where the stored velocity is zero BY DEFINITION:
            // that is where the cue was at rest. Direction: forward along the path it originally took.
            var forward = _track[1].Position - _track[0].Position;
            if (forward.LengthSquared < 1e-12) return baseVel;
            dir = forward.Normalized();
        }
        else if (_track != null && _track.Length > 0)
        {
            // Mid-track but momentarily stationary. Use the local heading.
            var heading = _track[_cursor].Velocity;
            dir = heading.LengthSquared > 1e-12 ? heading.Normalized() : new Vec2(0.0, 1.0);
        }
        else
        {
            // No usable path at all: nothing to derive a direction from, so respect the floor alone using
            // the cue's last travelled heading if there is one.
            if (Config.RewindMinReleaseFrac <= 0.0 || LaunchSpeed <= 0.0) return baseVel;
            dir = Config.LastResortReleaseDir;
        }

        // 1. POT BONUS: a fraction of the launch speed per potted ball.
        var bonus = PocketedObjectCount > 0 && Config.RewindPotBonus > 0.0
            ? Config.RewindPotBonus * LaunchSpeed * PocketedObjectCount
            : 0.0;

        // 1b. DEATH-THROE KICK: the second tap of a rapid cluster releases harder. This is an
        // ADDITION on top of the stored speed, exactly like the pot bonus, so it composes in the same
        // order and cannot inflate a release that had nothing stored.
        if (DeathThroeArmed && Config.RapidTapBonusFrac > 0.0)
        {
            bonus += Config.RapidTapBonusFrac * LaunchSpeed;
            Stats.DeathThroeKicks++;
        }

        var total = speed + bonus;

        // 2. RELEASE FLOOR: never hand the player a dead ball. See RewindMinReleaseFrac for why.
        // A death-throe kick raises the floor too: a desperate release that still produced a dead ball
        // would make the mechanic a lie.
        var floorFrac = Config.RewindMinReleaseFrac;
        if (DeathThroeArmed && Config.RapidTapMinReleaseFrac > floorFrac)
            floorFrac = Config.RapidTapMinReleaseFrac;
        var floor = floorFrac * LaunchSpeed;
        if (total < floor) total = floor;

        total = Math.Min(total, Config.SpeedCap);
        return dir * total;
    }

    private Vec2 ReverseReleaseVelocity()
    {
        if (_track == null || _cursor <= 0 || _cursor >= _track.Length) return Vec2.Zero;
        return -_track[_cursor].Velocity;
    }

    private void EndRewindInternal(bool zeroVelocity)
    {
        if (!Rewinding) return;
        Rewinding = false;
        DeathThroeArmed = false;
        if (zeroVelocity) Cue.Velocity = Vec2.Zero;
        _track = null;
        _cursor = 0;
        _u = 0.0;
    }

    // ------------------------------------------------------------------- step

    public void Step()
    {
        if (Frozen) return;

        LastIntervals = 0;
        LastMaxIntervalTravel = 0.0;
        LastMaxIntervalRelativeTravel = 0.0;
        LastRewindCrawlDistance = 0.0;
        _traceNoEvent = 0;
        _traceZeroToi = 0;
        _tracePosToi = 0;
        _traceSumH = 0.0;
        Array.Clear(_railTransformUsedThisStep, 0, _railTransformUsedThisStep.Length);

        if (Rewinding) StepRewind();
        else StepNormal();

        ClampSpeeds();
        UpdateBalloon();

        if (LastIntervals > MaxObservedIntervals) MaxObservedIntervals = LastIntervals;
        if (LastMaxIntervalTravel > MaxObservedIntervalTravel)
        {
            MaxObservedIntervalTravel = LastMaxIntervalTravel;
            MaxIntervalTravelDuration = _intervalDuration;
        }

        if (LastMaxIntervalRelativeTravel > MaxObservedIntervalRelativeTravel)
        {
            MaxObservedIntervalRelativeTravel = LastMaxIntervalRelativeTravel;
            MaxIntervalRelativePairA = _intervalMaxRelativeA;
            MaxIntervalRelativePairB = _intervalMaxRelativeB;
            MaxIntervalRelativeTick = Tick;
            MaxIntervalRelativeDuration = _maxRelativeDuration;
            MaxIntervalRelativeWasRewind = _maxRelativeWasRewind;
        }

        if (Config.AssertSubStepBudget && AssertionFailure == null)
        {
            if (LastMaxIntervalTravel > Config.SubStepTravelBudget * (1.0 + 1e-6))
                AssertionFailure = "sub-step travel budget exceeded: " + LastMaxIntervalTravel;
            else if (LastMaxIntervalRelativeTravel > Config.SubStepRelativeTravelBudget * (1.0 + 1e-6))
                AssertionFailure = "sub-step relative budget exceeded: " + LastMaxIntervalRelativeTravel;
        }

        Tick++;
        Time = Tick * Config.SimulationStep;
    }

    public void RunSteps(int count)
    {
        for (var i = 0; i < count; i++) Step();
    }

    /// <summary>Run until every free ball is at rest (or <paramref name="maxSteps"/> is hit).</summary>
    public int Settle(int maxSteps = 200000)
    {
        var n = 0;
        while (n < maxSteps && !Frozen && !AtRest())
        {
            Step();
            n++;
        }

        return n;
    }

    public bool AtRest()
    {
        foreach (var b in Balls)
        {
            if (b.Pocketed) continue;
            if (b.Velocity.X != 0.0 || b.Velocity.Y != 0.0) return false;
        }

        return true;
    }

    // --------------------------------------------------------- normal physics

    private void StepNormal()
    {
        var remaining = Config.SimulationStep;
        var intervals = 0;
        var maxTravel = 0.0;
        var maxRel = 0.0;
        // Friction is applied ONCE per physics step (locked §3: "v -= A_FRICTION * dt"), before
        // the interval loop. Applying it per interval would over-apply it: an interval advances
        // by its time of impact, not by its full length, so the velocity would be decelerated
        // several times per step and the analytic TOI (solved with one velocity) would no longer
        // agree with the movement (made with another), which makes a grazing contact creep
        // towards its own solution instead of reaching it.
        ApplyFriction(Config.SimulationStep, Time);
        RunFreeIntervals(ref remaining, ref intervals, ref maxTravel, ref maxRel);
        RecordIntervalStats(intervals, maxTravel, maxRel);
    }

    /// <summary>
    /// Free-ball physics with analytic contacts: within an interval velocities are constant, so
    /// the earliest ball-ball / ball-rail time of impact is solved exactly and the world is
    /// advanced *to* the contact instead of stepping over it (locked §5).
    /// </summary>
    private void RunFreeIntervals(ref double remaining, ref int intervals, ref double maxTravel, ref double maxRel)
    {
        var dt = Config.SimulationStep;
        var events = 0;
        var guardHit = false;

        while (remaining > TimeEpsilon)
        {
            if (intervals >= Config.MaxIntervalsPerStep || events >= Config.MaxEventsPerStep)
            {
                guardHit = true;
                _guardRemaining = remaining;
                GuardDiagnostic ??= $"intervals={intervals} events={events} " + DescribeState();
                break;
            }

            intervals++;
            var h = Math.Min(remaining, MaxIntervalDuration());
            var t0 = Time + (dt - remaining);
            BeginInterval(h, false);
            _traceSumH += h;

            var toi = FindEarliestEvent(h);
            if (toi < 0.0)
            {
                _traceNoEvent++;
                MoveFree(h, t0);
                MeasureMovement(ref maxTravel, ref maxRel);
                if (maxRel > Config.SubStepRelativeTravelBudget + 1e-6)
                {
                    NoteBudgetViolation(intervals, h, remaining);
                }

                CheckPocketCaptures();
                ApplyWhiteHoles();
                remaining -= h;
                continue;
            }

            if (toi > 0.0)
            {
                _tracePosToi++;
                MoveFree(toi, t0);
                MeasureMovement(ref maxTravel, ref maxRel);
                if (maxRel > Config.SubStepRelativeTravelBudget + 1e-6)
                {
                    NoteBudgetViolation(intervals, toi, remaining);
                }

                CheckPocketCaptures();
                ApplyWhiteHoles();
            }

            ResolveEvents(ref events);
            remaining -= toi;
            if (toi <= 0.0) _traceZeroToi++;
        }

        if (guardHit)
        {
            // Never lose simulated time. The remainder is still sub-stepped so that the chord
            // budget holds even on this fallback path. Counted so tests can assert it never ran.
            Stats.StepGuardHits++;
            var guardLoops = 0;
            while (remaining > TimeEpsilon && guardLoops < Config.MaxIntervalsPerStep)
            {
                guardLoops++;
                intervals++;
                var hh = Math.Min(remaining, MaxIntervalDuration());
                var tt = Time + (dt - remaining);
                BeginInterval(hh, false);
                MoveFree(hh, tt);
                MeasureMovement(ref maxTravel, ref maxRel);
                if (maxRel > Config.SubStepRelativeTravelBudget + 1e-6)
                {
                    NoteBudgetViolation(intervals, hh, remaining);
                }

                CheckPocketCaptures();
                ApplyWhiteHoles();
                ResolveAllOverlaps();
                remaining -= hh;
            }

            if (remaining > TimeEpsilon)
            {
                var tt = Time + (dt - remaining);
                BeginInterval(remaining, false);
                MoveFree(remaining, tt);
                MeasureMovement(ref maxTravel, ref maxRel);
                CheckPocketCaptures();
                ApplyWhiteHoles();
                ResolveAllOverlaps();
            }
        }
    }

    /// <summary>
    /// Rewind step: the cue is FORCED along the frozen snapshot at the constant crawl rate while
    /// the live history keeps appending; object balls continue normal physics inside the same
    /// chord-budgeted intervals and are pushed by the cue (one-way impulse, GDD §4.4).
    /// </summary>
    private void StepRewind()
    {
        var remaining = Config.SimulationStep;
        var intervals = 0;
        var maxTravel = 0.0;
        var maxRel = 0.0;
        var crawledTotal = 0.0;
        ApplyFriction(Config.SimulationStep, Time);

        while (remaining > TimeEpsilon)
        {
            if (intervals >= Config.MaxIntervalsPerStep)
            {
                Stats.StepGuardHits++;
                break;
            }

            intervals++;
            var h = Math.Min(remaining, MaxIntervalDurationRewind());
            var t0 = Time + (Config.SimulationStep - remaining);
            BeginInterval(h, true);

            var crawl = Config.RewindCrawlPxPerS * h;
            if (Budget.MaxDistance > 0.0)
            {
                var left = Budget.MaxDistance - RewindDistanceUsed;
                if (left < 0.0) left = 0.0;
                if (crawl > left) crawl = left;
            }

            var crawled = CrawlCue(crawl, t0, out var arrivedAtStart, out var budgetExhausted);
            crawledTotal += crawled;

            MoveFreeExceptCue(h, t0);
            MeasureMovement(ref maxTravel, ref maxRel);

            ResolveRewindContacts();
            ResolveAllOverlaps();
            ApplyRailsToFreeBalls();

            // White holes are evaluated after the position for this substep is final and after the
            // rails have been resolved, so the boost is applied to the ball's settled velocity for
            // the step. Rising-edge tracking inside makes once-per-substep correct regardless of how
            // many substeps the interval took.
            ApplyWhiteHoles();

            CheckPocketCaptures();
            ClampSpeeds();

            remaining -= h;

            if (arrivedAtStart)
            {
                EndRewindInternal(zeroVelocity: true);
                break;
            }

            if (budgetExhausted)
            {
                // Budget exhausted behaves exactly like a release (no player input is logged:
                // the auto-end is deterministic and must not be replayed twice).
                EndRewindInternal(zeroVelocity: false);
                break;
            }
        }

        LastRewindCrawlDistance = crawledTotal;
        if (!Rewinding && remaining > TimeEpsilon) RunFreeIntervals(ref remaining, ref intervals, ref maxTravel, ref maxRel);
        RecordIntervalStats(intervals, maxTravel, maxRel);
    }

    /// <summary>Interval length that keeps every ball within the absolute/relative travel budget.</summary>
    private double MaxIntervalDuration()
    {
        var dt = Config.SimulationStep;
        var maxSpeed = 0.0;
        var maxRel = 0.0;
        for (var i = 0; i < Balls.Length; i++)
        {
            var a = Balls[i];
            if (a.Pocketed) continue;
            var sa = a.Speed;
            if (sa > maxSpeed) maxSpeed = sa;
            for (var j = i + 1; j < Balls.Length; j++)
            {
                var b = Balls[j];
                if (b.Pocketed) continue;
                var rel = (a.Velocity - b.Velocity).Length;
                if (rel > maxRel) maxRel = rel;
            }
        }

        var byTravel = maxSpeed > 0.0 ? Config.SubStepTravelBudget / maxSpeed : dt;
        var byRel = maxRel > 0.0 ? Config.SubStepRelativeTravelBudget / maxRel : dt;
        return Math.Min(dt, Math.Min(byTravel, byRel));
    }

    /// <summary>
    /// Interval length during a rewind. The chord guarantee is about *positional displacement*
    /// inside one interval, and while rewinding the cue is kinematic: its position advances at
    /// the fixed crawl rate (its velocity field - the reverse of the stored v, used for the
    /// rewind impulse - does not move it). Its velocity can still jump when the crawl crosses a
    /// history entry, which is why the crawl rate, not the stored speed, sets the bound.
    /// </summary>
    private double MaxIntervalDurationRewind()
    {
        var budget = Config.SubStepTravelBudget;
        var relBudget = Config.SubStepRelativeTravelBudget;
        var h = Config.SimulationStep;
        var crawlRate = Config.RewindCrawlPxPerS;

        if (crawlRate > 0.0) h = Math.Min(h, budget / crawlRate);

        var maxObjectSpeed = 0.0;
        var maxObjectRelative = 0.0;
        for (var i = 0; i < Balls.Length; i++)
        {
            var a = Balls[i];
            if (a.Pocketed || a.IsCue) continue;
            var sa = a.Speed;
            if (sa > maxObjectSpeed) maxObjectSpeed = sa;
            for (var j = i + 1; j < Balls.Length; j++)
            {
                var b = Balls[j];
                if (b.Pocketed || b.IsCue) continue;
                var rel = (a.Velocity - b.Velocity).Length;
                if (rel > maxObjectRelative) maxObjectRelative = rel;
            }
        }

        if (maxObjectSpeed > 0.0) h = Math.Min(h, budget / maxObjectSpeed);
        var worstRelative = Math.Max(maxObjectRelative, crawlRate + maxObjectSpeed);
        if (worstRelative > 0.0) h = Math.Min(h, relBudget / worstRelative);

        return Math.Min(h, Config.SimulationStep);
    }

    private void RecordIntervalStats(int intervals, double maxTravel, double maxRel)
    {
        LastIntervals = intervals;
        LastMaxIntervalTravel = maxTravel;
        LastMaxIntervalRelativeTravel = maxRel;
        Stats.SubStepIntervals += intervals;
    }

    /// <summary>Snapshot positions so the interval's own movement can be measured exactly.</summary>
    private void BeginInterval(double duration, bool rewind)
    {
        _intervalDuration = duration;
        _intervalWasRewind = rewind;
        _intervalMaxRelativeA = -1;
        _intervalMaxRelativeB = -1;
        _intervalMaxTravelIndex = -1;
        for (var i = 0; i < Balls.Length; i++) _intervalStartPosition[i] = Balls[i].Position;
    }

    /// <summary>
    /// Exact displacement produced by the interval's *integration* movement, measured before any
    /// positional separation correction (those are exact geometry fixes, not integration error).
    /// These two numbers are what the chord-based sub-step guarantee is stated in, so they are
    /// measured rather than estimated from velocities.
    /// </summary>
    private void MeasureMovement(ref double maxTravel, ref double maxRelative)
    {
        var travel = 0.0;
        var travelIndex = -1;
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed) continue;
            var d = (b.Position - _intervalStartPosition[i]).Length;
            if (d > travel)
            {
                travel = d;
                travelIndex = i;
            }
        }

        var relative = 0.0;
        var relativeA = -1;
        var relativeB = -1;
        for (var i = 0; i < Balls.Length; i++)
        {
            var a = Balls[i];
            if (a.Pocketed) continue;
            for (var j = i + 1; j < Balls.Length; j++)
            {
                var b = Balls[j];
                if (b.Pocketed) continue;
                var d = ((a.Position - b.Position) - (_intervalStartPosition[i] - _intervalStartPosition[j])).Length;
                if (d > relative)
                {
                    relative = d;
                    relativeA = i;
                    relativeB = j;
                }
            }
        }

        if (travel > maxTravel)
        {
            maxTravel = travel;
            _intervalMaxTravelIndex = travelIndex;
        }

        if (relative > maxRelative)
        {
            maxRelative = relative;
            _intervalMaxRelativeA = relativeA;
            _intervalMaxRelativeB = relativeB;
            _maxRelativeDuration = _intervalDuration;
            _maxRelativeWasRewind = _intervalWasRewind;
        }
    }

    // -------------------------------------------------------------- friction

    private void ApplyFriction(double h, double timeStart)
    {
        if (Config.FrictionDecel <= 0.0) return;
        // ICE: the whole table's rolling deceleration is scaled. Applied to every ball, not only the
        // cue, because the card says "the table's friction is reduced" -- an ice card that only made
        // the cue slide would be a different, much weaker card.
        var tableScale = Cards.FrictionScale <= 0.0 ? 1.0 : Cards.FrictionScale;
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed) continue;
            if (b.IsCue && Rewinding) continue;
            var speed = b.Speed;
            if (speed <= 0.0) continue;
            // Per-type rolling deceleration: a heavy ball stops sooner than a light one. A negative
            // value inherits the global, so the locked constant stays authoritative by default.
            var decel = b.Params.FrictionDecel >= 0.0 ? b.Params.FrictionDecel : Config.FrictionDecel;
            decel *= tableScale;
            var next = speed - decel * h;
            if (next <= 0.0)
            {
                // Velocity crossing zero stops the ball exactly (locked §3) and the history is
                // flushed so the recorded path ends where the ball actually stopped.
                b.Velocity = Vec2.Zero;
                if (b.RecordsHistory) b.History.Flush(b.Position, Vec2.Zero, timeStart);
            }
            else
            {
                b.Velocity = b.Velocity.WithLength(next);
            }
        }
    }

    // ------------------------------------------------------------- movement

    private void MoveFree(double duration, double timeStart)
    {
        if (duration <= 0.0) return;
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed) continue;
            if (b.IsCue && Rewinding) continue;
            MoveOne(b, duration, timeStart);
        }
    }

    private void MoveFreeExceptCue(double duration, double timeStart)
    {
        if (duration <= 0.0) return;
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed || b.IsCue) continue;
            MoveOne(b, duration, timeStart);
        }
    }

    private static void MoveOne(Ball b, double duration, double timeStart)
    {
        var from = b.Position;
        var to = from + b.Velocity * duration;
        b.Position = to;
        if (b.RecordsHistory) b.History.Advance(from, to, b.Velocity, timeStart, duration);
    }

    // ---------------------------------------------------------------- events

    /// <summary>Earliest contact time inside [0, h], or -1 when nothing is going to happen.</summary>
    private double FindEarliestEvent(double h)
    {
        var best = double.PositiveInfinity;

        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed) continue;
            if (b.IsCue && Rewinding) continue;
            var t = RailTimeOfImpact(b);
            if (t >= 0.0 && t < best) best = t;
        }

        for (var i = 0; i < Balls.Length; i++)
        {
            var a = Balls[i];
            if (a.Pocketed) continue;
            if (a.IsCue && Rewinding) continue;
            for (var j = i + 1; j < Balls.Length; j++)
            {
                var b = Balls[j];
                if (b.Pocketed) continue;
                if (b.IsCue && Rewinding) continue;
                var t = PairTimeOfImpact(a, b);
                if (t >= 0.0 && t < best) best = t;
            }
        }

        return best <= h ? best : -1.0;
    }

    /// <summary>Analytic time until a ball touches a wall, or -1 when it is not heading at one.</summary>
    private double RailTimeOfImpact(Ball b)
    {
        var best = double.PositiveInfinity;
        var slop = 1e-9;

        if (b.Velocity.X < 0.0)
        {
            var wallX = Level.TableLeft + b.Radius;
            var t = b.Position.X <= wallX + slop ? 0.0 : (wallX - b.Position.X) / b.Velocity.X;
            if (t < best) best = t;
        }

        if (b.Velocity.X > 0.0)
        {
            var wallX = Level.TableRight - b.Radius;
            var t = b.Position.X >= wallX - slop ? 0.0 : (wallX - b.Position.X) / b.Velocity.X;
            if (t < best) best = t;
        }

        if (b.Velocity.Y < 0.0)
        {
            var wallY = Level.TableTop + b.Radius;
            var t = b.Position.Y <= wallY + slop ? 0.0 : (wallY - b.Position.Y) / b.Velocity.Y;
            if (t < best) best = t;
        }

        if (b.Velocity.Y > 0.0)
        {
            var wallY = Level.TableBottom - b.Radius;
            var t = b.Position.Y >= wallY - slop ? 0.0 : (wallY - b.Position.Y) / b.Velocity.Y;
            if (t < best) best = t;
        }

        return double.IsPositiveInfinity(best) ? -1.0 : Math.Max(0.0, best);
    }

    /// <summary>
    /// Analytic time of first contact of two circles in relative linear motion: solve
    /// |d + v t| = 2R + gap. Returns -1 when the pair is separating or never touches.
    ///
    /// The contact gap must be shared with <see cref="ResolvePair"/>: if the "is there an event"
    /// test and the "are they touching" test disagree at exactly-contact distance, a step spins
    /// forever on time-of-impact zero without ever resolving anything.
    /// </summary>
    private double PairTimeOfImpact(Ball a, Ball b)
    {
        var d = b.Position - a.Position;
        var v = b.Velocity - a.Velocity;
        var radius = a.Radius + b.Radius + Config.ContactGap;
        var c = d.LengthSquared - radius * radius;

        var closing = 2.0 * Vec2.Dot(d, v);
        if (c <= 0.0) return closing < 0.0 ? 0.0 : -1.0; // already touching: only an event if closing

        var vv = v.LengthSquared;
        if (vv <= 0.0) return -1.0;
        if (closing >= 0.0) return -1.0; // moving apart

        var disc = closing * closing - 4.0 * vv * c;
        if (disc < 0.0) return -1.0;

        var t = (-closing - Math.Sqrt(disc)) / (2.0 * vv);
        return t < 0.0 ? 0.0 : t;
    }

    private void ResolveEvents(ref int events)
    {
        // The world has already been advanced to the earliest contact: resolve everything that is
        // in contact at this instant. Rails first (they do not couple balls), then pairs by index.
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed) continue;
            if (b.IsCue && Rewinding) continue;
            ApplyRail(b);
        }

        for (var i = 0; i < Balls.Length; i++)
        {
            var a = Balls[i];
            if (a.Pocketed) continue;
            for (var j = i + 1; j < Balls.Length; j++)
            {
                var b = Balls[j];
                if (b.Pocketed) continue;
                if (ResolvePair(a, b)) events++;
            }
        }
    }

    private void ApplyRailsToFreeBalls()
    {
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed) continue;
            if (b.IsCue && Rewinding) continue;
            ApplyRail(b);
        }
    }

    /// <summary>
    /// Clamp a ball back inside the rail box, reflect the component that is actually moving into
    /// the wall, then apply the magnitude transform (locked §4) AT MOST ONCE per ball per physics
    /// step. A ball merely resting against a wall (zero normal velocity) is clamped but never
    /// boosted: with an additive gain that would otherwise be a free energy source.
    /// </summary>
    private bool ApplyRail(Ball b)
    {
        var pos = b.Position;
        var vel = b.Velocity;

        var touchLeft = pos.X - b.Radius <= Level.TableLeft + ContactSlop;
        var touchRight = pos.X + b.Radius >= Level.TableRight - ContactSlop;
        var touchTop = pos.Y - b.Radius <= Level.TableTop + ContactSlop;
        var touchBottom = pos.Y + b.Radius >= Level.TableBottom - ContactSlop;

        var hitLeft = touchLeft && vel.X < 0.0;
        var hitRight = touchRight && vel.X > 0.0;
        var hitTop = touchTop && vel.Y < 0.0;
        var hitBottom = touchBottom && vel.Y > 0.0;

        var x = pos.X;
        var y = pos.Y;
        var clamped = false;
        if (pos.X - b.Radius < Level.TableLeft)
        {
            x = Level.TableLeft + b.Radius;
            clamped = true;
        }

        if (pos.X + b.Radius > Level.TableRight)
        {
            x = Level.TableRight - b.Radius;
            clamped = true;
        }

        if (pos.Y - b.Radius < Level.TableTop)
        {
            y = Level.TableTop + b.Radius;
            clamped = true;
        }

        if (pos.Y + b.Radius > Level.TableBottom)
        {
            y = Level.TableBottom - b.Radius;
            clamped = true;
        }

        if (clamped) b.Position = new Vec2(x, y);

        if (!hitLeft && !hitRight && !hitTop && !hitBottom) return false;

        var vx = vel.X;
        var vy = vel.Y;
        if (hitLeft) vx = Math.Abs(vx);
        if (hitRight) vx = -Math.Abs(vx);
        if (hitTop) vy = Math.Abs(vy);
        if (hitBottom) vy = -Math.Abs(vy);
        b.Velocity = new Vec2(vx, vy);
        Stats.RailContacts++;

        var index = IndexOf(b);
        if (index >= 0 && _railTransformUsedThisStep[index] == 0)
        {
            _railTransformUsedThisStep[index] = 1;
            var speed = b.Velocity.Length;
            var p = b.Params;

            // The bonus is per-ball and CAPPED BY COUNT. Once a ball has used its allowance the
            // reflection and its restitution still apply exactly as before -- only the free energy
            // stops. That keeps the rail useful without letting one ball farm speed forever.
            var boosted = b.CanTakeWallBoost;
            // Resolution order: the type's own value, else the locked global for its role. A
            // negative value is the "inherit" sentinel, so the locked globals stay authoritative
            // unless a type deliberately overrides them.
            var typeGain = p.WallGain;
            var baseGain = b.IsCue ? Config.CueWallGain : Config.ObjectBallGain;
            var flatGain = boosted ? (typeGain < 0.0 ? baseGain : typeGain) : 0.0;

            // THE RAIL ENGINE: a cushion also returns a FRACTION OF THE LAUNCH SPEED as momentum. It is a
            // fraction of the shot the player actually took rather than a constant, so a full-power break
            // earns a full bounce and a tap earns a small one. Capped by MaxWallBoosts like the flat gain.
            var typeFrac = p.WallGainFrac;
            var baseFrac = b.IsCue ? Config.CueWallGainFrac : Config.ObjectBallGainFrac;
            var fracGain = boosted ? (typeFrac < 0.0 ? baseFrac : typeFrac) * LaunchSpeed : 0.0;

            var gain = flatGain + fracGain;
            var restitution = p.Restitution >= 0.0 ? p.Restitution : Config.WallRestitution;
            var next = Math.Min(speed * restitution + gain, Config.SpeedCap);
            b.Velocity = b.Velocity.WithLength(next);

            if (boosted) b.WallBoostsUsed++;
            Stats.RailTransforms++;
        }

        return true;
    }

    private int IndexOf(Ball ball)
    {
        for (var i = 0; i < Balls.Length; i++)
        {
            if (ReferenceEquals(Balls[i], ball)) return i;
        }

        return -1;
    }

    /// <summary>Near-elastic resolution with positional de-overlap. Never moves the rewinding cue.</summary>
    private bool ResolvePair(Ball a, Ball b)
    {
        if (a.IsCue && Rewinding) return false;
        if (b.IsCue && Rewinding) return false;

        var d = b.Position - a.Position;
        var distSq = d.LengthSquared;
        var minDist = a.Radius + b.Radius;
        var touchDist = minDist + Config.ResolveGap;
        // Stricter than PairTimeOfImpact's contact radius on purpose (ResolveGap > ContactGap):
        // any pair the event solver calls touching must be resolvable here, otherwise a step
        // spins on a zero time of impact that nothing ever acts on.
        if (distSq > touchDist * touchDist) return false;

        var dist = Math.Sqrt(distSq);
        var n = dist > 1e-9 ? d / dist : new Vec2(1.0, 0.0);

        Stats.BallBallContacts++;

        var penetration = minDist - dist + Config.SeparationEpsilon;
        var invA = 1.0 / a.Mass;
        var invB = 1.0 / b.Mass;
        var invSum = invA + invB;
        a.Position -= n * (penetration * (invA / invSum));
        b.Position += n * (penetration * (invB / invSum));

        var vAlong = Vec2.Dot(b.Velocity - a.Velocity, n);
        if (vAlong >= 0.0) return true;

        // Restitution comes from the two types involved (mean of the pair), so a "bouncier" ball
        // type really does leave a collision faster. A negative value inherits the locked global.
        var restA = a.Params.Restitution >= 0.0 ? a.Params.Restitution : Config.BallRestitution;
        var restB = b.Params.Restitution >= 0.0 ? b.Params.Restitution : Config.BallRestitution;
        var restitution = 0.5 * (restA + restB);

        var j = -(1.0 + restitution) * vAlong / invSum;

        // Capture pre-impact velocities so a per-type collision boost can scale only the velocity
        // that the impact actually adds.
        var aPreVelocity = a.Velocity;
        var bPreVelocity = b.Velocity;

        a.Velocity -= n * (j * invA);
        b.Velocity += n * (j * invB);

        // Optional per-type collision boost. 1.0 (the default) is a plain energy-conserving
        // collision. Above 1.0 the STRUCK ball's newly gained velocity is multiplied by ITS OWN
        // type's boost -- the "a collision accelerates the ball according to its type" behaviour
        // that was asked for.
        //
        // It must scale what the impact ADDED, not the struck ball's whole velocity: otherwise a
        // ball that was already rolling would have its pre-existing motion amplified too, and the
        // result would depend on history rather than on the collision.
        var strike = a.IsCue ? b : a;
        var struckBoost = strike.Params.CollisionBoost;
        if (struckBoost > 0.0 && Math.Abs(struckBoost - 1.0) > 1e-12)
        {
            var preVel = a.IsCue ? bPreVelocity : aPreVelocity;
            strike.Velocity = preVel + (strike.Velocity - preVel) * struckBoost;
        }

        // ANTIMATTER: "collisions no longer decelerate, and the lost momentum is stacked back in the
        // reverse direction".
        //
        // A restitution below 1 means the pair's closing speed along the normal drops by
        // (1 - e) * vAlong. That shortfall is the energy a normal collision throws away. This card
        // adds it BACK along the reverse normal, scaled by AntimatterGain: at 1.0 the collision
        // becomes perfectly non-losing, and above 1.0 the pair actually gains momentum, which is what
        // "叠加" asks for.
        //
        // It must NOT be folded into the restitution coefficient: restitution is clamped to [0,1] by
        // Validate() precisely because a coefficient above 1 is a divergence risk stated in the locked
        // table. A separate additive term keeps that invariant intact and makes the card removable.
        if (Cards.Antimatter)
        {
            var lost = (1.0 - restitution) * (-vAlong);
            if (lost > 0.0)
            {
                var add = lost * Cards.AntimatterGain;
                // Reverse normal: +n pushes b one way and a the other, exactly like the impulse above.
                a.Velocity -= n * (add * (invA / invSum));
                b.Velocity += n * (add * (invB / invSum));
                Stats.AntimatterBoosts++;
            }
        }

        if (Config.SpeedCap > 0.0)
        {
            if (a.Speed > Config.SpeedCap) a.Velocity = a.Velocity.WithLength(Config.SpeedCap);
            if (b.Speed > Config.SpeedCap) b.Velocity = b.Velocity.WithLength(Config.SpeedCap);
        }

        return true;
    }

    private void ResolveAllOverlaps()
    {
        for (var i = 0; i < Balls.Length; i++)
        {
            var a = Balls[i];
            if (a.Pocketed) continue;
            for (var j = i + 1; j < Balls.Length; j++)
            {
                var b = Balls[j];
                if (b.Pocketed) continue;
                ResolvePair(a, b);
            }
        }
    }

    // ----------------------------------------------------------------- rewind

    /// <summary>
    /// Crawl the cue backwards along the frozen snapshot by at most
    /// <paramref name="maxDistance"/> px of ARC LENGTH, appending the traversal to the LIVE
    /// history. Returns the arc length actually crawled.
    /// </summary>
    private double CrawlCue(double maxDistance, double timeStart, out bool arrivedAtStart, out bool budgetExhausted)
    {
        var cue = Cue;
        var from = cue.Position;
        var remaining = maxDistance;
        var arc = 0.0;
        arrivedAtStart = false;
        budgetExhausted = false;

        while (true)
        {
            if (_cursor <= 0)
            {
                arrivedAtStart = true;
                break;
            }

            var segLen = (_track[_cursor].Position - _track[_cursor - 1].Position).Length;
            if (segLen <= SegmentEpsilon)
            {
                _cursor--;
                _u = 1.0;
                continue;
            }

            var available = _u * segLen;
            if (remaining < available)
            {
                _u -= remaining / segLen;
                if (_u < 0.0) _u = 0.0;
                arc += remaining;
                remaining = 0.0;
                break;
            }

            remaining -= available;
            arc += available;
            _cursor--;
            _u = 1.0;
            if (_cursor == 0)
            {
                arrivedAtStart = true;
                break;
            }
        }

        var segmentIndex = arrivedAtStart ? 1 : _cursor;
        var travelVelocity = segmentIndex >= 1 && segmentIndex < _track.Length
            ? -_track[segmentIndex].Velocity
            : Vec2.Zero;

        var to = arrivedAtStart
            ? _track[0].Position
            : Vec2.Lerp(_track[_cursor - 1].Position, _track[_cursor].Position, _u);

        cue.Position = to;
        cue.Velocity = arrivedAtStart ? Vec2.Zero : travelVelocity;
        cue.Pocketed = false;

        // The traversal is itself appended to the live history (GDD §4.2). The stored velocity is
        // the reverse of what the ball had when it passed this point forwards, NOT the crawl rate:
        // crawling and release velocity are deliberately decoupled (params v1.1 §6.1).
        cue.History.Advance(from, to, travelVelocity, timeStart, Config.SimulationStep);

        RewindDistanceUsed += arc;
        if (Budget.MaxDistance > 0.0 && RewindDistanceUsed >= Budget.MaxDistance - 1e-12) budgetExhausted = true;
        return arc;
    }

    /// <summary>One-way rewind contact: push the object ball out, give it the normal impulse.</summary>
    private void ResolveRewindContacts()
    {
        var cue = Cue;
        if (cue.Pocketed) return;
        foreach (var target in Balls)
        {
            if (ReferenceEquals(target, cue) || target.Pocketed) continue;
            var d = target.Position - cue.Position;
            var distSq = d.LengthSquared;
            var minDist = cue.Radius + target.Radius;
            if (distSq >= minDist * minDist) continue;

            var dist = Math.Sqrt(distSq);
            var n = dist > 1e-9 ? d / dist : new Vec2(1.0, 0.0);
            var penetration = minDist - dist + Config.SeparationEpsilon;
            target.Position += n * penetration;

            var vRel = Vec2.Dot(cue.Velocity - target.Velocity, n);
            if (vRel > 0.0)
            {
                // j = max(0, v_rel . n) * K with K <= 1: no amplification, and a thin graze gives
                // less than a head-on hit because v_rel is the normal projection (locked §7).
                target.Velocity += n * (Config.RewindImpulseK * vRel);
                Stats.RewindContacts++;
            }
        }
    }

    // ---------------------------------------------------------------- white holes

    /// <summary>
    /// Energy-return zones. Applies once per ENTRY: a ball must leave the zone before it can be
    /// boosted again, so a ball cannot farm energy by drifting inside it.
    ///
    /// This exists because the cue ball is the only ball that can be rewound, so when it runs out of
    /// speed the level is over. A white hole turns "keep the cue alive" into a routing decision the
    /// player can plan, rather than a consequence of one strike.
    /// </summary>
    private void ApplyWhiteHoles()
    {
        if (Level.WhiteHoles.Count == 0) return;

        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed) continue;
            if (b.IsCue && Rewinding) continue; // the forced history path cannot be re-energised

            for (var h = 0; h < Level.WhiteHoles.Count; h++)
            {
                var hole = Level.WhiteHoles[h];
                if (!hole.AffectsObjectBalls && !b.IsCue) continue;

                var inside = (hole.Center - b.Position).LengthSquared < hole.Radius * hole.Radius;
                var wasInside = _whiteHoleInside[i, h];
                _whiteHoleInside[i, h] = inside;

                // Only fire on a rising edge, and only once the cooldown has elapsed. The edge alone
                // is not enough: a strong boost throws the ball out and friction brings it back,
                // which re-fired the hole repeatedly.
                if (!inside || wasInside) continue;
                if (Time - _whiteHoleLastFired[i, h] < hole.Cooldown) continue;

                var speed = b.Speed;

                // Direction: push the ball radially OUT from the hole centre. Chosen over "keep the
                // incoming direction" because it makes the exit predictable for a ball that has come
                // to rest INSIDE the hole -- which is the whole point of a rescue mechanic -- and
                // because it gives a stationary cue a deterministic direction instead of one derived
                // from a history heading that may not exist.
                var outward = b.Position - hole.Center;
                Vec2 dir;
                if (outward.LengthSquared > 1e-12)
                {
                    dir = outward.Normalized();
                }
                else if (speed > 1e-6)
                {
                    dir = b.Velocity / speed;          // dead centre but still moving: keep heading
                }
                else
                {
                    dir = new Vec2(0.0, 1.0);           // dead centre and stopped: eject downward
                }

                var next = Math.Max(speed * hole.Gain, hole.ExitSpeed);
                next = Math.Min(next, Math.Min(hole.MaxSpeed, Config.SpeedCap));
                if (next <= 0.0) continue;

                b.Velocity = dir * next;
                _whiteHoleLastFired[i, h] = Time;
                Stats.WhiteHoleActivations++;
            }
        }
    }

    /// <summary>Heading to use when a ball is stationary, so a white hole can restart it.</summary>
    private Vec2 LastHeading(Ball b)
    {
        var h = b.History;
        if (b.RecordsHistory && h != null && h.Samples.Count >= 2)
        {
            for (var i = h.Samples.Count - 1; i >= 1; i--)
            {
                var d = h.Samples[i].Position - h.Samples[i - 1].Position;
                if (d.LengthSquared > 1e-9) return d.Normalized();
            }
        }

        return new Vec2(0.0, 1.0);
    }

    // ---------------------------------------------------------------- pockets

    /// <summary>
    /// Locked pocket rule (§5): the ball's centre must be inside the capture circle AND its
    /// velocity must point into the pocket. The velocity condition stops the corner double-rail
    /// contact point (17.25 px from the corner centre) from being swallowed before the rails are
    /// resolved, which made T08b dead code in the prototype.
    /// </summary>
    public bool InPocketCapture(Ball ball)
    {
        foreach (var pocket in Level.Pockets)
        {
            var toCenter = pocket.Center - ball.Position;
            var r2 = pocket.Radius * pocket.Radius;
            if (toCenter.LengthSquared >= r2) continue;
            if (Vec2.Dot(ball.Velocity, toCenter) > 0.0) return true;
        }

        return false;
    }

    private void CheckPocketCaptures()
    {
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed) continue;
            if (b.IsCue && Rewinding) continue; // the forced path cannot be swallowed
            if (!InPocketCapture(b)) continue;
            Capture(b);
            if (Frozen) return;
        }
    }

    private void Capture(Ball b)
    {
        b.Pocketed = true;
        b.Velocity = Vec2.Zero;
        Stats.PocketCaptures++;

        // ------- the CUE going down is not progress -------------------------------------------
        if (b.IsCue)
        {
            // tibo: the cue being pocketed is normally just a wasted ball, but this card turns it
            // into a full refund of the rewind allowance, which is the whole point of the card.
            ApplyTiboRefund();
            return;
        }

        // ------- an OBJECT ball went down: this is where the reward lives -----------------------
        //
        // The user's rule: on a pot, draw three cards from the pool and make the player pick one.
        // Queued rather than applied here because the choice is the player's, and the simulation has
        // to stop until it is made. The win check is deliberately BELOW this so that the final pot of
        // a level still draws -- otherwise the last, most satisfying pot of every level would be the
        // one pot that gives nothing.
        QueueCardDraw();

        if (PocketedObjectCount < ObjectBallCount) return;
        Won = true;
        Frozen = true;
        EndRewindInternal(zeroVelocity: true);
    }

    /// <summary>Offer a fresh three-card draw, unless one is already waiting.</summary>
    private void QueueCardDraw()
    {
        if (AwaitingCardChoice) return;
        var hand = CardPool.Draw(ref _cardRng, Cards, 3);
        if (hand.Count == 0) return;   // pool exhausted: nothing left to offer
        PendingDraw = hand;
        CardDraws++;
    }

    /// <summary>
    /// Take one of the pending cards. Returns false when the index is out of range, which is how the
    /// UI refuses a stray click. The chosen card is applied immediately -- the user asked for the
    /// card to "take effect" on selection, not on some later trigger.
    /// </summary>
    public bool ChooseCard(int index)
    {
        if (!AwaitingCardChoice) return false;
        if (index < 0 || index >= PendingDraw.Count) return false;

        var id = PendingDraw[index];
        PendingDraw = null;
        Cards.Taken.Add(id);
        ApplyCard(CardPool.Get(id));
        return true;
    }

    /// <summary>Apply a card's effect. Every branch is idempotent in the sense that taking the same
    /// card twice is impossible (<see cref="CardPool.Draw"/> excludes taken ids), so no branch has to
    /// defend against being applied twice.</summary>
    private void ApplyCard(CardDef def)
    {
        if (def == null) return;

        switch (def.Id)
        {
            case CardId.Ice:
                // Multiplicative so a future "take Ice twice" (a different pool) composes.
                Cards.FrictionScale *= def.FrictionScale;
                break;

            case CardId.Antimatter:
                Cards.Antimatter = true;
                Cards.AntimatterGain = def.AntimatterGain;
                break;

            case CardId.Titan:
                ApplyTitan(def);
                break;

            case CardId.Tibo:
                // Nothing to do on acquisition: this card is a RULE about the cue being pocketed, and
                // the rule is checked in Capture(). Recording it in Taken is the whole effect.
                break;

            case CardId.Balloon:
                Cards.BalloonArmed = true;
                break;

            case CardId.TimeCocoon:
                ApplyTimeCocoon(def);
                break;
        }

        Stats.CardsTaken++;
    }

    /// <summary>
    /// Titan: the cue's radius grows and its mass grows with it.
    ///
    /// Why the mass increase is the interesting half: an earlier fix for "a full-power head-on hit
    /// leaves the cue with 2% of its speed" was to make the cue heavier, which is also why this card
    /// costs mass. The radius is what makes it read visually and what makes it easier to contact
    /// balls; the mass is the tax.
    /// </summary>
    private void ApplyTitan(CardDef def)
    {
        var cue = Cue;
        cue.Radius *= def.TitanRadiusScale;
        cue.Mass *= def.TitanMassScale;
        cue.Params.Mass = cue.Mass;
        Cards.Titan = true;

        // A bigger cue may now overlap the rail box or another ball. Push it to a legal position
        // rather than letting the next step resolve a large penetration in one go, which would look
        // like a teleport.
        ClampCueToTable(cue);
        ResolveAllOverlaps();
    }

    /// <summary>
    /// Time Cocoon: burn up to N rewind layers, put the cue back at its start position, and give the
    /// player a fresh swing. The other balls do NOT move -- that is the card's stated identity, and
    /// it is what makes it useful when the layout is already good and only the cue is stuck.
    ///
    /// "Immediately reduces at most 4 rewind layers" is read as CONSUMING the budget: the card spends
    /// layers to buy the reset, so it is not a free retry. Burning more than the player has is
    /// clamped rather than refused, so the card is never dead on arrival.
    /// </summary>
    private void ApplyTimeCocoon(CardDef def)
    {
        var burned = 0;
        if (Budget.MaxSegments > 0)
        {
            for (var i = 0; i < def.MaxLayersBurned && RewindSegmentsUsed < Budget.MaxSegments; i++)
            {
                RewindSegmentsUsed++;
                burned++;
            }
        }
        else
        {
            burned = def.MaxLayersBurned;
            RewindSegmentsUsed += burned;
        }

        Cards.LayersBurned = burned;

        // Return the cue to where the level started it, kill its velocity and drop its history so
        // the rewind verb starts from a clean slate.
        var cue = Cue;
        var spawn = Level.Balls[0];
        if (Rewinding) EndRewindInternal(zeroVelocity: true);
        cue.Position = new Vec2(spawn.X, spawn.Y);
        cue.Velocity = Vec2.Zero;
        cue.Pocketed = false;
        cue.History.Reset(cue.Position, Vec2.Zero, Time);
        _track = null;
        _cursor = 0;
        _u = 0.0;

        // "gets one more swing": give the shot back, clamped to the level's own allowance so a level
        // with maxShots 1 does not silently become a two-strike level.
        if (ShotsUsed > 0) ShotsUsed--;

        ClampCueToTable(cue);
        ResolveAllOverlaps();
    }

    /// <summary>tibo: refill the rewind allowance when the cue is pocketed.</summary>
    private void ApplyTiboRefund()
    {
        if (!Cards.Has(CardId.Tibo)) return;
        RewindSegmentsUsed = 0;
        RewindDistanceUsed = 0.0;
        Stats.TiboRefunds++;
    }

    /// <summary>Keep a ball's centre inside the rail box. Used after a radius change.</summary>
    private void ClampCueToTable(Ball b)
    {
        var x = b.Position.X;
        var y = b.Position.Y;
        if (x - b.Radius < Level.TableLeft) x = Level.TableLeft + b.Radius;
        if (x + b.Radius > Level.TableRight) x = Level.TableRight - b.Radius;
        if (y - b.Radius < Level.TableTop) y = Level.TableTop + b.Radius;
        if (y + b.Radius > Level.TableBottom) y = Level.TableBottom - b.Radius;
        b.Position = new Vec2(x, y);
    }

    // ------------------------------------------------------------------ balloon

    /// <summary>
    /// The balloon card, evaluated once per step after the physics has run.
    ///
    /// The card's bargain, exactly as specified: the next time the cue's speed reaches zero the run is
    /// LOST -- but the cue flies up, and the first ball it touches is carried into a pocket.
    ///
    /// Why it is phrased as "reaches zero" rather than "is at rest": the kernel's locked friction rule
    /// snaps a ball to exactly zero when its velocity crosses zero (§3), so "reached zero" is a real,
    /// observable event rather than an epsilon test that a slow-moving ball could satisfy by accident.
    /// The arm is one-shot: it fires once and then the card is spent, so a lost run cannot be
    /// mistaken for a win if the carried ball happens to drop.
    /// </summary>
    private void UpdateBalloon()
    {
        if (!Cards.BalloonArmed || Cards.Lost || Won || Frozen) return;

        // A rewind moves the cue kinematically, so its speed is not a "dead ball" signal: if the
        // balloon fired mid-rewind the player could never use the rewind verb again.
        if (Rewinding) return;

        var cue = Cue;

        if (Cards.BalloonFlying)
        {
            CarryBalloonTarget();
            return;
        }

        if (cue.Speed > 1e-9) return;

        // The cue has stopped: the balloon takes flight.
        Cards.BalloonFlying = true;
        Cards.BalloonTarget = -1;
        Stats.BalloonFailures++;

        // Fly to the nearest ball and carry it. Flying is modelled as an immediate decision rather
        // than as a visible arc, because the kernel has no notion of height and faking one with a
        // velocity would corrupt the deterministic physics. The VIEW draws the arc.
        var target = NearestBallTo(cue);
        if (target < 0)
        {
            LoseRun("气球飞起时台上已经没有可带走的球");
            return;
        }

        Cards.BalloonTarget = target;
    }

    /// <summary>
    /// Second half of the balloon flight: carry the touched ball into a pocket, then end the run.
    /// Kept separate from the arm so the view can show the cue rising before the ball drops.
    /// </summary>
    private void CarryBalloonTarget()
    {
        var target = Cards.BalloonTarget;
        if (target < 0 || target >= Balls.Length) return;
        var ball = Balls[target];
        if (ball.Pocketed)
        {
            LoseRun("气球带走的球在途中已被其他方式打进球袋");
            return;
        }

        // Aim at the nearest pocket and hand the ball enough speed to reach it. This is a REAL
        // capture (it goes through Capture, so it triggers the draw and the win check) rather than a
        // flag being flipped, so a balloon delivery can still win the level -- which is the entire
        // reason a player would ever take this card.
        var pocket = NearestPocketTo(ball.Position);
        if (pocket == null)
        {
            LoseRun("气球找不到可用的球袋");
            return;
        }

        var toPocket = pocket.Center - ball.Position;
        var dist = toPocket.Length;
        if (dist > 1e-9)
        {
            var dir = toPocket / dist;
            var speed = Math.Min(CardPool.Get(CardId.Balloon).BalloonCarrySpeed, Config.SpeedCap);
            ball.Velocity = dir * speed;
        }

        Cards.BalloonFlying = false;
        Cards.BalloonTarget = -1;

        // Deliver immediately instead of waiting for the ball to travel: the run is about to end, and
        // leaving the capture to the next step would make the outcome depend on how many steps the
        // caller happens to run after the loss.
        Capture(ball);
        if (!Won) LoseRun("气球把白球留在了台上");
    }

    private int NearestBallTo(Ball from)
    {
        var best = -1;
        var bestDist = double.PositiveInfinity;
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            if (b.Pocketed || b.IsCue) continue;
            var d = (b.Position - from.Position).LengthSquared;
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        return best;
    }

    private PocketDef NearestPocketTo(Vec2 position)
    {
        PocketDef best = null;
        var bestDist = double.PositiveInfinity;
        foreach (var p in Level.Pockets)
        {
            var d = (p.Center - position).LengthSquared;
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }

        return best;
    }

    private void LoseRun(string reason)
    {
        if (Cards.Lost) return;
        Cards.Lost = true;
        Cards.LostReason = reason;
        Cards.BalloonFlying = false;
        Cards.BalloonTarget = -1;
        Frozen = true;
        EndRewindInternal(zeroVelocity: true);
    }

    /// <summary>True once the level can no longer be won (the balloon card ended it).</summary>
    public bool Lost => Cards.Lost;

    // ------------------------------------------------------------------- misc

    private void ClampSpeeds()
    {
        foreach (var b in Balls)
        {
            if (b.Pocketed) continue;
            var speed = b.Speed;
            if (double.IsNaN(speed) || double.IsInfinity(speed)) continue; // HasNonFiniteState reports it
            if (speed > MaxObservedSpeed) MaxObservedSpeed = speed;
            if (speed > Config.SpeedCap) b.Velocity = b.Velocity.WithLength(Config.SpeedCap);
        }
    }

    /// <summary>True when any ball holds a NaN or Infinity in position or velocity.</summary>
    public bool HasNonFiniteState()
    {
        foreach (var b in Balls)
        {
            if (!b.Position.IsFinite || !b.Velocity.IsFinite) return true;
        }

        return false;
    }

    public IEnumerable<Ball> ObjectBalls()
    {
        foreach (var b in Balls)
        {
            if (b.Type == BallType.Object) yield return b;
        }
    }
}
