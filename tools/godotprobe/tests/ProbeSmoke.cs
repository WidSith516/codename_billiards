namespace GodotProbe;

using Godot;
using System.Collections.Generic;

/// <summary>
/// Headless acceptance gate. Run:
///   tools/godot/godot_console.exe --headless --path tools/godotprobe res://tests/probe_smoke.tscn
/// Prints PASS/FAIL per check and exits with a REAL process exit code (0 pass / 1 fail).
/// This is the pattern the real game's smoke gate must copy.
/// </summary>
public partial class ProbeSmoke : Node
{
    private readonly List<string> _fails = new();
    private int _count;

    private void Check(string name, bool ok, string detail = "")
    {
        _count++;
        if (ok)
        {
            GD.Print($"  PASS  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
        }
        else
        {
            _fails.Add(name);
            GD.Print($"  FAIL  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
        }
    }

    public override void _Ready()
    {
        GD.Print("=== godotprobe smoke ===");
        GD.Print("engine: " + (string)Engine.GetVersionInfo()["string"]);

        // --- C# assembly really loaded (not merely compiled) ---
        Check("C# assembly loaded", typeof(ProbeSmoke).Assembly.GetName().Name == "GodotProbe",
            typeof(ProbeSmoke).Assembly.GetName().Name!);

        // --- physics settings the billiards sim depends on ---
        var tps = (float)ProjectSettings.GetSetting("physics/common/physics_ticks_per_second", -1f);
        Check("physics_ticks_per_second == 60 (default)", tps == 60f, tps.ToString());
        var maxSteps = (int)ProjectSettings.GetSetting("physics/common/max_physics_steps_per_frame", -1);
        Check("max_physics_steps_per_frame == 8 (default)", maxSteps == 8, maxSteps.ToString());
        Check("physics/common/enable_object_picking exists",
            ProjectSettings.HasSetting("physics/common/enable_object_picking"));

        // --- fixed 1040x560 layout contract ---
        var vw = (int)ProjectSettings.GetSetting("display/window/size/viewport_width", -1);
        var vh = (int)ProjectSettings.GetSetting("display/window/size/viewport_height", -1);
        Check("viewport 1040x560", vw == 1040 && vh == 560, $"{vw}x{vh}");
        var stretch = (string)ProjectSettings.GetSetting("display/window/stretch/mode", "");
        Check("stretch mode is canvas_items", stretch == "canvas_items", stretch);

        // --- headless really is the dummy renderer (so screenshots MUST be non-headless) ---
        var ds = DisplayServer.GetName();
        Check("headless => DisplayServer is headless", ds == "headless", ds);

        // --- determinism: identical float sequence twice ---
        Check("fixed-step integration is reproducible", FixedStep() == FixedStep(), FixedStep().ToString("R"));

        // --- custom manual collision resolution does not need RigidBody2D ---
        // A 1-D elastic exchange of equal masses is the billiards core; assert it conserves
        // momentum and energy exactly within double precision.
        var (v1, v2) = ElasticSwap(5.0, -3.0);
        Check("elastic swap conserves momentum", System.Math.Abs((v1 + v2) - (5.0 + -3.0)) < 1e-12, $"{v1},{v2}");
        Check("elastic swap conserves energy",
            System.Math.Abs((v1 * v1 + v2 * v2) - (25.0 + 9.0)) < 1e-12);

        // --- NEGATIVE CONTROL ---
        // An acceptance gate that cannot fail is worse than no gate, so prove the
        // failure path really is wired to a non-zero process exit code:
        //   ... probe_smoke.tscn -- --selftest-fail
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a == "--selftest-fail")
            {
                Check("negative control (forced failure)", false, "expected: this run must exit 1");
            }
        }

        // --- exit with a real code ---
        GD.Print($"=== smoke summary: {_count - _fails.Count}/{_count} passed ===");
        if (_fails.Count == 0)
        {
            GD.Print("SMOKE PASS");
            GetTree().Quit(0);
        }
        else
        {
            GD.Print("SMOKE FAIL: " + string.Join(", ", _fails));
            GetTree().Quit(1);
        }
    }

    private static double FixedStep()
    {
        var pos = 0.0;
        var vel = 3.25;
        const double dt = 1.0 / 60.0;
        for (var i = 0; i < 600; i++)
        {
            vel *= 0.998;
            pos += vel * dt;
        }

        return pos;
    }

    private static (double, double) ElasticSwap(double v1, double v2)
    {
        // Equal masses, perfectly elastic 1-D collision => velocities exchange.
        return (v2, v1);
    }
}
