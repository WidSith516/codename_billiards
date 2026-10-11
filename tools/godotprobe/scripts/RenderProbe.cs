namespace GodotProbe;

using Godot;

/// <summary>
/// Screenshot / render probe. Draws a deliberately colourful + patterned frame,
/// captures the viewport and writes a PNG, then quits with a meaningful exit code.
/// This is the no-human verification primitive for the port.
///
/// Run (non-headless; D3D12 or opengl3):
///   tools/godot/godot_console.exe --path tools/godotprobe --resolution 1040x560 \
///       res://scenes/render_probe.tscn -- --out=&lt;abs path.png&gt;
/// </summary>
public partial class RenderProbe : Node2D
{
    private int _frames;
    private string _out = "user://render_probe.png";
    private int _minFrames = 6;

    public override void _Ready()
    {
        GD.Print("[render] display_driver=" + DisplayServer.GetName());
        GD.Print("[render] rendering_method=" + ProjectSettings.GetSetting("rendering/renderer/rendering_method", "?"));
        GD.Print("[render] video_adapter=" + RenderingServer.GetVideoAdapterName());
        GD.Print("[render] window_size=" + DisplayServer.WindowGetSize());

        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--out=", System.StringComparison.Ordinal))
            {
                _out = a.Substring("--out=".Length);
            }
            else if (a.StartsWith("--frames=", System.StringComparison.Ordinal)
                     && int.TryParse(a.Substring("--frames=".Length), out var f))
            {
                _minFrames = f;
            }
        }

        GD.Print("[render] out=" + _out + " (abs " + ProjectSettings.GlobalizePath(_out) + ")");
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, 1040, 560), new Color(0.07f, 0.09f, 0.14f));

        // Anti-aliased circles: draw_circle has NO antialiased parameter in 4.x,
        // so use the angle-sliced overload (see REPORT.md item 5).
        for (var i = 0; i < 13; i++)
        {
            DrawCircle(new Vector2(70 + i * 75, 130), 30f,
                Color.FromHsv(i / 13f, 0.85f, 0.95f), filled: true, width: -1f, antialiased: true);
        }

        // AA polyline "billiard trail" — the shape the real game needs.
        var pts = new Vector2[200];
        for (var i = 0; i < 200; i++)
        {
            var t = i / 199f;
            pts[i] = new Vector2(30 + t * 980, 300 + Mathf.Sin(t * 9f) * 70);
        }

        DrawPolyline(pts, new Color(0.35f, 0.9f, 1f), 4f, antialiased: true);

        DrawString(ThemeDB.FallbackFont, new Vector2(32, 500), "GODOT 4.7 RENDER PROBE",
            HorizontalAlignment.Left, -1, 34, Colors.White);
        DrawString(ThemeDB.FallbackFont, new Vector2(32, 535), Time.GetDatetimeStringFromSystem(),
            HorizontalAlignment.Left, -1, 18, new Color(0.8f, 0.85f, 0.95f));
    }

    public override void _Process(double delta)
    {
        if (++_frames < _minFrames)
        {
            return;
        }

        var img = GetViewport().GetTexture().GetImage();
        if (img == null)
        {
            GD.Print("[render] FAILED: viewport image is null");
            GetTree().Quit(2);
            return;
        }

        GD.Print($"[render] image={img.GetWidth()}x{img.GetHeight()} format={img.GetFormat()}");
        var err = img.SavePng(_out);
        GD.Print("[render] save_png err=" + err);
        GetTree().Quit(err == Error.Ok ? 0 : 3);
    }
}
