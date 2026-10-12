// REWIND — impact warp. Design-intent file: read this header before editing it.
//
// WHAT THIS FILE IS FOR
//   The rail impact used to be drawn as thin canvas strokes (two ellipse outlines plus a comet of
//   three concentric lines). On a starfield that reads as weak: a 1.5 px line on a noisy background
//   carries no sense of mass. This shader replaces the stroking with a real optical event -- the table
//   surface is displaced through a UV offset field, so the stars and the cushion edge visibly SLIDE
//   and TEAR at the contact instead of merely being annotated.
//
// THE DEFORMATION IS ELASTIC, AND THAT IS THE WHOLE POINT
//   A hit is not a fade-out. It is: the cushion compresses, springs back, overshoots a little, and
//   settles. That is a DAMPED OSCILLATION, not a decaying ramp, and the difference is very visible --
//   a pure decay reads as "something lit up", while a damped oscillation reads as "something was
//   struck". The sign of the envelope is what carries it: positive = compressed inward, negative =
//   sprung back past rest, and the alternating sign is the rebound.
//
// WHY A SUBVIEWPORT RATHER THAN WARPING EACH DRAW CALL
//   The table is drawn as immediate-mode primitives (a starfield of ~360 discs, rects, outlines).
//   Displacing each primitive's coordinates would distort the geometry but NOT the artwork the user
//   actually sees, and would have no way to show the surface tearing. Rendering the table once into a
//   texture and sampling it through this shader is what makes the distortion an actual UV offset.
//
// IT IS DECORATION. Nothing reads this shader; it cannot move a ball, a number or a test. The
// simulation has no idea it exists, and a missing shader degrades to an unwarped table (see
// Play.BuildTableWarp), never to a broken frame.
//
// NOTE ON THE RENDERER: this project ships gl_compatibility, where SCREEN_TEXTURE is restricted. The
// warp therefore samples TEXTURE -- the viewport the table was rendered into -- and never the screen.
shader_type canvas_item;
render_mode blend_mix;

// Size of the texture being warped, in pixels. The displacement field is defined in PIXELS (a dent of
// 30 px must be 30 px wide whatever the render target), so UV has to be converted to px and back.
uniform vec2 u_size = vec2(1040.0, 560.0);

// Impact state. xy = contact point in px, z = strength in px (0 = no impact, so the shader is an exact
// pass-through when idle), w = age in seconds.
uniform vec4 u_impact = vec4(0.0, 0.0, 0.0, 0.0);

// Inward normal of the wall that was struck: the cushion yields ALONG this axis, so the dent is
// directional rather than a symmetric blob. A radial-only dent reads as a lens; a directioned one
// reads as a collision.
uniform vec2 u_normal = vec2(1.0, 0.0);

uniform vec4 u_tint = vec4(0.50, 0.91, 1.0, 1.0);

// Damped-oscillation shape. omega sets how fast it springs back (rad/s), decay how quickly the ring
// dies. At omega=30/decay=7 the first rebound lands at ~0.105 s, i.e. about six frames at 60 Hz --
// fast enough to feel like an impact rather than a wobble.
uniform float u_omega = 30.0;
uniform float u_decay = 7.0;

// Width of the compressed zone centred on the contact, px. Wider than the ball (>2x radius) because
// the cushion is a connected material: it cannot dent in one 12 px spot.
uniform float u_sigma = 88.0;

// How far the travelling wave runs down the wall, px, and its spatial wavelength.
uniform float u_reach = 300.0;
uniform float u_ripple = 26.0;

// Refraction strength. Splitting the channels laterally by a fraction of the displacement is what
// makes the distortion read as the surface REFRACTING rather than as the image merely being shifted.
// Clamped hard: beyond ~2% it looks like a rendering fault rather than an impact.
uniform float u_split = 0.35;

// NOTE ON COLOR, because getting this wrong cost real debugging time.
//
// In a Godot 4 canvas_item fragment shader, the incoming COLOR already contains texture(TEXTURE, UV)
// multiplied by the node's modulate. Assigning `COLOR = something * COLOR` therefore applies the
// texture TWICE, which squares every channel and darkens the whole table -- measured: the render target
// averaged 39.94 luminance and the composite 12.96, i.e. the table was present and correct in the
// viewport but nearly black on screen. Because this shader samples TEXTURE itself, the sampled value is
// assigned directly and COLOR is not read at all.
void fragment()
{
    vec2 uv = UV;

    float strength = u_impact.z;
    vec3 col;
    float alpha;

    if (strength <= 0.001) {
        // Exact pass-through. This branch matters twice over: without it every idle frame would pay for
        // three extra texture fetches and a transcendental, and the warp would subtly resample the whole
        // table even when nothing had happened.
        vec4 src = texture(TEXTURE, uv);
        COLOR = vec4(src.rgb, src.a);
    } else {
        vec2 px = uv * u_size;
        vec2 toContact = px - u_impact.xy;
        float d = length(toContact);
        float age = u_impact.w;

        // ---- the elastic envelope -------------------------------------------------------------
        // cos() is what makes this an impact rather than a flash: it goes positive (compressed),
        // negative (sprung back past rest), positive again (a smaller second compression), and so on
        // while the exponential squeezes the whole thing to nothing.
        float env = exp(-u_decay * age) * cos(u_omega * age);

        // ---- two superposed deformations ------------------------------------------------------
        float dent = exp(-(d * d) / (2.0 * u_sigma * u_sigma));         // local compression
        float wave = sin(d / u_ripple - u_omega * age) * exp(-d / u_reach);  // wave down the wall

        vec2 radial = toContact / max(d, 1e-3);

        // The dent pushes along the wall normal (the cushion gives way); the wave pushes radially, so
        // the disturbance travels outward through the surface instead of staying where it started.
        vec2 push = -u_normal * dent + radial * wave * 0.45;
        vec2 off = push * (strength * env);

        vec2 duv = off / u_size;

        // ---- refract --------------------------------------------------------------------------
        vec2 perp = vec2(-duv.y, duv.x);
        float split = clamp(length(off) * 0.0045, 0.0, 0.02) * u_split;

        vec4 src = texture(TEXTURE, uv + duv);
        col.r = texture(TEXTURE, uv + duv + perp * split).r;
        col.g = src.g;
        col.b = texture(TEXTURE, uv + duv - perp * split).b;
        alpha = src.a;

        // ---- the shoulder catches the light ----------------------------------------------------
        // Displacement alone is hard to see on a flat-coloured rail; the highlight at the dent's
        // shoulder is what tells the eye the surface has a shape. Kept additive and tinted so it
        // cannot darken anything.
        float shoulder = dent * env;
        col += u_tint.rgb * max(shoulder, 0.0) * 0.55;

        // Compressed zones darken slightly: material under compression is denser, and this is what
        // stops the dent from reading as a bright blob drawn on top.
        col *= 1.0 - clamp(-shoulder, 0.0, 1.0) * 0.18;

        COLOR = vec4(col, alpha);
    }
}
