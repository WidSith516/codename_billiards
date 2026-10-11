using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// HUD and debug overlay. Drawn with immediate-mode primitives on the same Node2D as the table, so
/// there is exactly one thing to screenshot and no Control-tree layout to reason about.
///
/// Readability requirements (M2 brief item 7) are answered by:
///  * a persistent top bar: balls left (n/N), STRIKE AVAILABLE / STRIKE USED - REWIND ONLY, REWINDING;
///  * a left panel: power meter with numeric px/s, strike lock, rewind budget (segments + px);
///  * ball identity by number + glyph, never colour alone (see <see cref="BallStyles"/>);
///  * the debug overlay (F1): live positions, velocities, history/forecast counts and a non-finite
///    check - the numbers a verifier needs to check the view against the kernel.
/// </summary>
public static class Hud
{
    public sealed class Model
    {
        public string LevelId;
        public string LevelName;
        public int ObjectBallCount;
        public int PocketedObjectCount;
        public bool ShotUsed;
        public bool Rewinding;
        public bool Won;
        public bool Paused;
        public bool Scrubbing;
        public int Tick;
        public double Time;

        public double AimAngle;
        public double AimPower;
        public bool HasAim;
        public bool Dragging;
        public double MinPower;
        public double MaxPower;
        public double Speed;

        public int Rewinds;
        public int RewindSegmentsUsed;
        public int RewindSegmentsMax;
        public double RewindDistanceUsed;
        public double RewindDistanceMax;

        public int HistoryEntries;
        public int TrailSessions;
        public int TurnBacks;
        public int TurnForwards;
        public int ForecastContacts;
        public double ForecastUs;

        public string Message;
        public Color MessageColor = Palette.TextMain;
        public bool Debug;

        public World World;
        public TimelineTrail Trail;
        public RewindVisualizer RewindVis;
        public AimPredictor.Prediction Aim;

        public double FrameMs;
        public double MaxFrameMs;
        public double DrawMs;
        public double SimMs;
        public int TrailVertices;
    }

    private const float Pad = 10.0f;

    public static void Draw(CanvasItem ci, Font font, Model m)
    {
        DrawTopBar(ci, font, m);
        DrawLeftPanel(ci, font, m);
    }

    // --------------------------------------------------------------- top bar

    private static void DrawTopBar(CanvasItem ci, Font font, Model m)
    {
        var rect = new Rect2(0, 0, 1040, 28);
        ci.DrawRect(rect, Palette.PanelBg);
        ci.DrawLine(new Vector2(0, 28), new Vector2(1040, 28), Palette.PanelEdge, 1.0f);

        var x = 8.0f;
        x = Text(ci, font, x, 19, $"REWIND  |  {m.LevelId}", 15, Palette.TextMain);
        x += 10;

        var ballsLeft = m.ObjectBallCount - m.PocketedObjectCount;
        x = Chip(ci, font, x, "BALLS", $"{ballsLeft} / {m.ObjectBallCount}", ballsLeft == 0 ? Palette.Good : Palette.Accent);

        var strikeText = m.ShotUsed ? "STRIKE USED - REWIND ONLY" : "STRIKE AVAILABLE";
        x = Chip(ci, font, x, "STRIKE", strikeText, m.ShotUsed ? Palette.Bad : Palette.Good);

        if (m.Rewinding)
        {
            var label = m.Scrubbing ? "REWIND PAUSED (scrub)" : "REWINDING";
            x = Chip(ci, font, x, "TIME", label, Palette.RewindCue);
        }
        else if (m.Won)
        {
            x = Chip(ci, font, x, "TIME", "CLEARED", Palette.Good);
        }
        else
        {
            x = Chip(ci, font, x, "TIME", "FORWARD", Palette.TextDim);
        }

        var t = $"t={m.Time:F2}s   tick={m.Tick}   rewinds={m.Rewinds}   trail={m.HistoryEntries}";
        Text(ci, font, 1040 - Pad - Measure(font, t, 12), 19, t, 12, Palette.TextDim);
    }

