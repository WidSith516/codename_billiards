using System;
using System.Collections.Generic;

namespace Rewind.Sim;

/// <summary>
/// A white hole: a zone on the table that returns energy to any ball passing through it.
///
/// Design purpose: the cue ball is the only ball that can be rewound, so once it loses its speed
/// the level is over. A single strike plus rail bounces is a fragile energy supply; a white hole is
/// a PLACE the player can route the cue through to get energy back, which turns "keep the cue
/// alive" into a routing decision instead of luck.
///
/// Semantics: a ball entering the zone gets its speed set to at least <see cref="ExitSpeed"/>, or
/// multiplied by <see cref="Gain"/> if that is larger. One activation per entry, so a ball cannot
/// farm energy by sitting still inside it.
/// </summary>
public sealed class WhiteHoleDef
{
    public Vec2 Center;
    public double Radius = 30.0;

    /// <summary>Speed multiplier applied on entry. 1.0 means only <see cref="ExitSpeed"/> applies.</summary>
    public double Gain = 1.35;

    /// <summary>Minimum speed a ball leaves with, px/s. The floor is what makes it a reliable
    /// energy supply rather than a multiplier on nothing.</summary>
    public double ExitSpeed = 500.0;

    /// <summary>Hard ceiling so a chain of white holes cannot break the integrator.</summary>
    public double MaxSpeed = 1600.0;

    /// <summary>
    /// Seconds before the SAME ball can be energised by this hole again. Without it a strong boost
    /// throws the ball out of the zone and friction brings it straight back, and the hole re-fires;
    /// measured at four activations in a row, driving a ball that had started at rest to the speed
    /// ceiling. The cooldown makes "once per entry" mean once per deliberate entry.
    /// </summary>
    public double Cooldown = 0.5;

    /// <summary>When false only the cue ball is affected, which keeps object balls predictable.</summary>
    public bool AffectsObjectBalls = true;

    public WhiteHoleDef Clone() => (WhiteHoleDef)MemberwiseClone();
}

/// <summary>
/// A ball as described by level data.
/// </summary>
public sealed class BallSpawn
{
    public int Id;
    public BallType Type = BallType.Object;
    public double X;
    public double Y;
    public double Vx;
    public double Vy;
    public double Radius = -1.0; // <0 -> use config.BallRadius
    /// <summary>Relative mass. A NON-POSITIVE value is the "not specified" sentinel so the per-type
    /// table (<see cref="PhysicsConfig.Types"/>) supplies the default. Do not default this to 1.0:
    /// that silently overrode every type's own mass, which made a "heavy" ball weigh exactly as
    /// much as an ordinary one.</summary>
    public double Mass = -1.0;


    /// <summary>
    /// The type string exactly as written in the level file. M1 reserves the special-ball
    /// vocabulary (<c>bomb</c>, <c>anchor</c>, <c>heavy</c>) as DATA ONLY: they currently load
    /// and behave as ordinary object balls, so level files and the solver can already carry the
    /// field without the kernel pretending to implement behaviour it does not have.
    /// </summary>
    public string DeclaredType = "object";

    public bool IsReservedSpecialType => DeclaredType != "object" && DeclaredType != "cue";
}

/// <summary>
/// Everything that defines a level: table, pockets, ball spawns, rewind budget and optional
/// physics overrides. Port plan §7.1 requires this to be data driven (JSON) so that levels can
/// be generated and verified in bulk by the M4 solvability solver instead of being hardcoded.
/// </summary>
public sealed class LevelData
{
    public string Id = "unnamed";
    public string Name = "unnamed";

    public double TableLeft = 65.0;
    public double TableRight = 975.0;
    public double TableTop = 63.0;
    public double TableBottom = 497.0;

    public double BallRadius = 12.2;

    public readonly List<PocketDef> Pockets = new List<PocketDef>();
    public readonly List<BallSpawn> Balls = new List<BallSpawn>();

    /// <summary>White holes: energy-return zones. Empty in most levels.</summary>
    public readonly List<WhiteHoleDef> WhiteHoles = new List<WhiteHoleDef>();
    public RewindBudget Budget = RewindBudget.Unlimited;

    /// <summary>Level-specific physics overrides (already merged onto the defaults).</summary>
    public PhysicsConfig Config = new PhysicsConfig();

    public double TableWidth => TableRight - TableLeft;

    public double TableHeight => TableBottom - TableTop;

    // ------------------------------------------------------------------ builders

    public static LevelData EmptyTable(PhysicsConfig baseConfig = null)
    {
        var cfg = baseConfig == null ? new PhysicsConfig() : baseConfig.Clone();
        return new LevelData
        {
            Id = "inline",
            Name = "inline",
            TableLeft = cfg.TableLeft,
            TableRight = cfg.TableRight,
            TableTop = cfg.TableTop,
            TableBottom = cfg.TableBottom,
            BallRadius = cfg.BallRadius,
            Config = cfg,
        };
    }

