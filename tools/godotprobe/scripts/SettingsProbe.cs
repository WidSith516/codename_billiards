namespace GodotProbe;

using Godot;
using System.Collections.Generic;

/// <summary>
/// Dumps the EXACT project-setting names, current values and enum hint strings straight
/// from the engine, instead of trusting documentation or memory. This is what settles
/// items 4 and 5 of the brief with evidence rather than assertion.
/// </summary>
public partial class SettingsProbe : Node
{
    private static readonly string[] Wanted =
    {
        "physics/common/physics_ticks_per_second",
        "physics/common/max_physics_steps_per_frame",
        "physics/common/physics_jitter_fix",
        "physics/common/enable_object_picking",
        "physics/common/enable_object_picking_sort",
        "physics/2d/default_gravity",
        "physics/2d/solver/solver_iterations",
        "physics/2d/default_linear_damp",
        "display/window/stretch/mode",
        "display/window/stretch/aspect",
        "display/window/stretch/scale",
        "display/window/size/viewport_width",
        "display/window/size/viewport_height",
        "rendering/textures/canvas_textures/default_texture_filter",
        "rendering/anti_aliasing/quality/msaa_2d",
        "rendering/2d/snap/snap_2d_transforms_to_pixel",
        "rendering/2d/snap/snap_2d_vertices_to_pixel",
        "rendering/renderer/rendering_method",
        "application/run/max_fps",
        "application/run/flush_stdout_on_print",
        "application/config/use_custom_user_dir",
        "application/config/custom_user_dir_name",
        "application/config/name",
    };

    public override void _Ready()
    {
        GD.Print("=== settings dump (values read from the running engine) ===");
        foreach (var name in Wanted)
        {
            var has = ProjectSettings.HasSetting(name);
            var val = has ? ProjectSettings.GetSetting(name).ToString() : "<unset>";
            GD.Print($"  {(has ? "SET  " : "UNSET")} {name} = {val}");
        }

        // The polyline-relevant drawing API: prove the AA parameter really exists.
        var methods = new List<string>();
        foreach (var m in typeof(CanvasItem).GetMethods())
        {
            if (m.Name is "DrawCircle" or "DrawPolyline" or "DrawLine" or "DrawArc" or "DrawPolygon")
            {
                var ps = new List<string>();
                foreach (var pi in m.GetParameters())
                {
                    ps.Add($"{pi.ParameterType.Name} {pi.Name}{(pi.HasDefaultValue ? " = " + (pi.DefaultValue ?? "null") : "")}");
                }

                methods.Add($"  {m.Name}({string.Join(", ", ps)})");
            }
        }

        methods.Sort();
        GD.Print("=== CanvasItem drawing API (reflection over the real assembly) ===");
        foreach (var m in methods)
        {
            GD.Print(m);
        }

        GD.Print("=== Line2D relevant properties ===");
        foreach (var pi in typeof(Line2D).GetProperties())
        {
            if (pi.Name is "Points" or "Width" or "Antialiased" or "JointMode" or "BeginCapMode" or "EndCapMode" or "Closed" or "RoundPrecision" or "TextureMode" or "SharpLimit")
            {
                GD.Print($"  Line2D.{pi.Name} : {pi.PropertyType.Name}");
            }
        }

        GetTree().Quit(0);
    }
}
