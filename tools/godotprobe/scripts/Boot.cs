namespace GodotProbe;

using Godot;

/// <summary>
/// Boot dispatcher — the reason it exists:
///
/// An EXPORTED Godot build is compiled with disable_path_overrides, so you can NOT pass a
/// scene path or --path on its command line (verified: "this Godot binary was compiled
/// without support for path overrides. Aborting."). Therefore an exported build can only
/// run its configured main scene, and the ONLY way to drive it headlessly is through
/// user args after "--". This node is that entry point, and the real game must have one too.
///
///   GodotProbe.exe --headless -- --selftest
///   GodotProbe.exe            -- --shot=D:\path\out.png --frames=6
/// </summary>
public partial class Boot : Node
{
    public override void _Ready()
    {
        var wantSelfTest = false;
        var wantShot = false;

        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a == "--selftest")
            {
                wantSelfTest = true;
            }
            else if (a.StartsWith("--shot=", System.StringComparison.Ordinal))
            {
                wantShot = true;
            }
        }

        GD.Print("[boot] GodotProbe boot; args=" + string.Join(" ", OS.GetCmdlineUserArgs()));

        if (wantSelfTest)
        {
            AddChild(ResourceLoader.Load<PackedScene>("res://tests/probe_smoke.tscn").Instantiate());
            return;   // the smoke scene quits the tree with its own exit code
        }

        if (wantShot)
        {
            AddChild(ResourceLoader.Load<PackedScene>("res://scenes/render_probe.tscn").Instantiate());
            return;   // the render probe quits the tree with its own exit code
        }

        // Default: never hang an unattended run.
        GD.Print("[boot] no probe arg given; quitting immediately (unattended-safe)");
        GetTree().Quit(0);
    }
}
