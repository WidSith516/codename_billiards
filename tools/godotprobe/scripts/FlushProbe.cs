namespace GodotProbe;

using Godot;

/// <summary>
/// Proves why run/flush_stdout_on_print matters: with the flag OFF, GD.Print output that
/// has not yet been flushed is LOST when the process dies abruptly (crash / OS.Kill),
/// which is exactly the forensic case that matters. With it ON, the lines survive.
///
/// Run:  godot_console.exe --headless --path &lt;proj&gt; res://scenes/flush_probe.tscn -- --kill
/// </summary>
public partial class FlushProbe : Node
{
    public override void _Ready()
    {
        GD.Print("[flush] PRINT_BEFORE_DEATH_1");
        GD.Print("[flush] PRINT_BEFORE_DEATH_2");
        GD.Print("[flush] PRINT_BEFORE_DEATH_3");

        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a == "--kill")
            {
                // Hard, unhandled process termination - no shutdown, no flush.
                OS.Kill(OS.GetProcessId());
            }
        }

        GD.Print("[flush] survived (no kill requested)");
        GetTree().Quit(0);
    }
}
