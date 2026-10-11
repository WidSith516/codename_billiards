using Godot;
using System.Collections.Generic;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// Ball and pocket drawing. Every ball identity channel is redundant on purpose
/// (port plan §4.5):
///   * fill colour  - Okabe-Ito colour-blind-safe set (8 hues distinguishable under deuteranopia);
///   * ID number    - drawn large, centred, with a contrasting outline;
///   * type glyph   - cue = centre dot, object = index tick ring, (reserved) anchor = square,
///                    heavy = thick double ring, bomb = star;
///   * ring         - every ball gets a dark ring, so it cannot dissolve into the felt.
/// A ball is therefore identifiable with zero colour perception, and the old prototype's
/// invisible near-black ball is structurally impossible.
/// </summary>
public static class BallStyles
{
    private static readonly Dictionary<Color, Texture2D> SurfaceTextures = new();

    // Canvas renderBall() uses two offset circles, not a centred radial gradient.
    // Bake that same gradient once per fill; numbers and type glyphs stay dynamic.
    private static Texture2D SurfaceTexture(Color fill)
    {
        if (SurfaceTextures.TryGetValue(fill, out var cached)) return cached;
        const int size = 64;
        const float radius = 12.2f;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var p = new Vector2((x + 0.5f) * 2 * radius / size - radius,
                (y + 0.5f) * 2 * radius / size - radius);
            var q = p + new Vector2(5, 6);
            var dr = radius - 2;
            var a = 61 - dr * dr;
            var b = -2 * (q.Dot(new Vector2(5, 6)) + 2 * dr);
            var c = q.LengthSquared() - 4;
            var discriminant = Mathf.Max(0, b * b - 4 * a * c);
            var t = Mathf.Clamp((-b - Mathf.Sqrt(discriminant)) / (2 * a), 0, 1);
            var color = t < 0.18f ? Colors.White.Lerp(fill, t / 0.18f)
                : t < 0.77f ? fill : fill.Lerp(new Color("#273834"), (t - 0.77f) / 0.23f);
            color.A = Mathf.Clamp((radius - p.Length()) * size / (2 * radius) + 0.5f, 0, 1);
            image.SetPixel(x, y, color);
        }
        var texture = ImageTexture.CreateFromImage(image);
        SurfaceTextures.Add(fill, texture);
        return texture;
    }

    /// <summary>Reserved special ball types declared by level data (see LevelData.DeclaredType).</summary>
    public enum Glyph
    {
        Cue,
        Object,
        Anchor,
        Heavy,
        Bomb,
        Unknown,
    }

    public static Glyph GlyphFor(Ball b, BallSpawn spawn)
    {
        if (b.IsCue) return Glyph.Cue;
        var declared = spawn?.DeclaredType ?? "object";
        switch (declared)
        {
            case "anchor": return Glyph.Anchor;
            case "heavy": return Glyph.Heavy;
            case "bomb": return Glyph.Bomb;
            case "object": return Glyph.Object;
            default: return Glyph.Unknown;
        }
    }

    public static Color FillFor(Ball b)
    {
        return b.IsCue ? Palette.Cue : Palette.ObjectFill(b.Id);
    }

    /// <summary>Draw one ball. <paramref name="alpha"/> lets the caller fade/highlight it.</summary>
    public static void Draw(CanvasItem ci, Ball b, Glyph glyph, float highlightPulse, Font font, int labelSize)
    {
        var center = new Vector2((float)b.Position.X, (float)b.Position.Y);
        var r = (float)b.Radius;
        var fill = FillFor(b);

        if (b.Pocketed)
        {
            // Pocketed balls are still drawn - greyed, in place - so "pocketed forever" is visible
            // rather than the ball silently vanishing.
            var ghost = new Color(fill.R, fill.G, fill.B, 0.22f);
            ghost = ghost.Lerp(new Color("#6b7a74"), 0.7f);
            ci.DrawCircle(center, r, ghost, true, -1.0f, true);
            ci.DrawCircle(center, r, new Color(0.5f, 0.55f, 0.53f, 0.8f), false, 1.0f, true);
            var d = r * 0.72f;
            ci.DrawLine(center - new Vector2(d, d), center + new Vector2(d, d), new Color(0.75f, 0.8f, 0.78f, 0.55f), 1.4f, true);
            ci.DrawLine(center - new Vector2(d, -d), center + new Vector2(d, -d), new Color(0.75f, 0.8f, 0.78f, 0.55f), 1.4f, true);
            return;
        }

        // Highlight ring (used for "about to be hit by the rewind" and for the predicted target).
        if (highlightPulse > 0.001f)
        {
            var ringR = r + 5.0f + 2.0f * highlightPulse;
            ci.DrawCircle(center, ringR + 2.0f, new Color(Palette.ContactSoonSoft.R, Palette.ContactSoonSoft.G, Palette.ContactSoonSoft.B, 0.25f * highlightPulse), true, -1.0f, true);
            ci.DrawCircle(center, ringR, new Color(Palette.ContactSoon.R, Palette.ContactSoon.G, Palette.ContactSoon.B, 0.55f + 0.4f * highlightPulse), false, 3.0f, true);
        }

        ci.DrawCircle(center + new Vector2(0, 4), r, new Color(0, 0, 0, 0.35f), true, -1.0f, true);
        ci.DrawTextureRect(SurfaceTexture(fill), new Rect2(center - Vector2.One * r, Vector2.One * r * 2), false);
        ci.DrawCircle(center, r, Palette.BallRingDark, false, 2.0f, true);

        var label = Palette.Luma(fill) > 0.55f ? Palette.BallLabelLight : new Color("#f7fbf8");
        var labelOutline = Palette.Luma(fill) > 0.55f
            ? new Color(1f, 1f, 1f, 0.9f)
            : new Color(0f, 0f, 0f, 0.85f);

        if (glyph == Glyph.Cue)
        {
            // Cue ball: a solid centre dot + a thin cross. Reads as "the white" even in greyscale.
            ci.DrawCircle(center, r * 0.34f, new Color("#c8342c"), true, -1.0f, true);
            ci.DrawCircle(center, r * 0.34f, Palette.BallRingDark, false, 1.2f, true);
        }
        else
        {
            DrawGlyph(ci, center, r, glyph, label);
        }

        // ID number: the primary non-colour identity channel. Drawn for object balls only; the
        // cue ball's dot is its identity.
        if (!b.IsCue && font != null)
        {
            var text = b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, labelSize);
            var pos = center + new Vector2(-size.X * 0.5f, size.Y * 0.5f - font.GetDescent(labelSize) * 0.5f);
            pos.Y -= 1.0f;
            ci.DrawStringOutline(font, pos, text, HorizontalAlignment.Left, -1, labelSize, 4, labelOutline, TextServer.JustificationFlag.None, TextServer.Direction.Auto, TextServer.Orientation.Horizontal);
            ci.DrawString(font, pos, text, HorizontalAlignment.Left, -1, labelSize, label, TextServer.JustificationFlag.None, TextServer.Direction.Auto, TextServer.Orientation.Horizontal);
        }
    }

    private static void DrawGlyph(CanvasItem ci, Vector2 c, float r, Glyph glyph, Color color)
    {
        switch (glyph)
        {
            case Glyph.Anchor: // square
            {
                var s = r * 0.46f;
                var rect = new Rect2(c - new Vector2(s, s), new Vector2(s * 2f, s * 2f));
                ci.DrawRect(rect, color, true, 1.6f);
                break;
            }

            case Glyph.Heavy: // thick double ring
                ci.DrawCircle(c, r * 0.52f, color, false, 3.0f, true);
                break;

            case Glyph.Bomb: // star burst
            {
                for (var i = 0; i < 8; i++)
                {
                    var a = Mathf.Tau * i / 8.0f;
                    var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    ci.DrawLine(c + d * (r * 0.24f), c + d * (r * 0.66f), color, 1.6f, true);
                }

                break;
            }

            case Glyph.Unknown: // question mark: a type the view does not understand yet
                ci.DrawCircle(c, r * 0.5f, color, false, 2.0f, true);
                break;

            default: // Object: a small tick pair at the rim marks "plain object ball"
            {
                ci.DrawLine(c + new Vector2(-r * 0.78f, 0f), c + new Vector2(-r * 0.5f, 0f), color, 2.0f, true);
                ci.DrawLine(c + new Vector2(r * 0.5f, 0f), c + new Vector2(r * 0.78f, 0f), color, 2.0f, true);
                break;
            }
        }
    }
}
