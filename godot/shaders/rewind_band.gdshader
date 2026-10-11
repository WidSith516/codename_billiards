// REWIND — bottom decorative band. Design-intent file: read this header before editing it.
//
// Source: this Shadertoy fragment was the starting point, not the specification.
//
//   void mainImage(out vec4 O, vec2 u) {
//       O.xy = asin( 4.* u / iResolution.xy - 2. ),
//       O.zw = - O.xy;
//       O =  O.xxzz / 2. - O.ywyw / 3. + iTime;
//       O = fwidth(O) / abs( fract( O * 1.9 ) -.5 ); O += O.a; }
//
// WHAT WAS KEPT FROM IT: the asin setup, the (x,x,z,z)/2 - (y,w,y,w)/3 combination, the iTime phase
// and the 1.9 lattice. Those are what give the row of thin parallel lines a billiard-table feel.
//
// WHAT WAS REPLACED, and why (this is measured, not taste - see tools/analysis/band_metrics.py and
// the numbers in godot/BAND_REPORT.md):
//   * the shader computed the field and then divided fwidth(field) by |fract(field*1.9)-0.5|. That
//     form is a solid glowing bar here, not a sine: the denominator is small almost everywhere, so
//     the whole strip blows out. The field is now resolved into a lattice distance directly, which
//     gives lines of a chosen width in pixels.
//   * the three channels were given DIFFERENT WEIGHTS on the same field. Measured effect: the split
//     did not widen under rewind (0.14 -> 0.04 normalized profile difference). The channel split is
//     now an explicit lateral phase offset in PIXELS, which is both visible and measurable.
//
// INTENT (what this file is FOR, so a later edit does not quietly destroy it):
//   1. STEADY STATE IS A STEADY SINE. At u_time ~= 0 the field is almost static: a row of thin gold
//      lines (#ffd166). "Almost" is deliberate - it must not be a frozen image, so a residual drift
//      remains (DRIFT_* below).
//   2. REWIND REVERSES THE WAVE. u_time is fed in pixel-equivalent units whose SIGN is the sign of
//      the cue ball's motion: positive while the game runs forward, NEGATIVE while R rewinds. The
//      wave therefore travels the other way during rewind, and the phase travelled is proportional
//      to the distance actually rewound (Play.cs ReplayBand), not to wall-clock time.
//   3. REWIND SPLITS THE RGB AND STRETCHES THE COLOUR. Each channel samples the field at its own
//      lateral offset (u_split), so the lines separate into red/green/blue copies, and u_high widens
//      the separation and softens the line - the "stretched" read.
//   4. THE COLOUR STAYS IN THE PROJECT PALETTE. Steady gold #ffd166, rewind neon cyan #7de2ff
//      (Palette.Accent / Palette.SessionStripe).
//   5. IT IS DECORATION. Nothing reads this shader; it cannot move a ball, a number or a test.
//
// Deliberate deviation: every fract() sits inside an abs() with a floor. The literal port divides by
// a term that reaches exactly zero twice per line, which is a hard NaN/Inf source.
shader_type canvas_item;
render_mode blend_mix;

// Hard-coded so the pattern cannot change with the render target. u_res is fed with the band's DESIGN
// size, which is fixed; the render target may be larger (see u_rt) and that must not alter the look.
const vec2 BAND_RES = vec2(620.0, 14.0);

// Phase units of WAVE travel contributed by one pixel of u_time. The traversed phase of the band's
// vertical span is ~(asin range at the top/bottom rows) x this, i.e. about one lattice period, so
// the pattern really travels across the strip instead of merely shimmering.
const float TIME_COEFF = 1.6;

// "Almost" steady: residual drift amplitudes in phase units. Play.cs advances u_time at
// ReplayBand.RateIdle per second while the game is not rewinding.
const float DRIFT_COEFF = 2.8;
const float DRIFT_PERP = 0.35;

// Positive = the steady wave travels right, so a rewind travels left.
const float WAVE_DIR = 1.0;

// Breath: the original's sin * cos amplitude modulation, which is what makes the lines thicken and
// thin. Tiny at rest, full under rewind.
const float BREATH_FREQ_A = 10.0;
const float BREATH_FREQ_B = 8.0;

// Lattice and line geometry. LATTICE keeps the original's 1.9. LINE_WIDTH is the line's half-width
// in PIXELS, which is what keeps the lines thin at any band height instead of scaling with the
// local derivative.
const float LATTICE = 1.9;
const float LINE_WIDTH = 0.42;   // px
const float FLOOR = 1e-3;

