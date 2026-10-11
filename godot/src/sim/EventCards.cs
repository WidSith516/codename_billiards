using System;
using System.Collections.Generic;

namespace Rewind.Sim;

/// <summary>
/// The first-pass event card pool.
///
/// HOW IT ARRIVES: when an OBJECT ball is pocketed the game draws three distinct cards from the
/// pool, freezes the simulation and forces the player to take one. The cue ball going down does
/// NOT draw -- the user's call, and it keeps the cue's own pocket behaviour free for the Tibo card
/// to own, which is the only card that cares about it.
///
/// WHAT A CARD IS: a one-shot (or persistent, per card) modification to the run. Every card is
/// expressed as data plus a small enum, NOT as a subclass with virtual dispatch, because the whole
/// file must stay deterministic and serializable: a card's identity has to survive a
/// capture/restore round trip in the solver. An enum in the world state does that; a polymorphic
/// object would not.
///
/// DESIGN CONSTRAINT, learned earlier in this project: packaging and capability must stay
/// separable. The space-body art (Comet / Meteor / Pulsar / Nebula) is pure packaging and is
/// deliberately NOT what these cards use. A card is a real capability with real physics, and the
/// user can see from the HUD which ones are live.
/// </summary>
public enum CardId
{
    /// <summary>Not a card: the "no card" value, so a default-constructed state is valid.</summary>
    None = 0,

    /// <summary>冰球 Ice: the table's rolling friction drops. The cue keeps its speed for longer.</summary>
    Ice = 1,

    /// <summary>时之茧 Time Cocoon: burn up to 4 rewind layers, return the cue to its start point and
    /// grant a fresh swing; the other balls stay exactly where they are.</summary>
    TimeCocoon = 2,

    /// <summary>反物质 Antimatter: ball-ball collisions stop losing momentum and instead STACK the
    /// lost momentum back in the opposite direction.</summary>
    Antimatter = 3,

    /// <summary>泰坦 Titan: the cue grows in radius but gains mass.</summary>
    Titan = 4,

    /// <summary>tibo 额度卡: when the cue ball is pocketed, immediately refill the remaining rewind
    /// segments and distance.</summary>
    Tibo = 5,

    /// <summary>气球 Balloon: if the cue's speed reaches zero the level is lost -- but the cue flies
    /// up and carries the first ball it touches into a pocket.</summary>
    Balloon = 6,
}

/// <summary>
/// A card's static description: identity, text for the draw UI, and the numbers its effect uses.
/// Instances are the pristine pool entries; a card that has been ACQUIRED is tracked separately in
/// <see cref="CardState"/> so that the pool stays immutable and the draw stays reproducible.
/// </summary>
public sealed class CardDef
{
    public CardId Id;
    public string Name;
    public string Short;
    public string Flavor;

    /// <summary>Ice: multiplier applied to the rolling deceleration. 0.35 = friction is 65% lower.</summary>
    public double FrictionScale = 1.0;

    /// <summary>TimeCocoon: most rewind layers the card may burn.</summary>
    public int MaxLayersBurned = 4;

    /// <summary>Antimatter: fraction of the momentum a collision would have LOST that gets added
    /// back along the reverse of the collision normal. 1.0 = fully non-losing, and values above 1.0
    /// stack extra momentum, which is what "向反方向叠加碰撞的动量损耗" asks for.</summary>
    public double AntimatterGain = 1.15;

    /// <summary>Titan: cue radius multiplier and the mass it pays for it.</summary>
    public double TitanRadiusScale = 1.55;
    public double TitanMassScale = 2.2;

    /// <summary>Balloon: how fast the carried ball is thrown toward the pocket, px/s.</summary>
    public double BalloonCarrySpeed = 900.0;
}

/// <summary>
/// Which cards this run has taken, and the per-card live state that is not a PhysicsConfig value.
///
/// Kept separate from <see cref="World"/>'s scalar fields only for readability; the whole thing is
/// captured and restored as part of the world state so a solver rollback cannot lose a card.
/// </summary>
public sealed class CardState
{
    /// <summary>Ids taken this run, in acquisition order (the HUD lists them).</summary>
    public readonly List<CardId> Taken = new List<CardId>();

    /// <summary>
    /// Ice, expressed as the multiplier currently applied to friction. Starts at 1.0 and is set by
    /// the card. Stored rather than recomputed so stacked copies compose predictably.
    /// </summary>
    public double FrictionScale = 1.0;

    /// <summary>True while Antimatter is active.</summary>
    public bool Antimatter;

    /// <summary>Antimatter's gain.</summary>
    public double AntimatterGain;

    /// <summary>True while the cue has been enlarged by Titan (both radius and mass).</summary>
    public bool Titan;

    /// <summary>
    /// True while the balloon is armed: the next time the cue's speed reaches ZERO the level is lost.
    /// Cleared once it fires, and also cleared if the balloon delivers its ball.
    /// </summary>
    public bool BalloonArmed;

    /// <summary>True once the balloon has taken flight (the cue reached zero and is now carrying).</summary>
    public bool BalloonFlying;