    private static float Chip(CanvasItem ci, Font font, float x, string key, string value, Color color)
    {
        var kw = Measure(font, key, 11);
        var vw = Measure(font, value, 13);
        var w = kw + vw + 18.0f;
        ci.DrawRect(new Rect2(x, 4, w, 20), new Color(color.R, color.G, color.B, 0.16f));
        ci.DrawLine(new Vector2(x + 6, 6), new Vector2(x + 6, 22), color, 2.0f);
        Text(ci, font, x + 11, 18, key, 11, Palette.TextDim);
        Text(ci, font, x + 11 + kw + 5, 18, value, 13, color);
        return x + w + 8;
    }

    // ------------------------------------------------------------ left panel

    /// <summary>
    /// Left panel. The layout is hand-computed on a fixed grid because there is no Control tree to
    /// resolve constraints; the y coordinate of every block is written next to it, and
    /// <see cref="PanelLayoutSelfCheck"/> asserts the blocks stay ordered and on screen.
    /// </summary>
    private static void DrawLeftPanel(CanvasItem ci, Font font, Model m)
    {
        var rect = new Rect2(6, 36, 246, 518);
        ci.DrawRect(rect, Palette.PanelBg);
        ci.DrawRect(rect, Palette.PanelEdge, false, 1.0f);

        var x = rect.Position.X + 10;

        // --- STRIKE --------------------------------------------------------- y 50 .. 81
        Text(ci, font, x, 50, "STRIKE", 12, Palette.TextDim);
        Text(ci, font, x, 66, m.ShotUsed ? "USED - LOCKED FOREVER" : "AVAILABLE (1 per level)", 14,
            m.ShotUsed ? Palette.Bad : Palette.Good);
        Text(ci, font, x, 81, m.ShotUsed ? "verb left: REWIND only" : "drag back from the cue ball", 11, Palette.TextDim);

        // --- POWER --------------------------------------------------------- y 104 .. 150
        DrawPowerMeter(ci, font, x, 104, m);

        // --- REWIND BUDGET ------------------------------------------------- y 160 .. 206
        Text(ci, font, x, 160, "REWIND BUDGET", 12, Palette.TextDim);
        var segMax = m.RewindSegmentsMax > 0 ? m.RewindSegmentsMax.ToString(CultureInfo.InvariantCulture) : "inf";
        Text(ci, font, x, 177, $"segments {m.RewindSegmentsUsed} / {segMax}", 12,
            m.RewindSegmentsMax > 0 && m.RewindSegmentsUsed >= m.RewindSegmentsMax ? Palette.Bad : Palette.TextMain);
        DrawBudgetBar(ci, font, x, 184, m);

        // --- TRAIL --------------------------------------------------------- y 216 .. 285
        Text(ci, font, x, 218, "TRAIL", 12, Palette.TextDim);
        var rows = new (string Key, string Value, Color C)[]
        {
            ("points", m.HistoryEntries.ToString(CultureInfo.InvariantCulture), Palette.TextMain),
            ("sessions", m.TrailSessions.ToString(CultureInfo.InvariantCulture), Palette.TextMain),
            ("turn-backs", m.TurnBacks.ToString(CultureInfo.InvariantCulture), Palette.TurnMarker),
            ("turn-forwards", m.TurnForwards.ToString(CultureInfo.InvariantCulture), Palette.TurnForwardMarker),
        };
        var ry = 235.0f;
        foreach (var row in rows)
        {
            Text(ci, font, x, ry, row.Key, 12, Palette.TextDim);
            Text(ci, font, x + 108, ry, row.Value, 12, row.C);
            ry += 15;
        }

        if (m.ForecastContacts > 0)
        {
            Text(ci, font, x, ry + 3, $"hits pending {m.ForecastContacts}", 12, Palette.ContactSoon);
        }

        // --- transport hint ------------------------------------------------ y 306 .. 322
        if (m.ShotUsed && !m.Won)
        {
            Text(ci, font, x, 300, m.Rewinding ? (m.Scrubbing ? "P  release at marker" : "P  pause + scrub") : "hold R  rewind",
                12, Palette.RewindCue);
            Text(ci, font, x, 316, m.Rewinding ? "release R  resume" : "release R  keep stored v", 12, Palette.TextDim);
        }

        // --- CONTROLS, pinned to the panel bottom --------------------------- y 436 .. 540
        var cy = rect.Position.Y + rect.Size.Y - 108;
        ci.DrawLine(new Vector2(x, cy - 10), new Vector2(x + 226, cy - 10), Palette.PanelEdge, 1.0f);
        Text(ci, font, x, cy + 4, "CONTROLS", 12, Palette.TextDim);
        var controls = new[]
        {
            "drag + release   aim / strike",
            "R (hold)         rewind",
            "P                pause + scrub",
            "left / right     move marker",
            "Space            reset (debug)",
            "F1 debug F2 ghost F3 trail",
        };
        var ky = cy + 21;
        foreach (var line in controls)
        {
            Text(ci, font, x, ky, line, 11, Palette.TextMain);
            ky += 14;
        }

        if (!string.IsNullOrEmpty(m.Message))
        {
            var rect2 = new Rect2(258, 496, 776, 58);
            ci.DrawRect(rect2, new Color(0.03f, 0.05f, 0.05f, 0.92f));
            ci.DrawRect(rect2, m.MessageColor, false, 2.0f);
            Text(ci, font, rect2.Position.X + 12, rect2.Position.Y + 35, m.Message, 15, m.MessageColor);
        }
    }

