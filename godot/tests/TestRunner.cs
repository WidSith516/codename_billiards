using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Rewind.Tests;

/// <summary>
/// Tiny assertion runner for the headless kernel gate. Prints one line per check and returns a
/// REAL process exit code (0 = all green, 1 = at least one failure), so the gate can be scripted.
/// Deliberately dependency-free: no xUnit assembly to load headlessly.
/// </summary>
public sealed class TestRunner
{
    private readonly List<string> _failures = new List<string>();
    private string _section = string.Empty;

    public int Checks { get; private set; }

    public int Failures => _failures.Count;

    public void Section(string title)
    {
        _section = title;
        GD.Print(string.Empty);
        GD.Print($"--- {title} ---");
    }

    public void Check(string name, bool ok, string detail = "")
    {
        Checks++;
        var suffix = detail.Length > 0 ? "  [" + detail + "]" : string.Empty;
        if (ok)
        {
            GD.Print($"  PASS  {name}{suffix}");
            return;
        }

        _failures.Add(_section + " / " + name);
        GD.Print($"  FAIL  {name}{suffix}");
    }

    public void CheckClose(string name, double actual, double expected, double tolerance, string unit = "")
    {
        var delta = Math.Abs(actual - expected);
        Check(name, delta <= tolerance,
            $"{N(actual)}{unit} vs {N(expected)}{unit} (delta {N(delta)}, tol {N(tolerance)})");
    }

    public void CheckLess(string name, double actual, double bound, string unit = "")
    {
        Check(name, actual < bound, $"{N(actual)}{unit} < {N(bound)}{unit}");
    }

    public void CheckAtMost(string name, double actual, double bound, string unit = "")
    {
        Check(name, actual <= bound, $"{N(actual)}{unit} <= {N(bound)}{unit}");
    }

    public void CheckAtLeast(string name, double actual, double bound, string unit = "")
    {
        Check(name, actual >= bound, $"{N(actual)}{unit} >= {N(bound)}{unit}");
    }

    public static string N(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        var abs = Math.Abs(value);
        if (abs != 0.0 && (abs < 1e-3 || abs >= 1e6)) return value.ToString("0.######e+00", CultureInfo.InvariantCulture);
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    public int Summarize(string suiteName)
    {
        GD.Print(string.Empty);
        var passed = Checks - _failures.Count;
        GD.Print($"=== {suiteName} summary: {passed}/{Checks} checks passed ===");
        if (_failures.Count == 0)
        {
            GD.Print("GATE PASS");
            return 0;
        }

        GD.Print("GATE FAIL: " + string.Join(" | ", _failures));
        return 1;
    }
}
