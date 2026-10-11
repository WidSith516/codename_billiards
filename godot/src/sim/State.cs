using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rewind.Sim;

/// <summary>Serializable snapshot of one ball.</summary>
public sealed class BallState
{
    public int Id;
    public BallType Type;
    public double X;
    public double Y;
    public double Vx;
    public double Vy;
    public double Radius;
    public double Mass;
    public bool Pocketed;
    public HistorySample[] History = Array.Empty<HistorySample>();
    public double HistoryResidual;
}

/// <summary>
/// Whole-run state: balls, history, rewind snapshot, counters AND the input sequence, so the
/// solver can be built on plain data (requirement 10) and any run can be replayed exactly.
/// </summary>
public sealed class WorldState
{
    public int Version = 1;
    public string LevelId = string.Empty;
    public double Time;
    public int Tick;

    /// <summary>Strikes taken. Replaces the old bool so a level can allow more than one; Version 1 states
    /// written before this existed leave it at 0, and a single-strike level reads it as 0 == not yet used.</summary>
    public int ShotsUsed;

    public bool Won;
    public bool Frozen;
    public bool Rewinding;
    public int RewindSegmentsUsed;
    public double RewindDistanceUsed;
    public SimStats Stats = new SimStats();
    public BallState[] Balls = Array.Empty<BallState>();
    public HistorySample[] RewindTrack;
    public int RewindCursor;
    public double RewindU;
    public InputCommand[] Inputs = Array.Empty<InputCommand>();

    public WorldState Clone()
    {
        var copy = (WorldState)MemberwiseClone();
        copy.Stats = Stats.Clone();
        copy.Balls = new BallState[Balls.Length];
        for (var i = 0; i < Balls.Length; i++)
        {
            var b = Balls[i];
            copy.Balls[i] = new BallState
            {
                Id = b.Id,
                Type = b.Type,
                X = b.X,
                Y = b.Y,
                Vx = b.Vx,
                Vy = b.Vy,
                Radius = b.Radius,
                Mass = b.Mass,
                Pocketed = b.Pocketed,
                History = (HistorySample[])b.History.Clone(),
                HistoryResidual = b.HistoryResidual,
            };
        }

        copy.RewindTrack = RewindTrack == null ? null : (HistorySample[])RewindTrack.Clone();
        copy.Inputs = new InputCommand[Inputs.Length];
        for (var i = 0; i < Inputs.Length; i++) copy.Inputs[i] = Inputs[i].Clone();
        return copy;
    }
}

/// <summary>JSON codec for <see cref="WorldState"/> plus the determinism hash.</summary>
public static class StateCodec
{
    public const int CurrentVersion = 1;

    public static WorldState Capture(World world)
    {
        var state = new WorldState
        {
            Version = CurrentVersion,
            LevelId = world.Level.Id,
            Time = world.Time,
            Tick = world.Tick,
            ShotsUsed = world.ShotsUsed,
            Won = world.Won,
            Frozen = world.Frozen,
            Rewinding = world.Rewinding,
            RewindSegmentsUsed = world.RewindSegmentsUsed,
            RewindDistanceUsed = world.RewindDistanceUsed,
            Stats = world.Stats.Clone(),
            Balls = new BallState[world.Balls.Length],
            RewindTrack = world.RewindTrack == null ? null : (HistorySample[])world.RewindTrack.Clone(),
            RewindCursor = world.RewindCursor,
            RewindU = world.RewindFraction,
            Inputs = new InputCommand[world.Inputs.Count],
        };

        for (var i = 0; i < world.Balls.Length; i++)
        {
            var b = world.Balls[i];
            state.Balls[i] = new BallState
            {
                Id = b.Id,
                Type = b.Type,
                X = b.Position.X,
                Y = b.Position.Y,
                Vx = b.Velocity.X,
                Vy = b.Velocity.Y,
                Radius = b.Radius,
                Mass = b.Mass,
                Pocketed = b.Pocketed,
                History = b.History.Snapshot(),
                HistoryResidual = b.History.DistanceSinceSample,
            };
        }

        for (var i = 0; i < world.Inputs.Count; i++) state.Inputs[i] = world.Inputs[i].Clone();
        return state;
    }