// Exponent of the line's radial falloff: intensity = 1 / (1 + core^FALLOFF_POWER).
//
// This is the "it looks blurry" fix. At power 1 (what shipped first) the profile is a broad Lorentzian:
// the core only reaches ~85% and its flanks hold most of the energy, so at 1x the band measured 2 of
// its 3 lit rows as INTERMEDIATE luminance and read as a soft ribbon rather than a drawn line. A CPU
// sweep of the profile shape is in godot/BAND_REPORT.md section 4.3: power 2 lifts the core-to-flank
// step from 0.85 to 0.98 at the SAME one-pixel FWHM, i.e. crisper without getting thicker.
const float FALLOFF_POWER = 2.0;

uniform float u_time = 0.0;      // pixel-equivalent phase, signed: negative while rewinding
// NOTE: a uniform initialiser must be a LITERAL. `= BAND_RES` is rejected with
// "Expected constant expression after '='", which is how this line was found.
uniform vec2  u_res = vec2(620.0, 14.0);  // band DESIGN size; the pattern's coordinate space
// The render target in DEVICE px. Play scales this with the window (`ReplayBand.SyncToViewport`) so
// the band is rasterised at its true on-screen size instead of being upscaled - an upscaled 1-px line
// is blur, and that was measured: mean|dL/dx| 0.31 at scale 1 vs 0.12 at scale 2.
//
// Everything below works in DESIGN units, so this uniform is used in exactly one place: band_uv().
uniform vec2  u_rt = vec2(620.0, 14.0);
uniform float u_high = 0.0;      // 0 steady .. 1 rewinding (Play.cs ReplayBand.Highlight)
uniform vec3  u_gold = vec3(1.0, 0.82, 0.40);   // #ffd166
uniform vec3  u_neon = vec3(0.49, 0.886, 1.0);  // #7de2ff
uniform float u_emit = 1.0;      // global exposure, for tuning against the room backdrop
uniform float u_split = 1.6;     // lateral RGB separation at u_high = 1, in px
uniform float u_vflip = 0.0;     // 1 = mirror vertically, so the TOP band's rim faces upward
// (centre.x, centre.y, radius.x, radius.y) of the elliptical band mask in design px. Play.cs owns
// these numbers because it owns the layout; nothing here may invent a position.
uniform vec4  u_mask = vec4(310.0, 7.0, 96.0, 25.0);
// Hairline along the top edge: (centre y, half width) in design px.
uniform vec2  u_rim = vec2(2.0, 1.5);
// Diagnostic control bar (0 = off, 1 = on). Draws a 3-design-px pure-white bar across the middle of the
// render target, so tools/analysis/band_sharpness.py can read the rasterisation resolution straight off
// a screenshot: the bar's height in device px is 3 x the window scale IF AND ONLY IF the band is being
// rendered at device resolution rather than upscaled. Nothing in the game enables this.
uniform float u_ruler = 0.0;
// Dark backing plate: the colour, and how much of it. See the BACKING PLATE note in fragment().
uniform vec3  u_plate = vec3(0.045, 0.055, 0.065);
// 0.55, not the 0.88 tried first: at 0.88 the plate won and the lines read as faint marks ON a panel
// instead of lines on a dark strip - the strip got darker but the wave got quieter with it. Measured,
// the visible fraction of the strip fell to 0.24 at 0.88 and the plate had to lighten as well: at this
// value the strip keeps its contrast against the felt without swallowing the wave's glow.
uniform float u_plate_mix = 0.55;

// smoothstep with the argument order the call sites want: smoothstep(3,1,x) is undefined in GLSL
// when edge0 > edge1, and an earlier draft of this file was written that way.
float ss(float lo, float hi, float x)
{
    return smoothstep(lo, hi, clamp(x, lo, hi));
}

// The band's own coordinates. A TOP instance passes u_vflip = 1, which mirrors the strip in place:
// the rim hairline ends up along its top edge instead of its bottom one, so the upper band of the
// pair reads as the mirror of the lower one. It costs nothing because every use below samples the
// pattern through this vector rather than through UV directly.
//
// UV is a FRAGMENT built-in, not a global: it is not visible inside a helper function. Passing it in
// is the fix - `vec2 band_uv() { ... UV ... }` fails to compile with "Unknown identifier: UV".
vec2 band_uv(vec2 screen_uv)
{
    // Map to the render target, then divide by the device scale to land in DESIGN pixels. At scale 1
    // this is exactly `screen_uv * u_res`, so the pattern is unchanged from the pre-fix renders.
    vec2 uv = screen_uv * u_rt * (u_res / u_rt);
    if (u_vflip > 0.5) uv.y = u_res.y - uv.y;
    return uv;
}

// The asin setup, kept from the original. One value per input axis, so the field below can consume
// it twice the way O.xxzz / O.ywyw does.
vec2 enc(vec2 uv)
{
    return asin(clamp(4.0 * uv / u_res - 2.0, vec2(-1.0), vec2(1.0)));
}

