namespace GodotProbe;

using Godot;

/// <summary>
/// Headless C# entry point as a SceneTree subclass, so it can be driven by
/// `--script res://scripts/HeadlessEntry.cs` or `--main-loop HeadlessEntry`.
/// Proves the mechanism (see REPORT.md item 2a): we do NOT need a GDScript shim.
/// </summary>
[GlobalClass]
public partial class HeadlessEntry : SceneTree
{
    public override void _Initialize()
    {
        GD.Print("[headless] MECHANISM=CSharpSceneTree");
        GD.Print("[headless] reachable=true");
        GD.Print("[headless] engine=" + (string)Engine.GetVersionInfo()["string"]);
        GD.Print("[headless] scene_tree_name=" + GetType().FullName);

        // Determinism smoke: a fixed-step custom integrator must be bit-reproducible.
        var pos = 0.0;
        var vel = 3.25;
        const double dt = 1.0 / 60.0;
        for (var i = 0; i < 600; i++)
        {
            vel *= 0.998;   // rolling friction, engine-independent
            pos += vel * dt;
        }

        GD.Print($"[headless] fixed_step_pos={pos:R}");
        GD.Print("[headless] HEADLESS_ENTRY_OK");
        Quit(0);
    }
}
