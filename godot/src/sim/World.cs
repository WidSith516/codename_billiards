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
        _intervalStartPosition = new Vec2[Balls.Length];
        for (var i = 0; i < Balls.Length; i++) Balls[i] = other.Balls[i].Clone();

        Time = other.Time;
        Tick = other.Tick;
        ShotUsed = other.ShotUsed;
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

    public bool ShotUsed { get; private set; }

    public bool Won { get; private set; }

    /// <summary>Set when the level is won: the kernel stops advancing (stable terminal state).</summary>
    public bool Frozen { get; private set; }

    public bool Rewinding { get; private set; }

    public int RewindSegmentsUsed { get; private set; }

    public double RewindDistanceUsed { get; private set; }

    public Ball Cue => Balls[0];

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

    public int ObjectBallCount
    {
        get
        {
            var n = 0;
            foreach (var b in Balls)
            {
                if (b.Type == BallType.Object) n++;
            }

            return n;
        }
    }

    public int PocketedObjectCount
    {
        get
        {
            var n = 0;
            foreach (var b in Balls)
            {
                if (b.Type == BallType.Object && b.Pocketed) n++;
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
        ShotUsed = false;
        Won = false;
        Frozen = false;
        Rewinding = false;
        RewindSegmentsUsed = 0;
        RewindDistanceUsed = 0.0;
        _track = null;
        _cursor = 0;
        _u = 0.0;
        Inputs.Clear();
        Stats.Rewinds = 0;
        Stats.RewindContacts = 0;
        Stats.RailTransforms = 0;
        Stats.BallBallContacts = 0;
        Stats.RailContacts = 0;
        Stats.PocketCaptures = 0;
        Stats.SubStepIntervals = 0;
        Stats.StepGuardHits = 0;
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
        ShotUsed = s.ShotUsed;
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
        ShotUsed = true;
        Inputs.Add(new InputCommand { Tick = Tick, Kind = InputKind.Shot, A = angleDegrees, B = power });
        return true;
    }

    /// <summary>
    /// Begin rewinding the cue ball: freeze a read-only snapshot of the history as it is now and
    /// put the cursor at its end. The live history keeps appending during the rewind (GDD §4.2),
    /// which is what makes A→B→C→D rewind as D→C→B→A.
    /// </summary>
    public bool StartRewind()
    {
        if (Frozen || Won || Rewinding || !ShotUsed) return false;
        if (Budget.MaxSegments > 0 && RewindSegmentsUsed >= Budget.MaxSegments) return false;
        if (Budget.MaxDistance > 0.0 && RewindDistanceUsed >= Budget.MaxDistance) return false;

        var cue = Cue;
        if (cue.History.Count < 2) return false;

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
        RewindSegmentsUsed++;
        Stats.Rewinds++;
        Inputs.Add(new InputCommand { Tick = Tick, Kind = InputKind.RewindStart });
        return true;
    }

    /// <summary>
    /// Release: the cue keeps the reverse of the stored velocity at the current history entry
    /// (locked: release velocity is the stored v negated, decoupled from the 240 px/s crawl).
    /// </summary>
    public void EndRewind()
    {
        if (!Rewinding) return;
        Cue.Velocity = ReverseReleaseVelocity();
        EndRewindInternal(zeroVelocity: false);
        Inputs.Add(new InputCommand { Tick = Tick, Kind = InputKind.RewindEnd });
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
            var gain = boosted ? (typeGain < 0.0 ? baseGain : typeGain) : 0.0;
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
        a.Velocity -= n * (j * invA);
        b.Velocity += n * (j * invB);

        // Optional per-type collision boost. 1.0 (the shipped default) is a plain energy-conserving
        // collision. Values above 1.0 deliberately inject energy, which the user asked for; because
        // that silently changes every level's tolerance window, it is OFF by default and must be
        // measured before it is turned on. Only the ball that GAINED speed is scaled, so a boost on
        // one type does not amplify the other.
        var boostA = a.Params.CollisionBoost > 0.0 ? a.Params.CollisionBoost : 1.0;
        var boostB = b.Params.CollisionBoost > 0.0 ? b.Params.CollisionBoost : 1.0;
        if (boostA != 1.0 && b.Speed > a.Speed) a.Velocity = a.Velocity * boostA;
        if (boostB != 1.0 && a.Speed > b.Speed) b.Velocity = b.Velocity * boostB;

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
        if (b.Type != BallType.Object) return;
        if (PocketedObjectCount < ObjectBallCount) return;
        Won = true;
        Frozen = true;
        EndRewindInternal(zeroVelocity: true);
    }

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
