using System;
using System.Collections.Generic;

namespace Rewind.Sim;

public enum BallType
{
    /// <summary>The white ball: the only ball that records history and can be rewound.</summary>
    Cue = 0,

    /// <summary>Scoring ball. Pocketed permanently; never rewound.</summary>
    Object = 1,

    /// <summary>
    /// Reserved: heavy ball. Data-only for now -- it loads and plays, with its physical character
    /// (mass, friction, rail bonus) taken from <see cref="PhysicsConfig.Types"/> rather than from
    /// any special-case behaviour. It was previously a bare string in the level file with no
    /// effect at all.
    /// </summary>
    Heavy = 2,

    /// <summary>Reserved: bomb ball. Loads and plays; behaviour not yet implemented.</summary>
    Bomb = 3,

    /// <summary>Reserved: anchor ball. Loads and plays; behaviour not yet implemented.</summary>
    Anchor = 4,
}

/// <summary>
/// One entry of the cue ball's history: the position it actually occupied, the velocity it
/// had while travelling to that position, and the simulation time at which it was written.
/// Plain serializable data (params v1 §6).
/// </summary>
public readonly struct HistorySample
{
    public readonly Vec2 Position;
    public readonly Vec2 Velocity;
    public readonly double Time;

    public HistorySample(Vec2 position, Vec2 velocity, double time)
    {
        Position = position;
        Velocity = velocity;
        Time = time;
    }
}

/// <summary>
/// Append-only, arc-length-resampled history (params v1 §6 / port plan decision 2).
///
/// A new entry is appended every <see cref="Interval"/> px of *travelled arc length*, never
/// per frame: the prototype's "record whenever displacement &gt; 1e-5" produced entries
/// whose spacing had median 0.0996 px and max 10.39 px, which is what made rewind playback
/// jerky and the drawn trail cut corners.
///
/// Coordinate system of a single <see cref="Advance"/> call: the ball travels in a straight
/// line from <c>from</c> to <c>to</c> at constant velocity <c>v</c> (the integrator splits
/// motion at every collision event, so this holds exactly). Entries are emitted by
/// interpolating inside that segment, and each entry stores <c>v</c> - the velocity used to
/// *arrive* at that entry's position. Rewinding therefore takes the stored velocity negated,
/// with no step-size coupling (port plan decision 3).
/// </summary>
public sealed class ArcHistory
{
    private readonly List<HistorySample> _samples = new List<HistorySample>();
    private double _sinceSample;

    public ArcHistory(double interval)
    {
        Interval = interval > 0.0 ? interval : 1.0;
    }

    /// <summary>Arc-length sampling interval in px (locked: 2.0).</summary>
    public double Interval { get; }

    public List<HistorySample> Samples => _samples;

    public int Count => _samples.Count;

    public HistorySample this[int index] => _samples[index];

    /// <summary>Distance travelled since the last emitted entry.</summary>
    public double DistanceSinceSample => _sinceSample;

    public void Reset(Vec2 position, Vec2 velocity, double time)
    {
        _samples.Clear();
        _sinceSample = 0.0;
        _samples.Add(new HistorySample(position, velocity, time));
    }

    /// <summary>
    /// Advance the sampler over one straight, constant-velocity movement primitive.
    /// Emits one entry per <see cref="Interval"/> px travelled, interpolated in space.
    /// </summary>
    public void Advance(Vec2 from, Vec2 to, Vec2 velocity, double timeStart, double duration)
    {
        var delta = to - from;
        var segLen = delta.Length;
        if (segLen <= 0.0 || double.IsNaN(segLen)) return;

        var dir = delta / segLen;
        var travelled = 0.0;
        while (_sinceSample + (segLen - travelled) >= Interval)
        {
            var need = Interval - _sinceSample;
            travelled += need;
            var frac = travelled / segLen;
            var time = timeStart + duration * frac;
            _samples.Add(new HistorySample(from + dir * travelled, velocity, time));
            _sinceSample = 0.0;
        }

        _sinceSample += segLen - travelled;
    }

    /// <summary>
    /// Append an entry at the current position regardless of the arc-length residual.
    /// Used when the ball comes to a full stop and when a rewind snapshot is about to be
    /// taken, so that the history ends exactly where the ball is standing.
    /// </summary>
    public void Flush(Vec2 position, Vec2 velocity, double time)
    {
        _samples.Add(new HistorySample(position, velocity, time));
        _sinceSample = 0.0;
    }

    public HistorySample[] Snapshot()
    {
        return _samples.ToArray();
    }

    /// <summary>Restore from decoded state (deserialization only).</summary>
    public void LoadFrom(IEnumerable<HistorySample> samples, double distanceSinceSample)
    {
        _samples.Clear();
        _samples.AddRange(samples);
        _sinceSample = distanceSinceSample;
    }
}