    public LevelData AddPocket(double x, double y, double radius = -1.0)
    {
        Pockets.Add(new PocketDef(new Vec2(x, y), radius < 0.0 ? Config.PocketRadius : radius));
        return this;
    }

    /// <summary>Six pockets of the standard table: four corners plus two side pockets.</summary>
    public LevelData AddStandardPockets()
    {
        var midX = (TableLeft + TableRight) * 0.5;
        AddPocket(TableLeft, TableTop);
        AddPocket(midX, TableTop);
        AddPocket(TableRight, TableTop);
        AddPocket(TableLeft, TableBottom);
        AddPocket(midX, TableBottom);
        AddPocket(TableRight, TableBottom);
        return this;
    }

    public LevelData AddCue(double x, double y)
    {
        return AddBall(0, BallType.Cue, x, y, 0.0, 0.0);
    }

    public LevelData AddObject(double x, double y, double vx = 0.0, double vy = 0.0)
    {
        return AddBall(NextObjectId(), BallType.Object, x, y, vx, vy);
    }

    public LevelData AddBall(int id, BallType type, double x, double y, double vx = 0.0, double vy = 0.0)
    {
        Balls.Add(new BallSpawn { Id = id, Type = type, X = x, Y = y, Vx = vx, Vy = vy });
        return this;
    }

    private int NextObjectId()
    {
        var id = 1;
        foreach (var b in Balls)
        {
            if (b.Type == BallType.Object && b.Id >= id) id = b.Id + 1;
        }

        return id;
    }

    // --------------------------------------------------------------------- JSON

    public static LevelData FromJsonText(string text)
    {
        return FromJson(Json.Parse(text));
    }

    public static LevelData FromJson(JsonValue root)
    {
        if (root.Kind != JsonKind.Object) throw new JsonException("level root must be an object");

        var level = new LevelData
        {
            Id = root.Text("id", "unnamed"),
            Name = root.Text("name", "unnamed"),
        };

        // Physics overrides first: table/ball defaults below are taken from the merged config.
        level.Config = ApplyPhysicsOverrides(new PhysicsConfig(), root.Get("physics"));

        var table = root.Get("table");
        level.TableLeft = table == null ? level.Config.TableLeft : table.Num("left", level.Config.TableLeft);
        level.TableRight = table == null ? level.Config.TableRight : table.Num("right", level.Config.TableRight);
        level.TableTop = table == null ? level.Config.TableTop : table.Num("top", level.Config.TableTop);
        level.TableBottom = table == null ? level.Config.TableBottom : table.Num("bottom", level.Config.TableBottom);
        level.BallRadius = root.Num("ballRadius", level.Config.BallRadius);

        var pocketRadius = root.Num("pocketRadius", level.Config.PocketRadius);
        var pockets = root.Get("pockets");
        if (pockets != null)
        {
            if (pockets.Kind != JsonKind.Array) throw new JsonException("'pockets' must be an array");
            foreach (var p in pockets.Items)
            {
                level.Pockets.Add(new PocketDef(new Vec2(p.Num("x", 0.0), p.Num("y", 0.0)), p.Num("r", pocketRadius)));
            }
        }

        var balls = root.Arr("balls");
        foreach (var b in balls.Items)
        {
            var declared = b.Text("type", "object");
            var type = BallType.Object;
            switch (declared)
            {
                case "cue":
                    type = BallType.Cue;
                    break;
                case "object":
                    type = BallType.Object;
                    break;
                // No longer inert strings. Each maps to a real type whose physical character
                // (mass, friction, restitution, rail gain, rail-boost allowance) comes from
                // PhysicsConfig.Types. Behaviour beyond physics is still unimplemented, so a
                // bomb/anchor currently plays as its configured mass and friction rather than as
                // a special mechanic -- but it is a distinct type with distinct tunables.
                case "heavy":
                    type = BallType.Heavy;
                    break;
                case "bomb":
                    type = BallType.Bomb;
                    break;
                case "anchor":
                    type = BallType.Anchor;
                    break;
                default:
                    throw new JsonException("unknown ball type '" + declared + "'");
            }

            var id = b.Int("id", -1);
            var spawn = new BallSpawn
            {
                Id = id,
                Type = type,
                DeclaredType = declared,
                X = b.Num("x", 0.0),
                Y = b.Num("y", 0.0),
                Vx = b.Num("vx", 0.0),
                Vy = b.Num("vy", 0.0),
                Radius = b.Num("radius", -1.0),
                // -1 = "not specified", so the per-type table decides. Defaulting this to 1.0 here
                // (as it used to) overrode every type's own mass at parse time, which is why a
                // "heavy" ball weighed exactly as much as an ordinary one.
                Mass = b.Num("mass", -1.0),
            };
            level.Balls.Add(spawn);
        }

        // White holes (optional). Defaults come from WhiteHoleDef so a level only has to give a
        // position; everything else, including the switch-off for object balls, is inherited.
        var holes = root.Get("whiteHoles");
        if (holes != null)
        {
            if (holes.Kind != JsonKind.Array) throw new JsonException("'whiteHoles' must be an array");
            foreach (var h in holes.Items)
            {
                var def = new WhiteHoleDef
                {
                    Center = new Vec2(h.Num("x", 0.0), h.Num("y", 0.0)),
                    Radius = h.Num("r", 30.0),
                };
                if (h.Get("gain") != null) def.Gain = h.Num("gain", def.Gain);
                if (h.Get("exitSpeed") != null) def.ExitSpeed = h.Num("exitSpeed", def.ExitSpeed);
                if (h.Get("maxSpeed") != null) def.MaxSpeed = h.Num("maxSpeed", def.MaxSpeed);
                if (h.Get("cooldown") != null) def.Cooldown = h.Num("cooldown", def.Cooldown);
                if (h.Get("affectsObjectBalls") != null) def.AffectsObjectBalls = h.Num("affectsObjectBalls", 1.0) != 0.0;
                level.WhiteHoles.Add(def);
            }
        }

        var budget = root.Get("budget");
        if (budget != null)
        {
            level.Budget = new RewindBudget
            {
                MaxSegments = budget.Int("maxSegments", 0),
                MaxDistance = budget.Num("maxDistance", 0.0),
            };
        }

        level.Validate();
        return level;
    }

