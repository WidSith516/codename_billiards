using System;
using System.Collections.Generic;
using Godot;
using Rewind.Sim;

namespace Rewind.Game;

/// <summary>
/// Transient visual effects: the cushion ripple the user asked for, the light streak that rebounds
/// from the contact point, pot bursts, and card-draw flourishes.
///
/// WHY THIS IS ITS OWN FILE, with its own lifetime: every effect here is purely cosmetic and must
/// never be able to affect the simulation. Keeping them in a list processed by the view means the
/// kernel has no idea they exist, so a dropped frame or a paused game can never change physics. That
/// separation is also what let the earlier rewind visuals be added without touching a single locked
/// constant.
///
/// The rail effect is the one the user specified in detail:
///  * the ripple spreads across the WHOLE WALL, not a blob at the impact site, and it is clipped to
///    the wall band so it reads as the cushion deforming rather than as a screen-space flash;
///  * the streaking light is created AT THE CONTACT POINT and travels back along the ball's new
///    (reflected) heading.
/// </summary>
public sealed class Effects
{
    /// <summary>
    /// Which wall a ball most recently bounced off, or -1 when it is not moving.
    ///
    /// Derived from the REFLECTED velocity, not from proximity to a wall. Proximity seems obvious and is
    /// wrong here: effects are driven from per-frame stat deltas while a frame runs several physics steps,
    /// so by the time the view sees the rail counter move, the ball has already left the cushion and is
    /// within slop of nothing. Measured on the first capture attempt -- railTransforms=1, effects=0.
    ///
    /// The reflected heading is exactly what the reflection wrote, so it identifies the wall no matter how
    /// long ago the contact happened. The dominant axis wins, so a ball crossing the table diagonally
    /// after a bounce still reports the wall it came off rather than one it merely passed.
    ///
    /// Pure and static so the mapping is directly testable without a Play node or a screenshot.
    /// </summary>
    public static int WallFromHeading(Vec2 velocity)
    {
        var best = -1;
        var bestMag = 0.0;

        if (velocity.X < 0.0 && Math.Abs(velocity.X) > bestMag) { best = 1; bestMag = Math.Abs(velocity.X); }
        if (velocity.X > 0.0 && Math.Abs(velocity.X) > bestMag) { best = 0; bestMag = Math.Abs(velocity.X); }
        if (velocity.Y < 0.0 && Math.Abs(velocity.Y) > bestMag) { best = 3; bestMag = Math.Abs(velocity.Y); }
        if (velocity.Y > 0.0 && Math.Abs(velocity.Y) > bestMag) { best = 2; }
        return best;
    }

    private enum Kind
    {
        RailRipple,
        RailStreak,
        PotBurst,
        CardReveal,
    }

    private sealed class Fx
    {
        public Kind Kind;
        public double Age;
        public double Life;
        public Color Tint;
        public float Strength = 1.0f;

        /// <summary>RailRipple: which wall (0 = left, 1 = right, 2 = top, 3 = bottom).</summary>
        public int Wall;

        /// <summary>RailRipple: coordinate along the wall where the impact landed.</summary>
        public float Along;

        /// <summary>Streak / burst: world position.</summary>
        public Vector2 Origin;

        /// <summary>Streak: unit direction of travel.</summary>
        public Vector2 Direction;
    }

    private readonly List<Fx> _items = new List<Fx>();

    /// <summary>Live effect count (asserted by tests and shown in the debug overlay).</summary>
    public int Count => _items.Count;

    public void Clear() => _items.Clear();

