using Godot;
using Rewind.Game;

namespace Rewind.Tests;

/// <summary>
/// The on-screen transport (TouchControls): geometry, hit testing and availability.
///
/// WHY THESE ARE TESTS AND NOT A SCREENSHOT: two real layout collisions were found here by MEASURING,
/// and both were invisible to the eye in a rendered frame.
///   1. The first grid was 2 cols x 3 rows starting at y 424. Three rows need 144px but the free strip
///      is 86px tall, so the third row landed at y 524..568 -- past the 560px canvas edge AND over the
///      bottom replay band (y >= 546). The bottom half of "重置"/"选关" was simply not drawn.
///   2. The second was 3 cols x 2 rows: 3x104 + 2x8 = 328px against a 246px panel, so the third column
///      sat over the table and the notification toast.
/// A render of either layout looks plausible at a glance. Arithmetic does not.
///
/// The layout also has to obey the SAME rect for draw and hit test -- the bug class this project has hit
/// twice (menu text under a button, a hint bubble clipped by the playfield). Rect() is the single source,
/// and T02 asserts the hit test agrees with it, including on the borders.
/// </summary>
public static class TouchTests
{
    private static Rect2 R(TouchControls.Action a) => TouchControls.Rect(a);

    /// <summary>T01: the panel is the only block that has room, and the whole grid must fit inside it.</summary>
    public static void T01_GridFitsThePanel(TestRunner t)
    {
        t.Section("T01 - the button grid fits inside the left panel and inside the canvas");

        // The left panel, from Hud.DrawLeftPanel: new Rect2(6, 34, 246, 476).
        var panel = new Rect2(6, 34, 246, 476);
        // The replay band occupies y >= 546, and the canvas ends at 560. Both are hard limits.
        const float bandTop = 546.0f;
        const float canvasH = 560.0f;

        foreach (var a in System.Enum.GetValues<TouchControls.Action>())
        {
            var r = R(a);
            t.Check($"{a}: inside the panel horizontally",
                r.Position.X >= panel.Position.X && r.Position.X + r.Size.X <= panel.Position.X + panel.Size.X,
                $"x {r.Position.X}..{r.Position.X + r.Size.X} vs panel {panel.Position.X}..{panel.Position.X + panel.Size.X}");
            t.Check($"{a}: inside the panel vertically",
                r.Position.Y >= panel.Position.Y && r.Position.Y + r.Size.Y <= panel.Position.Y + panel.Size.Y,
                $"y {r.Position.Y}..{r.Position.Y + r.Size.Y} vs panel {panel.Position.Y}..{panel.Position.Y + panel.Size.Y}");
            t.Check($"{a}: clears the bottom replay band",
                r.Position.Y + r.Size.Y <= bandTop,
                $"bottom {r.Position.Y + r.Size.Y} vs band top {bandTop}");
            t.Check($"{a}: inside the canvas",
                r.Position.Y + r.Size.Y <= canvasH,
                $"bottom {r.Position.Y + r.Size.Y} vs canvas {canvasH}");
        }
    }

    /// <summary>T02: the rect the player sees is the rect the hit test uses, borders included.</summary>
    public static void T02_HitTestMatchesTheDrawnRect(TestRunner t)
    {
        t.Section("T02 - the hit test agrees with the drawn rect");

        foreach (var a in System.Enum.GetValues<TouchControls.Action>())
        {
            var r = R(a);
            var centre = r.Position + r.Size * 0.5f;
            t.Check($"{a}: the centre hits", TouchControls.Hit(centre) == a, "got " + TouchControls.Hit(centre));

            // Just inside each edge: a touch near the border of a button must still register, because a
            // fingertip is imprecise and the rect is already the minimum comfortable size.
            var inset = 1.0f;
            foreach (var (label, p) in new (string, Vector2)[]
                     {
                         ("left", new Vector2(r.Position.X + inset, centre.Y)),
                         ("right", new Vector2(r.Position.X + r.Size.X - inset, centre.Y)),
                         ("top", new Vector2(centre.X, r.Position.Y + inset)),
                         ("bottom", new Vector2(centre.X, r.Position.Y + r.Size.Y - inset)),
                     })
            {
                t.Check($"{a}: hits just inside the {label} edge",
                    TouchControls.Hit(p) == a, $"at {p} got {TouchControls.Hit(p)}");
            }
        }

        // A point on the table must hit NOTHING, or a tap meant for aiming would press a button.
        t.Check("a point on the table hits no button",
            TouchControls.Hit(new Vector2(520.0f, 280.0f)) == null,
            "the table centre returned " + TouchControls.Hit(new Vector2(520.0f, 280.0f)));
    }

