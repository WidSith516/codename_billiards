using Godot;

namespace Rewind.Game;

/// <summary>
/// A decorative sine band: a steady strip that reverses, splits its RGB and stretches its colour
/// while the player is rewinding. Shader: <c>res://shaders/rewind_band.gdshader</c>.
///
/// THE GAME HAS TWO OF THEM, one mirrored at the top of the page and one at the bottom
/// (<see cref="TopSpec"/> / <see cref="BottomSpec"/>). They are separate instances of THIS class and
/// therefore have separate SubViewports - that is deliberate. Sharing one render target between two
/// flipped quads would only work if the mirror could be done in the blit, and a canvas_item draw
/// cannot flip a texture rect; two 620x14 passes cost nothing. What the two instances DO share is the
/// phase: Play drives both from the same rewind distance, so they are one effect shown twice, not
/// two independent animations.
///
/// WHY IT EXISTS (do not "clean this up"):
///  * it is the only element in the game whose motion is literally the cue ball's own retrograde
///    motion. Its phase is the REWOUND DISTANCE, not wall-clock time, so at a glance the player can
///    feel how far a rewind has gone without reading the HUD numbers;
///  * it is pure decoration. It reads <c>World.Rewinding</c> and a distance Play already measures for
///    the HUD; it writes nothing into the kernel, holds no game state, and no test references it.
///    Removing it must not change a single physics number. If it ever does, that is a bug.
///
/// HOW IT IS WIRED, and why it is wired this way:
///  * a <see cref="SubViewport"/> renders the shader into a texture, and the play scene draws that
///    texture as ONE quad in UI space. A canvas_item shader put on the play node itself would have
///    shaded the table, the balls and the HUD, so the effect must be contained in its own target;
///  * the sub-viewport is exactly the band's design size, so at the design resolution the band is
///    pixel-for-pixel and at any other window size it scales with everything else
///    (project.godot: stretch canvas_items / aspect keep);
///  * <see cref="Update"/> pushes only uniforms, and never rebuilds resources: a frame costs one
///    full-target shader pass per instance.
/// </summary>
public sealed partial class ReplayBand : Node2D
{
    /// <summary>The drawing recipe for one instance, in design-canvas pixels (1040x560).</summary>
    public struct Layout
    {
        /// <summary>Shader render target = the band's own design size, in px.</summary>
        public Vector2I Resolution;

        /// <summary>Where the band quad lands on the design canvas.</summary>
        public Vector2 CanvasOrigin;

        /// <summary>How big the band quad is on the canvas.</summary>
        public Vector2 CanvasSize;

        /// <summary>Radius in px of the elliptical band mask handed to the shader (u_mask.zw).</summary>
        public Vector2 MaskRadius;

        /// <summary>Mirror the strip vertically, so the rim hairline faces the canvas edge this band
        /// is nearest. 1 for the top band, 0 for the bottom one.</summary>
        public bool VerticalFlip;

        /// <summary>Per-instance exposure (u_emit). The two bands have the same pattern but different
        /// backdrops - the lower one is over the dark room, the upper one over the lit table - and with
        /// one shared exposure the upper band measured 2.9x dimmer on screen. This is what makes the
        /// pair read as one effect in two places instead of one bright and one faint.</summary>
        public float Emit;

        /// <summary>Free-form name used in the debug line and the capture log, e.g. "top"/"bottom".</summary>
        public string Name;
    }

    /// <summary>Band width in design px. The render target and the drawn quad are the same size.</summary>
    public const int BandWidth = 620;

    /// <summary>Band height in design px: a wide, short strip. 14 px is exactly the free strip at each
    /// edge of the design canvas, and it is enough for the ported field to hold two thin lines.</summary>
    public const int BandHeight = 14;

    /// <summary>Band left edge on the design canvas: right of the HUD panel's x = 256 (panel plus its
    /// 6 px inset), so only the already-faded left tail can reach it. The mask tapers the ends, which
    /// is why a quad that starts at x = 210 still leaves that panel's corner untouched.</summary>
    public const int BandLeft = 210;

    /// <summary>Bottom band's top edge. It starts BELOW everything else that occupies the bottom of
    /// the canvas: the rail (the table's physics box ends at y = 497), the HUD panel (ends at y = 500)
    /// and the notification toast (Hud.cs draws it at y = 494..544, border included). 546..560 is the
    /// one free strip, so it cannot cover information - and it must END at 560, because anything below
    /// that is outside the viewport.
    ///
    /// Found the honest way, from captured frames: the first position (478) and the second (492) were
    /// drawn over the toast, and the third (546 with height 26) ran off the bottom edge, so half the
    /// band was never on screen.</summary>
    public const int BottomBandTop = 546;