    /// <summary>Restore a world from a state produced by <see cref="Capture"/> for the same level.</summary>
    public static void Restore(World world, WorldState state)
    {
        if (state.Balls.Length != world.Balls.Length)
            throw new JsonException("state has " + state.Balls.Length + " balls but the level has " + world.Balls.Length);

        for (var i = 0; i < world.Balls.Length; i++)
        {
            var b = world.Balls[i];
            var s = state.Balls[i];
            b.Position = new Vec2(s.X, s.Y);
            b.Velocity = new Vec2(s.Vx, s.Vy);
            b.Radius = s.Radius;
            b.Mass = s.Mass;
            b.Pocketed = s.Pocketed;
            b.History.LoadFrom(s.History, s.HistoryResidual);
        }

        world.LoadStateScalars(state);
    }

    public static string Encode(World world, bool indented = false)
    {
        return Json.Write(ToJson(Capture(world)), indented);
    }

    public static WorldState Decode(string json, LevelData level)
    {
        var state = FromJson(Json.Parse(json));
        if (!string.IsNullOrEmpty(state.LevelId) && state.LevelId != level.Id)
            throw new JsonException("state levelId '" + state.LevelId + "' does not match level '" + level.Id + "'");
        return state;
    }

    public static WorldState DecodeInto(string json, World world)
    {
        var state = Decode(json, world.Level);
        Restore(world, state);
        return state;
    }

    public static JsonValue ToJson(WorldState s)
    {
        var root = JsonValue.Object();
        root.Set("version", JsonValue.Of((double)s.Version));
        root.Set("levelId", JsonValue.Of(s.LevelId));
        root.Set("time", JsonValue.Of(s.Time));
        root.Set("tick", JsonValue.Of((double)s.Tick));
        root.Set("shotsUsed", JsonValue.Of(s.ShotsUsed));
        root.Set("won", JsonValue.Of(s.Won));
        root.Set("frozen", JsonValue.Of(s.Frozen));
        root.Set("rewinding", JsonValue.Of(s.Rewinding));
        root.Set("rewindSegmentsUsed", JsonValue.Of((double)s.RewindSegmentsUsed));
        root.Set("rewindDistanceUsed", JsonValue.Of(s.RewindDistanceUsed));
        root.Set("rewindCursor", JsonValue.Of((double)s.RewindCursor));
        root.Set("rewindU", JsonValue.Of(s.RewindU));

        var stats = JsonValue.Object();
        stats.Set("rewinds", JsonValue.Of((double)s.Stats.Rewinds));
        stats.Set("rewindContacts", JsonValue.Of((double)s.Stats.RewindContacts));
        stats.Set("railTransforms", JsonValue.Of((double)s.Stats.RailTransforms));
        stats.Set("ballBallContacts", JsonValue.Of((double)s.Stats.BallBallContacts));
        stats.Set("railContacts", JsonValue.Of((double)s.Stats.RailContacts));
        stats.Set("pocketCaptures", JsonValue.Of((double)s.Stats.PocketCaptures));
        stats.Set("subStepIntervals", JsonValue.Of((double)s.Stats.SubStepIntervals));
        stats.Set("stepGuardHits", JsonValue.Of((double)s.Stats.StepGuardHits));
        root.Set("stats", stats);

        var balls = JsonValue.Array();
        foreach (var b in s.Balls)
        {
            var o = JsonValue.Object();
            o.Set("id", JsonValue.Of((double)b.Id));
            o.Set("type", JsonValue.Of(b.Type == BallType.Cue ? "cue" : "object"));
            o.Set("x", JsonValue.Of(b.X));
            o.Set("y", JsonValue.Of(b.Y));
            o.Set("vx", JsonValue.Of(b.Vx));
            o.Set("vy", JsonValue.Of(b.Vy));
            o.Set("radius", JsonValue.Of(b.Radius));
            o.Set("mass", JsonValue.Of(b.Mass));
            o.Set("pocketed", JsonValue.Of(b.Pocketed));
            o.Set("historyResidual", JsonValue.Of(b.HistoryResidual));
            o.Set("history", SamplesToJson(b.History));
            balls.Add(o);
        }

        root.Set("balls", balls);
        root.Set("rewindTrack", s.RewindTrack == null ? JsonValue.Null() : SamplesToJson(s.RewindTrack));

        var inputs = JsonValue.Array();
        foreach (var e in s.Inputs)
        {
            var o = JsonValue.Object();
            o.Set("tick", JsonValue.Of((double)e.Tick));
            o.Set("kind", JsonValue.Of(KindName(e.Kind)));
            o.Set("a", JsonValue.Of(e.A));
            o.Set("b", JsonValue.Of(e.B));
            inputs.Add(o);
        }

        root.Set("inputs", inputs);
        return root;
    }

