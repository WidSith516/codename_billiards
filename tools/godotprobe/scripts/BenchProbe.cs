namespace GodotProbe;

using Godot;
using System.Collections.Generic;

/// <summary>
/// Measures the real per-frame cost of drawing a long polyline trail two ways:
///   (a) CanvasItem._Draw + DrawPolyline        (immediate mode, re-recorded each redraw)
///   (b) a Line2D child whose Points array is re-assigned every frame
/// at 200 / 1000 / 5000 / 20000 points, against an empty-draw baseline.
///
/// Run non-headless with vsync OFF, e.g.:
///   godot_console.exe --path tools/godotprobe --resolution 1040x560 \
///       --rendering-driver d3d12 --disable-vsync --max-fps 0 \
///       res://scenes/bench_probe.tscn
///
/// Caveat stated plainly: this measures average frame time with vsync disabled and no
/// GPU sync point, so it is a CPU-submit-dominated figure. It is a RELATIVE comparison
/// between the two idioms at increasing point counts, not an absolute GPU benchmark.
/// </summary>
public partial class BenchProbe : Node2D
{
    private static readonly int[] Sizes = { 200, 1000, 5000, 20000 };
    private const int WarmupFrames = 30;
    private const int MeasureFrames = 120;

    private enum Mode { Baseline, Draw, Line }

    private Mode _mode = Mode.Baseline;
    private int _sizeIndex = -1;
    private int _frame;
    private readonly List<double> _samples = new();
    private ulong _lastTick;
    private Line2D _line = null!;
    private Vector2[] _points = System.Array.Empty<Vector2>();
    private readonly List<string> _report = new();

    public override void _Ready()
    {
        _line = new Line2D
        {
            Width = 4f,
            Antialiased = true,
            JointMode = Line2D.LineJointMode.Round,
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
            Visible = false,
        };
        AddChild(_line);
        _lastTick = Time.GetTicksUsec();
        QueueRedraw();
    }

    private static Vector2[] BuildPoints(int n)
    {
        var pts = new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (float)(n - 1);
            pts[i] = new Vector2(20 + t * 1000, 280 + Mathf.Sin(t * 40f) * 120f);
        }

        return pts;
    }

    private void Advance()
    {
        // order: baseline, then for each size: draw-mode, line-mode
        if (_mode == Mode.Baseline && _sizeIndex < 0)
        {
            _mode = Mode.Draw;
            _sizeIndex = 0;
        }
        else if (_mode == Mode.Draw)
        {
            _mode = Mode.Line;
        }
        else
        {
            _mode = Mode.Draw;
            _sizeIndex++;
        }

        if (_sizeIndex >= Sizes.Length)
        {
            Finish();
            return;
        }

        _points = BuildPoints(Sizes[_sizeIndex]);
        _line.Visible = _mode == Mode.Line;
        _line.Points = _mode == Mode.Line ? _points : System.Array.Empty<Vector2>();
        _frame = 0;
        _samples.Clear();
        QueueRedraw();
    }

    private void Finish()
    {
        GD.Print("=== polyline benchmark (avg frame ms, vsync off) ===");
        foreach (var line in _report)
        {
            GD.Print(line);
        }

        GetTree().Quit(0);
    }

    public override void _Draw()
    {
        if (_mode != Mode.Draw || _points.Length == 0)
        {
            return;
        }

        DrawPolyline(_points, new Color(0.35f, 0.9f, 1f), 4f, antialiased: true);
    }

    public override void _Process(double delta)
    {
        var now = Time.GetTicksUsec();
        var ms = (now - _lastTick) / 1000.0;
        _lastTick = now;

        if (_frame < WarmupFrames)
        {
            _frame++;
            if (_mode == Mode.Line)
            {
                _line.Points = _points;   // force the rebuild each frame
            }

            return;
        }

        _samples.Add(ms);
        if (_mode == Mode.Line)
        {
            _line.Points = _points;
        }

        if (_mode == Mode.Draw)
        {
            QueueRedraw();
        }

        if (_samples.Count >= MeasureFrames)
        {
            _samples.Sort();
            var sum = 0.0;
            foreach (var s in _samples)
            {
                sum += s;
            }

            var avg = sum / _samples.Count;
            var p95 = _samples[(int)(_samples.Count * 0.95)];
            var label = _mode == Mode.Baseline ? "baseline (no draw)" : $"{_mode}";
            _report.Add($"  {label,-12} n={( _mode == Mode.Baseline ? 0 : Sizes[_sizeIndex]),6}  avg={avg,7:F3} ms  p95={p95,7:F3} ms  min={_samples[0],7:F3} ms");
            Advance();
        }

        _frame++;
    }
}