    /// <summary>Top band's top edge. The free strip there is y = 30..55: Hud.cs draws the top bar at
    /// y = 0..28 and both side panels start at y = 34, so 30..44 clears the bar by 2 px and the panel
    /// corner by 6 px, and the mirrored rim faces the canvas edge.
    ///
    /// NOT y = 0, which would be the exact mirror of the bottom band: the top bar's text sits at
    /// y = 8..24, so a band at the very top edge would draw over it. 30 is the nearest position that
    /// covers nothing - measured, not guessed.</summary>
    public const int TopBandTop = 30;

    /// <summary>Horizontal radius of the band mask, in px. Larger = a straighter, longer strip; this
    /// value tapers the ends over roughly a third of the width, which keeps the left tail clear of the
    /// HUD panels at both edges while the middle still reads as a deliberate band.</summary>
    public const float MaskRadiusX = 96.0f;

    /// <summary>Vertical radius of the mask. Deliberately much larger than the band's own height, so
    /// the fade is horizontal only: the strip stays evenly lit top to bottom, which is what makes it
    /// read as an edge decoration instead of a floating lozenge.</summary>
    public const float MaskRadiusY = 25.0f;

    /// <summary>Hairline along the band's outer edge: distance from that edge, and its half thickness.</summary>
    public const float RimInset = 2.0f;
    public const float RimHalfWidth = 1.5f;

    /// <summary>The mirrored pair. Play builds one instance from each.</summary>
    public static Layout BottomSpec => new Layout
    {
        Resolution = new Vector2I(BandWidth, BandHeight),
        CanvasOrigin = new Vector2(BandLeft, BottomBandTop),
        CanvasSize = new Vector2(BandWidth, BandHeight),
        MaskRadius = new Vector2(MaskRadiusX, MaskRadiusY),
        VerticalFlip = false,
        // Already at the top of the sRGB curve at 1.0 (a canvas_item draw is gamma-corrected), so
        // raising the lower band's exposure does nothing: measured peak 134.1 at emit 0.85, 6.0 - and
        // at 0.85 *linear* the 0.94 x 255 = 238 output is already saturated.
        Emit = 1.0f,
        Name = "bottom",
    };

    public static Layout TopSpec => new Layout
    {
        Resolution = new Vector2I(BandWidth, BandHeight),
        CanvasOrigin = new Vector2(BandLeft, TopBandTop),
        CanvasSize = new Vector2(BandWidth, BandHeight),
        MaskRadius = new Vector2(MaskRadiusX, MaskRadiusY),
        VerticalFlip = true,
        // The upper band measures ~2.9x dimmer than the lower one (peak 45.7 vs 134.1 at equal
        // exposure) because more of its wave's lattice falls inside the 14-px strip on this side of the
        // mirror and because it sits over the lit table rather than the dark room. 3.0 brings its peak
        // to the same order as the lower band's, which is what makes the pair read as one effect.
        Emit = 3.0f,
        Name = "top",
    };

    /// <summary>Phase units of wave travel gained per rewound pixel. 620 px of rewinding (one band
    /// width, ~2.6 s of crawl at the locked 240 px/s) scrolls the lattice exactly once, which is why
    /// the reversal is unmistakable at the speed the game actually rewinds.</summary>
    public const double RewindPhasePerPixel = 0.0038;

    /// <summary>Phase units added per second while the game is NOT rewinding: the steady drift that
    /// keeps the "steady sine" from being a still image.</summary>
    public const double RateIdle = 0.11;

    /// <summary>Rewind strength a scripted capture starts from, so a frozen frame still shows the
    /// rewind look. Live play ramps from 0 and cannot tell the difference at full crawl speed.</summary>
    public const float CaptureHighlight = 0.92f;

    /// <summary>Highlight units per second while a capture ramps up (see <see cref="PlaceCapture"/>).</summary>
    public const float CaptureRisePerSecond = 40.0f;

    private SubViewport _viewport;
    private ColorRect _quad;
    private ShaderMaterial _material;

    /// <summary>The recipe this instance was built with. DrawBand uses it, so two instances can live
    /// in different places: a hard-coded layout here was how the second band would have silently drawn
    /// on top of the first.</summary>
    private Layout _layout;