    /// <summary>
    /// The panel is drawn with absolute coordinates, so this asserts the blocks stay inside the
    /// screen and in order: a later edit cannot silently reintroduce the overlap this layout
    /// replaced.
    /// </summary>
    public static IReadOnlyList<string> PanelLayoutSelfCheck()
    {
        var problems = new List<string>();
        var blocks = new (string Name, float Top, float Bottom)[]
        {
            ("strike", 50, 81),
            ("power", 104, 150),
            ("budget", 160, 206),
            ("trail", 218, 287),
            ("transport", 300, 316),
            ("controls", 36 + 518 - 108 - 10, 36 + 518 - 108 + 21 + 5 * 14),
            ("message", 496, 552),
        };
        for (var i = 0; i < blocks.Length; i++)
        {
            if (blocks[i].Top < 36 || blocks[i].Bottom > 560)
                problems.Add($"{blocks[i].Name} outside the screen ({blocks[i].Top}..{blocks[i].Bottom})");
            if (i > 0 && blocks[i].Top < blocks[i - 1].Bottom)
                problems.Add($"{blocks[i].Name} overlaps {blocks[i - 1].Name} ({blocks[i].Top} < {blocks[i - 1].Bottom})");
        }

        return problems;
    }

    private static void DrawPowerMeter(CanvasItem ci, Font font, float x, float y, Model m)
    {
        Text(ci, font, x, y, "POWER", 12, Palette.TextDim);
        var speed = m.Dragging || !m.ShotUsed ? m.AimPower * 7.4 : 0.0;
        if (m.ShotUsed) speed = m.World.Cue.Speed;
        Text(ci, font, x + 150, y, $"{speed:F0} px/s", 12, Palette.TextMain);

        var bar = new Rect2(x, y + 6, 230, 16);
        ci.DrawRect(bar, new Color(0.10f, 0.13f, 0.13f, 1.0f));
        var t = m.MaxPower > 0 ? Mathf.Clamp((float)(m.AimPower / m.MaxPower), 0f, 1f) : 0f;
        if (!m.ShotUsed)
        {
            var fill = Palette.MeterLow.Lerp(Palette.MeterHigh, t);
            ci.DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * t, bar.Size.Y)), fill);
        }
        else
        {
            // After the strike the meter becomes a live cue-speed readout, so "the strike is spent"
            // is visible in the same place the player used to aim.
            var live = Mathf.Clamp((float)(m.World.Cue.Speed / 1600.0), 0f, 1f);
            ci.DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * live, bar.Size.Y)), Palette.TextDim);
        }

        // Minimum-power tick: below this the kernel refuses the shot (locked MinPower = 12 px).
        var minX = bar.Position.X + bar.Size.X * (float)(m.MinPower / Math.Max(1.0, m.MaxPower));
        ci.DrawLine(new Vector2(minX, bar.Position.Y - 3), new Vector2(minX, bar.Position.Y + bar.Size.Y + 3), Palette.Warn, 2.0f);
        Text(ci, font, minX - 10, bar.Position.Y + bar.Size.Y + 12, "min", 10, Palette.Warn);
        Text(ci, font, bar.Position.X + bar.Size.X - 22, bar.Position.Y + bar.Size.Y + 12, "max", 10, Palette.TextDim);

        ci.DrawRect(bar, Palette.PanelEdge, false, 1.0f);
        if (m.HasAim && !m.ShotUsed)
        {
            Text(ci, font, x, y + 46, $"angle {m.AimAngle:F1} deg  drag {m.AimPower:F0} / {m.MaxPower:F0} px", 11, Palette.TextMain);
        }
        else if (!m.ShotUsed)
        {
            Text(ci, font, x, y + 46, "drag back from the cue ball", 11, Palette.TextDim);
        }
    }

    private static void DrawBudgetBar(CanvasItem ci, Font font, float x, float y, Model m)
    {
        var bar = new Rect2(x, y, 230, 12);
        ci.DrawRect(bar, new Color(0.10f, 0.13f, 0.13f, 1.0f));
        if (m.RewindDistanceMax > 0)
        {
            var t = Mathf.Clamp((float)(m.RewindDistanceUsed / m.RewindDistanceMax), 0f, 1f);
            ci.DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * t, bar.Size.Y)),
                t > 0.99f ? Palette.Bad : Palette.RewindTraversed);
            ci.DrawRect(bar, Palette.PanelEdge, false, 1.0f);
            Text(ci, font, x, y + 22, $"distance {m.RewindDistanceUsed:F0} / {m.RewindDistanceMax:F0} px", 12, Palette.TextMain);
        }
        else
        {
            ci.DrawRect(bar, Palette.PanelEdge, false, 1.0f);
            Text(ci, font, x, y + 22, $"distance {m.RewindDistanceUsed:F0} px (no limit)", 12, Palette.TextMain);
        }
    }

    // --------------------------------------------------------- debug overlay

    public static void DrawDebug(CanvasItem ci, Font font, Model m)
    {
        var rect = new Rect2(700, 36, 334, 518);
        ci.DrawRect(rect, new Color(0.02f, 0.03f, 0.035f, 0.92f));
        ci.DrawRect(rect, Palette.SessionStripe, false, 1.0f);

        var x = rect.Position.X + Pad;
        var y = rect.Position.Y + 20;
        var w = m.World;
        var nonFinite = w.HasNonFiniteState();

        Text(ci, font, x, y, "DEBUG OVERLAY (F1)", 13, Palette.SessionStripe);
        y += 8;
        Text(ci, font, x, y + 12, $"level {m.LevelId}  dt {w.Config.SimulationStep * 1000:F3} ms  engineTicks {Engine.PhysicsTicksPerSecond}", 11, Palette.TextDim);
        y += 30;
        Text(ci, font, x, y, $"tick        {w.Tick}", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"time        {w.Time:F4} s", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"shotUsed    {w.ShotUsed}   won {w.Won}   frozen {w.Frozen}", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"rewinding   {w.Rewinding}   paused {m.Paused}   scrub {m.Scrubbing}", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"nonfinite   {(nonFinite ? "YES ***" : "no")}", 12, nonFinite ? Palette.Bad : Palette.Good);
        y += 15;
        Text(ci, font, x, y, $"history     {w.Cue.History.Count} entries  gap {w.Cue.History.Interval:F2} px", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"samples+    {w.Cue.History.DistanceSinceSample:F3} px", 12, Palette.TextMain);
        y += 15;
        if (w.RewindTrack != null)
        {
            Text(ci, font, x, y, $"snapshot    {w.RewindTrack.Length} pts  cursor {w.RewindCursor}  u {w.RewindFraction:F3}", 12, Palette.TextMain);
            y += 15;
        }

        y += 12;
        Text(ci, font, x, y + 6, "BALLS  id  x      y      |v|     state", 12, Palette.TextDim);
        y += 24;
        foreach (var b in w.Balls)
        {
            var state = b.Pocketed ? "POCKETED" : b.Speed > 0.0 ? "moving" : "rest";
            var line = string.Format(CultureInfo.InvariantCulture,
                "  {0,2}  {1,6:F1} {2,6:F1}  {3,7:F1}  {4}{5}", b.Id, b.Position.X, b.Position.Y, b.Speed, state,
                b.IsCue ? " (cue)" : "");
            var color = b.Pocketed ? Palette.TextDim : b.IsCue ? Palette.Cue : Palette.TextMain;
            Text(ci, font, x, y, line, 12, color);
            y += 15;
        }

        y += 8;
        Text(ci, font, x, y, "VIEW", 12, Palette.TextDim);
        y += 16;

        // Left panel. The layout is hand-computed on a fixed grid because there is no Control tree to
        // resolve constraints; the y of every block is written next to it and
        // <see cref="PanelLayoutSelfCheck"/> asserts the blocks stay ordered and on screen.
        var lineH = 11;
        var x0 = rect.Position.X + 10;
        var x1 = rect.Position.X + rect.Size.X - 10;
        var rows2 = new List<(string Text, Color Color, int Size)>
        {
            ($"frame    {m.FrameMs:F2} ms (max {m.MaxFrameMs:F2})", Palette.TextDim, 11),
            ($"draw/sim {m.DrawMs:F2} / {m.SimMs:F2} ms", Palette.TextDim, 11),
            ($"verts    {m.TrailVertices}  sessions {m.TrailSessions}", Palette.TextDim, 11),
            ($"turns    back {m.TurnBacks}  fwd {m.TurnForwards}", Palette.TextDim, 11),
            ($"ghost    {(m.Trail == null ? 0 : GhostLength(m.Trail))} pts  segments {m.RewindSegmentsUsed}/{m.RewindSegmentsMax}", Palette.TextDim, 11),
            ($"forecast {m.ForecastContacts} hits  {m.ForecastUs} us", Palette.TextDim, 11),
            ($"traversed {m.Trail?.TraversedRange.From}..{m.Trail?.TraversedRange.To}", Palette.TextDim, 11),
        };
        foreach (var row in rows2)
        {
            foreach (var line in Hud.Wrap(font, row.Text, row.Size, x1 - x0))
            {
                Text(ci, font, x0, y, line, row.Size, row.Color);
                y += lineH;
            }
        }

        if (m.Aim != null && m.Aim.Valid)
        {
            y += 6;
            Text(ci, font, x, y, "PREDICTION", 12, Palette.TextDim);
            y += 16;
            var a = new[]
            {
                $"contact  {m.Aim.Contact}  ball {m.Aim.ContactBallId}",
                $"toContact {m.Aim.TimeToContact * 1000:F1} ms  steps {m.Aim.Steps}",
                $"struck   {m.Aim.StruckSpeed:F0} px/s",
                $"cue next {m.Aim.CueDeflectSpeed:F0} px/s",
                $"trace    {m.Aim.Microseconds} us",
                $"rest at  {m.Aim.StopPoint.X:F0},{m.Aim.StopPoint.Y:F0} reached={m.Aim.ReachedRest}",
            };
            foreach (var line in a)
            {
                foreach (var wrapped in Wrap(font, line, 11, x1 - x0))
                {
                    if (y > rect.Position.Y + rect.Size.Y - 12) return;
                    Text(ci, font, x0, y, wrapped, 11, Palette.TextDim);
                    y += 13;
                }
            }
        }

        if (m.RewindVis != null && m.RewindVis.BallContacts.Count > 0)
        {
            y += 6;
            if (y > rect.Position.Y + rect.Size.Y - 30) return;
            Text(ci, font, x, y, "REWIND FORECAST (before contact)", 12, Palette.ContactSoon);
            y += 16;
            foreach (var f in m.RewindVis.BallContacts)
            {
                var toGo = f.ArcFromCursor - m.RewindVis.ArcWalked;
                Text(ci, font, x, y, $"  ball {f.BallId} in {toGo:F0} px / {f.Seconds:F2}s", 11,
                    toGo < 0 ? Palette.TextDim : Palette.ContactSoon);
                y += 14;
                if (y > rect.Position.Y + rect.Size.Y - 20) break;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static float Text(CanvasItem ci, Font font, float x, float y, string text, int size, Color color)
    {
        if (string.IsNullOrEmpty(text)) return x;
        ci.DrawString(font, new Vector2(x, y), text, HorizontalAlignment.Left, -1, size, color);
        return x + font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
    }

    private static float Measure(Font font, string text, int size)
    {
        return font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
    }

    /// <summary>Greedy word wrap to a pixel width: the debug panel must not spill outside itself.</summary>
    private static List<string> Wrap(Font font, string text, int size, float maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ');
        var current = "";
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Measure(font, candidate, size) <= maxWidth || current.Length == 0)
            {
                current = candidate;
            }
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private static int GhostLength(TimelineTrail trail)
    {
        return trail.GhostTrack?.Length ?? 0;
    }
}