    /// <summary>Advance every effect and drop the expired ones.</summary>
    public void Update(double delta)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var fx = _items[i];
            fx.Age += delta;
            if (fx.Age >= fx.Life) _items.RemoveAt(i);
        }
    }

    /// <summary>
    /// A rail hit. <paramref name="wall"/> selects the cushion; <paramref name="along"/> is the
    /// position along it in table coordinates; <paramref name="heading"/> is the ball's velocity
    /// AFTER the reflection, which is what the streak follows.
    ///
    /// <paramref name="boosted"/> separates a hit that actually paid out energy from one that merely
    /// bounced: the player must be able to SEE whether the rail is still giving, because the
    /// allowance runs out and there is otherwise no way to tell.
    /// </summary>
    public void RailHit(int wall, float along, Vector2 origin, Vector2 heading, Color tint, bool boosted)
    {
        var ripple = new Fx
        {
            Kind = Kind.RailRipple,
            Life = boosted ? 0.85 : 0.45,
            Wall = wall,
            Along = along,
            Tint = tint,
            Strength = boosted ? 1.0f : 0.45f,
        };
        _items.Add(ripple);

        if (!boosted) return;

        var dir = heading.LengthSquared() > 1e-9f ? heading.Normalized() : new Vector2(0, 1);
        _items.Add(new Fx
        {
            Kind = Kind.RailStreak,
            Life = 0.55,
            Origin = origin,
            Direction = dir,
            Tint = tint,
        });
    }

    /// <summary>A pot: a compact burst at the pocket.</summary>
    public void Pot(Vector2 at, Color tint)
    {
        _items.Add(new Fx { Kind = Kind.PotBurst, Life = 0.6, Origin = at, Tint = tint });
    }

    /// <summary>The card hand appearing.</summary>
    public void CardReveal()
    {
        _items.Add(new Fx { Kind = Kind.CardReveal, Life = 0.45, Tint = Palette.TextMain });
    }

    /// <summary>
    /// Draw the rail ripples in TABLE space, clipped to the cushion band.
    ///
    /// The clip is the whole point: an unclipped ring would spill onto the felt and read as a ball
    /// effect. Clipping to the rail's own rectangle means the wave visibly travels ALONG the wall and
    /// dies at its ends, which is what "波纹散开，仅作用于墙面上" describes.
    /// </summary>
    public void DrawRailRipples(CanvasItem ci, Rect2 table)
    {
        foreach (var fx in _items)
        {
            if (fx.Kind != Kind.RailRipple) continue;

            var t = (float)(fx.Age / fx.Life);
            var eased = 1.0f - (1.0f - t) * (1.0f - t);      // fast out, slow settle
            var radius = RailBand * 0.5f + eased * RippleReach;

            // Opacity. Tuned up from 0.75 after measuring: the wave has to read ON TOP of the rail
            // artwork, which is itself a mid-tone, and the aging curve (1-t) drops the tail fast. At 0.95
            // the ring survives the art and still fades out well before the effect expires.
            var alpha = (1.0f - t) * 0.95f * fx.Strength;
            var tint = fx.Tint;
            tint.A = alpha;

            var band = RailBandRect(table, fx.Wall);

            // Two ring outlines so the wave reads as a spread rather than a single swelling line.
            for (var ring = 0; ring < 2; ring++)
            {
                var r = radius - ring * (RippleReach * 0.32f);
                if (r <= 1.0f) continue;
                var c = tint;
                c.A = alpha * (ring == 0 ? 1.0f : 0.5f);
                DrawClippedArc(ci, band, fx, r, c);
            }
        }
    }

    /// <summary>
    /// Width of the cushion band the ripple lives in, px.
    ///
    /// The physics rail box is the FELT boundary; the visible cushion sits outside it. 16 px matched the
    /// physical cushion width, but a 16 px band cannot show a wave "spreading across the wall" -- it
    /// reads as a line. Widened to 26 so the wave has a face to travel over, which is what the user
    /// asked to see.
    /// </summary>
    private const float RailBand = 26.0f;

    /// <summary>
    /// How far a ripple spreads along the wall, px.
    ///
    /// Tuned DOWN from 190 after measuring the first working capture: at 190 the two rings spanned
    /// 684 px of a 910 px rail, so the wave read as a diffuse haze over the whole cushion rather than as
    /// a wave emanating from a point. 120 keeps it visibly a local disturbance that travels out and
    /// dies, which is what "波纹散开" describes.
    /// </summary>
    private const float RippleReach = 120.0f;

    /// <summary>Line width of a ripple ring. Thicker than the default 1 px so it survives the scrim and
    /// the rail artwork underneath it.</summary>
    private const float RippleWidth = 2.6f;

    private static Rect2 RailBandRect(Rect2 table, int wall)
    {
        switch (wall)
        {
            case 0: return new Rect2(table.Position.X - RailBand, table.Position.Y - RailBand,
                RailBand, table.Size.Y + RailBand * 2.0f);
            case 1: return new Rect2(table.Position.X + table.Size.X, table.Position.Y - RailBand,
                RailBand, table.Size.Y + RailBand * 2.0f);
            case 2: return new Rect2(table.Position.X - RailBand, table.Position.Y - RailBand,
                table.Size.X + RailBand * 2.0f, RailBand);
            default: return new Rect2(table.Position.X - RailBand, table.Position.Y + table.Size.Y,
                table.Size.X + RailBand * 2.0f, RailBand);
        }
    }

    /// <summary>
    /// Draw one ripple ring, clipped to the wall band.
    ///
    /// THE GEOMETRY MATTERS AND THE OBVIOUS VERSION IS WRONG. A circle centred ON the cushion expands
    /// perpendicular to the wall as much as along it, so clipping it to a 16 px band leaves a sliver a
    /// few pixels tall: the wave is technically there and visually invisible. (Measured on the first
    /// capture -- 32 pixels total, all of them from the streak, none from the ring.)
    ///
    /// What the user asked for is a wave that TRAVELS ACROSS THE WALL FACE. So the ring is the
    /// intersection of the band with a circle whose radius is the wavefront distance, which for an
    /// infinite band is a line at that distance from the contact point -- drawn as an arc that bulges
    /// slightly, giving the "spreading outward" read while staying entirely on the cushion.
    /// </summary>
    private static void DrawClippedArc(CanvasItem ci, Rect2 band, Fx fx, float radius, Color color)
    {
        // The contact point, projected onto the band's centre line.
        var horizontal = fx.Wall <= 1;
        var center = horizontal
            ? new Vector2(band.Position.X + RailBand * 0.5f, fx.Along)
            : new Vector2(fx.Along, band.Position.Y + RailBand * 0.5f);

        const int steps = 48;
        var previous = Vector2.Zero;
        var have = false;
        var half = RailBand * 0.5f;

        // A shallow elliptical arc along the wall: `along` is the axis the wave travels down, and the
        // band's own half-width is the perpendicular extent, so the whole ring stays ON the wall.
        for (var i = 0; i <= steps; i++)
        {
            var a = Mathf.Tau * i / steps;
            var ax = Mathf.Cos(a) * radius;
            var ay = Mathf.Sin(a) * half;
            var p = horizontal ? center + new Vector2(ay, ax) : center + new Vector2(ax, ay);
            if (!band.HasPoint(p))
            {
                have = false;
                continue;
            }

            if (have) ci.DrawLine(previous, p, color, RippleWidth);
            previous = p;
            have = true;
        }
    }

    /// <summary>
    /// The rebound streak: a short comet that leaves the contact point along the ball's new heading.
    /// Parented to the table, so it must be drawn in the same transform the balls use.
    /// </summary>
    public void DrawStreaks(CanvasItem ci)
    {
        foreach (var fx in _items)
        {
            if (fx.Kind != Kind.RailStreak) continue;

            var t = (float)(fx.Age / fx.Life);
            var alpha = (1.0f - t) * (1.0f - t);

            // The head runs out from the contact point and the tail stretches behind it: the light
            // is "created at the collision point", so it must not exist before it.
            var travel = 30.0f + t * 150.0f;
            var tail = 34.0f + t * 60.0f;
            var head = fx.Origin + fx.Direction * travel;
            var back = fx.Origin + fx.Direction * (travel - tail);

            var c = fx.Tint;
            c.A = alpha;
            ci.DrawLine(back, head, c, 3.0f);
            c.A = alpha * 0.45f;
            ci.DrawLine(fx.Origin, head, c, 1.5f);

            var dot = fx.Tint;
            dot.A = alpha;
            ci.DrawCircle(fx.Origin, 3.5f * (1.0f - t * 0.5f), dot);
        }
    }

    /// <summary>Pot bursts, in table space.</summary>
    public void DrawPotBursts(CanvasItem ci)
    {
        foreach (var fx in _items)
        {
            if (fx.Kind != Kind.PotBurst) continue;

            var t = (float)(fx.Age / fx.Life);
            var eased = 1.0f - (1.0f - t) * (1.0f - t);
            var r = 6.0f + eased * 46.0f;
            var c = fx.Tint;
            c.A = (1.0f - t) * 0.8f;
            ci.DrawArc(fx.Origin, r, 0.0f, Mathf.Tau, 40, c, 2.5f);
            c.A *= 0.5f;
            ci.DrawArc(fx.Origin, r * 0.55f, 0.0f, Mathf.Tau, 28, c, 1.5f);
        }
    }

    /// <summary>True while a card reveal flourish is running (the card UI expands on it).</summary>
    public float CardRevealT
    {
        get
        {
            foreach (var fx in _items)
            {
                if (fx.Kind == Kind.CardReveal) return (float)(fx.Age / fx.Life);
            }

            return 1.0f;
        }
    }
}
