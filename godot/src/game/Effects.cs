using System;
using System.Collections.Generic;
using Godot;
using Rewind.Sim;

namespace Rewind.Game;

/// <summary>
/// Transient visual effects: the rail impact, pot bursts, and card-draw flourishes.
///
/// WHY THIS IS ITS OWN FILE, with its own lifetime: every effect here is purely cosmetic and must
/// never be able to affect the simulation. Keeping them in a list processed by the view means the
/// kernel has no idea they exist, so a dropped frame or a paused game can never change physics.
///
/// THE RAIL IMPACT WAS REBUILT, and the reason is worth recording because the old version looked
/// reasonable in code and weak on screen. It drew two thin elliptical ring outlines plus a comet of
/// three 1.5-3 px lines. That is ANNOTATION: it draws lines NEAR an event without displacing anything.
/// On a 360-disc starfield a 1.5 px line carries no sense of mass, which is exactly what the user
/// reported ("流光效果和撞击感太弱").
///
/// What replaces it is an OPTICAL event plus a MATERIAL one:
///   1. the table surface is displaced through a UV offset field (see shaders/impact_warp.gdshader),
///      so the starfield and the cushion edge visibly slide and tear at the contact;
///   2. the cushion's own edge is bent and springs back, drawn as real geometry.
///
/// BOTH ARE DRIVEN BY THE SAME TWO FORMULAS below, deliberately. The UV warp is a shader and the
/// cushion bend is canvas geometry, so they cannot literally share code -- but they share the
/// CONSTANTS and the closed form, and <see cref="ElasticEnvelope"/> / <see cref="DentWeight"/> are the
/// single authoritative copy for the C# side. If the two disagree, the dent and the distortion
/// visibly detach from each other, which is the failure this arrangement exists to prevent.
///
/// EVERYTHING HERE IS DECORATION. Nothing reads these values; they cannot move a ball, a number or a
/// test. A missing shader degrades to an unwarped table, never to a broken frame.
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

    /// <summary>
    /// Inward normal of a wall -- the direction pointing INTO the table, away from the cushion.
    ///
    /// The dent is DIRECTIONAL: a cushion struck on its right face gives way to the right. A purely
    /// radial displacement would read as a lens held over the table, not as a collision.
    ///
    /// Wall ids match <see cref="WallFromHeading"/> and Play's Contact*Of helpers:
    /// 0 = left (x = TableLeft), 1 = right (x = TableRight), 2 = top (small y), 3 = bottom (large y).
    /// </summary>
    public static Vector2 InwardNormal(int wall)
    {
        switch (wall)
        {
            case 0: return new Vector2(1.0f, 0.0f);
            case 1: return new Vector2(-1.0f, 0.0f);
            case 2: return new Vector2(0.0f, 1.0f);
            case 3: return new Vector2(0.0f, -1.0f);
            default: return Vector2.Zero;
        }
    }

    /// <summary>
    /// The elastic envelope: how hard the material is displaced right now, as a signed fraction.
    ///
    /// cos() is what makes this an IMPACT rather than a flash, and it is the single most important
    /// line in this file. It goes positive (the cushion compresses), then NEGATIVE (it springs back
    /// past its rest position), then positive again (a smaller second compression), while the
    /// exponential squeezes the whole thing toward zero. The alternating sign IS the rebound.
    ///
    /// A plain decay curve -- exp(-k*t), the obvious choice -- can only ever be >= 0, so it can only
    /// ever describe "something lit up and faded". It cannot describe "something was STRUCK". Given the
    /// user's report of a weak impact, that distinction is the fix.
    /// </summary>
    public static float ElasticEnvelope(double age, float omega, float decay)
        => (float)(Math.Exp(-decay * age) * Math.Cos(omega * age));

    /// <summary>
    /// How much of the deformation a point <paramref name="dist"/> px from the contact receives.
    ///
    /// A Gaussian, because the cushion is a CONNECTED material: it cannot dent in one 12 px spot.
    /// Sigma is deliberately several ball-radii wide (see <see cref="DentSigma"/>) so the disturbance
    /// spreads along the rail the way a run of material actually compresses.
    /// </summary>
    public static float DentWeight(float dist, float sigma)
        => (float)Math.Exp(-(dist * dist) / (2.0 * sigma * sigma));

    private enum Kind
    {
        RailImpact,
        PotBurst,
        CardReveal,
    }

    private sealed class Fx
    {
        public Kind Kind;
        public double Age;
        public double Life;
        public Color Tint;

        /// <summary>RailImpact: which wall (see <see cref="InwardNormal"/>).</summary>
        public int Wall;

        /// <summary>RailImpact: coordinate along the wall where the impact landed.</summary>
        public float Along;

        /// <summary>RailImpact / PotBurst: the contact point, in table coordinates.</summary>
        public Vector2 Origin;

        /// <summary>RailImpact: peak displacement, px. Which of the two push values is chosen here,
        /// at spawn, is what makes a spent rail visibly different from a paying one.</summary>
        public float PeakPush;

        /// <summary>RailImpact: whether the rail actually paid out (see <see cref="RailHit"/>).</summary>
        public bool Boosted;
    }

    /// <summary>
    /// One live impact, flattened for the warp shader. Produced by <see cref="TryGetImpact"/>.
    ///
    /// The envelope is NOT applied here: the shader evaluates it from <see cref="Age"/>, so the
    /// oscillation is computed once and cannot be double-applied into a stronger-than-intended warp.
    /// </summary>
    public readonly struct Impact
    {
        public Impact(Vector2 contact, Vector2 normal, float peakPush, float age, Color tint)
        {
            Contact = contact;
            Normal = normal;
            PeakPush = peakPush;
            Age = age;
            Tint = tint;
        }

        /// <summary>Contact point in table px, which equals texture px for the warp target.</summary>
        public Vector2 Contact { get; }

        /// <summary>Inward wall normal: the axis the cushion yields along.</summary>
        public Vector2 Normal { get; }

        /// <summary>Peak displacement magnitude, px, before the envelope is applied.</summary>
        public float PeakPush { get; }

        /// <summary>Age in seconds, for the shader's own envelope evaluation.</summary>
        public float Age { get; }

        /// <summary>Tint of the impact, so the shader's shoulder highlight matches the rail readout.</summary>
        public Color Tint { get; }
    }

    private readonly List<Fx> _items = new List<Fx>();

    /// <summary>Live effect count (asserted by tests and shown in the debug overlay).</summary>
    public int Count => _items.Count;

    public void Clear() => _items.Clear();

    // ---- tuning ---------------------------------------------------------------------------------

    /// <summary>
    /// How far a boosted impact displaces the surface at its peak, in pixels.
    ///
    /// This is the "撞击感" dial. At 8 px the warp was measurable but not felt; 30 px visibly drags the
    /// starfield and the cushion edge, and because the envelope is signed it does so in BOTH
    /// directions over the life of the rebound.
    /// </summary>
    public const float BoostedPushPx = 30.0f;

    /// <summary>A hit that did NOT pay out still deforms the cushion -- it just does it faintly, so the
    /// player can see that energy was spent without confusing it with a full impact. This is the whole
    /// readout for "the rail stopped giving".</summary>
    public const float SpentPushPx = 11.0f;

    /// <summary>Spring-back rate, rad/s. The first rebound lands at omega*t = pi, i.e. 0.105 s -- about
    /// six frames at 60 Hz: fast enough to read as an impact, slow enough to see.</summary>
    public const float Omega = 30.0f;

    /// <summary>Decay, 1/s. exp(-7 * 0.55) = 0.021, so the oscillation is spent before the effect
    /// expires and a new impact never fights a stale one.</summary>
    public const float Decay = 7.0f;

    /// <summary>Width of the compressed zone, px. Wider than the ball (radius ~12) because the cushion
    /// compresses as a run of material rather than at a point.</summary>
    public const float DentSigma = 88.0f;

    /// <summary>Lifetime of a boosted impact, s.</summary>
    private const double ImpactLife = 0.55;

    /// <summary>Lifetime of a spent (non-paying) impact: shorter, because nothing was returned.</summary>
    private const double SpentImpactLife = 0.34;

    /// <summary>How long the contact flash lasts, s. Short on purpose: the old comet spread its light
    /// over 180 px and ~0.55 s and read as nothing.</summary>
    private const double FlashLife = 0.13;

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
    /// position along it in table coordinates; <paramref name="origin"/> is the contact point.
    ///
    /// <paramref name="boosted"/> separates a hit that actually paid out energy from one that merely
    /// bounced: the player must be able to SEE whether the rail is still giving, because the allowance
    /// runs out and there is otherwise no way to tell. A spent hit still deforms, just far less.
    /// </summary>
    public void RailHit(int wall, float along, Vector2 origin, Vector2 heading, Color tint, bool boosted)
    {
        _ = heading;   // the old comet followed this; a directioned dent does not need it

        _items.Add(new Fx
        {
            Kind = Kind.RailImpact,
            Life = boosted ? ImpactLife : SpentImpactLife,
            Wall = wall,
            Along = along,
            Origin = origin,
            Tint = tint,
            PeakPush = boosted ? BoostedPushPx : SpentPushPx,
            Boosted = boosted,
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
    /// The strongest live impact, flattened for the warp shader. False when nothing is happening, which
    /// switches the shader to an exact pass-through.
    ///
    /// Returns the MAXIMUM by current displacement rather than the newest, because two contacts a few
    /// frames apart (a fast ball can hit two rails inside one frame) should reinforce on screen rather
    /// than have the older one silently cancelled by the newer.
    /// </summary>
    public bool TryGetImpact(out Impact impact)
    {
        impact = default;
        var found = false;
        var bestPush = 0.0f;

        foreach (var fx in _items)
        {
            if (fx.Kind != Kind.RailImpact) continue;

            var normal = InwardNormal(fx.Wall);
            if (normal == Vector2.Zero) continue;

            var push = fx.PeakPush * Math.Abs(ElasticEnvelope(fx.Age, Omega, Decay));
            if (found && push <= bestPush) continue;

            impact = new Impact(fx.Origin, normal, fx.PeakPush, (float)fx.Age, fx.Tint);
            bestPush = push;
            found = true;
        }

        return found;
    }

    /// <summary>Current signed displacement of one impact at its centre, px. Positive = the cushion is
    /// compressed outward, negative = it has sprung back through its rest position.</summary>
    private static float PushAt(Fx fx) => fx.PeakPush * ElasticEnvelope(fx.Age, Omega, Decay);

    /// <summary>
    /// Draw the rail impacts in TABLE space: the contact flash and the bent cushion edge.
    ///
    /// The cushion edge is the one that sells the material. It is drawn as a polyline displaced by
    /// exactly the Gaussian the UV warp uses, so it bows outward, springs back through straight, and
    /// overshoots -- the same motion being applied to the surface underneath it.
    /// </summary>
    public void DrawRailImpacts(CanvasItem ci, Rect2 table)
    {
        foreach (var fx in _items)
        {
            if (fx.Kind != Kind.RailImpact) continue;

            var inward = InwardNormal(fx.Wall);
            if (inward == Vector2.Zero) continue;
            var outward = -inward;   // the cushion gives way AWAY from the table

            var t = (float)(fx.Age / fx.Life);
            var alpha = (1.0f - t) * (1.0f - t);

            // ---- 1. the contact flash: small, bright, fast --------------------------------------
            var flashT = Mathf.Clamp((float)(fx.Age / FlashLife), 0.0f, 1.0f);
            if (flashT < 1.0f)
            {
                var fade = (1.0f - flashT) * (1.0f - flashT);
                var c = fx.Tint;
                c.A = fade * 0.85f;
                ci.DrawCircle(fx.Origin, 5.0f + flashT * 9.0f, c, true, -1.0f, true);
                c.A = fade * 0.45f;
                ci.DrawCircle(fx.Origin, 9.0f + flashT * 22.0f, c, false, 2.0f, true);
            }

            // ---- 2. the bent cushion edge -------------------------------------------------------
            var push = PushAt(fx);
            if (Math.Abs(push) < 0.05f) continue;

            var lit = fx.Tint;
            lit.A = Mathf.Clamp(alpha * 1.1f, 0.0f, 1.0f);
            var fill = fx.Tint;
            fill.A = Mathf.Clamp(alpha * 0.30f, 0.0f, 1.0f);

            // Sample across +/-3 sigma: beyond that the Gaussian is under 1% and the extra points
            // would only add pixels that never move.
            const int steps = 64;
            const float span = DentSigma * 3.0f;
            var step = 2.0f * span / steps;

            var previous = Vector2.Zero;      // deformed position of the previous sample
            var previousRest = Vector2.Zero;  // its rest position
            var have = false;

            for (var i = 0; i <= steps; i++)
            {
                var s = -span + step * i;
                var rest = CushionEdge(fx.Wall, table, fx.Along + s);
                var p = rest + outward * (push * DentWeight(s, DentSigma));

                if (have)
                {
                    ci.DrawLine(previous, p, lit, 3.0f);

                    // The sliver between rest and deformed position, so the bend reads as compressed
                    // material rather than as a line that moved. A quad per segment, ordered around
                    // its perimeter so it is a simple polygon.
                    ci.DrawColoredPolygon(new[] { previousRest, previous, p, rest }, fill);
                }

                previous = p;
                previousRest = rest;
                have = true;
            }

            // ---- 3. the compressed shoulder catching light --------------------------------------
            // Displacement alone is hard to see on a flat-coloured rail; a soft highlight where the
            // material is squeezed gives the eye the shape. Only while actually compressed (push > 0),
            // so the rebound does not glow.
            if (push > 0.0f)
            {
                var glow = fx.Tint;
                glow.A = Mathf.Clamp(alpha * 0.30f, 0.0f, 1.0f);
                ci.DrawCircle(fx.Origin, DentSigma * 0.5f, glow, true, -1.0f, true);
            }
        }
    }

    /// <summary>
    /// The cushion's rest edge for a wall, at a given position along it.
    ///
    /// The table box IS the cushion's inner face -- the visible cushion sits just outside it -- so the
    /// dent pulls from this line outward, into the rail band.
    /// </summary>
    private static Vector2 CushionEdge(int wall, Rect2 table, float along)
    {
        switch (wall)
        {
            case 0: return new Vector2(table.Position.X, along);
            case 1: return new Vector2(table.Position.X + table.Size.X, along);
            case 2: return new Vector2(along, table.Position.Y);
            default: return new Vector2(along, table.Position.Y + table.Size.Y);
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