// The original's O.xxzz / 2. - O.ywyw / 3., as the 2-vector the lattice is taken on.
vec2 base_field(vec2 e)
{
    return vec2(e.x / 2.0 - e.y / 3.0, e.y / 2.0 - e.x / 3.0);
}

// Locally-varying luminance used ONLY for the line-quality weighting. It never moves a line: every
// channel and every term below is a function of the same `shimmer` for a given pixel, so it cannot
// introduce a lateral shift of its own.
float shimmer(vec2 uv, float t)
{
    vec2 q = uv / u_res - 0.5;
    return abs(sin(3.0 * q.x + 2.0 * q.y + t * 0.6)) * abs(cos(2.0 * q.x + 4.0 * q.y + t * 0.4));
}

// B (blue) is the reference channel: it is offset 0, so the split is anchored in the palette's own
// most-visible-for-cyan channel and the other two are measured against it.
vec3 channel_offset(float hi)
{
    float d = u_split * hi;
    return vec3(d, d * 0.5, 0.0);
}

// One channel's brightness. `scale` and `phase` are the channel's own; `breath` is shared so the
// three channels stay one object rather than three unrelated pictures.
float lobe(vec2 uv, float scale, float breath)
{
    vec2 e = enc(uv);
    vec2 v = base_field(e) * scale;

    // Amplitude breathing (the original's sin * cos), then the travelling phase. The phase term
    // carries the sign of the motion; the drift rides along with it so the whole field unfolds
    // backwards during a rewind instead of merely crawling the other way.
    v *= 1.0 + breath;
    v += WAVE_DIR * (TIME_COEFF * u_time + DRIFT_COEFF * sin(u_time * 0.7));
    v += DRIFT_PERP * cos(u_time * 0.5);

    // Lattice distance in each component, flattened to one scalar: 0 on a line centre, 0.5/(1/LATTICE)
    // away from it. `+ 0.5` before the fract puts the line centres on the lattice nodes.
    vec2 d = abs(fract(v * LATTICE + 0.5) - 0.5) / LATTICE;

    // Line width in PIXELS. fwidth(v) is how much the field moves across one pixel, so dividing the
    // pixel target by it converts the target into field units at every row - a wide band and a short
    // band get the same thin line, which is what makes the lines stay lines when the band is resized.
    vec2 slope = max(fwidth(v), vec2(FLOOR));
    vec2 core = d / slope / max(LINE_WIDTH, FLOOR);

    // A higher FALLOFF_POWER trades the broad Lorentzian halo for a defined edge at the same FWHM.
    // `pow` keeps the exponent in one place; the shader language has no `**`.
    vec2 coreP = pow(core, vec2(FALLOFF_POWER));
    return 1.0 / (1.0 + coreP.x * coreP.x + coreP.y * coreP.y);
}