    public string ToJsonText(bool indented = true)
    {
        return Json.Write(ToJson(), indented);
    }

    public JsonValue ToJson()
    {
        var root = JsonValue.Object();
        root.Set("id", JsonValue.Of(Id));
        root.Set("name", JsonValue.Of(Name));

        var table = JsonValue.Object();
        table.Set("left", JsonValue.Of(TableLeft));
        table.Set("right", JsonValue.Of(TableRight));
        table.Set("top", JsonValue.Of(TableTop));
        table.Set("bottom", JsonValue.Of(TableBottom));
        root.Set("table", table);
        root.Set("ballRadius", JsonValue.Of(BallRadius));

        var pockets = JsonValue.Array();
        foreach (var p in Pockets)
        {
            var o = JsonValue.Object();
            o.Set("x", JsonValue.Of(p.Center.X));
            o.Set("y", JsonValue.Of(p.Center.Y));
            o.Set("r", JsonValue.Of(p.Radius));
            pockets.Add(o);
        }

        root.Set("pockets", pockets);

        var balls = JsonValue.Array();
        foreach (var b in Balls)
        {
            var o = JsonValue.Object();
            o.Set("id", JsonValue.Of((double)b.Id));
            o.Set("type", JsonValue.Of(b.DeclaredType));
            o.Set("x", JsonValue.Of(b.X));
            o.Set("y", JsonValue.Of(b.Y));
            if (b.Vx != 0.0) o.Set("vx", JsonValue.Of(b.Vx));
            if (b.Vy != 0.0) o.Set("vy", JsonValue.Of(b.Vy));
            if (b.Radius > 0.0) o.Set("radius", JsonValue.Of(b.Radius));
            // Only emit an explicit mass when the caller actually set one (the sentinel is <= 0).
            if (b.Mass > 0.0 && b.Mass != 1.0) o.Set("mass", JsonValue.Of(b.Mass));
            balls.Add(o);
        }

        root.Set("balls", balls);

        var budget = JsonValue.Object();
        budget.Set("maxSegments", JsonValue.Of((double)Budget.MaxSegments));
        budget.Set("maxDistance", JsonValue.Of(Budget.MaxDistance));
        root.Set("budget", budget);
        return root;
    }

