using Godot;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// The playable kernel view. Milestone M1 is the kernel, so this node deliberately does the
/// minimum needed to see the kernel run: it owns a <see cref="World"/>, advances it with a fixed
/// step, and draws table, balls and the cue's history trail with <c>_Draw</c>.
///
/// The frame delta is NEVER passed to the simulation: the accumulator only decides *how many*
/// fixed steps to run, each of which is exactly <c>PhysicsConfig.SimulationStep</c> long.
/// </summary>
public partial class KernelView : Node2D
{
    private const int MaxStepsPerFrame = 8;

    private World _world;
    private double _accumulator;
    private bool _aiming;
    private Vector2 _aimPoint;
    private string _screenshotPath;
    private int _frames;
    private bool _captureQuit;

    public World World => _world;

    public override void _Ready()
    {
        var levelPath = $"res://levels/{LevelIds.H5Rack}.json";
        var levelArg = UserArg("--level=");
        if (levelArg != null) levelPath = levelArg.StartsWith("res://") ? levelArg : $"res://levels/{levelArg}.json";

        _world = new World(LevelLoader.Load(levelPath));
        _screenshotPath = UserArg("--out=");

        GD.Print($"[kernel] level={_world.Level.Id} balls={_world.Balls.Length} pockets={_world.Level.Pockets.Count}");
        GD.Print($"[kernel] dt={_world.Config.SimulationStep:R} friction={_world.Config.FrictionDecel} " +
                 $"railFixedPoint={_world.Config.RailFixedPoint} cap={_world.Config.SpeedCap}");
        GD.Print($"[kernel] historyStepPx={_world.Config.HistoryStepPx} crawlPxPerS={_world.Config.RewindCrawlPxPerS} " +
                 $"rewindImpulseK={_world.Config.RewindImpulseK}");
        GD.Print($"[kernel] physicsJitterFix={ProjectSettings.GetSetting("physics/common/physics_jitter_fix", -1f)} " +
                 $"ticksPerSecond={ProjectSettings.GetSetting("physics/common/physics_ticks_per_second", -1)}");
        GD.Print("[kernel] KERNEL_VIEW_READY");
    }

    private static string UserArg(string prefix)
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith(prefix)) return arg.Substring(prefix.Length);
        }

        return null;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_world == null) return;

        var dt = _world.Config.SimulationStep;
        _accumulator += dt;
        var steps = 0;
        while (_accumulator >= dt - 1e-12 && steps < MaxStepsPerFrame)
        {
            _world.Step();
            _accumulator -= dt;
            steps++;
        }

        // Never let a backlog spiral: the kernel is a fixed-step simulation, not a real-time one.
        if (_accumulator > dt) _accumulator = 0.0;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_screenshotPath == null || _captureQuit) return;
        if (++_frames < 8) return;
        _captureQuit = true;
        var image = GetViewport().GetTexture().GetImage();
        if (image == null)
        {
            GD.Print("[kernel] screenshot failed: no viewport image");
            GetTree().Quit(2);
            return;
        }

        var err = image.SavePng(_screenshotPath);
        GD.Print($"[kernel] screenshot={_screenshotPath} err={err} size={image.GetWidth()}x{image.GetHeight()}");
        GetTree().Quit(err == Error.Ok ? 0 : 3);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_world == null) return;

        switch (@event)
        {
            case InputEventKey key:
                if (key.Keycode == Key.R && !key.Echo)
                {
                    if (key.Pressed) _world.StartRewind();
                    else _world.EndRewind();
                }

                if (key.Pressed && !key.Echo && key.Keycode == Key.Space) _world.Reset();
                break;

            case InputEventMouseButton button when button.ButtonIndex == MouseButton.Left:
                if (button.Pressed)
                {
                    var cue = _world.Cue;
                    if (!cue.Pocketed && (new Vector2((float)cue.Position.X, (float)cue.Position.Y) - button.Position).Length() < cue.Radius + 10.0f)
                    {
                        _aiming = true;
                        _aimPoint = button.Position;
                    }
                }
                else if (_aiming)
                {
                    _aiming = false;
                    var aim = ReleaseShot(button.Position);
                    if (aim.HasValue) GD.Print($"[kernel] shot angle={aim.Value.X:F3} power={aim.Value.Y:F3} speed={_world.Cue.Speed:F3}");
                }

                break;

            case InputEventMouseMotion motion when _aiming:
                _aimPoint = motion.Position;
                break;
        }
    }

    private Vector2? ReleaseShot(Vector2 releasePoint)
    {
        var cue = _world.Cue;
        var dx = cue.Position.X - releasePoint.X;
        var dy = cue.Position.Y - releasePoint.Y;
        var len = System.Math.Sqrt(dx * dx + dy * dy);
        if (len < _world.Config.MinPower * 0.5) return null;
        var angle = System.Math.Atan2(dy, dx) * 180.0 / System.Math.PI;
        return _world.Shot(angle, len) ? new Vector2((float)angle, (float)len) : null;
    }

    public override void _Draw()
    {
        if (_world == null) return;
        var level = _world.Level;

        var felt = new Rect2(
            (float)level.TableLeft, (float)level.TableTop,
            (float)level.TableWidth, (float)level.TableHeight);
        DrawRect(felt, new Color("#14402f"));

        foreach (var pocket in level.Pockets)
        {
            DrawCircle(new Vector2((float)pocket.Center.X, (float)pocket.Center.Y), (float)pocket.Radius, new Color("#08110f"));
        }

        // Cue ball history trail: the kernel's arc-length samples, drawn without decimation.
        var history = _world.Cue.History;
        if (history.Count >= 2)
        {
            var points = new Vector2[history.Count];
            for (var i = 0; i < history.Count; i++)
            {
                var sample = history[i];
                points[i] = new Vector2((float)sample.Position.X, (float)sample.Position.Y);
            }

            DrawPolyline(points, _world.Rewinding ? new Color("#ffaa65") : new Color("#b9dec0"), 1.5f, true);
        }

        foreach (var ball in _world.Balls)
        {
            if (ball.Pocketed) continue;
            var center = new Vector2((float)ball.Position.X, (float)ball.Position.Y);
            var color = ball.IsCue ? new Color("#f3f1e9") : new Color("#e7b841");
            DrawCircle(center, (float)ball.Radius, color, true, -1.0f, true);
            DrawCircle(center, (float)ball.Radius, new Color("#0b1a15"), false, 1.5f, true);
        }

        if (_aiming)
        {
            var cue = _world.Cue;
            var from = new Vector2((float)cue.Position.X, (float)cue.Position.Y);
            var dx = from.X - _aimPoint.X;
            var dy = from.Y - _aimPoint.Y;
            var len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len > 1.0f)
            {
                var dir = new Vector2(dx / len, dy / len);
                DrawLine(from + dir * 20.0f, from + dir * 450.0f, new Color("#ffe1a6"), 1.0f, true);
            }
        }
    }
}