void fragment()
{
    vec2 uv = band_uv(UV);
    float hi = clamp(u_high, 0.0, 1.0);

    float soft = smoothstep(0.10, 0.62, shimmer(uv, u_time));

    // Breathing amplitude: tiny at rest so the steady state is a clean sine, real under rewind.
    float breath = (0.020 + 0.150 * hi) * (0.35 + 0.65 * soft) * sin(u_time * BREATH_FREQ_A)
                   * cos(u_time * BREATH_FREQ_B);
    // Per-channel lattice scale = the original's three channel weights (the /2 and /3 terms), plus
    // the lateral pixel offset that produces the visible RGB separation.
    vec3 off = channel_offset(hi);
    vec3 scale = vec3(0.55, 0.90, 1.50);

    vec3 col;
    col.r = lobe(uv + vec2(off.r, 0.0), scale.r, breath);
    col.g = lobe(uv + vec2(off.g, 0.0), scale.g, breath);
    col.b = lobe(uv + vec2(off.b, 0.0), scale.b, breath);
    col = clamp(col, 0.0, 1.0);

    // Hue-preserving exposure: multiply the palette, never average the channels.
    float e = dot(col, vec3(0.2126, 0.7152, 0.0722));
    vec3 pale = mix(u_gold, u_neon, hi);
    // 2.0 rather than 1.55: with a dark plate now behind the strip the lines have to out-read their own
    // backdrop, and the old level only looked adequate because the room behind the lower band was
    // already dark.
    vec3 rgb = pale * (e * 2.0 + e * e * 1.5);

    // What is left after the luminance weight is pure hue difference: red on one flank of a line,
    // cyan on the other, zero in the dark. This is the "phase offset" the eye reads as a split.
    vec3 d = col - vec3(e);
    rgb *= 1.0 + vec3(1.0, 1.02, 1.10) * d * (0.55 + 0.85 * hi);

    // Rewind gets a harder edge: a bright pass that widens as u_high rises. This is the "stretch".
    rgb += pale * pow(e, 2.6) * 0.55 * hi;

    // Rewind hue decision, and it has to be a DECISION rather than a hint. Measured on the band's own
    // lit pixels, the steady state is red-dominant (R/G/B 148/111/78): the palette's red simply wins
    // the tone map, so no amount of ADDED blue crosses over - three earlier attempts moved the number
    // by 7, 12 and 26 of the 70 it needed, and all the band did was get slightly brighter.
    //
    // So the rewind KEEPS red and lifts green and blue multiplicatively, which moves chroma without
    // moving exposure the way an additive term does. Steady state is untouched: at hi = 0 all three
    // weights are exactly 1, which is why the gold capture is identical to before this term existed.
    rgb *= vec3(1.0, 1.0 + 0.85 * hi, 1.0 + 2.20 * hi);
    rgb = min(rgb, vec3(1.4));

    // Geometry: one elliptical mask, so the band cannot land on the HUD panel or the rail. The radii
    // are much larger than the band, which makes the fade a taper rather than a visible oval.
    float m = ss(1.0, 0.15, length((uv - u_mask.xy) / u_mask.zw));

    // BACKING PLATE. This is what makes the strip READABLE, and it is the answer to "it looks blurry".
    //
    // The lower band sits over the dark room and read fine. The upper one sits over the table's green
    // felt, where a gold line and its backdrop are both mid-brightness: measured on the top strip, the
    // area between the lines and the flanking rows differed by only ~8 luminance, so the wave dissolved
    // into the cloth. Brightening the lines cannot fix that - the problem is contrast with the BACKDROP,
    // not the lines' level. A dark plate under the wave restores the contrast, and it rides the same
    // elliptical mask so it tapers with the band instead of reading as another HUD panel.
    rgb = mix(rgb, u_plate, u_plate_mix);

    // A hairline along the band's outer edge separates it from the table above it. It rides u_high, so
    // "rewind is happening" is legible without reading any text.
    float top = 1.0 - ss(0.0, u_rim.y, abs(uv.y - u_rim.x));
    rgb += u_neon * top * (0.25 + 0.75 * hi) * m;

    // Alpha: opaque on the lines, fading out only in the genuinely dark space between them.
    //
    // This used to be `alpha = max channel` - i.e. it tracked the brightest channel, not the
    // perceptual luminance. Over the dark room behind the bottom band that reads fine; over the
    // table's FELT behind the top one, the green backdrop pulled the gold steady state to
    // 163/145/85 (a greenish gold) because a third of the strip was the felt showing through.
    //
    // Two attempts are recorded here because the first one was a regression I had to measure to see:
    //   * `0.45 + 0.55 * dot(rgb, luminanceWeights)` - colour correct, but it cut the mid-tones so
    //     the whole band went dim (bottom band lit pixels 157 -> 123) and the rewind state lost its
    //     punch. The dot product is perceptually weighted and therefore small for a saturated colour.
    //   * the shipped form below: track the MAX channel, which is what "is this pixel lit" actually
    //     asks, and lift the floor. Lines stay near-opaque on both backdrops, the glow keeps its
    //     smooth tail, and the measured numbers land in between the two.
    // What the band would draw on its own, before the optional ruler overrides it.
    float mx = max(max(col.r, col.g), col.b);   // per-pixel "lit" amount, before the palette is applied
    float a = clamp((0.42 + 0.58 * clamp(mx * 1.6, 0.0, 1.0)) * m, 0.0, 1.0);
    // The plate must be opaque wherever it is drawn: it exists to be a dark backdrop, and the mask's
    // taper is already carried by `m` so it is not applied twice.
    a = max(a, u_plate_mix * m);

    // Diagnostic ruler, drawn last so nothing can tint it. Its half-height is expressed in DEVICE
    // pixels and converted through the device scale, and it is forced fully opaque: the bar's height
    // and peak are then a measurement of the rasterisation resolution and of nothing else.
    // NOTE: no variable declarations inside a branch, and no reuse of an existing name - Godot's shader
    // compiler rejected both (`Unknown identifier: 'a'` and `Redefinition of 'scale'`; `scale` is already
    // the per-channel lattice scale above). This is a new name on purpose.
    float devScale = max(u_rt.y / u_res.y, 1e-3);
    float dy = abs(uv.y - u_res.y * 0.5);
    float ruler = u_ruler * ss(1.5 * devScale + 1.0, 1.5 * devScale, dy);
    rgb = mix(rgb, vec3(1.0), ruler);
    a = max(a, ruler);

    COLOR = vec4(rgb * u_emit, a);
}