    public static WorldState FromJson(JsonValue root)
    {
        if (root.Kind != JsonKind.Object) throw new JsonException("state root must be an object");
        var s = new WorldState
        {
            Version = root.Int("version", -1),
            LevelId = root.Text("levelId", string.Empty),
            Time = root.Num("time", 0.0),
            Tick = root.Int("tick", 0),
            ShotsUsed = (int)root.Num("shotsUsed", 0.0),
            Won = root.Flag("won", false),
            Frozen = root.Flag("frozen", false),
            Rewinding = root.Flag("rewinding", false),
            RewindSegmentsUsed = root.Int("rewindSegmentsUsed", 0),
            RewindDistanceUsed = root.Num("rewindDistanceUsed", 0.0),
            RewindCursor = root.Int("rewindCursor", 0),
            RewindU = root.Num("rewindU", 0.0),
        };

        if (s.Version != CurrentVersion)
            throw new JsonException("unsupported state version " + s.Version);

        var stats = root.Get("stats");
        if (stats != null)
        {
            s.Stats = new SimStats
            {
                Rewinds = stats.Int("rewinds", 0),
                RewindContacts = stats.Int("rewindContacts", 0),
                RailTransforms = stats.Int("railTransforms", 0),
                BallBallContacts = stats.Int("ballBallContacts", 0),
                RailContacts = stats.Int("railContacts", 0),
                PocketCaptures = stats.Int("pocketCaptures", 0),
                SubStepIntervals = (long)stats.Num("subStepIntervals", 0.0),
                StepGuardHits = stats.Int("stepGuardHits", 0),
            };
        }

        var balls = root.Arr("balls");
        s.Balls = new BallState[balls.Count];
        for (var i = 0; i < balls.Count; i++)
        {
            var o = balls.At(i);
            s.Balls[i] = new BallState
            {
                Id = o.Int("id", i),
                Type = o.Text("type", "object") == "cue" ? BallType.Cue : BallType.Object,
                X = o.Num("x", 0.0),
                Y = o.Num("y", 0.0),
                Vx = o.Num("vx", 0.0),
                Vy = o.Num("vy", 0.0),
                Radius = o.Num("radius", 12.2),
                Mass = o.Num("mass", 1.0),
                Pocketed = o.Flag("pocketed", false),
                History = SamplesFromJson(o.Arr("history")),
                HistoryResidual = o.Num("historyResidual", 0.0),
            };
        }

        var track = root.Get("rewindTrack");
        s.RewindTrack = track == null || track.Kind == JsonKind.Null ? null : SamplesFromJson(track);

        var inputs = root.Get("inputs");
        if (inputs != null && inputs.Kind == JsonKind.Array)
        {
            s.Inputs = new InputCommand[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
            {
                var o = inputs.At(i);
                s.Inputs[i] = new InputCommand
                {
                    Tick = o.Int("tick", 0),
                    Kind = KindFromName(o.Text("kind", "shot")),
                    A = o.Num("a", 0.0),
                    B = o.Num("b", 0.0),
                };
            }
        }

        return s;
    }

    private static JsonValue SamplesToJson(HistorySample[] samples)
    {
        var arr = JsonValue.Array();
        foreach (var sample in samples)
        {
            var o = JsonValue.Object();
            o.Set("x", JsonValue.Of(sample.Position.X));
            o.Set("y", JsonValue.Of(sample.Position.Y));
            o.Set("vx", JsonValue.Of(sample.Velocity.X));
            o.Set("vy", JsonValue.Of(sample.Velocity.Y));
            o.Set("t", JsonValue.Of(sample.Time));
            arr.Add(o);
        }

        return arr;
    }

    private static HistorySample[] SamplesFromJson(JsonValue arr)
    {
        if (arr == null || arr.Kind != JsonKind.Array) return Array.Empty<HistorySample>();
        var samples = new HistorySample[arr.Count];
        for (var i = 0; i < arr.Count; i++)
        {
            var o = arr.At(i);
            samples[i] = new HistorySample(
                new Vec2(o.Num("x", 0.0), o.Num("y", 0.0)),
                new Vec2(o.Num("vx", 0.0), o.Num("vy", 0.0)),
                o.Num("t", 0.0));
        }

        return samples;
    }

    private static string KindName(InputKind kind)
    {
        switch (kind)
        {
            case InputKind.Shot: return "shot";
            case InputKind.RewindStart: return "rewindStart";
            default: return "rewindEnd";
        }
    }

    private static InputKind KindFromName(string name)
    {
        switch (name)
        {
            case "shot": return InputKind.Shot;
            case "rewindStart": return InputKind.RewindStart;
            default: return InputKind.RewindEnd;
        }
    }
}

/// <summary>
/// Deterministic 64-bit FNV-1a hash over every field of the state, in a fixed order, using the
/// exact IEEE-754 bit patterns. Two runs that produce the same hash produced the same run.
/// </summary>
public static class StateHash
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Of(World world) => Of(StateCodec.Capture(world));