    /// <summary>T03: no two buttons overlap, so Hit() cannot be ambiguous.</summary>
    public static void T03_ButtonsDoNotOverlap(TestRunner t)
    {
        t.Section("T03 - buttons never overlap each other");

        var all = System.Enum.GetValues<TouchControls.Action>();
        for (var i = 0; i < all.Length; i++)
        {
            for (var j = i + 1; j < all.Length; j++)
            {
                var a = R(all[i]);
                var b = R(all[j]);
                var disjoint = a.Position.X + a.Size.X <= b.Position.X
                               || b.Position.X + b.Size.X <= a.Position.X
                               || a.Position.Y + a.Size.Y <= b.Position.Y
                               || b.Position.Y + b.Size.Y <= a.Position.Y;
                t.Check($"{all[i]} and {all[j]} do not overlap", disjoint, $"{a} vs {b}");
            }
        }
    }

    /// <summary>
    /// T04: which actions are available in which state.
    ///
    /// This is the part a screenshot cannot check at all: an unavailable button is drawn DIMMED, so it
    /// looks like a button either way. The rule that matters most is that rewind requires the shot to
    /// have been taken -- offering it before the strike would let a player press a control whose only
    /// possible outcome is the "no history to rewind" error.
    /// </summary>
    public static void T04_AvailabilityFollowsState(TestRunner t)
    {
        t.Section("T04 - availability follows the game state");

        // Before the shot: rewind is not yet meaningful, reset and back are.
        t.Check("before the strike, rewind is unavailable",
            !TouchControls.Enabled(TouchControls.Action.Rewind, false, false, false, false, false));
        t.Check("before the strike, pause is unavailable (nothing to pause)",
            !TouchControls.Enabled(TouchControls.Action.PauseScrub, false, false, false, false, false));
        t.Check("reset is available at rest",
            TouchControls.Enabled(TouchControls.Action.Reset, false, false, false, false, false));
        t.Check("back is always available",
            TouchControls.Enabled(TouchControls.Action.Back, false, false, false, false, false)
            && TouchControls.Enabled(TouchControls.Action.Back, true, true, true, true, true));

        // After the strike: rewind becomes the primary action.
        t.Check("after the strike, rewind is available",
            TouchControls.Enabled(TouchControls.Action.Rewind, true, false, false, false, false));
        t.Check("while rewinding, pause is available",
            TouchControls.Enabled(TouchControls.Action.PauseScrub, true, true, false, false, false));
        t.Check("while rewinding, reset is refused (it would strand the session)",
            !TouchControls.Enabled(TouchControls.Action.Reset, true, true, false, false, false));

        // A won level: the shot is used and the level is over, so rewind must not invite a no-op.
        t.Check("after winning, rewind is unavailable",
            !TouchControls.Enabled(TouchControls.Action.Rewind, true, false, false, true, false));

        // A lost run still needs reset and back, which is how the player recovers from a failure.
        t.Check("after losing, reset is still available",
            TouchControls.Enabled(TouchControls.Action.Reset, true, false, false, false, true));
        t.Check("after losing, back is still available",
            TouchControls.Enabled(TouchControls.Action.Back, true, false, false, false, true));
    }

    /// <summary>
    /// T05: every action has a visible label.
    ///
    /// A filled rectangle with no text is an unusable control, and the label lookup is a switch that
    /// returns a default empty string -- so a newly added action would silently render as a blank box.
    /// This is the check that catches it.
    /// </summary>
    public static void T05_EveryActionIsLabelled(TestRunner t)
    {
        t.Section("T05 - every button carries a label");

        foreach (var a in System.Enum.GetValues<TouchControls.Action>())
        {
            var label = TouchControls.Label(a);
            t.Check($"{a} has a non-empty label", !string.IsNullOrEmpty(label), $"got '{(label ?? "null")}'");
            // A CJK label must not be so long that it overflows its 104px button at 15px type. 6 chars
            // is a generous ceiling for this face and leaves margin inside the rect.
            t.Check($"{a} label is short enough for the button", label != null && label.Length <= 6,
                $"'{label}' is {label?.Length} chars");
        }
    }
}
