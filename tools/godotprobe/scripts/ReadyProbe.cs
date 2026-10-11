namespace GodotProbe;

using Godot;

/// <summary>
/// Probe A: proves a C# Node's _Ready() output reaches stdout in a headless run,
/// and that the C# assembly was actually loaded by the engine (not just compiled).
/// </summary>
public partial class ReadyProbe : Node
{
    public override void _Ready()
    {
        GD.Print("[probe] READY_PROBE_OK pid=" + System.Environment.ProcessId);
        GD.Print("[probe] engine=" + (string)Engine.GetVersionInfo()["string"]);
        GD.Print("[probe] assembly=" + typeof(ReadyProbe).Assembly.GetName().Name);
        GD.Print("[probe] display_driver=" + DisplayServer.GetName());
        GD.Print("[probe] rendering_driver=" + RenderingServer.GetVideoAdapterName());
        GD.Print("[probe] physics_ticks_per_second="
                + (float)ProjectSettings.GetSetting("physics/common/physics_ticks_per_second", 60f));
        GD.Print("[probe] max_physics_steps_per_frame="
                + (int)ProjectSettings.GetSetting("physics/common/max_physics_steps_per_frame", 8));
    }
}