    /// <summary>u_time: pixel-equivalent phase. Its SIGN is the direction the wave travels.</summary>
    private float _time;

    /// <summary>0 = steady, 1 = rewinding. Smoothed, so the changeover has an attack and a decay.</summary>
    private float _highlight;

    /// <summary>Rewound distance in px at the previous update; the phase advance comes from its delta.</summary>
    private double _arc;

    /// <summary>True when a scripted capture explicitly placed the band in a state. Such a band must
    /// not be advanced by the live rules afterwards, or the capture would photograph an in-between.</summary>
    private bool _held;

    /// <summary>Whether the last <see cref="Update"/> call reported an active rewind.</summary>
    public bool Active { get; private set; }

    /// <summary>Diagnostic mirror of the uniforms, so a capture log and the HUD report the same numbers.</summary>
    public float Phase => _time;
    public float Highlight => _highlight;
    public bool Held => _held;
    public string BandName => _layout.Name ?? "?";

    /// <summary>Diagnostics for the self test: where this instance actually draws. Exposed rather than
    /// re-deriving it in Play, so the test checks the shipped layout and not a copy of it.</summary>
    public float BandTopForTest => _layout.CanvasOrigin.Y;
    public string BandSizeForTest => $"{_layout.Resolution.X}x{_layout.Resolution.Y}" +
                                     (_layout.VerticalFlip ? " flipped" : "");

    /// <summary>The render target's actual pixel size, for the resize trace.</summary>
    public string RenderTargetSizeForTest => _viewport == null ? "none" : $"{_viewport.Size.X}x{_viewport.Size.Y}";

    /// <summary>This instance's exposure, for the resize trace.</summary>
    public float EmitForTest => _layout.Emit;

    /// <summary>Set by <c>--band-ruler</c>: draw the 3-device-px white control bar inside the render
    /// target. Used only by tools/analysis/band_sharpness.py; never on in the game.</summary>
    public bool Ruler
    {
        get => _ruler;
        set
        {
            if (_ruler == value) return;
            _ruler = value;
            PushUniforms(null);
        }
    }

    private bool _ruler;

    /// <summary>Hide the band without hiding the render target. Hiding this NODE would stop the
    /// sub-viewport rendering, which is fine for <c>--no-band</c> but is not what "draw nothing"
    /// should mean anywhere else, so the flag only gates <see cref="DrawBand"/>.</summary>
    public void SetHidden(bool hidden)
    {
        _drawn = !hidden;
    }

    private bool _drawn = true;

    /// <summary>
    /// Device pixels per design-canvas pixel. The render target is sized with this so the band is
    /// rasterised at its TRUE on-screen size.
    ///
    /// WHY THIS EXISTS - it is the whole anti-blur fix. A band rendered at a fixed 620x14 design-pixel
    /// texture and then drawn into a stretched canvas is UPSCALED by this factor, and a linear-filtered
    /// upscale of a 1-px line is blur. Measured on this machine by capturing the same frozen frame at
    /// two window sizes and taking the mean horizontal luminance gradient per canvas pixel:
    ///
    ///     1040x560  (scale 1): mean|dL/dx| = 0.31   peak luminance = 203
    ///     2080x1120 (scale 2): mean|dL/dx| = 0.12   peak luminance = 151
    ///
    /// i.e. the edges were 2.6x softer in canvas terms and the lines 26% dimmer at 2x. Rendering at
    /// 1240x28 and drawing it back at its native size removes the resample entirely, at EVERY scale,
    /// for a cost proportional to the pixels actually on screen.
    /// </summary>
    private float _deviceScale = 1.0f;

    /// <summary>Size the render target to the band's real on-screen pixels. Called on build and on
    /// every window resize; a no-op when the scale has not changed, so it costs one comparison a frame.</summary>
    public void SyncToViewport(float deviceScale)
    {
        if (_viewport == null) return;
        deviceScale = Mathf.Clamp(deviceScale, 0.25f, 4.0f);
        if (Mathf.Abs(deviceScale - _deviceScale) < 0.005f) return;

        _deviceScale = deviceScale;
        _viewport.Size = new Vector2I(
            Mathf.Max(1, Mathf.RoundToInt(_layout.Resolution.X * deviceScale)),
            Mathf.Max(1, Mathf.RoundToInt(_layout.Resolution.Y * deviceScale)));
        PushUniforms(null);
    }