    public static ulong Of(WorldState s)
    {
        var h = Offset;
        MixString(ref h, s.LevelId);
        MixDouble(ref h, s.Time);
        MixInt(ref h, s.Tick);
        MixInt(ref h, s.ShotsUsed);
        MixInt(ref h, s.Won ? 1 : 0);
        MixInt(ref h, s.Frozen ? 1 : 0);
        MixInt(ref h, s.Rewinding ? 1 : 0);
        MixInt(ref h, s.RewindSegmentsUsed);
        MixDouble(ref h, s.RewindDistanceUsed);
        MixInt(ref h, s.Stats.Rewinds);
        MixInt(ref h, s.Stats.RewindContacts);
        MixInt(ref h, s.Stats.RailTransforms);
        MixInt(ref h, s.Stats.BallBallContacts);
        MixInt(ref h, s.Stats.RailContacts);
        MixInt(ref h, s.Stats.PocketCaptures);
        MixInt(ref h, s.Stats.StepGuardHits);
        MixInt(ref h, s.Balls.Length);
        foreach (var b in s.Balls)
        {
            MixInt(ref h, b.Id);
            MixInt(ref h, (int)b.Type);
            MixDouble(ref h, b.X);
            MixDouble(ref h, b.Y);
            MixDouble(ref h, b.Vx);
            MixDouble(ref h, b.Vy);
            MixDouble(ref h, b.Radius);
            MixDouble(ref h, b.Mass);
            MixInt(ref h, b.Pocketed ? 1 : 0);
            MixInt(ref h, b.History.Length);
            foreach (var sample in b.History)
            {
                MixDouble(ref h, sample.Position.X);
                MixDouble(ref h, sample.Position.Y);
                MixDouble(ref h, sample.Velocity.X);
                MixDouble(ref h, sample.Velocity.Y);
                MixDouble(ref h, sample.Time);
            }

            MixDouble(ref h, b.HistoryResidual);
        }

        MixInt(ref h, s.RewindTrack == null ? -1 : s.RewindTrack.Length);
        if (s.RewindTrack != null)
        {
            foreach (var sample in s.RewindTrack)
            {
                MixDouble(ref h, sample.Position.X);
                MixDouble(ref h, sample.Position.Y);
                MixDouble(ref h, sample.Velocity.X);
                MixDouble(ref h, sample.Velocity.Y);
            }
        }

        MixInt(ref h, s.RewindCursor);
        MixDouble(ref h, s.RewindU);
        MixInt(ref h, s.Inputs.Length);
        foreach (var e in s.Inputs)
        {
            MixInt(ref h, e.Tick);
            MixInt(ref h, (int)e.Kind);
            MixDouble(ref h, e.A);
            MixDouble(ref h, e.B);
        }

        return h;
    }

    public static string Hex(World world) => Of(world).ToString("x16", CultureInfo.InvariantCulture);

    public static string Hex(WorldState s) => Of(s).ToString("x16", CultureInfo.InvariantCulture);

    private static void Mix(ref ulong h, ulong v)
    {
        h ^= v;
        h *= Prime;
    }

    private static void MixInt(ref ulong h, int v)
    {
        Mix(ref h, (ulong)(uint)v);
        Mix(ref h, (ulong)(uint)(v >> 32));
    }

    private static void MixDouble(ref ulong h, double d)
    {
        // Normalize -0.0 to +0.0: same physics, same hash.
        if (d == 0.0) d = 0.0;
        Mix(ref h, (ulong)BitConverter.DoubleToInt64Bits(d));
    }

    private static void MixString(ref ulong h, string s)
    {
        if (s == null)
        {
            MixInt(ref h, -1);
            return;
        }

        MixInt(ref h, s.Length);
        foreach (var c in s) MixInt(ref h, c);
    }
}
