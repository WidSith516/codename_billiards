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
    private const float CanvasW = 1040.0f;
    private const float CanvasH = 560.0f;

    /// <summary>A button big enough for a fingertip. Android's guidance is 48 dp; these sit over the
    /// felt at the bottom right, where two 132x54 targets fit inside the table's lower-right corner
    /// without reaching the middle pocket.</summary>
    private const float W = 132.0f;
    private const float H = 54.0f;
    private const float GapX = 10.0f;
    private const float GapY = 10.0f;
    private const int Columns = 2;

    /// <summary>Margin from the canvas edges.</summary>
    private const float MarginRight = 12.0f;
    private const float MarginBottom = 46.0f;

    /// <summary>
    /// The grid hugs the BOTTOM-RIGHT CORNER, as asked.
    ///
    /// WHY THIS MOVED OFF THE SIDE PANEL: the first version put the four buttons inside the left panel,
    /// because that block was free and nothing on the table was covered. The player asked for the
    /// conventional mobile arrangement instead -- where a thumb naturally rests -- so the buttons now
    /// overlap the felt at the lower right. Two measured layouts were rejected along the way:
    ///   * 2 cols x 3 rows at y 424 overflowed the canvas (the third row landed at y 524..568) and lay
    ///     over the bottom replay band;
    ///   * 3 cols x 2 rows was too wide for the 246px panel and spilled onto the table anyway.
    ///
    /// WHAT THE OVERLAP COSTS: buttons drawn over the felt can hide a ball resting in the lower-right
    /// corner. That is accepted because the table's lower-right corner is the least useful place to
    /// leave the cue (the pockets that finish a level are on the bottom rail but the corner is behind
    /// them), and because the buttons are translucent so a ball is still partly visible through them.
    /// The bottom edge stops at y = 514, above the bottom replay band at y >= 546.
    /// The notification toast is kept clear by Hud moving it up when the touch controls are shown.
    /// </summary>
    private static float GridLeft => CanvasW - MarginRight - (Columns * W + (Columns - 1) * GapX);
    private static float GridTop => CanvasH - MarginBottom - (2 * H + GapY);

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

    /// <summary>The collapse toggle: hides the four buttons so the whole table is visible.
    ///
    /// WHY A TOGGLE AND NOT A SWIPE: the player asked for "可以缩放面板" (a collapsible panel) for the
    /// mobile build. The literal request was about zooming the panel, and what the panel actually needs
    /// on a phone is to get OUT OF THE WAY -- the table is the game, the readouts are reference. A tap
    /// target is also unambiguous next to a drag-to-aim surface, whereas a swipe or pinch would have to
    /// be told apart from aiming, which is the same gesture.
    ///
    /// Deliberately NOT routed through Action/Enabled: it is always available, it acts on the view
    /// rather than on the kernel, and folding it into the same availability logic would let a state
    /// (e.g. a pending card draw, a lost run) trap the controls on screen with no way to dismiss them.
    /// </summary>
    public static Rect2 CollapseToggleRect()
        => new Rect2(CanvasW - MarginRight - 34.0f, GridTop - 44.0f, 34.0f, 34.0f);

    /// <summary>Where a button's rect is, or an empty rect when the grid is collapsed (so the hit test
    /// and the draw cannot disagree about a hidden button).
    ///
    /// The single source of truth for both draw and hit test.</summary>
    public static Rect2 Rect(Action a, bool collapsed = false)
    {
        if (collapsed) return new Rect2();

        var i = 0;
        for (var k = 0; k < Order.Length; k++)
        {
            if (Order[k] == a) { i = k; break; }
        }

        var col = i % Columns;
        var row = i / Columns;
        return new Rect2(GridLeft + col * (W + GapX), GridTop + row * (H + GapY), W, H);
    }

    /// <summary>Which button contains <paramref name="point"/>, or null. Returns null for every point
    /// when collapsed, which is what makes a collapsed control genuinely non-interactive rather than
    /// merely invisible.</summary>
    public static Action? Hit(Vector2 point, bool collapsed = false)
    {
        if (collapsed) return null;
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
    ///
    /// TRANSLUCENT, because these now sit OVER the felt: a fully opaque button would hide a ball behind
    /// it, and a ball the player cannot see is a shot they cannot plan. Alpha is low enough to read the
    /// cloth through, high enough that the label stays legible over both the dark felt and the bright
    /// cushion.
    /// </summary>
    public static void Draw(CanvasItem ci, Font font, Action? held, bool collapsed,
        bool shotUsed, bool rewinding, bool won, bool lost)
    {
        DrawCollapseToggle(ci, font, collapsed);
        if (collapsed) return;

        foreach (var a in Order)
        {
            var r = Rect(a);
            var on = Enabled(a, shotUsed, rewinding, false, won, lost);
            var pressed = held == a;

            var body = !on ? BodyDisabled
                : pressed ? BodyPressed
                : BodyNormal;
            ci.DrawRect(r, body);

            // The border carries the state without relying on the fill alone: a pressed button must be
            // distinguishable from a merely-enabled one even in a screenshot or on a dim screen, and
            // the fill is deliberately faint so the felt shows through.
            var edge = !on ? EdgeDisabled
                : pressed ? Palette.TextMain
                : Palette.Accent;
            ci.DrawRect(r, edge, false, pressed ? 3.0f : 1.5f);

            var label = Label(a);
            var color = on ? Palette.TextMain : Palette.TextDim;
            var size = 16;
            var w = font.GetStringSize(label, HorizontalAlignment.Left, -1, size).X;
            // A dark shadow under the text, because the label can sit over bright felt or a light ball
            // and a thin CJK glyph would otherwise vanish against it.
            ci.DrawString(font, new Vector2(r.Position.X + (r.Size.X - w) * 0.5f + 1.0f,
                r.Position.Y + r.Size.Y * 0.5f + 6.0f),
                label, HorizontalAlignment.Left, -1, size, TextShadow);
            ci.DrawString(font, new Vector2(r.Position.X + (r.Size.X - w) * 0.5f,
                r.Position.Y + r.Size.Y * 0.5f + 5.0f),
                label, HorizontalAlignment.Left, -1, size, color);
        }
    }

    // Translucent bodies. Kept as named colours rather than inline literals so the "how see-through is
    // the felt" decision is one number to tune, in one place, per state.
    private static readonly Color BodyNormal = new Color(0.07f, 0.12f, 0.13f, 0.55f);
    private static readonly Color BodyPressed = new Color(1.0f, 0.82f, 0.40f, 0.72f);
    private static readonly Color BodyDisabled = new Color(0.05f, 0.08f, 0.09f, 0.40f);
    private static readonly Color EdgeDisabled = new Color(0.23f, 0.29f, 0.27f, 0.55f);
    private static readonly Color TextShadow = new Color(0.0f, 0.0f, 0.0f, 0.55f);

    /// <summary>
    /// The small always-available tab that hides or shows the four buttons.
    ///
    /// Drawn in both states: collapsed, it is the ONLY way back, so it can never be conditional on the
    /// state that the buttons themselves are conditional on -- otherwise a pending card draw (which
    /// suppresses the buttons) would leave the player with no controls and no way to recall them.
    /// </summary>
    private static void DrawCollapseToggle(CanvasItem ci, Font font, bool collapsed)
    {
        var r = CollapseToggleRect();
        ci.DrawRect(r, collapsed ? BodyPressed : BodyNormal);
        ci.DrawRect(r, Palette.Accent, false, 1.5f);

        // A chevron pointing the way the panel will move. Drawn as two SEPARATE line segments rather than
        // one three-point polyline, because a polyline through (left, right) with a shared apex forms a
        // zero-area triangle and Godot rejects it ("Invalid polygon data, triangulation failed"), which
        // aborted the whole draw and silently cost the capture its PNG.
        var c = r.Position + r.Size * 0.5f;
        var d = 5.0f;
        var h = 7.0f;
        if (collapsed)
        {
            // Collapsed: the tab points up, meaning "the controls come back up out of this corner".
            var apex = c + new Vector2(0, -h * 0.6f);
            ci.DrawLine(c + new Vector2(-d, h * 0.4f), apex, Palette.TextMain, 2.5f, true);
            ci.DrawLine(c + new Vector2(d, h * 0.4f), apex, Palette.TextMain, 2.5f, true);
        }
        else
        {
            var apex = c + new Vector2(0, h * 0.4f);
            ci.DrawLine(c + new Vector2(-d, -h * 0.6f), apex, Palette.TextMain, 2.5f, true);
            ci.DrawLine(c + new Vector2(d, -h * 0.6f), apex, Palette.TextMain, 2.5f, true);
        }
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
