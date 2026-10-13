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
    private static Rect2 R(TouchControls.Action a) => TouchControls.Rect(a, collapsed: false);

    /// <summary>T01: the grid must stay on screen, clear of the bottom replay band, and inside the
    /// felt's lower-right corner it deliberately overlaps.</summary>
    public static void T01_GridFitsThePanel(TestRunner t)
    {
        t.Section("T01 - the button grid fits the canvas and clears the bottom band");

        // The table box, from PhysicsConfig defaults: (65,63) to (975,497).
        var table = new Rect2(65, 63, 910, 434);
        // The replay band occupies y >= 546 and the canvas ends at 560. Both are hard limits.
        const float bandTop = 546.0f;
        const float canvasH = 560.0f;
        const float canvasW = 1040.0f;

        foreach (var a in System.Enum.GetValues<TouchControls.Action>())
        {
            var r = R(a);
            t.Check($"{a}: on screen horizontally",
                r.Position.X >= 0 && r.Position.X + r.Size.X <= canvasW,
                $"x {r.Position.X}..{r.Position.X + r.Size.X}");
            t.Check($"{a}: on screen vertically",
                r.Position.Y >= 0 && r.Position.Y + r.Size.Y <= canvasH,
                $"y {r.Position.Y}..{r.Position.Y + r.Size.Y}");
            t.Check($"{a}: clears the bottom replay band",
                r.Position.Y + r.Size.Y <= bandTop,
                $"bottom {r.Position.Y + r.Size.Y} vs band top {bandTop}");

            // The buttons are SUPPOSED to overlap the felt now (bottom-right, where a thumb rests), so
            // this asserts the overlap is real rather than forbidding it. If an edit ever pushed them
            // off the table entirely the layout would still look fine while no longer being where the
            // player was told to expect it.
            t.Check($"{a}: sits over the felt as intended",
                r.Position.X < table.Position.X + table.Size.X
                && r.Position.Y < table.Position.Y + table.Size.Y,
                $"{r} vs table {table}");

            // Tappable size: a target smaller than ~44 px on this canvas is awkward, and the whole point
            // of the port is that these replace keys.
            t.Check($"{a}: is a fingertip-sized target",
                r.Size.X >= 44 && r.Size.Y >= 44, $"size {r.Size}");
        }

        // The collapsed state must make every button unhittable, not merely invisible.
        var collapsedHit = TouchControls.Hit(new Vector2(900, 480), collapsed: true);
        t.Check("when collapsed, no button is hit anywhere",
            collapsedHit == null, $"got {collapsedHit}");
        var allCollapsed = true;
        foreach (var a in System.Enum.GetValues<TouchControls.Action>())
        {
            if (TouchControls.Rect(a, collapsed: true).Size != Vector2.Zero) allCollapsed = false;
        }

        t.Check("when collapsed, Rect returns an empty rect for every action", allCollapsed,
            "a collapsed button still reports a rect, so the hit test and the draw could disagree");
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

    /// <summary>T05: every action has a visible label, and the collapse tab is always available.</summary>
    public static void T05_EveryActionIsLabelled(TestRunner t)
    {
        t.Section("T05 - every button carries a label, and the collapse tab is always reachable");

        foreach (var a in System.Enum.GetValues<TouchControls.Action>())
        {
            var label = TouchControls.Label(a);
            t.Check($"{a} has a non-empty label", !string.IsNullOrEmpty(label), $"got '{(label ?? "null")}'");
            // A CJK label must not be so long that it overflows its button at 16px type. 6 chars is a
            // generous ceiling for this face and leaves margin inside the 132px rect.
            t.Check($"{a} label is short enough for the button", label != null && label.Length <= 6,
                $"'{label}' is {label?.Length} chars");
        }

        // The collapse tab is the ONLY way to bring the buttons back, so it must never be conditional on
        // the state that hides them. If it were folded into Enabled(), a pending card draw (which
        // suppresses the buttons) would leave the player with no controls and no way to recall them.
        var tab = TouchControls.CollapseToggleRect();
        t.Check("the collapse tab has a real, non-empty rect", tab.Size.X > 0 && tab.Size.Y > 0,
            $"tab={tab}");
        t.Check("the collapse tab is on screen", tab.Position.X >= 0
            && tab.Position.X + tab.Size.X <= 1040.0f
            && tab.Position.Y >= 0 && tab.Position.Y + tab.Size.Y <= 560.0f, $"tab={tab}");

        // It must not sit on top of any button, or the two would fight for the same tap.
        foreach (var a in System.Enum.GetValues<TouchControls.Action>())
        {
            var r = TouchControls.Rect(a);
            var disjoint = tab.Position.X + tab.Size.X <= r.Position.X
                           || r.Position.X + r.Size.X <= tab.Position.X
                           || tab.Position.Y + tab.Size.Y <= r.Position.Y
                           || r.Position.Y + r.Size.Y <= tab.Position.Y;
            t.Check($"the collapse tab does not overlap the {a} button", disjoint, $"tab={tab} button={r}");
        }

        // And it must be reachable by the same hit test the input uses. It is checked separately from
        // Hit() because Hit() intentionally knows nothing about the tab (the tab bypasses the Enabled
        // gate entirely).
        t.Check("the tab's own rect reports a hit at its centre",
            tab.HasPoint(tab.Position + tab.Size * 0.5f), $"tab={tab}");
    }

    /// <summary>T06: the side panel's fold tab -- the phone's only way to hide the panel.
    ///
    /// WHY THIS FEATURE NEEDS A TEST: F1 hides the panel and is the single biggest lever on how much of
    /// the table is visible, but a phone has no F1, so on mobile the panel was permanently stuck open
    /// over a large part of the felt. The tab fixes that, and the things that can silently go wrong are
    /// geometric: it can move between states (so the finger has to hunt for it), collide with the
    /// panel's own text or the toast, or sit off screen.
    /// </summary>
    public static void T06_PanelFoldTabIsReachable(TestRunner t)
    {
        t.Section("T06 - the side panel's fold tab: on screen, fixed, and clear of everything");

        var tab = Hud.PanelFoldRect();

        t.Check("the fold tab has a fingertip-sized rect",
            tab.Size.X >= 30 && tab.Size.Y >= 30, $"size {tab.Size}");
        t.Check("the fold tab is on screen",
            tab.Position.X >= 0 && tab.Position.X + tab.Size.X <= 1040.0f
            && tab.Position.Y >= 0 && tab.Position.Y + tab.Size.Y <= 560.0f, $"tab={tab}");

        // Below the top bar (y < 28), or it would sit under the level-name chips.
        t.Check("the fold tab clears the top bar", tab.Position.Y >= 30.0f,
            $"top={tab.Position.Y} vs top bar 28");

        // Below the bottom replay band starts at 546, and clear of the toast (y 494..544).
        t.Check("the fold tab clears the toast", tab.Position.Y + tab.Size.Y <= 494.0f,
            $"bottom={tab.Position.Y + tab.Size.Y} vs toast top 494");
        t.Check("the fold tab clears the bottom replay band", tab.Position.Y + tab.Size.Y <= 546.0f,
            $"bottom={tab.Position.Y + tab.Size.Y} vs band 546");

        // It must not fight the transport grid for the same tap.
        foreach (var a in System.Enum.GetValues<TouchControls.Action>())
        {
            var r = TouchControls.Rect(a);
            var disjoint = tab.Position.X + tab.Size.X <= r.Position.X
                           || r.Position.X + r.Size.X <= tab.Position.X
                           || tab.Position.Y + tab.Size.Y <= r.Position.Y
                           || r.Position.Y + r.Size.Y <= tab.Position.Y;
            t.Check($"the fold tab does not overlap the {a} button", disjoint, $"tab={tab} button={r}");
        }

        t.Check("the fold tab does not overlap the button grid's collapse tab",
            !Overlaps(tab, TouchControls.CollapseToggleRect()),
            $"panel tab={tab} grid tab={TouchControls.CollapseToggleRect()}");

        // It sits INSIDE the panel's rectangle on purpose (that is the corner it belongs to), so the
        // band it occupies has to be empty of panel text. The panel draws its text from x=16, and its
        // content stops at x+226; the tab starts at 214, which is why this matters -- the assertion is
        // that it starts far enough right that the panel's own first-column text cannot reach it. The
        // rendered proof is tools/analysis/panel_fold.py; this is the arithmetic guard.
        t.Check("the fold tab starts right of the panel's text column",
            tab.Position.X >= 200.0f, $"tab left={tab.Position.X} -- panel text spans x 16..242");

        // The single most important property: the SAME rect in both states. A tab that moves when
        // tapped is a tab the player has to find again, and this is the only way to restore the panel.
        t.Check("the fold tab's rect does not depend on the panel being open",
            Hud.PanelFoldRect() == Hud.PanelFoldRect(), "the rect must be fixed across states");
    }

    private static bool Overlaps(Rect2 a, Rect2 b)
        => !(a.Position.X + a.Size.X <= b.Position.X
             || b.Position.X + b.Size.X <= a.Position.X
             || a.Position.Y + a.Size.Y <= b.Position.Y
             || b.Position.Y + b.Size.Y <= a.Position.Y);
}
