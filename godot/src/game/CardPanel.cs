using System.Collections.Generic;
using Godot;
using Rewind.Sim;

namespace Rewind.Game;

/// <summary>
/// The event-card hand: three cards drawn when an object ball is potted, of which the player must
/// take exactly one.
///
/// WHY A SEPARATE TYPE INSTEAD OF METHODS ON Play: the layout is the part that has to be right, and
/// the layout is pure geometry. Keeping the rects in one place means the DRAW and the HIT TEST read
/// the same numbers, which is the bug class that bit this project twice already -- a menu whose text
/// ran under a button, and a hint bubble clipped by the playfield's overscan. Here the rect function
/// is shared, so a click can never land somewhere the eye does not see a card.
/// </summary>
public static class CardPanel
{
    /// <summary>Card size, in design-canvas pixels.</summary>
    private const float W = 250.0f;
    private const float H = 330.0f;
    private const float Gap = 28.0f;

    /// <summary>Canvas the cards are laid out for.</summary>
    private const float CanvasW = 1040.0f;
    private const float CanvasH = 560.0f;

    /// <summary>Vertical centre of the row.</summary>
    private const float RowCenterY = CanvasH * 0.5f + 22.0f;

    /// <summary>Where the row starts, horizontally.</summary>
    private static float RowLeft(int count) => (CanvasW - (count * W + (count - 1) * Gap)) * 0.5f;

    /// <summary>The rect of card <paramref name="index"/> out of <paramref name="count"/>.</summary>
    public static Rect2 Rect(int index, int count)
    {
        if (count <= 0) return new Rect2();
        return new Rect2(RowLeft(count) + index * (W + Gap), RowCenterY - H * 0.5f, W, H);
    }

    /// <summary>
    /// Which card contains <paramref name="point"/>, or -1.
    ///
    /// A LIFTED card is drawn taller than its rect, so the hit test uses the same expanded box the
    /// eye sees; otherwise a click on the raised top edge of the hovered card would miss it.
    /// </summary>
    public static int IndexAt(Vector2 point, int count, int hover)
    {
        for (var i = 0; i < count; i++)
        {
            var r = Rect(i, count);
            if (i == hover) r = new Rect2(r.Position.X, r.Position.Y - Lift, r.Size.X, r.Size.Y + Lift);
            if (r.HasPoint(point)) return i;
        }

        return -1;
    }

    /// <summary>How far the hovered card rises, px.</summary>
    private const float Lift = 18.0f;

    /// <summary>
    /// Draw the hand. <paramref name="reveal"/> is 0..1 from the reveal flourish; cards slide up into
    /// place so the panel does not simply appear.
    ///
    /// MUST be called inside the inverse-ViewScale transform, like the rest of the UI: the Play node is
    /// scaled, so UI drawn in node space is silently clipped near the right edge.
    /// </summary>
    public static void Draw(CanvasItem ci, Font font, IList<CardId> hand, int hover, float reveal)
    {
        if (hand == null || hand.Count == 0) return;

        var slide = (1.0f - Mathf.Clamp(reveal, 0.0f, 1.0f)) * 54.0f;

        // Dim the table so the choice is unambiguous.
        var scrim = Colors.Black;
        scrim.A = 0.62f * Mathf.Clamp(reveal, 0.0f, 1.0f);
        ci.DrawRect(new Rect2(0, 0, CanvasW, CanvasH), scrim);

        DrawBanner(ci, font, reveal);

        for (var i = 0; i < hand.Count; i++)
        {
            var r = Rect(i, hand.Count);
            r.Position = new Vector2(r.Position.X, r.Position.Y + slide);
            if (i == hover) r.Position = new Vector2(r.Position.X, r.Position.Y - Lift);
            DrawOne(ci, font, r, CardPool.Get(hand[i]), i, i == hover);
        }
    }

    private static void DrawBanner(CanvasItem ci, Font font, float reveal)
    {
        var a = Mathf.Clamp(reveal, 0.0f, 1.0f);
        var title = "球落袋 · 三选一";
        var sub = "按 1 / 2 / 3 或直接点击卡面 —— 选定后立即生效";

        var ts = 30;
        var titleSize = font.GetStringSize(title, HorizontalAlignment.Left, -1, ts);
        var tColor = Palette.TextMain;
        tColor.A = a;
        ci.DrawString(font, new Vector2((CanvasW - titleSize.X) * 0.5f, 104), title,
            HorizontalAlignment.Left, -1, ts, tColor);

        var ss = 15;
        var subSize = font.GetStringSize(sub, HorizontalAlignment.Left, -1, ss);
        var sColor = Palette.TextDim;
        sColor.A = a;
        ci.DrawString(font, new Vector2((CanvasW - subSize.X) * 0.5f, 130), sub,
            HorizontalAlignment.Left, -1, ss, sColor);
    }

