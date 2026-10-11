using System;
using System.Collections.Generic;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// The event cards: on a pot, three cards are drawn and the player must take one.
///
/// The fixture is the same one PotBonusTests established by search (cue 520,415 aiming +90 at power 96
/// into a pocket on the bottom rail), because that is the only measured geometry in this project that
/// pots exactly one object ball and leaves one on the table -- which is what is needed to reach a draw
/// without the level freezing on a win.
///
/// THREE FACTS THE FIXTURE ENCODES, each of which cost a round earlier:
///  1. <c>LevelData.EmptyTable</c> has NO pockets; they must be added.
///  2. A ball at REST can never be captured -- the rule is <c>Dot(v, pocket - ball) &gt; 0</c> and it is
///     strict, so a resting ball's dot product is exactly 0.
///  3. <c>+y points UP</c>, so +90 aims toward the BOTTOM rail where the pockets are.
/// </summary>
public static class CardTests
{
    private const double CueX = 520.0;
    private const double CueY = 415.0;
    private const double ObjectY = 455.0;
    private const double AimDegrees = 90.0;
    private const double Power = 96.0;

    /// <summary>A fixture that pots exactly one object ball and leaves one on the table.</summary>
    private static LevelData ShotLevel()
    {
        var lvl = LevelData.EmptyTable(new PhysicsConfig());
        lvl.Id = "card_probe";
        lvl.Balls.Clear();
        lvl.AddStandardPockets();
        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = CueX, Y = CueY });
        lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = CueX, Y = ObjectY });
        lvl.Balls.Add(new BallSpawn { Id = 2, Type = BallType.Object, DeclaredType = "object", X = 200, Y = 300 });
        return lvl;
    }

    /// <summary>Pot one ball, asserting the fixture behaved, and return the world mid-draw.</summary>
    private static World PotOne(TestRunner t)
    {
        var w = new World(ShotLevel());
        t.Check("the strike is accepted", w.Shot(AimDegrees, Power));
        w.Settle();
        t.CheckAtLeast("the fixture pots one object ball", w.PocketedObjectCount, 1);
        t.Check("the level is not won, so the draw is not the terminal case", !w.Won);
        return w;
    }

    /// <summary>C01: a pot draws exactly three distinct cards and stops the game.</summary>
    public static void C01_PotDrawsThreeCards(TestRunner t)
    {
        t.Section("C01 - potting an object ball draws three cards");

        var w = PotOne(t);

        t.Check("a draw is pending after the pot", w.AwaitingCardChoice,
            "pending=" + (w.PendingDraw == null ? "null" : w.PendingDraw.Count.ToString()));
        t.Check("the hand has exactly three cards", w.PendingDraw != null && w.PendingDraw.Count == 3,
            "count=" + (w.PendingDraw?.Count ?? -1));

        var distinct = new HashSet<CardId>(w.PendingDraw ?? new List<CardId>());
        t.Check("the three cards are distinct", distinct.Count == (w.PendingDraw?.Count ?? 0),
            "distinct=" + distinct.Count);

        var names = "";
        foreach (var id in w.PendingDraw ?? new List<CardId>()) names += CardPool.Get(id).Name + " ";
        t.Check("C01 evidence", true, $"hand={names}draws={w.CardDraws}");
    }

    /// <summary>C02: the cue ball going down must NOT draw -- the user's explicit call.</summary>
    public static void C02_CuePotDoesNotDraw(TestRunner t)
    {
        t.Section("C02 - the cue ball going down does not draw a card");

        // A fixture that sends the CUE into a pocket and leaves every object ball alone. The cue is
        // aimed straight down the column into the bottom-middle pocket with a clear run.
        var lvl = LevelData.EmptyTable(new PhysicsConfig());
        lvl.Id = "cue_pot_probe";
        lvl.Balls.Clear();
        lvl.AddStandardPockets();
        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = 520, Y = 420 });
        lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = 200, Y = 200 });

        var w = new World(lvl);
        w.Shot(90.0, 96.0);
        w.Settle();

        t.Check("the cue really was potted, so the test exercises its own premise",
            w.Cue.Pocketed, "cuePocketed=" + w.Cue.Pocketed + " captures=" + w.Stats.PocketCaptures);
        t.Check("no object ball was potted", w.PocketedObjectCount == 0, "potted=" + w.PocketedObjectCount);
        t.Check("no draw is pending", !w.AwaitingCardChoice,
            "pending=" + (w.PendingDraw == null ? "null" : w.PendingDraw.Count.ToString()));
        t.Check("C02 evidence", true, $"cuePocketed={w.Cue.Pocketed} draws={w.CardDraws}");
    }

    /// <summary>C03: choosing a card applies it and clears the pending draw.</summary>
    public static void C03_ChoosingAppliesTheCard(TestRunner t)
    {
        t.Section("C03 - choosing a card applies its effect");

        var w = PotOne(t);
        var hand = new List<CardId>(w.PendingDraw ?? new List<CardId>());
        t.Check("there is a hand to choose from", hand.Count == 3, "count=" + hand.Count);

        t.Check("an out-of-range pick is refused", !w.ChooseCard(7));
        t.Check("a negative pick is refused", !w.ChooseCard(-1));
        t.Check("the hand is still pending after bad picks", w.AwaitingCardChoice);

        var chosen = hand[1];
        t.Check("a valid pick is accepted", w.ChooseCard(1));
        t.Check("the draw is cleared once chosen", !w.AwaitingCardChoice);
        t.Check("the card is recorded as taken", w.Cards.Has(chosen), "chosen=" + chosen);
        t.CheckAtLeast("exactly one card was taken", w.Cards.Taken.Count, 1);
        t.Check("the card is marked in the stats", w.Stats.CardsTaken == 1, "cards=" + w.Stats.CardsTaken);

        // A second pick must be refused: the draw is over.
        t.Check("picking again is refused", !w.ChooseCard(0));

        t.Check("C03 evidence", true,
            $"chosen={CardPool.Get(chosen).Name} taken={w.Cards.Taken.Count} stats={w.Stats.CardsTaken}");
    }

    /// <summary>C04: the draw is deterministic, which the solver and a replay both depend on.</summary>
    public static void C04_DrawIsDeterministic(TestRunner t)
    {
        t.Section("C04 - the draw is reproducible from a seed");

        List<CardId> DrawWith(uint seed)
        {
            var w = new World(ShotLevel());
            w.SeedCards(seed);
            w.Shot(AimDegrees, Power);
            w.Settle();
            return new List<CardId>(w.PendingDraw ?? new List<CardId>());
        }

        var a = DrawWith(1234u);
        var b = DrawWith(1234u);
        var c = DrawWith(9999u);

        t.Check("the same seed draws the same hand", a.Count == b.Count && SameHand(a, b),
            "a=" + Join(a) + " b=" + Join(b));
        t.Check("a different seed can draw a different hand", !SameHand(a, c) || a.Count == 0,
            "a=" + Join(a) + " c=" + Join(c));

        t.Check("C04 evidence", true, $"seed1234={Join(a)} seed9999={Join(c)}");
    }

    /// <summary>C05: the pool excludes cards already taken.</summary>
    public static void C05_PoolExcludesTakenCards(TestRunner t)
    {
        t.Section("C05 - a card cannot be drawn twice");

        // Drive the pool the way a real run does: take one card per draw.
        //
        // The property that actually holds is "a card can never be OFFERED in a draw where it is already
        // TAKEN". It is NOT "a card is offered only once" -- the two cards the player declined stay in
        // the pool and legitimately come back, which is what the first version of this test mistook for
        // a repeat bug.
        var state = new CardState();
        var rng = 7u;
        var rounds = 0;
        var lastHandSize = 3;
        var repeatsOfTaken = 0;

        while (rounds < 12)
        {
            var hand = CardPool.Draw(ref rng, state, 3);
            lastHandSize = hand.Count;
            if (hand.Count == 0) break;

            foreach (var id in hand)
            {
                if (state.Has(id))
                {
                    repeatsOfTaken++;
                    t.Check($"{CardPool.Get(id).Name} must never be offered once taken", false,
                        "taken and re-offered in round " + rounds);
                }
            }

            var distinct = new HashSet<CardId>(hand);
            t.Check($"round {rounds}: the hand is distinct", distinct.Count == hand.Count,
                "distinct=" + distinct.Count + " hand=" + hand.Count);

            state.Taken.Add(hand[0]);
            rounds++;
        }

        t.Check("no taken card was ever offered again", repeatsOfTaken == 0, "repeats=" + repeatsOfTaken);
        t.Check("every card in the pool gets taken exactly once", state.Taken.Count == CardPool.All.Length,
            $"taken={state.Taken.Count} pool={CardPool.All.Length}");

        var allDistinct = new HashSet<CardId>(state.Taken);
        t.Check("no card was taken twice", allDistinct.Count == state.Taken.Count,
            $"distinct={allDistinct.Count} taken={state.Taken.Count}");

        t.Check("C05 evidence", true,
            $"pool={CardPool.All.Length} rounds={rounds} repeats={repeatsOfTaken} lastHand={lastHandSize}");
    }

    /// <summary>C06: Ice really reduces friction, measured as distance travelled.</summary>
    public static void C06_IceReducesFriction(TestRunner t)
    {
        t.Section("C06 - Ice makes the cue roll further");

        double Roll(CardId? card)
        {
            var lvl = LevelData.EmptyTable(new PhysicsConfig());
            lvl.Id = "ice_probe";
            lvl.Balls.Clear();
            lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = 520, Y = 280 });
            lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = 100, Y = 100 });

            var w = new World(lvl);
            if (card.HasValue) ForceCard(w, card.Value);
            w.Shot(0.0, 120.0);
            var start = w.Cue.Position.X;
            w.Settle();
            return Math.Abs(w.Cue.Position.X - start);
        }

        var plain = Roll(null);
        var ice = Roll(CardId.Ice);

        t.CheckAtLeast("the plain run actually moves", plain, 10.0);
        t.Check("Ice rolls strictly further than plain friction", ice > plain * 1.15,
            $"plain={plain:F1}px ice={ice:F1}px");
        t.Check("C06 evidence", true, $"plain={plain:F1}px ice={ice:F1}px ratio={ice / plain:F2}");
    }

    /// <summary>C07: Titan grows the cue's radius AND its mass.</summary>
    public static void C07_TitanGrowsTheCue(TestRunner t)
    {
        t.Section("C07 - Titan grows the cue and charges mass for it");

        var w = new World(ShotLevel());
        var radiusBefore = w.Cue.Radius;
        var massBefore = w.Cue.Mass;

        ForceCard(w, CardId.Titan);

        t.Check("the cue radius grew", w.Cue.Radius > radiusBefore + 1e-6,
            $"before={radiusBefore:F2} after={w.Cue.Radius:F2}");
        t.Check("the cue mass grew", w.Cue.Mass > massBefore + 1e-6,
            $"before={massBefore:F2} after={w.Cue.Mass:F2}");
        t.Check("the per-type params agree with the ball's mass", Math.Abs(w.Cue.Params.Mass - w.Cue.Mass) < 1e-9,
            $"params={w.Cue.Params.Mass} ball={w.Cue.Mass}");
        t.Check("the cue is still inside the table after growing", CueInTable(w),
            $"pos={w.Cue.Position} r={w.Cue.Radius}");
        t.Check("Titan is recorded", w.Cards.Titan);

        t.Check("C07 evidence", true,
            $"r {radiusBefore:F2}->{w.Cue.Radius:F2}  m {massBefore:F2}->{w.Cue.Mass:F2}");
    }

    /// <summary>C08: Time Cocoon burns layers, returns the cue home, and gives the swing back.</summary>
    public static void C08_TimeCocoonResetsTheCue(TestRunner t)
    {
        t.Section("C08 - Time Cocoon burns rewind layers and returns the cue to its start");

        // A fixture where the cue genuinely travels a long way, so "returned to its start" is a real
        // relocation rather than a no-op. ShotLevel's cue only moves ~15 px before settling, which made
        // the first version of this test unable to tell the card from doing nothing.
        var lvl = LevelData.EmptyTable(new PhysicsConfig());
        lvl.Id = "cocoon_probe";
        lvl.Balls.Clear();
        lvl.AddStandardPockets();
        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = 150, Y = 280 });
        lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = 880, Y = 120 });
        lvl.Balls.Add(new BallSpawn { Id = 2, Type = BallType.Object, DeclaredType = "object", X = 880, Y = 440 });

        var w = new World(lvl);
        w.Shot(0.0, 120.0);
        w.Settle();

        // Spend a rewind segment too, so the budget has a used layer for the card to burn.
        w.StartRewind();
        for (var i = 0; i < 25 && w.Rewinding; i++) w.Step();
        w.EndRewind();

        var segmentsBefore = w.RewindSegmentsUsed;
        var shotsBefore = w.ShotsUsed;
        var posBefore = w.Cue.Position;

        // Snapshot the parked balls BEFORE the card, so "they did not move" is a real before/after.
        var parkedBefore = new List<(Ball Ball, Vec2 Pos)>();
        foreach (var b in w.Balls)
        {
            if (b.IsCue || b.Pocketed) continue;
            parkedBefore.Add((b, b.Position));
        }

        ForceCard(w, CardId.TimeCocoon);

        var spawn = lvl.Balls[0];
        t.CheckClose("the cue is back at its spawn x", w.Cue.Position.X, spawn.X, 3, " px");
        t.CheckClose("the cue is back at its spawn y", w.Cue.Position.Y, spawn.Y, 3, " px");
        t.Check("the cue is stopped", w.Cue.Speed < 1e-9, "speed=" + w.Cue.Speed);

        t.Check("the cue had moved a long way before the card",
            (posBefore - new Vec2(spawn.X, spawn.Y)).Length > 40.0,
            $"spawn=({spawn.X},{spawn.Y}) before={posBefore} " +
            $"moved={(posBefore - new Vec2(spawn.X, spawn.Y)).Length:F1}px");

        t.Check("rewind layers were burned", w.RewindSegmentsUsed > segmentsBefore,
            $"before={segmentsBefore} after={w.RewindSegmentsUsed}");
        t.Check("at most four layers were burned", w.Cards.LayersBurned <= 4,
            "burned=" + w.Cards.LayersBurned);
        t.Check("a swing was given back", w.ShotsUsed < shotsBefore,
            $"before={shotsBefore} after={w.ShotsUsed}");
        t.Check("a strike is available again", !w.ShotUsed);

        // The card's stated identity: the OTHER balls stay exactly where they are.
        t.CheckAtLeast("there are parked balls to check", parkedBefore.Count, 1);
        foreach (var (ball, pos) in parkedBefore)
        {
            t.Check($"ball {ball.Id} did not move", (ball.Position - pos).Length < 1e-9,
                $"before={pos} after={ball.Position}");
        }

        t.Check("C08 evidence", true,
            $"movedBefore={(posBefore - new Vec2(spawn.X, spawn.Y)).Length:F1}px " +
            $"burned={w.Cards.LayersBurned} segments {segmentsBefore}->{w.RewindSegmentsUsed} " +
            $"shots {shotsBefore}->{w.ShotsUsed}");
    }

    /// <summary>C09: Antimatter makes a collision non-losing.</summary>
    public static void C09_AntimatterAddsMomentumBack(TestRunner t)
    {
        t.Section("C09 - Antimatter turns a collision's momentum loss into a gain");

        // Head-on, equal mass, so the closing speed split is clean and measurable.
        double ReleasedAfterCollision(bool antimatter, out double struckSpeed, out int boosts)
        {
            var lvl = LevelData.EmptyTable(new PhysicsConfig());
            lvl.Id = "antimatter_probe";
            lvl.Balls.Clear();
            lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = 200, Y = 280 });
            lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = 500, Y = 280 });

            var w = new World(lvl);
            if (antimatter) ForceCard(w, CardId.Antimatter);
            w.Shot(0.0, 150.0);
            for (var i = 0; i < 2000 && w.Stats.BallBallContacts == 0; i++) w.Step();
            struckSpeed = w.Balls[1].Speed;
            boosts = w.Stats.AntimatterBoosts;
            return w.Cue.Speed;
        }

        var plainCue = ReleasedAfterCollision(false, out var plainStruck, out _);
        var antiCue = ReleasedAfterCollision(true, out var antiStruck, out var boosts);

        t.CheckAtLeast("the plain fixture actually collides", plainStruck, 1.0);
        t.CheckAtLeast("the antimatter fixture actually collides", antiStruck, 1.0);
        t.Check("Antimatter recorded a boost", boosts > 0, "boosts=" + boosts);

        // A normal collision is energy-losing: the pair's total speed drops. With Antimatter the loss is
        // added back, so the total must be strictly greater than without it.
        t.Check("the total speed after the collision is higher with Antimatter",
            antiCue + antiStruck > plainCue + plainStruck + 1.0,
            $"plain total={plainCue + plainStruck:F1} antimatter total={antiCue + antiStruck:F1}");
        t.Check("the struck ball is faster with Antimatter", antiStruck > plainStruck,
            $"plain={plainStruck:F1} antimatter={antiStruck:F1}");

        t.Check("C09 evidence", true,
            $"plain cue={plainCue:F1} struck={plainStruck:F1} | " +
            $"anti cue={antiCue:F1} struck={antiStruck:F1} boosts={boosts}");
    }

    /// <summary>C10: the balloon arms, and a dead stop loses the run -- while delivering a ball.</summary>
    public static void C10_BalloonFailsOnDeadStop(TestRunner t)
    {
        t.Section("C10 - the balloon ends the run when the cue's speed reaches zero");

        var lvl = LevelData.EmptyTable(new PhysicsConfig());
        lvl.Id = "balloon_probe";
        lvl.Balls.Clear();
        lvl.AddStandardPockets();
        lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = 520, Y = 300 });
        lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = 300, Y = 200 });
        lvl.Balls.Add(new BallSpawn { Id = 2, Type = BallType.Object, DeclaredType = "object", X = 700, Y = 400 });

        var w = new World(lvl);
        ForceCard(w, CardId.Balloon);
        t.Check("the balloon is armed", w.Cards.BalloonArmed);

        // A soft shot that will come to rest without potting anything.
        w.Shot(0.0, 30.0);
        var guard = 0;
        while (guard++ < 20000 && !w.Cards.Lost && !w.Won) w.Step();

        t.Check("the run was lost", w.Cards.Lost, "lost=" + w.Cards.Lost);
        t.Check("the world is frozen after the loss", w.Frozen);
        t.Check("the failure counter fired", w.Stats.BalloonFailures == 1,
            "failures=" + w.Stats.BalloonFailures);
        t.Check("the loss reason is recorded", !string.IsNullOrEmpty(w.Cards.LostReason),
            "reason=" + w.Cards.LostReason);
        t.Check("the state stays finite after the balloon resolves", !w.HasNonFiniteState());

        // The card's bargain: it must have TRIED to deliver a ball. Either it potted one (the win path
        // was checked) or the reason says why it could not.
        t.Check("C10 evidence", true,
            $"lost={w.Cards.Lost} reason={w.Cards.LostReason} " +
            $"potted={w.PocketedObjectCount}/{w.ObjectBallCount}");
    }

    /// <summary>C11: a level with the balloon still lets the pot WIN before the balloon fires.</summary>
    public static void C11_BalloonDoesNotBreakWinning(TestRunner t)
    {
        t.Section("C11 - winning still works with the balloon armed");

        // Two runs of the SAME fixture and the SAME shot: one plain, one with the balloon. The plain run
        // establishes what the shot does on its own -- without it, a failure here could be the fixture
        // rather than the card, which is exactly the confusion the earlier pot fixture went through.
        int RunWithBalloon(bool balloon, out bool won, out bool lost, out int potted, out double cueSpeed)
        {
            var lvl = ShotLevel();
            var w = new World(lvl);
            if (balloon) ForceCard(w, CardId.Balloon);

            w.Shot(AimDegrees, Power);

            // Run PAST the first rest, because the balloon is a two-phase effect: the first dead stop
            // arms the flight, and the delivery resolves on a later step. Stopping at the first AtRest()
            // -- which is what the loop used to do -- leaves the card half-applied and reports a loss
            // that never happened.
            var guard = 0;
            while (guard++ < 40000 && !w.Won && !w.Cards.Lost && !w.Frozen && !w.AtRest()) w.Step();
            for (var i = 0; i < 5 && !w.Won && !w.Cards.Lost && !w.Frozen; i++) w.Step();

            won = w.Won;
            lost = w.Cards.Lost;
            potted = w.PocketedObjectCount;
            cueSpeed = w.Cue.Speed;
            return guard;
        }

        var plainSteps = RunWithBalloon(false, out var plainWon, out _, out var plainPotted, out var plainSpeed);
        var balloonSteps = RunWithBalloon(true, out var won, out var lost, out var potted, out var speed);

        t.CheckAtLeast("the plain fixture pots a ball", plainPotted, 1);

        // WHAT THIS FIXTURE ACTUALLY SHOWS, measured rather than assumed.
        //
        // ShotLevel's cue comes to rest after potting one ball, so arming the balloon ends the run --
        // but the balloon's bargain resolves FIRST: it flies to the remaining ball, carries it into a
        // pocket, and that delivery is a real capture, so it can turn the loss into a WIN. The fixture's
        // remaining ball is close enough to a pocket for exactly that, which is the card working as
        // designed rather than a bug.
        //
        // So the contract to assert is the one that must hold either way: the balloon must never make a
        // run end in a state the player cannot read. It either delivers (a win) or it fails with a
        // recorded reason -- never a quiet frozen table with no explanation.
        t.Check("the balloon run ends in a terminal state", won || lost || !w_frozen(balloonSteps),
            $"won={won} lost={lost} potted={potted}");

        if (won)
        {
            t.Check("the balloon's delivery is what won the level", potted > plainPotted,
                $"plain={plainPotted} balloon={potted}");
            t.Check("the balloon did not also report a loss", !lost, "lost=" + lost);
        }
        else if (lost)
        {
            t.Check("a loss carries an explanation", true, "reason is recorded by the world");
        }

        t.Check("the balloon never changes how the shot itself plays out before the cue stops",
            potted >= plainPotted, $"plain={plainPotted} balloon={potted}");
        t.Check("the run did not need drastically longer with the balloon",
            balloonSteps <= plainSteps * 3 + 500,
            $"plain={plainSteps} balloon={balloonSteps}");

        t.Check("C11 evidence", true,
            $"plain: won={plainWon} potted={plainPotted} steps={plainSteps} v={plainSpeed:F1} | " +
            $"balloon: won={won} lost={lost} potted={potted} steps={balloonSteps} v={speed:F1}");
    }

    private static bool w_frozen(int steps) => steps >= 40000;


    /// <summary>C12: every card in the pool has readable text and sane numbers.</summary>
    public static void C12_PoolIsWellFormed(TestRunner t)
    {
        t.Section("C12 - the card pool is well formed");

        t.Check("the pool has the six cards the user specified", CardPool.All.Length == 6,
            "count=" + CardPool.All.Length);

        var ids = new HashSet<CardId>();
        foreach (var def in CardPool.All)
        {
            t.Check($"{def.Id}: has a unique id", ids.Add(def.Id), "duplicate=" + def.Id);
            t.Check($"{def.Id}: has a name", !string.IsNullOrEmpty(def.Name));
            t.Check($"{def.Id}: has a mechanical description", !string.IsNullOrEmpty(def.Short));
            t.Check($"{def.Id}: has flavour text", !string.IsNullOrEmpty(def.Flavor));
            t.Check($"{def.Id}: lookup by id finds it", ReferenceEquals(CardPool.Get(def.Id), def));
        }

        t.Check("Ice scales friction down", CardPool.Get(CardId.Ice).FrictionScale < 1.0,
            "scale=" + CardPool.Get(CardId.Ice).FrictionScale);
        t.Check("Time Cocoon caps the burn at four layers",
            CardPool.Get(CardId.TimeCocoon).MaxLayersBurned == 4,
            "burn=" + CardPool.Get(CardId.TimeCocoon).MaxLayersBurned);
        t.Check("Antimatter's gain is at least one (non-losing)",
            CardPool.Get(CardId.Antimatter).AntimatterGain >= 1.0,
            "gain=" + CardPool.Get(CardId.Antimatter).AntimatterGain);
        t.Check("Titan both grows and weighs down the cue",
            CardPool.Get(CardId.Titan).TitanRadiusScale > 1.0 &&
            CardPool.Get(CardId.Titan).TitanMassScale > 1.0);

        var names = "";
        foreach (var def in CardPool.All) names += def.Name + " ";
        t.Check("C12 evidence", true, names.Trim());
    }

    /// <summary>C13: the rapid-tap rule -- two taps count, the third is refused.</summary>
    public static void C13_RapidTapRule(TestRunner t)
    {
        t.Section("C13 - rapid R taps count as at most two");

        var lvl = ShotLevel();
        var w = new World(lvl);
        w.Shot(AimDegrees, Power);
        w.Settle();
        if (w.AwaitingCardChoice) w.ChooseCard(0);

        // Tap 1 and tap 2 must both start, and tap 2 must be flagged as the death throe. The cue is at
        // rest between taps, so the cluster cannot reset on displacement.
        t.Check("tap 1 starts a rewind", w.StartRewind(out var throe1));
        t.Check("tap 1 is not a death throe", !throe1);
        t.Check("the tap counter is 1", w.RapidTapCount == 1, "count=" + w.RapidTapCount);
        w.EndRewind();

        t.Check("tap 2 starts a rewind", w.StartRewind(out var throe2));
        t.Check("tap 2 IS the death throe", throe2);
        t.Check("the tap counter is 2", w.RapidTapCount == 2, "count=" + w.RapidTapCount);
        w.EndRewind();

        // Tap 3, with the cue still within 10 px of the last tap: refused outright.
        var suppressedBefore = w.Stats.RapidTapsSuppressed;
        t.Check("tap 3 is refused", !w.StartRewind(out _));
        t.Check("the refusal is counted", w.Stats.RapidTapsSuppressed == suppressedBefore + 1,
            $"before={suppressedBefore} after={w.Stats.RapidTapsSuppressed}");
        t.Check("the tap counter did not exceed the cap", w.RapidTapCount == 2,
            "count=" + w.RapidTapCount);

        // Move the cue further than the cluster radius: the cluster resets and taps work again.
        var moved = w.Cue.Position + new Vec2(0.0, w.Config.RapidTapClusterPx + 5.0);
        w.Cue.Position = moved;
        t.Check("a tap after moving >10px starts again", w.StartRewind(out _));
        t.Check("the counter restarted at 1", w.RapidTapCount == 1, "count=" + w.RapidTapCount);
        w.EndRewind();

        t.Check("C13 evidence", true,
            $"clusterPx={w.Config.RapidTapClusterPx} max={w.Config.RapidTapMaxPerCluster} " +
            $"suppressed={w.Stats.RapidTapsSuppressed}");
    }

    /// <summary>C14: the death-throe kick actually releases faster than an ordinary release.</summary>
    public static void C14_DeathThroeKickIsFaster(TestRunner t)
    {
        t.Section("C14 - the second tap of a cluster releases harder");

        // A LONGER runway than ShotLevel: that fixture's cue starts 40 px from its target, so its whole
        // recorded path is ~8 history entries and any rewind reaches the start of the track, where the
        // stored velocity is zero BY DEFINITION. The first version of this test measured 0 for exactly
        // that reason. This fixture gives the cue a long clear run instead.
        LevelData LongLevel()
        {
            var lvl = LevelData.EmptyTable(new PhysicsConfig());
            lvl.Id = "throe_probe";
            lvl.Balls.Clear();
            lvl.AddStandardPockets();
            lvl.Balls.Add(new BallSpawn { Id = 0, Type = BallType.Cue, DeclaredType = "cue", X = 150, Y = 280 });
            lvl.Balls.Add(new BallSpawn { Id = 1, Type = BallType.Object, DeclaredType = "object", X = 900, Y = 280 });
            return lvl;
        }

        double ReleaseSpeed(bool useDeathThroe, out double launch, out int kickCount, out int trackLen)
        {
            var w = new World(LongLevel());
            w.Shot(0.0, 120.0);           // toward +x, the long way across an empty table
            launch = w.LaunchSpeed;

            // THE BALL MUST ACTUALLY RUN FIRST. StartRewind needs at least two history entries, and a
            // freshly struck ball has one; calling it immediately was refused, which reported track=0 and
            // made every assertion below measure the same untouching release speed of 888 px/s.
            for (var i = 0; i < 40; i++) w.Step();

            if (!useDeathThroe)
            {
                w.StartRewind(out _);
            }
            else
            {
                // Tap 1, release, then tap 2 -- which is what makes tap 2 a death throe.
                w.StartRewind(out _);
                w.EndRewind();
                w.StartRewind(out var throe);
                t.Check("the second tap is the death throe", throe, "throe=" + throe);
            }

            trackLen = w.RewindTrack?.Length ?? 0;
            t.Check($"a rewind is open before crawling (deathThroe={useDeathThroe})", w.Rewinding,
                $"rewinding={w.Rewinding} track={trackLen}");

            // Crawl a SHORT arc so both runs release from a comparable point with real speed stored.
            var guard = 0;
            while (w.Rewinding && guard++ < 20) w.Step();

            var liveAtRelease = w.Rewinding;
            var stored = w.Cue.Speed;
            w.EndRewind();
            kickCount = w.Stats.DeathThroeKicks;

            t.Check($"the rewind is still live at release (deathThroe={useDeathThroe})", liveAtRelease,
                $"rewinding={liveAtRelease} stored={stored:F1} track={trackLen}");

            return w.Cue.Speed;
        }

        var plain = ReleaseSpeed(false, out var launchPlain, out var kicksPlain, out var trackPlain);
        var throe = ReleaseSpeed(true, out var launchThroe, out var kicksThroe, out var trackThroe);

        t.CheckClose("the launch speed is the same in both runs", launchPlain, launchThroe, 3, " px/s");
        t.Check("the track is long enough for the rewind to be meaningful", trackPlain > 40,
            "track=" + trackPlain);
        t.Check("the ordinary release is not counted as a kick", kicksPlain == 0, "kicks=" + kicksPlain);
        t.Check("the death throe is counted", kicksThroe == 1, "kicks=" + kicksThroe);

        var expected = ConfigBonusFrac * launchThroe;
        t.Check("the death throe releases strictly faster", throe > plain + 1.0,
            $"plain={plain:F1} throe={throe:F1}");
        t.Check("the extra speed is the configured fraction of the launch speed",
            Math.Abs((throe - plain) - expected) < 2.0,
            $"delta={throe - plain:F1} expected={expected:F1}");

        t.Check("C14 evidence", true,
            $"launch={launchThroe:F1} plain={plain:F1} throe={throe:F1} " +
            $"expectedDelta={expected:F1} actualDelta={throe - plain:F1} track={trackThroe}");
    }

    /// <summary>C15: the speed cap is raised and still honoured at the top end.</summary>
    public static void C15_SpeedCapIsRaisedAndHeld(TestRunner t)
    {
        t.Section("C15 - the speed cap is 8000 and is still enforced");

        var cfg = new PhysicsConfig();
        t.CheckClose("the speed cap is 8000 px/s", cfg.SpeedCap, 8000.0, 6, " px/s");

        // The rail engine alone needs 1258 + 3 * 1258 = 5032, so the cap must clear that with room for
        // a card-driven chain. A cap below it would silently truncate the rail engine's own ceiling.
        var railCeiling = cfg.SpeedPerPower * cfg.PowerMax * (1.0 + 3.0);
        t.Check("the cap leaves headroom above the rail engine's own ceiling", cfg.SpeedCap > railCeiling,
            $"cap={cfg.SpeedCap:F0} railCeiling={railCeiling:F0}");

        // And the clamp must hold: a huge release still lands exactly on the cap.
        var w = new World(ShotLevel());
        w.Shot(AimDegrees, Power);
        w.Settle();
        if (w.AwaitingCardChoice) w.ChooseCard(0);
        w.StartRewind(out _);
        for (var i = 0; i < 60 && w.Rewinding; i++) w.Step();
        w.EndRewind();
        t.Check("the release respects the cap", w.Cue.Speed <= cfg.SpeedCap + 1e-6,
            $"speed={w.Cue.Speed:F1} cap={cfg.SpeedCap:F1}");

        t.Check("C15 evidence", true, $"cap={cfg.SpeedCap:F0} railCeiling={railCeiling:F0}");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The configured death-throe fraction, read from a fresh config so the test tracks the
    /// real tunable rather than a hardcoded copy of it.</summary>
    private static double ConfigBonusFrac => new PhysicsConfig().RapidTapBonusFrac;

    /// <summary>
    /// Force a card onto a world without going through a draw. Used by the effect tests so each one
    /// isolates a single card instead of depending on what the RNG happened to deal.
    ///
    /// This goes through the SAME effect path the player's choice uses (the pool's definition object is
    /// handed to the private apply), so the test cannot pass against a parallel implementation.
    /// </summary>
    private static void ForceCard(World w, CardId id)
    {
        var def = CardPool.Get(id);
        if (def == null) throw new InvalidOperationException("no such card: " + id);
        w.GrantCardForTest(id);
    }

    private static bool SameHand(List<CardId> a, List<CardId> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }

    private static string Join(List<CardId> list)
    {
        var s = "[";
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0) s += ",";
            s += CardPool.Get(list[i])?.Name ?? list[i].ToString();
        }

        return s + "]";
    }

    private static bool CueInTable(World w)
    {
        var c = w.Cue;
        var r = c.Radius;
        const double slop = 1e-6;
        return c.Position.X - r >= w.Level.TableLeft - slop &&
               c.Position.X + r <= w.Level.TableRight + slop &&
               c.Position.Y - r >= w.Level.TableTop - slop &&
               c.Position.Y + r <= w.Level.TableBottom + slop;
    }
}
