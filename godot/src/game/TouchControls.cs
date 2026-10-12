using Godot;

namespace Rewind.Game;

/// <summary>
/// On-screen controls for touch devices: the actions that have no touch equivalent.
///
/// WHY THIS EXISTS AT ALL: Godot's emulate_mouse_from_touch makes the touchscreen drive the mouse
/// handlers, so aiming (drag back from the cue ball) and picking a card (tap it) already work on a
/// phone with no extra code. What does NOT work is anything bound to a KEY. The whole transport --
/// rewind (R), pause/scrub (P), reset (Space), back to the level select (Esc) and the micro-adjust
/// nudge (left/right) -- is keyboard-only, so on a phone the player could aim and shoot but never
/// rewind, which is the entire game.
///
/// WHY BUTTONS RATHER THAN GESTURES: R is press-AND-hold with a meaningful release (release rewinds
/// the shot, and a second tap inside the same 10 px cluster arms the death-throe boost), so the
/// control has to expose both a press and a release at a specific moment. A swipe or a tap-to-toggle
/// gesture would have to reinvent that timing and would make "hold to rewind" ambiguous with "drag to
/// aim", which are adjacent on screen. A held button maps to press and release exactly.
///
/// WHY THE RECTS ARE HERE AND NOT IN Hud: the draw and the hit test must read the SAME numbers. That
/// is the bug class this project has hit twice (menu text running under a button, and a hint bubble
/// clipped by the playfield). One function returns the rect, the drawer and the hit test both use it.
///
/// LAYOUT: the left panel occupies x 6..252 and the top bar y 0..28. The table is x 262..990,
/// y 62..498. The bottom strip y 500..544 is free of the table but is where the notification toast
/// draws (Hud.cs puts it at 262,494 700x50), and y 546..560 is the bottom replay band. The buttons
/// therefore sit in the LEFT PANEL's own area, stacked over the controls legend at the very bottom of
/// the panel (y 428..520) -- which is exactly where a player looks for them, and where they cannot
/// cover the table, the toast, or the band.
/// </summary>
public static class TouchControls
{
    /// <summary>Design canvas, same units as every other UI rect in this project.</summary>
    private const float CanvasH = 560.0f;

    /// <summary>A button big enough for a fingertip. Android's own guidance is 48 dp; the panel is the
    /// constraint here, so the grid is sized to fit the space the keyboard legend already occupies
    /// rather than to a nominal minimum. 104x40 is still a large target -- comparable to a card.</summary>
    private const float W = 104.0f;
    private const float H = 40.0f;
    private const float GapX = 8.0f;
    private const float GapY = 8.0f;

    /// <summary>Left edge of the button grid: inside the panel, which spans x 6..252.</summary>
    private const float Left = 14.0f;

    /// <summary>
    /// Top edge of the grid. This is the block Hud.cs already reserves for the CONTROLS legend
    /// (it starts at panel-bottom minus 108, i.e. y ~402, and runs to ~500).
    ///
    /// WHY THAT BLOCK, AND NOT A NEW STRIP: two earlier layouts were measured and both collided.
    ///   * 2 cols x 3 rows at y 424 needed 144px of a strip only 86px tall, so the third row landed at
    ///     y 524..568 -- past the 560px canvas edge AND over the bottom replay band (y>=546).
    ///   * 3 cols x 2 rows was too WIDE: 3x104 + 2x8 = 328px against a 246px panel, so the third column
    ///     sat over the table and the notification toast.
    /// The legend block fits six buttons' worth of space and, on a touch device, is showing text that
    /// does not apply -- "按 R" and "F1" are keys a phone does not have. So the block is shared: the
    /// keyboard legend on desktop, the buttons on touch. Nothing is displaced and nothing is hidden.
    /// </summary>
    private const float Top = 414.0f;

    /// <summary>Columns in the grid. Two: the panel is 246px wide and each button is 104, so three
    /// columns would overflow into the table.</summary>
    private const int Columns = 2;

    /// <summary>The actions, in draw order.</summary>
    public enum Action
    {
        Rewind,
        PauseScrub,
        Reset,
        Back,
    }

    private static readonly Action[] Order =
    {
        Action.Rewind, Action.PauseScrub,
        Action.Reset, Action.Back,
    };

    /// <summary>Where a button's rect is. The single source of truth for both draw and hit test.</summary>
    public static Rect2 Rect(Action a)
    {
        var i = 0;
        for (var k = 0; k < Order.Length; k++)
        {
            if (Order[k] == a) { i = k; break; }
        }

        var col = i % Columns;
        var row = i / Columns;
        return new Rect2(Left + col * (W + GapX), Top + row * (H + GapY), W, H);
    }

