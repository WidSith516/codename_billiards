using System;
using System.Collections.Generic;

namespace Rewind.Sim;

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
    public double Mass = 1.0;

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
                case "bomb":     // reserved: data only in M1 (behaves as an object ball)
                case "anchor":   // reserved: data only in M1
                case "heavy":    // reserved: data only in M1
                    type = BallType.Object;
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
                Mass = b.Num("mass", 1.0),
            };
            level.Balls.Add(spawn);
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
            if (b.Mass != 1.0) o.Set("mass", JsonValue.Of(b.Mass));
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