/// <summary>A pocket: a capture circle in table coordinates (params v1 §5).</summary>
public sealed class PocketDef
{
    public Vec2 Center;
    public double Radius;

    public PocketDef(Vec2 center, double radius)
    {
        Center = center;
        Radius = radius;
    }
}

/// <summary>Per-level rewind budget (port plan §4.2 "回溯预算"; level data driven).</summary>
public sealed class RewindBudget
{
    /// <summary>0 means unlimited.</summary>
    public int MaxSegments;

    /// <summary>0 means unlimited. Measured as travelled arc length along the history, in px.</summary>
    public double MaxDistance;

    public static RewindBudget Unlimited => new RewindBudget { MaxSegments = 0, MaxDistance = 0.0 };
}

public enum InputKind
{
    Shot = 0,
    RewindStart = 1,
    RewindEnd = 2,
}

/// <summary>
/// One recorded player input. The whole run is replayable from (level, input sequence),
/// which is what makes an offline solvability solver possible (requirement 10).
/// </summary>
public sealed class InputCommand
{
    public int Tick;
    public InputKind Kind;

    /// <summary>Shot: aim angle in degrees (screen space, +y down).</summary>
    public double A;

    /// <summary>Shot: drag power in px (clamped to PowerMax).</summary>
    public double B;

    public InputCommand Clone() => new InputCommand { Tick = Tick, Kind = Kind, A = A, B = B };
}

public sealed class SimStats
{
    public int Rewinds;
    public int RewindContacts;
    public int RailTransforms;
    public int BallBallContacts;
    public int RailContacts;
    public int PocketCaptures;
    public long SubStepIntervals;
    public int StepGuardHits;

    public SimStats Clone()
    {
        return (SimStats)MemberwiseClone();
    }

    public void CopyFrom(SimStats other)
    {
        Rewinds = other.Rewinds;
        RewindContacts = other.RewindContacts;
        RailTransforms = other.RailTransforms;
        BallBallContacts = other.BallBallContacts;
        RailContacts = other.RailContacts;
        PocketCaptures = other.PocketCaptures;
        SubStepIntervals = other.SubStepIntervals;
        StepGuardHits = other.StepGuardHits;
    }
}

/// <summary>
/// A ball. Plain serializable data: position, velocity, radius, mass, type, pocketed flag
/// (requirement 3). No engine types, no behaviour.
/// </summary>
public sealed class Ball
{
    public int Id;
    public BallType Type;
    public Vec2 Position;
    public Vec2 Velocity;
    public double Radius;
    public double Mass;
    public bool Pocketed;

    /// <summary>
    /// Per-ball physical character, resolved from <see cref="PhysicsConfig.Types"/> at build time
    /// (a level may override individual fields). Kept on the ball so the hot loop never has to
    /// re-index the table, and so a type change mid-run would take effect on the next rebuild.
    /// </summary>
    public BallTypeParams Params = new BallTypeParams();

    /// <summary>
    /// How many times this ball has already taken the rail speed bonus. Compared against
    /// <see cref="BallTypeParams.MaxWallBoosts"/>; the reflection itself is never capped, only the
    /// bonus. Exposed so the HUD can show remaining boosts and a level budget can use them.
    /// </summary>
    public int WallBoostsUsed;

    /// <summary>
    /// Remaining rail speed boosts, or null when unlimited. Null rather than a sentinel int so the
    /// HUD can render "unlimited" without guessing a magic number.
    /// </summary>
    public int? WallBoostsRemaining =>
        Params.MaxWallBoosts < 0 ? null : Math.Max(0, Params.MaxWallBoosts - WallBoostsUsed);

    /// <summary>Whether a rail hit right now would grant the speed bonus.</summary>
    public bool CanTakeWallBoost => Params.MaxWallBoosts < 0 || WallBoostsUsed < Params.MaxWallBoosts;

    /// <summary>Only the cue ball records in M1 (GDD §4.1: rewind applies to the cue alone).</summary>
    public bool RecordsHistory;

    public ArcHistory History;

    public Ball(int id, BallType type, Vec2 position, double radius, double mass, bool recordsHistory, double historyInterval)
    {
        Id = id;
        Type = type;
        Position = position;
        Velocity = Vec2.Zero;
        Radius = radius;
        Mass = mass;
        RecordsHistory = recordsHistory;
        History = new ArcHistory(historyInterval);
    }

    public bool IsCue => Type == BallType.Cue;

    public bool IsActive => !Pocketed;

    public double Speed => Velocity.Length;

    public Ball Clone()
    {
        var clone = new Ball(Id, Type, Position, Radius, Mass, RecordsHistory, History.Interval)
        {
            Velocity = Velocity,
            Pocketed = Pocketed,
            Params = Params,
            WallBoostsUsed = WallBoostsUsed,
        };
        clone.History.LoadFrom(History.Samples, History.DistanceSinceSample);
        return clone;
    }
}