    /// <summary>Which button contains <paramref name="point"/>, or null. Hit-tested in the order the
    /// buttons are drawn, which does not matter because the rects never overlap.</summary>
    public static Action? Hit(Vector2 point)
    {
        foreach (var a in Order)
        {
            if (Rect(a).HasPoint(point)) return a;
        }

        return null;
    }

    public static string Label(Action a)
    {
        switch (a)
        {
            case Action.Rewind: return "回溯";
            case Action.PauseScrub: return "暂停";
            case Action.Reset: return "重置";
            case Action.Back: return "选关";
            default: return "";
        }
    }

    /// <summary>
    /// A button is only tappable when the action is actually available. Drawing unavailable actions
    /// dimmed -- rather than hiding them -- keeps the layout from moving under the player's thumb,
    /// and the reason is readable from the same state the panel already shows.
    ///
    /// There is deliberately NO nudge-left / nudge-right button. On a keyboard the arrow keys exist
    /// because a keyboard has no way to point at the path; on touch the player simply DRAGS the release
    /// marker, which is already wired up (Play.ScrubTo, reached through emulate_mouse_from_touch). Adding
    /// arrow buttons would duplicate an interaction the touch player gets for free, at the cost of two
    /// more targets crowding the panel.
    /// </summary>
    public static bool Enabled(Action a, bool shotUsed, bool rewinding, bool scrubbing, bool won, bool lost)
    {
        switch (a)
        {
            case Action.Rewind:
                // Rewind needs a history to walk back through, i.e. the shot must have been taken.
                return shotUsed && !won;
            case Action.PauseScrub:
                return rewinding;
            case Action.Reset:
                return !rewinding;
            case Action.Back:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Draw the grid. <paramref name="held"/> is the action currently under a finger, so it can be
    /// shown pressed: without that feedback a hold-to-rewind button looks identical whether or not the
    /// game has registered the press, and the whole control is a guess.
    /// </summary>
    public static void Draw(CanvasItem ci, Font font, Action? held,
        bool shotUsed, bool rewinding, bool won, bool lost)
    {
        foreach (var a in Order)
        {
            var r = Rect(a);
            var on = Enabled(a, shotUsed, rewinding, false, won, lost);
            var pressed = held == a;

            var body = !on ? Palette.Panel
                : pressed ? Palette.Accent
                : Palette.PanelRaised;
            ci.DrawRect(r, body);

            // The border carries the state without relying on the fill alone: a pressed button must be
            // distinguishable from a merely-enabled one even in a screenshot or on a dim screen.
            var edge = !on ? Palette.PanelEdge
                : pressed ? Palette.TextMain
                : Palette.Accent;
            ci.DrawRect(r, edge, false, pressed ? 3.0f : 1.5f);

            var label = Label(a);
            var color = on ? Palette.TextMain : Palette.TextDim;
            var size = 15;
            var w = font.GetStringSize(label, HorizontalAlignment.Left, -1, size).X;
            ci.DrawString(font, new Vector2(r.Position.X + (r.Size.X - w) * 0.5f,
                r.Position.Y + r.Size.Y * 0.5f + 5.0f),
                label, HorizontalAlignment.Left, -1, size, color);
        }

        // A one-line reminder of the touch equivalent of holding R, because the button alone does not
        // convey that RELEASING it is what commits the rewind.
        var hint = held == Action.Rewind ? "松开 = 保留当时速度" : "按住回溯，松开保留速度";
        ci.DrawString(font, new Vector2(Left, Top - 8.0f), hint, HorizontalAlignment.Left, -1, 11,
            Palette.TextDim);
    }

    /// <summary>
    /// Whether the on-screen controls should be shown at all.
    ///
    /// Decided by an actual touchscreen being present, NOT by the platform name. A desktop with a
    /// touch monitor would benefit, a phone always reports one, and -- importantly -- a device that
    /// reports none is not spammed with buttons for controls its keyboard already covers.
    /// </summary>
    public static bool ShouldShow()
    {
        if (DisplayServer.IsTouchscreenAvailable()) return true;

        // Explicit override, so the touch layout can be captured and reviewed on a desktop for
        // evidence and layout checks without a touchscreen. It also makes the feature testable.
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a == "--touch-controls") return true;
        }

        return false;
    }
}