    public void Build(Layout layout)
    {
        if (_viewport != null) return;
        _layout = layout;

        // Nearest filtering for BOTH passes on the band's path: this node's own draw and the texture
        // rect inside the render target. `Play._Ready` sets Linear on the play node for the table
        // artwork, which would otherwise smooth the band's upscale - see DrawBand.
        TextureFilter = TextureFilterEnum.Nearest;

        _viewport = new SubViewport
        {
            Name = $"RewindBandTarget_{layout.Name}",
            Size = layout.Resolution,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        _viewport.GuiDisableInput = true;

        _material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/rewind_band.gdshader"),
        };

        _quad = new ColorRect
        {
            Name = $"RewindBandQuad_{layout.Name}",
            Color = new Color(1, 1, 1, 1),
            Material = _material,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        _quad.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        _viewport.AddChild(_quad);
        AddChild(_viewport);

        PushUniforms(layout);
    }

    /// <summary>
    /// Advance the band one frame.
    /// </summary>
    /// <param name="rewinding">Whether the kernel is currently crawling the cue ball backwards.</param>
    /// <param name="arcWalked">
    /// Distance already rewound in px: Play's own <c>ArcWalked()</c>, the same number the HUD prints.
    /// Its per-frame DELTA is the phase advance, which is what keeps the band free of frame-rate
    /// drift - the quantity it tracks is the kernel's fixed-step history, not the render clock.
    /// </param>
    /// <param name="delta">Frame delta in seconds, used for the envelope and the idle drift.</param>
    /// <param name="risePerSecond">
    /// How fast <paramref name="rewinding"/> becomes <see cref="Highlight"/>. A scripted capture
    /// prepares a rewind and then freezes the world, so it needs the rewind look on frame one.
    /// </param>
    public void Update(bool rewinding, double arcWalked, double delta, float risePerSecond)
    {
        if (_material == null) return;
        if (delta < 0.0) delta = 0.0;
        if (delta > 0.1) delta = 0.1; // a stall must not teleport the wave

        Active = rewinding;
        if (!_held)
        {
            var target = rewinding ? 1.0f : 0.0f;
            var rise = Mathf.Max(0.5f, risePerSecond);
            var rate = target > _highlight ? rise : rise * 0.25f; // decay is slower than the attack
            _highlight = Mathf.MoveToward(_highlight, target, (float)(rate * delta));

            // Forward play drifts at RateIdle per second; a rewind advances BACKWARDS by the
            // distance actually rewound. That asymmetry is the whole effect.
            var advance = rewinding
                ? (arcWalked - _arc) * RewindPhasePerPixel
                : RateIdle * delta;
            _time += (float)advance;
        }

        _arc = rewinding ? arcWalked : 0.0;

        // The lattice period is 1/1.9, so wrapping the phase by exactly one period is seamless AND
        // keeps u_time bounded. Unbounded it would both lose float precision in a long session and
        // make a captured phase impossible to compare between two runs.
        const double lattice = 1.0 / 1.9;
        _time = (float)(_time - Mathf.Floor(_time / (float)lattice) * lattice);

        PushUniforms(null);
    }

    /// <summary>
    /// Freeze the band in a scripted state. Deterministic on purpose: a capture that says "the R
    /// transition" has to show the transition, and a frame counter is not an honest way to hit one.
    /// </summary>
    /// <param name="captureStrength">Target highlight, e.g. <see cref="CaptureHighlight"/>.</param>
    /// <param name="ageSeconds">Seconds into the attack. The live rise rate is 9.0/s, so 0.055 s
    /// lands at highlight ~= 0.5: the moment R was pressed, half-way through the changeover.</param>
    public void PlaceCapture(float captureStrength, double ageSeconds)
    {
        _held = true;
        _highlight = Mathf.Clamp((float)(ageSeconds * 9.0), 0.0f, captureStrength);
        _arc = 0.0;
        PushUniforms(null);
    }

    /// <summary>Tell the band which state it is being displayed in. Live play sets this every frame
    /// from <c>World.Rewinding</c>; a capture says it explicitly, because a forced rewind look is not
    /// the same thing as a kernel that is actually rewinding and the label must not lie about it.</summary>
    public void SetActive(bool active)
    {
        Active = active;
    }

    /// <summary>Undo <see cref="PlaceCapture"/> so live play resumes normally.</summary>
    public void ReleaseCapture()
    {
        _held = false;
    }

    /// <summary>
    /// Pin the phase, so a capture can be taken at the SAME wave phase with the band steady and
    /// rewinding. That is what makes the two states comparable pixel for pixel: without it, the two
    /// frames would also differ by wherever the drift happened to be, and the comparison would prove
    /// nothing about the rewind. Wrapped to the lattice period, so any argument is legal.
    /// </summary>
    public void SetPhase(float phase)
    {
        const double lattice = 1.0 / 1.9;
        _time = (float)(phase - Mathf.Floor(phase / (float)lattice) * lattice);
        PushUniforms(null);
    }

    public void Reset()
    {
        _held = false;
        _time = 0.0f;
        _highlight = 0.0f;
        _arc = 0.0;
        Active = false;
        PushUniforms(null);
    }

    private void PushUniforms(Layout? layout)
    {
        if (_material == null || _viewport == null) return;
        // u_res is the DESIGN size, never the render-target size: the shader's pattern must not change
        // with the window. u_rt is the render target in device px, used only to map the fragment to
        // design coordinates inside band_uv().
        var design = layout.HasValue ? layout.Value.Resolution : _layout.Resolution;
        var radius = layout.HasValue ? layout.Value.MaskRadius : _layout.MaskRadius;

        _material.SetShaderParameter("u_time", _time);
        _material.SetShaderParameter("u_high", _highlight);
        _material.SetShaderParameter("u_res", new Vector2(design.X, design.Y));
        _material.SetShaderParameter("u_rt", new Vector2(_viewport.Size.X, _viewport.Size.Y));
        _material.SetShaderParameter("u_mask", new Vector4(design.X * 0.5f, design.Y * 0.5f, radius.X, radius.Y));
        _material.SetShaderParameter("u_rim", new Vector2(RimInset, RimHalfWidth));
        _material.SetShaderParameter("u_vflip", _layout.VerticalFlip ? 1.0f : 0.0f);
        _material.SetShaderParameter("u_ruler", _ruler ? 1.0f : 0.0f);
        _material.SetShaderParameter("u_emit", _layout.Emit <= 0.0f ? 1.0f : _layout.Emit);
        _material.SetShaderParameter("u_gold", new Vector3(Palette.Accent.R, Palette.Accent.G, Palette.Accent.B));
        _material.SetShaderParameter("u_neon", new Vector3(Palette.SessionStripe.R, Palette.SessionStripe.G,
            Palette.SessionStripe.B));
    }

    /// <summary>Mean luminance of the illuminated part of the band, read back from the render target.
    /// Exposed for callers that want to check the deployed exposure; returns 0 when the target cannot
    /// be read, which is a no-op rather than a failure.</summary>
    public float MeanLuminance()
    {
        if (_viewport == null) return 0.0f;
        var image = _viewport.GetTexture()?.GetImage();
        if (image == null) return 0.0f;
        var w = image.GetWidth();
        var h = image.GetHeight();
        double sum = 0.0;
        var n = 0;
        for (var y = 0; y < h; y += 2)
        {
            for (var x = 0; x < w; x += 2)
            {
                var c = image.GetPixel(x, y);
                if (c.R + c.G + c.B <= 0.0f) continue;
                sum += 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                n++;
            }
        }

        return n == 0 ? 0.0f : (float)(sum / n);
    }

    /// <summary>One line for the F1 debug overlay and the capture log: what the band currently is.</summary>
    public string Stats()
    {
        return $"mode={(Active ? "REWIND" : "STEADY")}{(Held ? "/HELD" : "")} phase={_time:F3} high={_highlight:F3}";
    }

    /// <summary>
    /// Draw the rendered band into the CURRENT canvas space. The play scene calls this last, in UI
    /// (unscaled) space, so the band lands on top of the table and the HUD, not under them.
    /// Named DrawBand rather than Draw so it cannot be confused with CanvasItem.Draw.
    ///
    /// The rect is the DESIGN rect, but the texture behind it is `_deviceScale` times larger, so the
    /// two are a 1:1 device-pixel mapping and there is no resample. That is why this node and the
    /// texture rect are both set to NEAREST filtering: with a matched size there is nothing to
    /// interpolate, and nearest forbids it if the match is ever a fraction off.
    /// </summary>
    public void DrawBand(CanvasItem ci, Color tint)
    {
        if (_viewport == null || !_drawn) return;
        var tex = _viewport.GetTexture();
        if (tex == null) return;
        ci.DrawTextureRect(tex, new Rect2(_layout.CanvasOrigin, _layout.CanvasSize), false, tint);
    }
}