    /// <summary>Id of the ball the flying balloon is carrying, or -1.</summary>
    public int BalloonTarget = -1;

    /// <summary>How many rewind layers Time Cocoon burned when it fired (for the HUD).</summary>
    public int LayersBurned;

    /// <summary>True once the level is lost (the balloon fired). Distinct from Won/Frozen.</summary>
    public bool Lost;

    /// <summary>Why the run was lost, for the result panel.</summary>
    public string LostReason;

    /// <summary>Cards this run has taken, as a display string.</summary>
    public IEnumerable<CardId> All => Taken;

    public bool Has(CardId id) => Taken.Contains(id);

    public CardState Clone()
    {
        var c = new CardState
        {
            FrictionScale = FrictionScale,
            Antimatter = Antimatter,
            AntimatterGain = AntimatterGain,
            Titan = Titan,
            BalloonArmed = BalloonArmed,
            BalloonFlying = BalloonFlying,
            BalloonTarget = BalloonTarget,
            LayersBurned = LayersBurned,
            Lost = Lost,
            LostReason = LostReason,
        };
        c.Taken.AddRange(Taken);
        return c;
    }

    public void Reset(PhysicsConfig config)
    {
        Taken.Clear();
        FrictionScale = 1.0;
        Antimatter = false;
        AntimatterGain = 0.0;
        Titan = false;
        BalloonArmed = false;
        BalloonFlying = false;
        BalloonTarget = -1;
        LayersBurned = 0;
        Lost = false;
        LostReason = null;
        _ = config;
    }
}

/// <summary>
/// The card pool: the six cards the user specified, plus the draw itself.
///
/// The draw is deterministic given a seed, because the solver and the replay both need to be able
/// to reproduce a run. <see cref="World"/> owns the RNG state so that capture/restore is exact.
/// </summary>
public static class CardPool
{
    /// <summary>The six initial cards, in the order the user described them.</summary>
    public static readonly CardDef[] All =
    {
        new CardDef
        {
            Id = CardId.Ice,
            Name = "冰球",
            Short = "桌面摩擦减少",
            Flavor = "台面结了一层薄冰，白球滑得更远。",
            FrictionScale = 0.35,
        },
        new CardDef
        {
            Id = CardId.TimeCocoon,
            Name = "时之茧",
            Short = "烧掉最多 4 层倒带 · 白球回起点 · 重新挥杆",
            Flavor = "把自己裹进茧里，时间替你退回起点。其他球保持原位。",
            MaxLayersBurned = 4,
        },
        new CardDef
        {
            Id = CardId.Antimatter,
            Name = "反物质",
            Short = "碰撞不再减速，并把动量损耗反向叠回",
            Flavor = "反物质让每一次对撞都变成加法。",
            AntimatterGain = 1.15,
        },
        new CardDef
        {
            Id = CardId.Titan,
            Name = "泰坦",
            Short = "白球变大，但更重",
            Flavor = "白球膨胀成一颗小行星，推得动一切，也难被推动。",
            TitanRadiusScale = 1.55,
            TitanMassScale = 2.2,
        },
        new CardDef
        {
            Id = CardId.Tibo,
            Name = "tibo 额度卡",
            Short = "白球落袋时，重置剩余倒带次数与里程",
            Flavor = "掉下去也算数 —— 代价被后台报销了。",
        },
        new CardDef
        {
            Id = CardId.Balloon,
            Name = "气球",
            Short = "速度归零即失败 · 但会飞起带走第一颗碰到的球",
            Flavor = "氮气充满了白球。它只能飞一次，落点由它自己决定。",
            BalloonCarrySpeed = 900.0,
        },
    };

    public static CardDef Get(CardId id)
    {
        foreach (var c in All)
        {
            if (c.Id == id) return c;
        }

        return null;
    }

    /// <summary>
    /// Draw <paramref name="count"/> DISTINCT cards from the pool, excluding anything the run has
    /// already taken, using <paramref name="rng"/> as the source of randomness.
    ///
    /// Excludes taken cards so a run cannot be handed the same card twice (which would make half the
    /// draws a no-op and waste the player's choice). When the remaining pool is smaller than the
    /// requested count the draw returns what is left rather than repeating.
    /// </summary>
    public static List<CardId> Draw(ref uint rng, CardState state, int count)
    {
        var candidates = new List<CardId>();
        foreach (var c in All)
        {
            if (state != null && state.Has(c.Id)) continue;
            candidates.Add(c.Id);
        }

        var result = new List<CardId>();
        for (var i = 0; i < count && candidates.Count > 0; i++)
        {
            var pick = (int)(Next(ref rng) % (uint)candidates.Count);
            result.Add(candidates[pick]);
            candidates.RemoveAt(pick);
        }

        return result;
    }

    /// <summary>
    /// xorshift32. Deterministic, tiny, and serializable as a single uint, which is what lets a
    /// solver snapshot and roll back a run that has a card draw in it.
    /// </summary>
    public static uint Next(ref uint state)
    {
        if (state == 0u) state = 0x9E3779B9u;
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