    private static void DrawOne(CanvasItem ci, Font font, Rect2 r, CardDef def, int index, bool hover)
    {
        if (def == null) return;

        // Card body. The hovered card is brighter and gets a coloured frame: the state must be readable
        // without relying on the lift alone.
        var body = hover ? Palette.PanelRaised : Palette.Panel;
        ci.DrawRect(r, body);

        var border = hover ? Palette.Accent : Palette.PanelEdge;
        ci.DrawRect(r, border, false, hover ? 3.0f : 1.5f);

        // A colour-coded top band so the six cards are distinguishable at a glance even before the
        // text is read.
        var band = CardColor(def.Id);
        ci.DrawRect(new Rect2(r.Position.X, r.Position.Y, r.Size.X, 8.0f), band);

        var pad = 16.0f;
        var x = r.Position.X + pad;
        var y = r.Position.Y + 44.0f;
        var innerW = r.Size.X - pad * 2.0f;

        // Index badge, so the keyboard shortcut and the card are visibly linked.
        ci.DrawCircle(new Vector2(r.Position.X + r.Size.X - 26.0f, r.Position.Y + 30.0f), 15.0f, band);
        var badge = (index + 1).ToString();
        var bs = font.GetStringSize(badge, HorizontalAlignment.Left, -1, 17);
        ci.DrawString(font, new Vector2(r.Position.X + r.Size.X - 26.0f - bs.X * 0.5f, r.Position.Y + 36.0f),
            badge, HorizontalAlignment.Left, -1, 17, Palette.TextMain);

        // Card name, wrapped to the card width.
        y = DrawWrapped(ci, font, def.Name, x, y, innerW, 24, Palette.TextMain, 2);
        y += 12.0f;

        // The mechanical effect: this is what the player is actually choosing.
        y = DrawWrapped(ci, font, def.Short, x, y, innerW, 14, Palette.Accent, 5);
        y += 10.0f;

        // Flavour last and dimmest, so it never competes with the numbers.
        DrawWrapped(ci, font, def.Flavor, x, y, innerW, 13, Palette.TextDim, 6);
    }

    /// <summary>
    /// Greedy line wrap. Chinese text has no spaces, so wrapping is per CHARACTER: a word-based wrap
    /// would emit one enormous line and overflow the card, which is exactly the failure the level
    /// names hit earlier.
    /// </summary>
    private static float DrawWrapped(CanvasItem ci, Font font, string text, float x, float y,
        float width, int size, Color color, int maxLines)
    {
        if (string.IsNullOrEmpty(text)) return y;

        var line = "";
        var lines = 0;
        foreach (var ch in text)
        {
            var candidate = line + ch;
            if (font.GetStringSize(candidate, HorizontalAlignment.Left, -1, size).X > width && line.Length > 0)
            {
                ci.DrawString(font, new Vector2(x, y), line, HorizontalAlignment.Left, -1, size, color);
                y += size + 6.0f;
                lines++;
                line = ch.ToString();
                if (lines >= maxLines)
                {
                    // Out of room: the rest is dropped rather than spilling out of the card. The text
                    // is authored short enough that this never fires; it is here so a longer string
                    // added later degrades instead of corrupting the layout.
                    return y;
                }
            }
            else
            {
                line = candidate;
            }
        }

        if (line.Length > 0)
        {
            ci.DrawString(font, new Vector2(x, y), line, HorizontalAlignment.Left, -1, size, color);
            y += size + 6.0f;
        }

        return y;
    }

    /// <summary>A stable colour per card, used for the band and the index badge.</summary>
    public static Color CardColor(CardId id)
    {
        switch (id)
        {
            case CardId.Ice: return new Color("#56b4e9");          // sky blue
            case CardId.TimeCocoon: return new Color("#cc79a7");   // reddish purple
            case CardId.Antimatter: return new Color("#d55e00");   // vermillion
            case CardId.Titan: return new Color("#e69f00");        // orange
            case CardId.Tibo: return new Color("#009e73");         // bluish green
            case CardId.Balloon: return new Color("#f0e442");      // yellow
            default: return Palette.TextDim;
        }
    }
}