    /// <summary>
    /// Merge the optional "physics" object of a level file onto <paramref name="cfg"/>.
    /// Unknown keys are rejected: a typo in a level file must fail loudly, not silently
    /// leave the shipped default in place (that is exactly how two builds diverge).
    /// </summary>
    public static PhysicsConfig ApplyPhysicsOverrides(PhysicsConfig cfg, JsonValue physics)
    {
        if (physics == null) return cfg;
        if (physics.Kind != JsonKind.Object) throw new JsonException("'physics' must be an object");

        foreach (var kv in physics.Members)
        {
            // "types" carries per-ball-type physics; everything else is a scalar global.
            if (kv.Key == "types")
            {
                ApplyTypeOverrides(cfg, kv.Value);
                continue;
            }

            var v = kv.Value.AsDouble(double.NaN);
            switch (kv.Key)
            {
                case "simulationStep": cfg.SimulationStep = v; break;
                case "ballRadius": cfg.BallRadius = v; break;
                case "tableLeft": cfg.TableLeft = v; break;
                case "tableRight": cfg.TableRight = v; break;
                case "tableTop": cfg.TableTop = v; break;
                case "tableBottom": cfg.TableBottom = v; break;
                case "powerMax": cfg.PowerMax = v; break;
                case "speedPerPower": cfg.SpeedPerPower = v; break;
                case "minPower": cfg.MinPower = v; break;
                case "maxShots": cfg.MaxShots = (int)Math.Round(v); break;
                case "frictionDecel": cfg.FrictionDecel = v; break;
                case "wallRestitution": cfg.WallRestitution = v; break;
                case "cueWallGain": cfg.CueWallGain = v; break;
                case "objectBallGain": cfg.ObjectBallGain = v; break;
                case "speedCap": cfg.SpeedCap = v; break;
                case "ballRestitution": cfg.BallRestitution = v; break;
                case "historyStepPx": cfg.HistoryStepPx = v; break;
                case "rewindImpulseK": cfg.RewindImpulseK = v; break;
                case "pocketRadius": cfg.PocketRadius = v; break;
                case "rewindCrawlPxPerS": cfg.RewindCrawlPxPerS = v; break;
                case "subStepTravelBudget": cfg.SubStepTravelBudget = v; break;
                case "subStepRelativeTravelBudget": cfg.SubStepRelativeTravelBudget = v; break;
                case "maxIntervalsPerStep": cfg.MaxIntervalsPerStep = (int)Math.Round(v); break;
                case "maxEventsPerStep": cfg.MaxEventsPerStep = (int)Math.Round(v); break;
                case "separationEpsilon": cfg.SeparationEpsilon = v; break;
                default:
                    throw new JsonException("unknown physics override '" + kv.Key + "'");
            }
        }

        return cfg;
    }

    /// <summary>
    /// Merge the optional <c>physics.types</c> object: a map from type name to that type's physical
    /// parameters. This is how a designer tunes mass / friction / restitution / rail gain and the
    /// rail-boost allowance without a rebuild, e.g.
    /// <code>
    /// "physics": { "types": { "heavy": { "mass": 3, "frictionDecel": 900, "maxWallBoosts": 0 } } }
    /// </code>
    /// A negative value is the inherit sentinel (use the locked global); <c>maxWallBoosts</c>
    /// negative means unlimited.
    /// </summary>
    public static void ApplyTypeOverrides(PhysicsConfig cfg, JsonValue types)
    {
        if (types == null) return;
        if (types.Kind != JsonKind.Object) throw new JsonException("'physics.types' must be an object");

        foreach (var entry in types.Members)
        {
            var type = ParseTypeName(entry.Key);
            if (type == null)
                throw new JsonException("unknown ball type '" + entry.Key + "' in physics.types");
            if (entry.Value.Kind != JsonKind.Object)
                throw new JsonException("physics.types." + entry.Key + " must be an object");

            var p = cfg.For(type.Value).Clone();
            foreach (var f in entry.Value.Members)
            {
                var v = f.Value.AsDouble(double.NaN);
                switch (f.Key)
                {
                    case "mass": p.Mass = v; break;
                    case "frictionDecel": p.FrictionDecel = v; break;
                    case "restitution": p.Restitution = v; break;
                    case "wallGain": p.WallGain = v; break;
                    case "maxWallBoosts": p.MaxWallBoosts = (int)Math.Round(v); break;
                    case "collisionBoost": p.CollisionBoost = v; break;
                    default:
                        throw new JsonException("unknown type override '" + f.Key + "'");
                }
            }

            cfg.Types[(int)type.Value] = p;
        }
    }

    /// <summary>Type name to enum, or null when the name is unknown.</summary>
    private static BallType? ParseTypeName(string name)
    {
        switch (name)
        {
            case "cue": return BallType.Cue;
            case "object": return BallType.Object;
            case "heavy": return BallType.Heavy;
            case "bomb": return BallType.Bomb;
            case "anchor": return BallType.Anchor;
            default: return null;
        }
    }

    public void Validate()
    {
        Config.Validate();
        if (TableRight <= TableLeft || TableBottom <= TableTop)
            throw new JsonException("level '" + Id + "': degenerate table box");
        if (Balls.Count == 0) throw new JsonException("level '" + Id + "': no balls");
        if (Balls[0].Type != BallType.Cue)
            throw new JsonException("level '" + Id + "': ball 0 must be the cue ball");

        var seen = new HashSet<int>();
        foreach (var b in Balls)
        {
            if (!seen.Add(b.Id)) throw new JsonException("level '" + Id + "': duplicate ball id " + b.Id);
        }

        foreach (var p in Pockets)
        {
            if (p.Radius <= 0.0) throw new JsonException("level '" + Id + "': pocket radius must be > 0");
        }
    }
}
