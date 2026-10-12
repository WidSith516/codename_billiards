using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// M2 presentation layer: one playable level, end to end, keyboard + mouse.
///
/// What is on screen and why (requirements 1-8 of the M2 brief):
///  1. the table, balls, pockets and a HUD, driven by the deterministic kernel at its fixed step;
///  2. strike input: drag back from the cue ball to set angle + power, release to strike, a power
///     meter, and - the core rule - the strike locks forever after one use. The UI says so in
///     words and refuses to enter aim mode again;
///  3. the recorded cue history as a time-graded polyline with no corner cutting, segmented per
///     rewind session, with turn-around markers (<see cref="TimelineTrail"/>);
///  4. the rewind state: orange cue, highlighted traversed span, and a forecast that highlights the
///     balls the rewind is about to push *before* the contact (<see cref="RewindVisualizer"/>);
///  5. exact aim prediction via kernel simulation, not a fixed-length line (<see cref="AimPredictor"/>);
///  6. rewind scrubbing: pause mid-rewind, move the release point backwards, release there;
///  7. state readability: object balls left, strike used, rewinding, win; ball identity is carried
///     by a number and a glyph, never by colour alone (<see cref="BallStyles"/>);
///  8. a toggleable debug overlay with live numbers, including a non-finite check.
///
/// Headless verification hooks (all of this is what makes the screenshots evidence):
///   --level=&lt;id&gt; --out=&lt;file.png&gt; --capture-frames=N --min-frames=N
///   --state=aim|strike|rewind|win|debug      prepare that UI state, then capture
///   --selftest=prediction|trail|all          exactness checks, exit non-zero on failure
///   --bench=trail50k|idle                    frame-timing measurement
///   --script=&lt;name&gt;                        extra scripted demos
/// </summary>
public partial class Play : Node2D
{
    // ------------------------------------------------------------------ config
    private const int MaxStepsPerFrame = 8;

    /// <summary>Longest real frame we will simulate. Guards the spiral of death after a stall.</summary>
    private const double MaxFrameDelta = 0.25;

    /// <summary>Playfield scale: the table geometry comes from the level, this only fills the canvas.</summary>
    private const float ViewScale = 1.0f;

    /// <summary>The design canvas width, in the same units as <see cref="ViewScale"/>. Used to turn the
    /// real viewport size into a device-pixel scale for the decorative bands.</summary>
    private const float DesignCanvasWidth = 1040.0f;

    /// <summary>The design canvas height. The warp target is rendered at the full design size so the
    /// impact's pixel-space distances (dent sigma, push) mean the same thing as level coordinates.</summary>
    private const float DesignCanvasHeight = 560.0f;

    /// <summary>Shorthand for the warp render target size, in px.</summary>
    private const int DesignWidth = (int)DesignCanvasWidth;
    private const int DesignHeight = (int)DesignCanvasHeight;

    /// <summary>
    /// Which 60 Hz frame of a live impact the <c>--state=rail</c> capture freezes on.
    ///
    /// Frame 0 is the peak of the elastic envelope (cos(0) = 1), which is the only moment the cushion
    /// is compressed as far as it goes. See the comment at the capture site: because the envelope is a
    /// damped COSINE it passes through zero on the way back, so freezing at an arbitrary age can
    /// photograph a fully-settled table.
    /// </summary>
    private const double PeakHoldAge = 0.0;

    private World _world;
    private LevelData _level;
    private readonly AimPredictor.Cache _aimCache = new AimPredictor.Cache();
    private TimelineTrail _trail;
    private readonly RewindVisualizer _rewindVis = new RewindVisualizer();

    /// <summary>Transient cosmetic effects (rail impacts, pot bursts, card reveals). Never read by
    /// the simulation, so they cannot affect it.</summary>
    private readonly Effects _effects = new Effects();

    /// <summary>The table surface's render target and the material that UV-displaces it on impact.
    /// Null when the warp shader is unavailable, in which case the surface draws unwarped.</summary>
    private SubViewport _warpViewport;
    private Node2D _warpSource;
    private ShaderMaterial _warpMaterial;

    /// <summary>The room backdrop. A ShowBehindParent child, so it renders BEFORE the warped table
    /// layer; both sit under everything Play's own _Draw paints.</summary>
    private Node2D _roomLayer;

    /// <summary>Set by <c>--dump-warp=</c>: write the warp render target itself to disk, so "the target
    /// is empty" can be told apart from "the shader output is wrong".</summary>
    private string _dumpWarpPath;

    /// <summary>Set by <c>--no-fx</c>: force the warp to its idle pass-through as well as clearing the
    /// effect list. Needed so a "no effects" baseline is genuinely free of the distortion.</summary>
    private bool _suppressWarp;

    /// <summary>The impact currently fed to the warp shader, cached so the idle transition can be
    /// detected without rebuilding the vector every frame.</summary>
    private bool _warpActive;
    private Vector2 _impactContact;
    private Vector2 _impactNormal;
    private float _impactPush;
    private float _impactAge;
    private Color _impactTint = Colors.White;

    /// <summary>Index the player has highlighted in the card hand, or -1.</summary>
    private int _cardHover = -1;

    /// <summary>
    /// Set by <c>--state=rail</c>: hold transient effects at their birth so a capture taken several
    /// frames later still shows them. Capture plumbing only; live play never sets this.
    /// </summary>
    private bool _captureHoldEffects;

    /// <summary>The hand currently on screen, mirrored from the world so the draw is not rebuilt every
    /// frame (and so a paused draw cannot shift under the cursor).</summary>
    private readonly System.Collections.Generic.List<CardId> _cardHand =
        new System.Collections.Generic.List<CardId>();

    // The decorative sine bands. Two of them, mirrored at the top and bottom of the page; see
    // godot/src/game/ReplayBand.cs. Decoration with no game state, driven by the rewind distance the
    // HUD already measures.
    private ReplayBand[] _bands;
    private bool _showBand = true;
    private double _bandArc;
    private double _bandHoldAge;
    private double _bandHoldSeconds = 0.055;

    /// <summary>Set by <c>--band-phase=</c>: pin the wave phase so two capture states can be compared
    /// at the same phase. NaN means "not pinned".</summary>
    private double _bandPhase = double.NaN;

    /// <summary>Seconds before a pinned band (<c>--band-mode</c>) hands control back to live play.
    /// Long by default so a capture never sees it; short enough to interactively inspect a state.</summary>
    private double _bandReleaseCountdown = 600.0;
    private string _bandForceMode;

    private Font _font;
    private Texture2D _tableTexture;
    private Node2D _tableBackground;
    private double _accumulator;
    private double _wallClock;

    // ---------------------------------------------------------------- aiming
    private bool _dragging;
    private Vector2 _dragPoint;
    private double _aimAngle;
    private double _aimPower;
    private bool _hasAim;

    // ---------------------------------------------------------------- capture
    private string _outPath;
    // Null unless the caller explicitly asks for a scripted capture state. This MUST default to
    // null rather than "aim": see the guard around PrepareState in _Ready.
    private string _stateName;
    private int _captureFrames = 6;
    private int _minFrames = 4;
    private int _frames;
    private bool _captureQuit;
    private string _benchMode;
    private string _selfTestMode;
    private string _script;
    private int _scriptTicks;
    private bool _freezeWorld;
    private bool _scriptDone;

    // ---------------------------------------------------------------- hud state
    private bool _debugOverlay;

    /// <summary>Whether the whole left HUD panel is drawn. F1 toggles it so a recording sees a
    /// clean table; the top bar is always drawn.</summary>
    private bool _showHud = true;

    /// <summary>Guards the one-time progress write when the level is cleared.</summary>
    private bool _resultRecorded;
    private int _earnedStars;

    /// <summary>
    /// On-level coaching, shown only the first time a player opens a level.
    ///
    /// Deliberately bubbles pinned to the thing being taught rather than a modal tutorial: the user
    /// asked for the teaching to happen inside the existing level, and a step is marked done by the
    /// player ACTUALLY performing the action, not by pressing "next". Reading is skippable; doing is
    /// not.
    /// </summary>
    private int _tutorialStep;
    private bool _tutorialActive;
    private double _tutorialStepStartTime;

    private bool TutorialVisible => _tutorialActive;
    private bool _verbose;
    private double _messageTimer;
    private string _message = "";
    private Color _messageColor = Palette.TextMain;

    // ---------------------------------------------------------------- timing
    private readonly List<double> _frameMs = new List<double>();
    private double _lastFrameAt;
    private double _lastFrameMs;
    private double _drawMs;
    private double _simMs;
    private double _maxFrameMs;
    private int _authoredVertexCounts;

    // ---------------------------------------------------------------- selftest
    private int _checksRun;
    private int _checksFailed;

    public World World => _world;
    public TimelineTrail Trail => _trail;

    // ================================================================== setup

    /// <summary>
    /// The UI font, and the only reason this is explicit: the localization is Simplified Chinese and
    /// Godot's built-in fallback font contains no CJK glyphs, so a translated string drawn with it
    /// renders as tofu boxes (□□□) while every "is the frame non-blank" check still passes.
    /// The file is a plain single-face .ttf on purpose - .ttc collections need a face index and are
    /// the other way this silently fails. See LOCALIZATION.md for the licence note.
    /// </summary>
    private const string UiFontPath = "res://assets/DroidSansFallback.ttf";

    private static Font LoadUiFont()
    {
        Font font = null;
        if (ResourceLoader.Exists(UiFontPath))
        {
            font = ResourceLoader.Load<FontFile>(UiFontPath);
        }

        if (font == null && FileAccess.FileExists(UiFontPath))
        {
            // Reachable when the .ttf has not been through the importer yet (fresh checkout, so no
            // .godot/imported entry): load the file directly rather than falling back to tofu.
            var direct = new FontFile();
            if (direct.LoadDynamicFont(UiFontPath) == Error.Ok) font = direct;
        }

        if (font == null)
        {
            GD.PushError($"[play] CJK font not loadable from {UiFontPath}; Chinese text will be tofu");
            font = ThemeDB.FallbackFont;
        }

        return font;
    }

    /// <summary>
    /// True only when the caller explicitly asked for a scripted capture state.
    ///
    /// Extracted as a pure function because the original inline logic defaulted the state name to
    /// "aim", so PrepareState always ran, always ended by freezing the world, and the shipping game
    /// therefore never stepped its simulation: striking did nothing at all. That failure is silent
    /// (the game renders and accepts input normally), so it needs a test that does not depend on
    /// reading the screen. A null state name means live play and must never freeze.
    /// </summary>
    public static bool IsScriptedCapture(string stateName) => stateName != null;

    public override void _Ready()
    {
        _font = LoadUiFont();

        // Apply the saved presentation theme BEFORE anything is loaded or drawn, so the table texture,
        // pocket style and ball colours all agree. Themes are colours only -- no physics, no geometry.
        //
        // --theme=N overrides the saved choice. Needed because the theme decides which table-artwork
        // branch runs, and that branch is now routed through the warp like every other; without an
        // override the non-default themes could only be reached by clicking through the menu, so a
        // capture-based check of them was impossible.
        var themeArg = IntArg("--theme=", -1);
        Palette.ApplyTheme(themeArg >= 0 ? themeArg : Menu.ThemeIndex);

        _tableTexture = GD.Load<Texture2D>("res://assets/h5/table.svg");
        TextureFilter = TextureFilterEnum.Linear;

        // Player-facing text is Simplified Chinese and Godot's built-in fallback font has no CJK
        // glyphs at all, so anything that draws without an explicit font would render as tofu boxes.
        // Installing the same font as the window theme default makes that failure mode unreachable
        // for Controls added later, not just for the immediate-mode draw calls below.
        GetTree().Root.Theme = new Theme { DefaultFont = _font, DefaultFontSize = 13 };

        // H5 artwork uses the same 1040x560 design canvas as the game. Keep the complete frame
        // and six pocket mouths visible; physics and input remain in level coordinates.
        Scale = new Vector2(ViewScale, ViewScale);

        // The front end sets SelectedLevelId when the player picks a level; the CLI argument still
        // wins so every existing capture/bench invocation keeps working unchanged.
        var levelId = Arg("--level=") ?? Menu.SelectedLevelId ?? LevelIds.Corridor;
        _outPath = Arg("--out=");
        // Only a scripted capture prepares a frozen state. Defaulting this to "aim" (as it did)
        // meant PrepareState ran on EVERY launch, and PrepareState ends by setting _freezeWorld,
        // so the shipping game started with the world frozen and _world.Step() was never called:
        // the cue ball could not move. It also meant every screenshot we ever captured came from
        // a frozen world, so the visual evidence loop could never have caught it.
        _stateName = Arg("--state=");
        _captureFrames = IntArg("--capture-frames=", 6);
        _minFrames = IntArg("--min-frames=", 4);
        _benchMode = Arg("--bench=");
        _selfTestMode = Arg("--selftest=");
        _script = Arg("--script=");
        _verbose = HasArg("--verbose");
        _debugOverlay = HasArg("--debug") || _stateName == "debug";

        // ---- decorative sine bands, mirrored top and bottom (see ReplayBand.cs) ---------------
        // A band failure must never take the game down with it: the effect is decoration, so the
        // nodes are built defensively and a missing shader leaves the table exactly as it was.
        _showBand = !HasArg("--no-band");
        _bandForceMode = Arg("--band-mode=");
        _bandHoldAge = DoubleArg("--band-hold=", -1.0);
        try
        {
            var specs = new[] { ReplayBand.BottomSpec, ReplayBand.TopSpec };
            _bands = new ReplayBand[specs.Length];
            for (var i = 0; i < specs.Length; i++)
            {
                var band = new ReplayBand { Name = $"ReplayBand_{specs[i].Name}" };
                AddChild(band);
                band.Build(specs[i]);
                band.SetHidden(!_showBand);
                band.Ruler = HasArg("--band-ruler");
                _bands[i] = band;
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[play] bands disabled: {ex.GetType().Name} {ex.Message}");
            _bands = null;
        }

        _bandHoldSeconds = DoubleArg("--band-hold-sec=", 0.055);
        _bandReleaseCountdown = DoubleArg("--band-release-sec=", 600.0);
        if (_bands != null && _bandForceMode != null)
        {
            // A scripted capture freezes the world immediately after PrepareState, so a live ramp
            // would never be photographed. Placing BOTH bands explicitly makes the captured state a
            // decision rather than a frame-count coincidence - and they must be placed together, or a
            // capture would show one band steady and the other rewinding.
            var phaseArg = Arg("--band-phase=");
            if (phaseArg != null && double.TryParse(phaseArg, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pinned)) _bandPhase = pinned;
            if (_bandForceMode == "rewind")
            {
                foreach (var b in _bands)
                {
                    b.SetActive(true);
                    b.PlaceCapture(ReplayBand.CaptureHighlight,
                        (float)(_bandHoldAge >= 0.0 ? _bandHoldAge : _bandHoldSeconds));
                }
            }
            else if (_bandForceMode == "steady")
            {
                foreach (var b in _bands)
                {
                    b.SetActive(false);
                    b.PlaceCapture(0.0f, 0.0f);
                }
            }
            else
            {
                GD.Print($"[play] unknown --band-mode='{_bandForceMode}', expected steady|rewind");
            }
        }

        var levelPath = levelId.StartsWith("res://") ? levelId : $"res://levels/{levelId}.json";
        _level = LevelLoader.Load(levelPath);
        _world = new World(_level);

        // ---- the table surface, rendered into a texture so it can be DISPLACED ----------------
        // BuildTableWarp creates the nodes: a SubViewport holds the table, and a sibling Node2D blits
        // it back through the impact-warp shader. The room backdrop stays in the main canvas
        // (Play.DrawRoom), because the warp target is transparent outside the table art.
        BuildTableWarp();

        // The event-sound differencing baseline. It MUST be set here, the moment the world exists,
        // and not down with the audio setup: every --selftest / --trace-shot branch below returns
        // before that point, and PlayEventSounds then ran with a null baseline and threw a
        // NullReferenceException from the frame loop. That was invisible while the only headless
        // self tests were ones that quit before a rewind state change, and it is exactly the kind of
        // latent null a new self test is supposed to surface.
        _prevStats = _world.Stats.Clone();
        _trail = new TimelineTrail(this)
        {
            LabelFont = _font,
            ShowTrail = !HasArg("--no-trail"),
            ShowGhost = !HasArg("--no-ghost"),
            ShowSessionStripes = !HasArg("--no-stripes"),
            ShowTurnMarkers = !HasArg("--no-turns"),
            HaloPass = !HasArg("--no-halo"),
            ChunkVertices = IntArg("--chunk=", 2048),
            DebugDraw = HasArg("--trace-draw"),
        };

        GD.Print($"[play] level={_level.Id} name=\"{_level.Name}\" balls={_world.Balls.Length} " +
                 $"objectBalls={_world.ObjectBallCount} pockets={_level.Pockets.Count}");
        GD.Print($"[play] dt={_world.Config.SimulationStep:R} radius={_world.Config.BallRadius} " +
                 $"power={_world.Config.PowerMax}x{_world.Config.SpeedPerPower} friction={_world.Config.FrictionDecel} " +
                 $"crawl={_world.Config.RewindCrawlPxPerS} history={_world.Config.HistoryStepPx}");
        GD.Print($"[play] budget segments={_world.Budget.MaxSegments} distance={_world.Budget.MaxDistance}");
        GD.Print($"[play] physics_jitter_fix={ProjectSettings.GetSetting("physics/common/physics_jitter_fix", -1f)} " +
                 $"ticks={ProjectSettings.GetSetting("physics/common/physics_ticks_per_second", -1)} " +
                 $"textureFilter={ProjectSettings.GetSetting("rendering/textures/canvas_textures/default_texture_filter", -1)}");
        GD.Print("[play] KERNEL_VIEW_READY");

        if (_selfTestMode == "gradient")
        {
            // Needs real frames, so it runs from _Process rather than here.
            StartGradientCheck();
            return;
        }

        if (_selfTestMode == "input")
        {
            RunInputSelfTest();
            GetTree().Quit(_checksFailed == 0 ? 0 : 1);
            return;
        }

        if (_selfTestMode == "band")
        {
            RunBandSelfTest();
            GetTree().Quit(_checksFailed == 0 ? 0 : 1);
            return;
        }

        if (_selfTestMode != null)
        {
            RunSelfTests(_selfTestMode);
            return;
        }

        if (HasArg("--liveselftest"))
        {
            // Prove, in a LIVE (unfrozen) run, that a strike actually moves the ball and that a
            // rewind can then finish the level. This drives the real _PhysicsProcess path rather
            // than calling World.Step() directly, which is the whole point: two separate freezes
            // (a broken accumulator, then an always-on capture freeze) let the game ship with a
            // completely dead main loop while every direct-step test passed.
            RunLiveSelfTest();
            return;
        }

        if (HasArg("--trace-shot"))
        {
            if (HasArg("--trace-path")) TracePath(DoubleArg("--angle=", 90.0), DoubleArg("--power=", 170.0));
            else if (HasArg("--compare-ref")) TraceCompare(DoubleArg("--angle=", 90.0), DoubleArg("--power=", 170.0));
            else TraceShot(DoubleArg("--angle=", 0.0), DoubleArg("--power=", 170.0), IntArg("--trace-steps=", 6));
            GetTree().Quit(0);
            return;
        }

        if (_script != null)
        {
            RunScriptedDemo(_script);
            return;
        }

        if (_benchMode != null)
        {
            PrepareBench(_benchMode);
            _freezeWorld = true;
            return;
        }

        if (_stateName != null) PrepareState(_stateName);

        // The audio autoload, if it is up. Null in a headless run, and every call site tolerates
        // that, so audio can never be the reason a gate or a capture fails.
        _audio = GetNodeOrNull<Audio>("/root/Audio");

        // Coaching runs only in real play, never during a scripted capture: an evidence screenshot
        // must show the game, not a tutorial bubble.
        if (_stateName == null && Menu.TutorialPending) ResetTutorial();

        // ...unless a capture explicitly asks for a coaching step, so the bubbles can be verified
        // without a human performing the actions.
        var tutorialArg = Arg("--tutorial=");
        if (tutorialArg != null && int.TryParse(tutorialArg, out var tutorialStep))
        {
            _tutorialActive = true;
            _tutorialStep = Math.Clamp(tutorialStep, 0, 3);
            _tutorialStepStartTime = -10.0;
        }

        // Live-play invariant: unless the caller asked for a scripted capture, the world must be
        // free to step. This is asserted here (and covered by F04) because a frozen world is
        // completely silent -- the game renders, accepts your drag, and simply never moves.
        GD.Print($"[play] live={(_stateName == null)}\n[play] freezeWorld={_freezeWorld} paused={_rewindVis.Paused}");
        if (_stateName == null && _freezeWorld) GD.PushError("[play] live mode started FROZEN - the ball will not move");
    }

    /// <summary>
    /// Live-path self test: strike, let the REAL frame loop run, then rewind and finish the level.
    ///
    /// Everything here goes through the same code a human's input would reach. A test that calls
    /// World.Step() directly cannot catch a dead main loop; this can.
    /// </summary>
    private void RunLiveSelfTest()
    {
        var fail = 0;
        GD.Print($"[selftest] live path: freezeWorld={_freezeWorld} paused={_rewindVis.Paused}");
        if (_freezeWorld) { GD.Print("[selftest] FAIL: live mode started frozen"); fail++; }

        var start = _world.Cue.Position;
        SetAim(-90.0, 96.0);
        GD.Print($"[selftest] aiming angle={_aimAngle} power={_aimPower}");
        FireShot();
        if (!_world.ShotUsed) { GD.Print("[selftest] FAIL: the strike was not accepted"); fail++; }

        // Run the real accumulate-and-step loop for ~2 s of simulated time.
        var dt = _world.Config.SimulationStep;
        var frames = 0;
        while (_world.Time < 2.0 && frames < 2000)
        {
            var n = PlanFrameSteps(ref _accumulator, 1.0 / 60.0, dt);
            for (var i = 0; i < n; i++) _world.Step();
            frames++;
        }

        var moved = (_world.Cue.Position - start).Length;
        GD.Print($"[selftest] after {frames} live frames: cue moved {moved:F1} px, " +
                 $"tick={_world.Tick} history={_world.Cue.History.Count} potted={_world.PocketedObjectCount}/{_world.ObjectBallCount}");
        if (moved < 50.0) { GD.Print("[selftest] FAIL: the cue ball barely moved"); fail++; }
        if (_world.Cue.History.Count < 10) { GD.Print("[selftest] FAIL: history was not recorded"); fail++; }
        if (_world.HasNonFiniteState()) { GD.Print("[selftest] FAIL: non-finite state"); fail++; }

        // Finish it: rewind to the witness release arc, then release.
        _world.Settle();
        var afterStrike = _world.PocketedObjectCount;
        var rewound = _world.StartRewind();
        if (!rewound) { GD.Print("[selftest] FAIL: rewind refused"); fail++; }
        else
        {
            var track = _world.RewindTrack;
            var arcs = new double[track == null ? 0 : track.Length];
            for (var i = 1; i < arcs.Length; i++)
                arcs[i] = arcs[i - 1] + (track[i].Position - track[i - 1].Position).Length;
            var guard = 0;
            while (_world.Rewinding && guard++ < 20000)
            {
                var cur = _world.RewindCursor;
                if (cur >= 0 && cur < arcs.Length && arcs[cur] <= 102.0) break;
                _world.Step();
            }
            _world.EndRewind();
            _world.Settle();
        }

        GD.Print($"[selftest] strikeOnly={afterStrike} final={_world.PocketedObjectCount}/{_world.ObjectBallCount} won={_world.Won}");

        // WHAT THIS ASSERTS, and why it is no longer "the level cleared".
        //
        // This selftest exists to catch the LIVE PATH being dead -- the two shipped bugs where the ball never
        // moved at all (a frame-loop accumulator that never reached its threshold, and a world frozen at
        // startup). Both were SILENT: the game rendered, accepted the drag, and did nothing.
        //
        // "Did the level clear" was only ever a proxy for that, and it is now the WRONG proxy: levels carry an
        // increasing ball count, so one hard-coded strike plus one rewind cannot clear the later ones, and
        // failing here would report a CONTENT design choice as a code defect.
        //
        // So it asserts the live PATH instead -- the cue moved, history was recorded, the rewind engaged and
        // released, the state stayed finite. Those are exactly the failures that used to be invisible.
        if (afterStrike == 0 && _world.PocketedObjectCount == 0 && _world.Cue.Speed <= 0.0 && !rewound)
        {
            GD.Print("[selftest] FAIL: nothing happened -- the live path looks dead");
            fail++;
        }

        if (_world.HasNonFiniteState()) { GD.Print("[selftest] FAIL: non-finite state after the rewind"); fail++; }

        GD.Print(fail == 0 ? "[selftest] PASS" : $"[selftest] FAIL ({fail} problem(s))");
        GetTree().Quit(fail == 0 ? 0 : 1);
    }

    private static string Arg(string prefix)
    {
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length);
        }

        return null;
    }

    private static bool HasArg(string flag)
    {
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a == flag) return true;
        }

        return false;
    }

    private static int IntArg(string prefix, int fallback)
    {
        var s = Arg(prefix);
        return s != null && int.TryParse(s, out var v) ? v : fallback;
    }

    private static double DoubleArg(string prefix, double fallback)
    {
        var s = Arg(prefix);
        return s != null && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    // ============================================================ scripted states

    /// <summary>Drive the game into a named UI state without a human. Used for the evidence captures.</summary>
    private void PrepareState(string state)
    {
        switch (state)
        {
            case "aim":
                // Full power at 7 degrees: the line is long enough to show the ghost ball and the
                // struck ball leave direction instead of a straight-in pot.
                SetAim(7.0, 170.0);
                break;

            case "strike":
                // A real strike; the trail is then whatever the kernel recorded.
                SetAim(7.0, 170.0);
                FireShot();
                Settle();
                break;

            case "rewind":
                SetAim(7.0, 170.0);
                FireShot();
                Settle();
                BeginRewind();
                // Walk the crawl part-way so the traversed highlight, the forecast rings and the
                // remaining path are all visible at once.
                AdvanceRewindToArc(260.0);
                break;

            case "rewind-contact":
                SetAim(30.0, 170.0);
                FireShot();
                Settle();
                BeginRewind();
                AdvanceUntilContactHighlighted();
                break;

            case "scrub":
                SetAim(7.0, 170.0);
                FireShot();
                Settle();
                BeginRewind();
                AdvanceRewindToArc(240.0);
                _rewindVis.BeginScrub();
                _rewindVis.SetPlannedRelease(Math.Max(0, _rewindVis.TraversedFrom + (int)DoubleArg("--scrub-back=", 120.0)));
                break;

            case "win":
                RunWinDemo();
                break;

            case "debug":
                SetAim(7.0, 170.0);
                FireShot();
                Settle();
                BeginRewind();
                AdvanceRewindToArc(180.0);
                _debugOverlay = true;
                break;

            case "rail":
                // Freeze the world at the instant a rail is hit, with the impact at PEAK deformation.
                //
                // The effect is spawned through the SAME production path the frame loop uses
                // (SpawnRailEffects) and the warp is fed through the same UpdateWarp, so the capture
                // proves the shipped code rather than a parallel one.
                SetAim(7.0, 170.0);
                FireShot();
                for (var i = 0; i < 6000; i++)
                {
                    var beforeRails = _world.Stats.RailTransforms;
                    var beforeBoosts = _world.Stats.RailBoosts;
                    _world.Step();
                    if (_world.Stats.RailTransforms > beforeRails)
                    {
                        SpawnRailEffects(_world.Stats.RailTransforms - beforeRails,
                            _world.Stats.RailBoosts - beforeBoosts);
                        break;
                    }
                }

                _freezeWorld = true;

                // Hold the effect at the chosen age. This is normally implied by --out=, because a
                // scripted capture waits N frames before saving while effects age in REAL time, so a
                // 0.55 s impact would be gone by the time the shutter opens.
                //
                // --hold-fx forces the HOLD without --out=, for captures that take the picture some
                // other way. (An earlier version of this comment claimed the exported build cannot use
                // --out= at all because res:// is a read-only PCK. That is true of a res:// path and
                // FALSE in general: SavePng takes an OS path, so an ABSOLUTE --out= writes fine from the
                // packed build and the exported .exe can screenshot itself. Verified both ways.)
                _captureHoldEffects = _outPath != null || HasArg("--hold-fx");

                // Hold the impact at its PEAK, not at an arbitrary age. The elastic envelope is
                // cos-based, so it passes through zero on the way back: capturing at t=0.35 s (which the
                // old version did, when the effect WAS the fade) would now land on or near a zero
                // crossing and photograph a table that looks untouched. A capture has to be taken at a
                // moment the effect is actually at its strongest or it proves nothing.
                if (_captureHoldEffects)
                {
                    // --hold-age= lets a capture be taken at any point in the oscillation, which is how
                    // the compression -> rest -> overshoot sequence is photographed. Without it only the
                    // peak is reachable and the rebound cannot be shown at all.
                    var holdAge = DoubleArg("--hold-age=", PeakHoldAge / 60.0);
                    _effects.Update(holdAge);
                }

                // --no-fx renders the identical frozen state with the effects suppressed, so the two
                // captures can be differenced to prove exactly which pixels the effect owns. Without this
                // the only available comparison is between two different game states, which differ
                // everywhere and proves nothing.
                //
                // It must suppress the WARP as well as the geometry. Clearing the effect list alone left
                // the shader running with its last uniforms, so the "no effects" frame still carried the
                // distortion -- which would have silently inflated every measured footprint.
                if (HasArg("--no-fx")) { _effects.Clear(); _suppressWarp = true; }
                UpdateWarp();

                GD.Print($"[play] rail state: railTransforms={_world.Stats.RailTransforms} " +
                         $"effects={_effects.Count} cue=({_world.Cue.Position.X:F0},{_world.Cue.Position.Y:F0}) " +
                         $"r={_world.Cue.Radius:F1} v=({_world.Cue.Velocity.X:F0},{_world.Cue.Velocity.Y:F0}) " +
                         $"wall={ContactWallOf(_world.Cue)} warpActive={_warpActive} " +
                         $"push={_impactPush:F1}");
                break;

            case "cards":
                // Drive a real pot and stop with the hand on screen.
                //
                // The aim is SEARCHED, not guessed: this level's own witness (-90 deg / power 96) is the
                // only shot on record that pots a ball here, and a hard 7-degree break does not pot at
                // all -- which is how the first version of this capture produced an empty hand.
                SetAim(-90.0, 96.0);
                FireShot();
                Settle();
                if (!_world.AwaitingCardChoice)
                {
                    // Still nothing: try the rewind the level is actually built around, since the
                    // corridor's second ball is potted by the reverse pass, not by the strike.
                    BeginRewind();
                    AdvanceRewindToArc(102.0);
                    ReleaseRewind();
                    Settle();
                }

                if (!_world.AwaitingCardChoice)
                {
                    for (var i = 0; i < 20000 && !_world.AwaitingCardChoice && !_world.Frozen; i++) _world.Step();
                }

                _cardHover = 1;

                // Let the reveal flourish finish before freezing: the cards slide up from below, so a
                // capture taken on the frame the hand appears shows them mid-slide and half off-screen.
                _effects.Update(1.0);

                _freezeWorld = true;
                GD.Print($"[play] cards state: awaiting={_world.AwaitingCardChoice} " +
                         $"hand={_cardHand.Count} potted={_world.PocketedObjectCount}/{_world.ObjectBallCount}");
                break;

            default:
                GD.Print($"[play] unknown state '{state}', falling back to aim");
                SetAim(7.0, 170.0);
                break;
        }

        _freezeWorld = true; // keeps the captured frame identical to the prepared state
        GD.Print($"[play] state={state} ticks={_world.Tick} time={_world.Time:F3} shotUsed={_world.ShotUsed} " +
                 $"rewinding={_world.Rewinding} won={_world.Won} pocketed={_world.PocketedObjectCount}/{_world.ObjectBallCount} " +
                 $"history={_world.Cue.History.Count}");
        if (_bands != null)
        {
            foreach (var b in _bands) GD.Print($"[play] band {b.BandName} {b.Stats()}");
        }
    }

    private void SetAim(double angleDeg, double power)
    {
        _hasAim = true;
        _aimAngle = angleDeg;
        _aimPower = power;
        _dragPoint = CueScreen() - new Vector2((float)(Math.Cos(angleDeg * Math.PI / 180.0) * power),
            (float)(Math.Sin(angleDeg * Math.PI / 180.0) * power));
    }

    private Vector2 CueScreen()
    {
        var c = _world.Cue.Position;
        return new Vector2((float)c.X, (float)c.Y);
    }

    private void FireShot()
    {
        if (!_world.Shot(_aimAngle, _aimPower)) return;
        _aimCache.Invalidate();
        Notify($"击球  角度 {_aimAngle:F1}°  力度 {_aimPower:F0} px  速度 {_world.Cue.Speed:F0} px/s", Palette.Accent);
    }

    private void Settle(int maxSteps = 12000)
    {
        var n = 0;
        while (n < maxSteps && !_world.Won && !_world.AtRest() && !_world.Frozen)
        {
            _world.Step();
            n++;
        }

        _scriptTicks += n;
    }

    private void BeginRewind()
    {
        if (!_world.StartRewind())
        {
            Notify("无法回溯（没有可走路径 / 预算已耗尽 / 没有历史）", Palette.Bad);
            return;
        }

        _rewindVis.Reset();
        _aimCache.Invalidate();
        Notify("回溯中 · 按住 R 倒退，松开保留当时的逆向速度", Palette.RewindCue);
    }

    /// <summary>Crawl until the traversed arc reaches <paramref name="arc"/> px (or the path ends).</summary>
    private void AdvanceRewindToArc(double arc)
    {
        var guard = 0;
        while (_world.Rewinding && ArcWalked() < arc && guard++ < 40000)
        {
            _world.Step();
            _scriptTicks++;
        }

        _trail.Sync(_world);
        _rewindVis.ArcWalked = ArcWalked();
        _rewindVis.EnsureForecast(_world, _trail.TraversedRange.From, _trail.TraversedRange.To);
    }

    /// <summary>Crawl until some ball is highlighted by the contact forecast (for the evidence shot).</summary>
    private void AdvanceUntilContactHighlighted()
    {
        var guard = 0;
        while (_world.Rewinding && guard++ < 40000)
        {
            _world.Step();
            _scriptTicks++;
            _trail.Sync(_world);
            _rewindVis.ArcWalked = ArcWalked();
            _rewindVis.EnsureForecast(_world, _trail.TraversedRange.From, _trail.TraversedRange.To);
            var any = false;
            foreach (var f in _rewindVis.BallContacts)
            {
                if (_rewindVis.HighlightWeight(f.BallId) > 0.2f) any = true;
            }

            if (any) return;
        }
    }

    private double ArcWalked()
    {
        var track = _world.RewindTrack;
        if (track == null) return 0.0;
        return RewindVisualizer.ArcAt(track, _world.RewindCursor, _world.RewindFraction) is var total
               && track.Length > 1
            ? RewindVisualizer.ArcAt(track, track.Length - 1, 1.0) - total
            : 0.0;
    }

    /// <summary>The band's wave phase, wrapped to one lattice period, expressed as the SHORTEST SIGNED
    /// displacement. 1/1.9 is the field's period, so this is exact, not an approximation.</summary>
    private static double ShortestSignedPhase(double delta)
    {
        const double lattice = 1.0 / 1.9;
        delta -= Math.Floor(delta / lattice + 0.5) * lattice;
        return delta;
    }

    /// <summary>
    /// Test the decorative band's MOTION, which no screenshot can show: a captured frame proves what
    /// the band looks like, not which way it travels.
    ///
    /// What is asserted:
    ///  1. the band builds and is visible;
    ///  2. while the cue ball is rewinding, 60 sim frames of real crawl DECREASE the wave phase;
    ///  3. while the game runs forward, 60 idle frames INCREASE it;
    ///  4. the highlight rises during the rewind and decays after it;
    ///  5. the phase gain equals the rewound distance, in px, scaled by ReplayBand.RewindPhasePerPixel.
    ///     That last one is the design intent - the band is a readout of distance rewound, not a
    ///     wall-clock animation - so it is checked as arithmetic rather than described in a comment.
    /// </summary>
    private void RunBandSelfTest()
    {
        GD.Print("[band] decorative sine bands (top + bottom), driven by real kernel steps");
        if (_bands == null || _bands.Length == 0) { Check("both bands build", false, "_bands is empty"); return; }
        Check("both bands build", _bands.Length == 2,
            $"{_bands.Length} band(s): " + string.Join(", ", System.Array.ConvertAll(_bands,
                b => $"{b.BandName}@{b.BandTopForTest} at {b.BandSizeForTest}")));

        SetAim(7.0, 170.0);
        FireShot();
        Settle();
        BeginRewind();

        // Both instances are driven from the same numbers, so they are asserted separately rather
        // than assumed to agree: the whole point of the pair is that they move as one effect.
        var phase0 = new float[_bands.Length];
        var phase1 = new float[_bands.Length];
        var phase2 = new float[_bands.Length];
        var phase3 = new float[_bands.Length];
        for (var i = 0; i < _bands.Length; i++) phase0[i] = _bands[i].Phase;

        var arc0 = ArcWalked();
        for (var i = 0; i < 60; i++) { _world.Step(); UpdateBand(_world.Config.SimulationStep); }
        var arc1 = ArcWalked();
        for (var i = 0; i < _bands.Length; i++) phase1[i] = _bands[i].Phase;

        // The phase is wrapped to the lattice period (Play.ReplayBand does that on purpose: it keeps
        // u_time bounded and comparable between runs), so every comparison here has to use the
        // SHORTEST SIGNED delta. Comparing raw values made a 0.070 backwards step read as +0.456
        // forwards, which is how this check first failed.
        var expected = ShortestSignedPhase((arc1 - arc0) * ReplayBand.RewindPhasePerPixel);
        for (var i = 0; i < _bands.Length; i++)
        {
            var b = _bands[i];
            var signed = ShortestSignedPhase(phase1[i] - phase0[i]);
            Check($"[{b.BandName}] holding R moves the wave BACKWARDS", signed < 0.0,
                $"phase {phase0[i]:F4} -> {phase1[i]:F4} shortest delta {signed:F4}");
            Check($"[{b.BandName}] rewind raises the highlight", b.Highlight > 0.9f,
                $"highlight={b.Highlight:F3}");
            Check($"[{b.BandName}] phase gain equals the distance rewound", Math.Abs(signed - expected) < 1e-4,
                $"rewound={(arc1 - arc0):F2}px expected={expected:F5} actual={signed:F5} " +
                $"err={Math.Abs(signed - expected):E2}");
        }

        // The pair must be ONE effect shown twice, not two animations that happen to look alike.
        var drift = ShortestSignedPhase(phase1[0] - phase1[_bands.Length - 1]);
        Check("the two bands hold the same phase", _bands.Length < 2 || Math.Abs(drift) < 1e-6,
            $"phase delta top-vs-bottom={drift:E2}");

        ReleaseRewind();
        for (var i = 0; i < _bands.Length; i++) phase2[i] = _bands[i].Phase;
        for (var i = 0; i < 60; i++) { _world.Step(); UpdateBand(_world.Config.SimulationStep); }
        for (var i = 0; i < _bands.Length; i++) phase3[i] = _bands[i].Phase;

        for (var i = 0; i < _bands.Length; i++)
        {
            var b = _bands[i];
            var forward = ShortestSignedPhase(phase3[i] - phase2[i]);
            Check($"[{b.BandName}] running forward moves the wave FORWARD", forward > 0.0,
                $"phase {phase2[i]:F4} -> {phase3[i]:F4} shortest delta {forward:F4}");
            Check($"[{b.BandName}] the highlight decays after the rewind", b.Highlight < 0.5f,
                $"highlight={b.Highlight:F3}");
        }

        // Geometry as arithmetic: the two strips must sit in the free bands at each edge and must not
        // overlap each other. Both were found by measuring the HUD, and both are load-bearing.
        Check("the two strips do not overlap and stay on the canvas",
            ReplayBand.TopBandTop + ReplayBand.BandHeight <= ReplayBand.BottomBandTop
            && ReplayBand.TopBandTop >= 0
            && ReplayBand.BottomBandTop + ReplayBand.BandHeight <= 560,
            $"top y={ReplayBand.TopBandTop}..{ReplayBand.TopBandTop + ReplayBand.BandHeight} " +
            $"bottom y={ReplayBand.BottomBandTop}..{ReplayBand.BottomBandTop + ReplayBand.BandHeight}");

        GD.Print($"=== M2 selftest summary: {_checksRun - _checksFailed}/{_checksRun} checks passed ===");
        GD.Print(_checksFailed == 0 ? "M2 SELFTEST PASS" : $"M2 SELFTEST FAIL: {_checksFailed} check(s) failed");
    }

    /// <summary>
    /// Advance the decorative sine bands. Decoration only: nothing here is read by the kernel, the
    /// HUD or a test, and a band that fails to build leaves the game exactly as it was.
    ///
    /// The phase source is the REWOUND DISTANCE, not wall-clock time, which is the design intent:
    /// while the player holds R the wave travels backwards in proportion to the ground the cue ball
    /// has actually given up, so the band is a second, non-textual readout of the same number the
    /// HUD prints as "distance N / 2400 px". Both instances are handed the SAME numbers in the same
    /// frame, so the pair is one effect shown twice rather than two drifting animations.
    /// </summary>
    private void UpdateBand(double delta)
    {
        if (_bands == null || !_showBand) return;

        // FIRST, before any early return: size the render targets to the window's REAL device pixels.
        //
        // Two things had to be discovered here, both by measurement:
        //  1. this must not live below the forced-state branch - written there first, it never ran
        //     during a scripted capture, so the 2x-window captures stayed upscaled;
        //  2. GetVisibleRect().Size.X is ALWAYS the 1040-wide design canvas, whatever the window is
        //     (traced: --resolution 2080x1120 gives win=2080x1120 but visibleRect=1040x560). The real
        //     ratio lives in GetScreenTransform(): with `stretch/mode=canvas_items`, Windows upscales
        //     the finished 1040x560 frame to the window, and this band is part of that frame. Using the
        //     visible rect alone reported scale 1.0 for every window, which is why the fix appeared to
        //     do nothing while a 1-px line still came out 4 device px wide at 2x.
        var screen = GetViewport().GetScreenTransform();
        var scale = Mathf.Max(0.25f, (Mathf.Abs(screen.X.X) + Mathf.Abs(screen.Y.Y)) * 0.5f);
        foreach (var b in _bands) b.SyncToViewport(scale);
        if (HasArg("--trace-band"))
        {
            var win = DisplayServer.WindowGetSize();
            var vis = GetViewport().GetVisibleRect().Size;
            GD.Print($"[band] trace win={win.X}x{win.Y} visibleRect={vis.X:F0}x{vis.Y:F0} " +
                     $"screenTransform=({screen.X.X:F3},{screen.Y.Y:F3}) scale={scale:F3} " +
                     $"bandRt={_bands[0].RenderTargetSizeForTest} emit={_bands[0].EmitForTest:F2}");
        }

        // --band-mode=steady|rewind pins the state. It exists so a headless capture of "the band
        // while rewinding" is reproducible instead of depending on which frame the crawl reached.
        var forced = _bandForceMode != null && _bandForceMode != "off";
        if (forced)
        {
            // A held band still tracks the live arc so it cannot jump when it is handed back.
            _bandArc = _world.Rewinding ? ArcWalked() : 0.0;
            _bandReleaseCountdown -= delta;
            foreach (var b in _bands)
            {
                if (!double.IsNaN(_bandPhase)) b.SetPhase((float)_bandPhase);
                if (_bandReleaseCountdown <= 0.0) b.ReleaseCapture();
            }

            return;
        }

        var rewinding = _world.Rewinding;
        var arc = rewinding ? ArcWalked() : 0.0;

        // Scripted captures freeze the world, so the live 9/s attack would never be photographed:
        // while frozen the bands are allowed to reach the capture strength instead.
        var rise = _freezeWorld ? ReplayBand.CaptureRisePerSecond : 9.0f;
        foreach (var b in _bands) b.Update(rewinding, arc, delta, rise);
        _bandArc = arc;
    }

    /// <summary>
    /// Win-state demo. Tries an honest greedy autoplay first (real strokes + real rewinds, no
    /// kernel writes); if the level is not winnable that way within the budget, falls back to
    /// potting the remaining object balls directly and letting the kernel award the win. The mode
    /// used is printed, so the report can state exactly how the win frame was produced.
    /// </summary>
    private void RunWinDemo()
    {
        var mode = Arg("--win-mode=") ?? "auto";
        if (mode == "auto" || mode == "play")
        {
            var planner = new Solver(_world);
            var plan = planner.FindWinSequence(DoubleArg("--win-seconds=", 20.0));
            var step = 0;
            if (plan.Count > 0)
            {
                foreach (var shot in plan)
                {
                    if (_world.Won) break;
                    step++;
                    Notify($"自动击球 {step}/{plan.Count}：角度 {shot.Angle:F2}°  力度 {shot.Power:F1}", Palette.Accent);
                    _aimAngle = shot.Angle;
                    _aimPower = shot.Power;
                    FireShot();
                    Settle();
                    GD.Print($"[play] autoplay stroke {step} done: pocketed={_world.PocketedObjectCount}/{_world.ObjectBallCount} won={_world.Won}");

                    if (_world.Won) break;

                    // A full rewind puts the cue back where the stroke started, which is what the
                    // planner modelled. The kernel ends the rewind by itself at the path start.
                    if (!_world.StartRewind()) break;
                    _rewindVis.Reset();
                    var guard = 0;
                    while (_world.Rewinding && guard++ < 60000)
                    {
                        _world.Step();
                        _scriptTicks++;
                    }
                }

                if (_world.Won)
                {
                    GD.Print($"[play] WIN-DEMO mode=autoplay strokes={step} pocketed={_world.PocketedObjectCount}/{_world.ObjectBallCount}");
                    Settle(600);
                    return;
                }

                GD.Print($"[play] WIN-DEMO autoplay did not clear the table after {step} strokes " +
                         $"(pocketed {_world.PocketedObjectCount}/{_world.ObjectBallCount}); falling back to forced pot");
            }
            else
            {
                GD.Print("[play] WIN-DEMO planner found no sequence; falling back to forced pot");
            }
        }

        GD.Print("[play] WIN-DEMO mode=forced (balls are placed INSIDE a pocket's capture circle moving " +
                 "inward; the capture rule, the win rule and the freeze are the unchanged kernel's)");
        foreach (var ball in _world.Balls)
        {
            if (ball.IsCue || ball.Pocketed) continue;
            var pocket = NearestPocket(ball.Position);
            var outward = (ball.Position - pocket.Center).Normalized();
            if (outward.LengthSquared < 1e-12) outward = new Vec2(1.0, 0.0);

            // Distance to the pocket centre must be strictly inside PocketRadius (24 px) - sitting on
            // the tangent circle is NOT enough, the ball then slides around the rim and never enters.
            // The offset also has to clear the rails: the ball moves 2.5 px per step and the kernel's
            // capture sample runs at the END of the step, so a ball placed too close to a cushion gets
            // reflected off it before the capture sample is taken.
            ball.Position = pocket.Center + outward * (pocket.Radius * 0.72);
            ball.Velocity = -outward * 300.0;
            ball.Pocketed = false;
            GD.Print($"[play] forced pot ball {ball.Id}: pocket=({pocket.Center.X},{pocket.Center.Y}) r={pocket.Radius} " +
                     $"-> pos={ball.Position} v={ball.Velocity} inCapture={_world.InPocketCapture(ball)}");
        }

        // The kernel's rule is sampled at physics steps, so the world must actually be stepped: a
        // "Settle" that returns immediately because nothing is moving would demonstrate nothing.
        for (var i = 0; i < 240; i++)
        {
            _world.Step();
            if (i < 3 || _world.Stats.PocketCaptures > 0)
            {
                GD.Print($"[play] forced pot step {i + 1}: captures={_world.Stats.PocketCaptures} " +
                         $"pocketed={_world.PocketedObjectCount} won={_world.Won} " +
                         $"b1={_world.Balls[1].Position} pocketed1={_world.Balls[1].Pocketed}");
            }

            if (_world.Won) break;
        }

        Settle(600);
        GD.Print($"[play] WIN-DEMO after forced pot: won={_world.Won} frozen={_world.Frozen} " +
                 $"pocketed={_world.PocketedObjectCount}/{_world.ObjectBallCount} captures={_world.Stats.PocketCaptures}");
    }

    private PocketDef NearestPocket(Vec2 p)
    {
        PocketDef best = null;
        var bestD = double.MaxValue;
        foreach (var pocket in _level.Pockets)
        {
            var d = (pocket.Center - p).LengthSquared;
            if (d < bestD)
            {
                bestD = d;
                best = pocket;
            }
        }

        return best;
    }

    /// <summary>Extra scripted demos used by the report (angle/power probes, h5 break, etc).</summary>
    private void RunScriptedDemo(string name)
    {
        switch (name)
        {
            case "break-h5":
                SetAim(DoubleArg("--angle=", 35.0), DoubleArg("--power=", 170.0));
                FireShot();
                Settle();
                break;

            case "long-session":
                RunLongSession();
                break;

            case "live-strike":
                // Fire WITHOUT settling, so the real frame loop advances the world.
                //
                // Every other scripted state settles inside PrepareState (during _Ready), which consumes
                // the simulation's stat counters before _Process ever runs. PlayEventSounds drives the
                // effects by DIFFERENCING those counters once per frame, so in a settled state it sees
                // no delta and spawns nothing -- which is exactly why the live effect path could not be
                // verified by any existing capture. This mode leaves the shot in flight so the same code
                // path that runs in normal play is exercised, and --trace-fx can then show it firing.
                SetAim(DoubleArg("--angle=", 7.0), DoubleArg("--power=", 170.0));
                FireShot();
                GD.Print("[play] live-strike: fired, NOT settled; frames will advance the world");
                break;

            default:
                GD.Print($"[play] unknown script '{name}'");
                break;
        }

        GD.Print($"[play] script={name} ticks={_world.Tick} time={_world.Time:F2} pocketed={_world.PocketedObjectCount}/" +
                 $"{_world.ObjectBallCount} won={_world.Won} history={_world.Cue.History.Count} rewinds={_world.Stats.Rewinds} " +
                 $"rewindDistance={_world.RewindDistanceUsed:F1}");
    }

    /// <summary>Many strokes + rewinds in one run: the "long session" state for timing and stability.</summary>
    private void RunLongSession()
    {
        var rng = new Random(12345);
        for (var i = 0; i < 12 && !_world.Won; i++)
        {
            SetAim(rng.NextDouble() * 360.0, 40.0 + rng.NextDouble() * 130.0);
            if (!_world.Shot(_aimAngle, _aimPower)) break;
            Settle();
            var guard = 0;
            if (_world.StartRewind())
            {
                while (_world.Rewinding && guard++ < 20000) _world.Step();
            }
        }
    }

    // ================================================================ benchmark

    private void PrepareBench(string mode)
    {
        GD.Print($"[play] bench={mode}");
        if (mode == "idle" || mode == "aim-sweep")
        {
            SetAim(7.0, 170.0);
            return;
        }

        // Build a synthetic trail of the requested size by stitching a real recorded path into a
        // repeating pattern, then time the SAME draw path the real trail uses (same vertex array,
        // same chunking, same gradient). The number is about the rendering, not a special case.
        SetAim(7.0, 170.0);
        FireShot();
        Settle();
        var real = _world.Cue.History.Count;
        var target = Math.Max(real + 1, (int)DoubleArg("--points=", 50000.0));
        _benchTargetVertices = target;
        var t0 = Time.GetTicksUsec();
        var laps = _trail.SeedSyntheticTrail(target);
        GD.Print($"[play] bench targetVertices={target} realHistory={real} laps={laps} seeded={_trail.CachedVertexCount} " +
                 $"chunks={_trail.DebugChunkCount()} seedUs={Time.GetTicksUsec() - t0}");
    }

    private int _benchTargetVertices = -1;

    // =================================================================== step

    /// <summary>
    /// Decide how many simulation steps a real frame of length <paramref name="frameDelta"/>
    /// should advance, updating <paramref name="accumulator"/> in place.
    ///
    /// Extracted as a pure static so it can be tested at exactly the layer that broke. The
    /// original inline bug was `_accumulator += dt` -- adding the step size instead of the frame
    /// time -- which made this return 0 forever. That is invisible to any test that calls
    /// <c>World.Step()</c> directly, which is what every scripted capture and the whole test
    /// suite did, so the game shipped with a dead main loop while 344 assertions passed.
    /// Callers must keep accumulator bookkeeping inside this method.
    /// </summary>
    public static int PlanFrameSteps(ref double accumulator, double frameDelta, double dt,
        int maxStepsPerFrame = MaxStepsPerFrame, double maxFrameDelta = MaxFrameDelta)
    {
        accumulator += System.Math.Min(frameDelta, maxFrameDelta);
        var steps = 0;
        while (accumulator >= dt - 1e-12 && steps < maxStepsPerFrame)
        {
            accumulator -= dt;
            steps++;
        }

        // Real time has outrun simulated time; drop the backlog instead of accruing a debt.
        if (steps >= maxStepsPerFrame || accumulator > dt) accumulator = 0.0;
        return steps;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_world == null) return;

        var dt = _world.Config.SimulationStep;
        var t0 = Time.GetTicksUsec();

        var steps = PlanFrameSteps(ref _accumulator, delta, dt);
        for (var i = 0; i < steps; i++)
        {
            // A scripted capture freezes the world at the prepared state: the crawl would otherwise
            // keep running during the frames that precede the screenshot and capture another moment.
            //
            // A pending card draw also stops stepping: the user asked for the draw to PAUSE the game
            // and require a choice, so no physics may advance while the hand is on screen.
            if (!_rewindVis.Paused && !_freezeWorld && !CardChoiceOpen) _world.Step();
        }

        UpdateTutorial();
        PlayEventSounds();
        _simMs = (Time.GetTicksUsec() - t0) / 1000.0;
    }

    public override void _Process(double delta)
    {
        if (_world == null) return;

        if (_benchMode == "aim-sweep") SetAim(7.0 + 15.0 * Math.Sin(_frames * 0.1), 170.0);

        // Prepare presentation once per rendered frame, after all fixed physics ticks. Redrawing
        // from _PhysicsProcess rebuilds the entire canvas up to eight times during a slow frame.
        // Live telemetry. Absence of this was half of why two dead-main-loop bugs survived: in live
        // mode the game printed nothing at all, so there was no signal to read.
        if (_verbose && _stateName == null && _frames > 0 && _frames % 120 == 0)
        {
            GD.Print($"[live] frame={_frames} tick={_world.Tick} simT={_world.Time:F2} " +
                     $"cue=({_world.Cue.Position.X:F0},{_world.Cue.Position.Y:F0}) v={_world.Cue.Speed:F0} " +
                     $"hist={_world.Cue.History.Count} rewinding={_world.Rewinding} " +
                     $"potted={_world.PocketedObjectCount}/{_world.ObjectBallCount} won={_world.Won}");
        }

        // The trail is the view's own cache of the kernel's append-only history: it must be kept in
        // step with the world exactly once per frame, before anything is drawn or inspected. The
        // trail50k benchmark seeds the cache directly, and a Sync would immediately shrink it back
        // to the live history, so that mode skips the sync (and the world is frozen anyway).
        if (_benchMode != "trail50k") _trail.Sync(_world);
        _rewindVis.ArcWalked = _world.Rewinding ? ArcWalked() : 0.0;
        if (_world.Rewinding)
        {
            _rewindVis.EnsureForecast(_world, _trail.TraversedRange.From, _trail.TraversedRange.To);
        }

        // Scripted capture: keep advancing the prepared state until the capture frame count.
        if (_outPath != null && _script != null && !_scriptDone && _world.Rewinding)
        {
            // A scripted rewind capture keeps crawling so the traversed span stays visible.
        }

        QueueRedraw();
        _wallClock += delta;
        if (_messageTimer > 0.0) _messageTimer -= delta;
        _rewindVis.ArcWalked = _world.Rewinding ? ArcWalked() : 0.0;

        // The band must be advanced AFTER the steps above, so the phase delta it reads is this
        // frame's real rewind and not the previous frame's remainder.
        UpdateBand(delta);
        // A scripted capture freezes the world and then waits N frames before saving. Effects age by real
        // time, so a 0.85 s ripple would be gone by then and the screenshot would show an empty table.
        // Holding them is capture plumbing only; live play ages them normally.
        if (!_captureHoldEffects) _effects.Update(delta);
        // The impact feed must follow the effects update, so the shader and the geometry it
        // accompanies are computed from the same instant rather than a frame apart.
        UpdateWarp();
        UpdateCards();
        var now = Time.GetTicksUsec() / 1000.0;
        if (_lastFrameAt > 0.0)
        {
            var ms = now - _lastFrameAt;
            _lastFrameMs = ms;
            if (_outPath != null || _benchMode != null) _frameMs.Add(ms);
            if (ms > _maxFrameMs) _maxFrameMs = ms;
        }

        _lastFrameAt = now;
        _frames++;

        // The gradient check reads back the framebuffer, so it must run in a real (non-headless)
        // frame, after the trail has actually been drawn at least once.
        if (_gradientCheckPending && _frames >= 6)
        {
            _gradientCheckPending = false;
            _freezeWorld = true;
            RunGradientCheck();
            GD.Print($"=== M2 selftest summary: {_checksRun - _checksFailed}/{_checksRun} checks passed ===");
            GD.Print(_checksFailed == 0 ? "M2 SELFTEST PASS" : $"M2 SELFTEST FAIL: {_checksFailed} check(s) failed");
            GetTree().Quit(_checksFailed == 0 ? 0 : 1);
            return;
        }

        if (_outPath == null || _captureQuit) return;
        if (_frames < Math.Max(_minFrames, _captureFrames)) return;

        _captureQuit = true;

        // Dump the warp target itself FIRST, if asked. Doing it before the composite means the two files
        // describe the same frame, and it answers the question the composite cannot: is the target empty,
        // or is the shader misreading it?
        if (_dumpWarpPath != null && _warpViewport != null)
        {
            var raw = _warpViewport.GetTexture()?.GetImage();
            if (raw == null)
            {
                GD.Print("[play] WARP_DUMP FAIL no texture image");
            }
            else
            {
                var werr = raw.SavePng(_dumpWarpPath);
                GD.Print($"[play] WARP_DUMP file={_dumpWarpPath} err={werr} " +
                         $"size={raw.GetWidth()}x{raw.GetHeight()}");
            }
        }

        var image = GetViewport().GetTexture().GetImage();
        if (image == null)
        {
            GD.Print("[play] CAPTURE FAIL no viewport image");
            GetTree().Quit(2);
            return;
        }

        var err = image.SavePng(_outPath);
        GD.Print($"[play] CAPTURE file={_outPath} err={err} size={image.GetWidth()}x{image.GetHeight()} frames={_frames}");
        PrintFrameTiming();
        GetTree().Quit(err == Error.Ok ? 0 : 3);
    }

    private void PrintFrameTiming()
    {
        if (_frameMs.Count == 0) return;
        var arr = _frameMs.ToArray();
        Array.Sort(arr);
        double sum = 0;
        foreach (var v in arr) sum += v;
        var mean = sum / arr.Length;
        var p50 = arr[arr.Length / 2];
        var p95 = arr[Math.Min(arr.Length - 1, (int)(arr.Length * 0.95))];
        GD.Print($"[play] FRAME_TIMING frames={arr.Length} mean={mean:F3}ms p50={p50:F3}ms p95={p95:F3}ms max={_maxFrameMs:F3}ms " +
                 $"files={_authoredVertexCounts}");
    }

    // =================================================================== input

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_world == null) return;

        switch (@event)
        {
            case InputEventKey key when !key.Echo:
                HandleKey(key);
                break;

            case InputEventMouseButton button:
                HandleMouse(button);
                break;

            case InputEventMouseMotion motion:
                if (CardChoiceOpen)
                {
                    var hover = CardIndexAt(motion.Position);
                    if (hover != _cardHover)
                    {
                        _cardHover = hover;
                        if (hover >= 0) _audio?.Play(SoundBank.Cue.UiHover, 0.7f, 1.0f);
                    }

                    break;
                }

                if (_dragging) _dragPoint = motion.Position;
                if (_rewindVis.Scrubbing && motion.ButtonMask.HasFlag(MouseButtonMask.Left))
                {
                    ScrubTo(motion.Position);
                }

                break;
        }
    }

    private void HandleKey(InputEventKey key)
    {
        // A pending draw OWNS the keyboard: the user asked for the choice to be mandatory, so while a
        // hand is on screen only the three pick keys are accepted. Without this guard R / Space /
        // Escape would let the player act on a frozen world and desynchronise the hand from the state
        // it was drawn for.
        if (CardChoiceOpen)
        {
            if (!key.Pressed) return;
            switch (key.Keycode)
            {
                case Key.Key1: ChooseCard(0); break;
                case Key.Key2: ChooseCard(1); break;
                case Key.Key3: ChooseCard(2); break;
            }

            return;
        }

        switch (key.Keycode)
        {
            case Key.R:
                if (key.Pressed)
                {
                    if (_rewindVis.Scrubbing)
                    {
                        // Resume the crawl so a normal release works from wherever it lands.
                        _rewindVis.EndScrub(keepPaused: false);
                        Notify("继续回溯", Palette.RewindCue);
                    }
                    else if (_world.Rewinding)
                    {
                        // Holding R again while already rewinding = release.
                        ReleaseRewind();
                    }
                    else
                    {
                        BeginRewind();
                    }
                }
                else if (_world.Rewinding && !_rewindVis.Scrubbing)
                {
                    ReleaseRewind();
                }

                break;

            case Key.P:
                if (!key.Pressed) break;
                if (_world.Rewinding)
                {
                    if (_rewindVis.Scrubbing) _rewindVis.EndScrub(keepPaused: false);
                    else _rewindVis.BeginScrub();
                    Notify(_rewindVis.Scrubbing
                        ? "已暂停 · 拖动或用 ← → 把释放点向后移，按 R 在该点松开"
                        : "继续回溯", Palette.RewindTarget);
                }
                else
                {
                    Notify("还没有可回溯的历史（先击球，再按住 R）", Palette.TextDim);
                }

                break;

            case Key.Left:
                if (key.Pressed && _rewindVis.Scrubbing)
                {
                    _rewindVis.NudgePlannedRelease(-(int)Math.Max(1.0, DoubleArg("--scrub-step=", 10.0)));
                }

                break;

            case Key.Right:
                if (key.Pressed && _rewindVis.Scrubbing)
                {
                    _rewindVis.NudgePlannedRelease((int)Math.Max(1.0, DoubleArg("--scrub-step=", 10.0)));
                }

                break;

            case Key.Escape:
                // Back to the front end. Deliberately always available: the level select is the only
                // way to reach the other levels, so being unable to leave a level would strand the
                // player in level 1.
                if (key.Pressed)
                {
                    if (_world.Rewinding) ReleaseRewind();
                    GetTree().ChangeSceneToFile("res://scenes/menu.tscn");
                }

                break;

            case Key.Enter:
                if (key.Pressed && _rewindVis.Scrubbing)
                {
                    ReleaseAtPlannedPoint();
                }

                break;

            case Key.Space:
                if (!key.Pressed) break;
                if (_world.Rewinding) Notify("回溯中无法重置", Palette.Bad);
                else
                {
                    ResetLevel();
                }

                break;

            case Key.F1:
                // F1 hides the ENTIRE left panel, and shows the numeric debug readings when the
                // panel is visible. Keeping both on one key means one press gives a clean playfield
                // for recording, and pressing again restores both the rules and the numbers.
                if (key.Pressed)
                {
                    _showHud = !_showHud;
                    _debugOverlay = _showHud;
                }

                break;

            case Key.F2:
                if (key.Pressed)
                {
                    _trail.ShowGhost = !_trail.ShowGhost;
                    Notify($"残影路径：{(_trail.ShowGhost ? "开" : "关")}", Palette.TextDim);
                }

                break;

            case Key.F3:
                if (key.Pressed)
                {
                    _trail.ShowTrail = !_trail.ShowTrail;
                    Notify($"历史轨迹：{(_trail.ShowTrail ? "开" : "关")}", Palette.TextDim);
                }

                break;
        }
    }

    private void HandleMouse(InputEventMouseButton button)
    {
        if (button.ButtonIndex != MouseButton.Left) return;

        // A pending draw owns the mouse too: the only legal click is on a card.
        if (CardChoiceOpen)
        {
            if (!button.Pressed) return;
            var hit = CardIndexAt(button.Position);
            if (hit >= 0) ChooseCard(hit);
            return;
        }

        if (button.Pressed)
        {
            // 1. Grab the scrub marker.
            if (_rewindVis.Scrubbing && _trail.HasGeometry)
            {
                var p = _trail.PointAt(_rewindVis.PlannedReleaseIndex);
                if ((p - button.Position).Length() < 22.0f)
                {
                    _scrubGrabbed = true;
                    return;
                }

                ScrubTo(button.Position);
                _scrubGrabbed = true;
                return;
            }

            // 2. Start aiming, but ONLY when the strike is still available and the cue is at rest.
            if (_world.ShotUsed || _world.Rewinding || _world.Won)
            {
                Notify(_world.Won ? "本关已清台"
                    : _world.Rewinding ? "回溯中无法瞄准"
                    : "每关仅一杆 · 已经用掉，剩余唯一操作是回溯", Palette.Bad);
                return;
            }

            var cue = CueScreen();
            if ((cue - button.Position).Length() < (float)_world.Cue.Radius + 14.0f)
            {
                _dragging = true;
                _dragPoint = button.Position;
            }
            else
            {
                Notify("请点中白球，再向后拖动并松开", Palette.TextDim);
            }
        }
        else
        {
            if (_scrubGrabbed)
            {
                _scrubGrabbed = false;
                return;
            }

            if (!_dragging) return;
            _dragging = false;
            var cue = CueScreen();
            var drag = _dragPoint - cue;
            var len = drag.Length();
            if (len < (float)_world.Config.MinPower)
            {
                Notify($"至少要拖动 {(float)_world.Config.MinPower:F0} px（本次只有 {len:F0}）", Palette.Warn);
                return;
            }

            var angle = Mathf.RadToDeg(Mathf.Atan2(drag.Y, drag.X));
            _aimAngle = angle;
            _aimPower = Math.Min(len, (float)_world.Config.PowerMax);
            _hasAim = true;
            FireShot();
        }
    }

    private bool _scrubGrabbed;

    /// <summary>Project the mouse onto the recorded path and move the release marker there.</summary>
    private void ScrubTo(Vector2 screen)
    {
        if (!_trail.HasGeometry) return;

        var from = _trail.TraversedRange.From;
        var to = _trail.TraversedRange.To;
        var best = -1;
        var bestD = float.MaxValue;
        for (var i = Math.Max(0, from); i <= to; i++)
        {
            var d = (_trail.PointAt(i) - screen).LengthSquared();
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        if (best < 0) return;
        if (best > to)
        {
            Notify("回溯不能沿路径向前跳", Palette.Bad);
            return;
        }

        var old = _rewindVis.PlannedReleaseIndex;
        _rewindVis.SetPlannedRelease(best);
        if (old >= 0 && _rewindVis.PlannedReleaseIndex <= old)
        {
            Notify($"释放点后移了 {Math.Abs(_rewindVis.PlannedReleaseIndex - old)} 个采样点", Palette.TextDim);
        }
    }

    private void ReleaseRewind()
    {
        if (_rewindVis.Scrubbing)
        {
            ReleaseAtPlannedPoint();
            return;
        }

        _world.EndRewind();
        _rewindVis.Reset();
        Notify($"已松开 · 速度取该历史点存储的 v（{_world.Cue.Speed:F0} px/s）", Palette.RewindTraversed);
    }

    /// <summary>
    /// Release at the scrubbed point. The crawl only walks backwards, so if the planned point is
    /// behind the cursor the kernel is advanced (same deterministic crawl, no state injection) until
    /// the planned history node is reached; if it is ahead of the cursor the input is refused.
    /// </summary>
    private void ReleaseAtPlannedPoint()
    {
        var target = _rewindVis.PlannedReleaseIndex;
        if (target < 0)
        {
            ReleaseRewind();
            return;
        }

        var guard = 0;
        while (_world.Rewinding && guard++ < 400000)
        {
            var live = _liveIndexForTrack(_world.RewindCursor);
            if (live <= target) break;
            _world.Step();
            _scriptTicks++;
        }

        _rewindVis.EndScrub(keepPaused: false);
        _world.EndRewind();
        _rewindVis.Reset();
        Notify($"已在微调点松开 · 存储的 v 为 {_world.Cue.Speed:F0} px/s", Palette.RewindTraversed);
    }

    private int _liveIndexForTrack(int trackIndex)
    {
        var to = _trail.TraversedRange.To;
        var from = _trail.TraversedRange.From;
        if (from < 0) return to;
        return Math.Clamp(from + trackIndex * (to - from) / Math.Max(1, _world.RewindCursor == 0 ? 1 : _world.RewindTrack.Length - 1), from, to);
    }

    private void ResetLevel()
    {
        _world.Reset();
        _trail.Reset();
        _rewindVis.Reset();
        _aimCache.Invalidate();
        _dragging = false;
        _hasAim = false;
        _resultRecorded = false;
        _earnedStars = 0;
        // Replay the coaching on an explicit reset: the player asked to try again, so re-prompting
        // the step they were on is useful rather than annoying.
        if (Menu.TutorialPending) ResetTutorial();
        Notify("关卡已重置 · 击球重新可用（调试键，不是规则）", Palette.TextDim);
    }

    // ---------------------------------------------------------------- audio

    /// <summary>Counters from the previous frame, so events are found by difference.</summary>
    private Rewind.Sim.SimStats _prevStats;

    /// <summary>
    /// Turns physics events into sound.
    ///
    /// Events are detected by DIFFERENCING the kernel's counters rather than by the kernel calling
    /// into audio: the kernel stays free of engine dependencies (that is what lets the headless gate
    /// and the solvability solver link it), and one place decides what a given event sounds like.
    ///
    /// Pitch is jittered per event so a rack break does not sound like one sound repeated twenty
    /// times, and hit volume scales with impact speed so a soft touch is audibly soft.
    /// </summary>
    private void PlayEventSounds()
    {
        var s = _world.Stats;

        // Ball-ball contacts: fire once per event, but cap the count per frame so a big break does
        // not spawn twenty overlapping players and clip the master bus.
        var contacts = s.BallBallContacts - _prevStats.BallBallContacts;
        if (contacts > 0)
        {
            var cue = contacts >= 4 ? SoundBank.Cue.BallHitHard : SoundBank.Cue.BallHitSoft;
            var n = Math.Min(contacts, 3);
            for (var i = 0; i < n; i++)
            {
                var speed = _world.Balls.Length > 0 ? _world.Cue.Speed : 0.0;
                var loud = Mathf.Clamp((float)(speed / 1258.0) + 0.18f, 0.18f, 1.0f);
                _audio?.Play(cue, loud, 0.92f + (float)GD.Randf() * 0.20f);
            }
        }

        var rails = s.RailTransforms - _prevStats.RailTransforms;
        if (rails > 0)
        {
            // Rail pitch rises with how many boosts the cue has already taken: the player can HEAR
            // that the rail is still paying out.
            var used = _world.Cue != null ? _world.Cue.WallBoostsUsed : 0;
            var pitch = 0.95f + Math.Min(used, 6) * 0.06f;
            _audio?.Play(SoundBank.Cue.RailBounce, 0.75f, pitch);
            SpawnRailEffects(rails, s.RailBoosts - _prevStats.RailBoosts);
        }

        var pockets = s.PocketCaptures - _prevStats.PocketCaptures;
        if (pockets > 0)
        {
            _audio?.Play(SoundBank.Cue.Pocket, 0.95f, 0.96f + (float)GD.Randf() * 0.12f);
            SpawnPotEffects(pockets);
        }

        var holes = s.WhiteHoleActivations - _prevStats.WhiteHoleActivations;
        if (holes > 0) _audio?.Play(SoundBank.Cue.WhiteHole, 0.95f);

        var rewindContacts = s.RewindContacts - _prevStats.RewindContacts;
        if (rewindContacts > 0) _audio?.Play(SoundBank.Cue.RewindContact, 0.8f, 1.0f + rewindContacts * 0.05f);

        // Rewind enter/leave is a state change, not a counter.
        if (_world.Rewinding != _wasRewinding)
        {
            _audio?.Play(_world.Rewinding ? SoundBank.Cue.RewindStart : SoundBank.Cue.RewindRelease, 0.9f);
            _wasRewinding = _world.Rewinding;
        }

        if (_world.Won && !_playedWinSound)
        {
            _playedWinSound = true;
            _audio?.Play(SoundBank.Cue.Win, 1.0f);
        }

        _prevStats = s.Clone();
    }

    private bool _wasRewinding;
    private bool _playedWinSound;

    // ------------------------------------------------------------------ effects

    /// <summary>
    /// Turn a rail hit into the ripple + streak the user asked for.
    ///
    /// THE WALL IS FOUND BY PROJECTION, NOT BY PROXIMITY, and that distinction is a real bug fix.
    ///
    /// The kernel reports only a COUNT, so which wall was hit has to be recovered from the ball's state.
    /// The obvious version tests "is the ball's edge within slop of a wall" -- which works only if the
    /// effect is spawned on the very step the ball touches the cushion. But sounds and effects are driven
    /// from per-FRAME stat deltas, and a frame runs several physics steps: by the time the view notices
    /// the counter moved, the ball has already left the cushion, matches no wall, and the effect silently
    /// never appears. Measured on the first capture attempt: effects=0 with railTransforms=1.
    ///
    /// So instead the ball is projected back onto whichever wall its REFLECTED velocity says it just came
    /// off: after a bounce off the right wall the cue is heading left, and so on. That is exact regardless
    /// of how many steps have passed since the contact, because the reflection direction is what the
    /// reflection wrote.
    /// </summary>
    private void SpawnRailEffects(int count, int boosts)
    {
        var ball = _world.Cue;
        if (ball == null || _level == null) return;

        var heading = new Vector2((float)ball.Velocity.X, (float)ball.Velocity.Y);

        var wall = ContactWallOf(ball);
        if (wall < 0) return;

        var along = ContactAlongOf(ball);
        var contact = ContactPointOf(ball);

        // Whether the rail actually PAID OUT, taken from the kernel's own count rather than from the
        // ball's allowance. Reading CanTakeWallBoost here is always false for a hit that paid, because
        // the kernel consumes the allowance in the same call that spends it -- so every impact was
        // drawn as the weak "spent" variant and the strong one was unreachable. See SimStats.RailBoosts.
        var boosted = boosts > 0;

        // ONE impact, never a fan of them.
        //
        // The previous version spawned up to two more copies offset along the wall whenever a frame
        // contained several bounces. That was a workaround for thin-stroke effects: a single 1.5 px
        // ring carried so little presence that stacking copies was the only way to make a multi-bounce
        // frame visible. A displacement-based impact needs no stacking -- it is already a large,
        // soft-edged deformation, and duplicates would read as a lumpy, obviously fake smear rather
        // than as one cushion being hit. The user asked for FEWER effects with MORE force, and this is
        // exactly that trade.
        _effects.RailHit(wall, along, contact, heading, Palette.RailBoost, boosted);

        // (count is retained in the signature for the caller's stat delta and is deliberately unused:
        // the deformation already spans the whole contact region, so a second bounce in the same frame
        // falls inside the same dent and adds nothing to draw.)
        _ = count;
    }

    /// <summary>
    /// Which wall the ball most recently bounced off, or -1. Delegates to
    /// <see cref="Effects.WallFromHeading"/> so the mapping has exactly one implementation and is unit
    /// testable without a Play node.
    /// </summary>
    private int ContactWallOf(Ball ball) => Effects.WallFromHeading(ball.Velocity);

    /// <summary>Position along the contacted wall, in table coordinates.</summary>
    private float ContactAlongOf(Ball ball)
    {
        var wall = ContactWallOf(ball);
        return wall <= 1 ? (float)ball.Position.Y : (float)ball.Position.X;
    }

    /// <summary>The contact point itself: the ball projected onto the wall it came off.</summary>
    private Vector2 ContactPointOf(Ball ball)
    {
        var wall = ContactWallOf(ball);
        var r = (float)ball.Radius;
        var pos = new Vector2((float)ball.Position.X, (float)ball.Position.Y);
        switch (wall)
        {
            case 0: return new Vector2((float)_level.TableLeft + r, pos.Y);
            case 1: return new Vector2((float)_level.TableRight - r, pos.Y);
            case 2: return new Vector2(pos.X, (float)_level.TableTop + r);
            case 3: return new Vector2(pos.X, (float)_level.TableBottom - r);
            default: return pos;
        }
    }

    /// <summary>Pot bursts, driven from the same stat delta the pot sound uses.</summary>
    private void SpawnPotEffects(int pockets)
    {
        if (pockets <= 0 || _level == null) return;
        foreach (var b in _world.Balls)
        {
            if (!b.Pocketed) continue;
            var at = new Vector2((float)b.Position.X, (float)b.Position.Y);

            // Snap the burst to the pocket the ball actually fell into, not to where the ball sat.
            var best = at;
            var bestDist = float.MaxValue;
            foreach (var p in _level.Pockets)
            {
                var c = new Vector2((float)p.Center.X, (float)p.Center.Y);
                var d = (c - at).LengthSquared();
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }

            _effects.Pot(best, Palette.Accent);
        }
    }

    // ------------------------------------------------------------------ cards

    /// <summary>
    /// Mirror the world's pending hand into the view. The world OWNS the hand but pauses nothing
    /// itself -- the kernel is stepped by whoever calls Step -- so the view stops stepping while a
    /// draw is pending, which is what "pause and require a choice" means in practice.
    /// </summary>
    private void UpdateCards()
    {
        if (_world == null) return;

        if (!_world.AwaitingCardChoice)
        {
            if (_cardHand.Count > 0)
            {
                _cardHand.Clear();
                _cardHover = -1;
            }

            return;
        }

        var pending = _world.PendingDraw;
        var changed = _cardHand.Count != pending.Count;
        if (!changed)
        {
            for (var i = 0; i < pending.Count; i++)
            {
                if (_cardHand[i] != pending[i]) changed = true;
            }
        }

        if (!changed) return;

        _cardHand.Clear();
        _cardHand.AddRange(pending);
        _cardHover = -1;
        _effects.CardReveal();
        _audio?.Play(SoundBank.Cue.UiHover, 1.0f, 0.85f);
    }

    /// <summary>Take card <paramref name="index"/> from the hand and resume play.</summary>
    private void ChooseCard(int index)
    {
        if (!_world.AwaitingCardChoice) return;
        if (index < 0 || index >= _cardHand.Count) return;

        var id = _cardHand[index];
        if (!_world.ChooseCard(index)) return;

        var def = CardPool.Get(id);
        _cardHand.Clear();
        _cardHover = -1;
        _rewindVis.Reset();
        _trail.Reset();
        _aimCache.Invalidate();
        _audio?.Play(SoundBank.Cue.UiClick, 1.0f, 1.05f);
        Notify($"获得事件卡 · {def?.Name ?? id.ToString()}：{def?.Short}", Palette.Accent);
        QueueRedraw();
    }

    /// <summary>True while the draw is open: the simulation must not advance.</summary>
    private bool CardChoiceOpen => _world != null && _world.AwaitingCardChoice;

    /// <summary>The physical table box, in table space.</summary>
    private Rect2 TableBox() => new Rect2(
        (float)_level.TableLeft, (float)_level.TableTop,
        (float)_level.TableWidth, (float)_level.TableHeight);

    /// <summary>Draw the hand. UI space, so this must run inside the inverse-scale transform.</summary>
    private void DrawCardPanel()
    {
        if (!CardChoiceOpen) return;

        // Sync HERE as well as in _Process. A scripted capture can reach _Draw without another _Process
        // tick, and the first version of the cards capture therefore drew nothing while the world
        // correctly reported a pending hand -- an inconsistency between the two halves of the feature.
        // UpdateCards is idempotent, so calling it from both places is free.
        UpdateCards();
        if (_cardHand.Count == 0) return;
        CardPanel.Draw(this, _font, _cardHand, _cardHover, _effects.CardRevealT);
    }

    /// <summary>Which card is under a point, or -1. Uses the SAME rect source as the drawing.</summary>
    private int CardIndexAt(Vector2 point)
    {
        if (!CardChoiceOpen) return -1;
        return CardPanel.IndexAt(point, _cardHand.Count, _cardHover);
    }

    /// <summary>The autoloaded audio front end, or null when it is unavailable (headless runs).</summary>
    private Audio _audio;

    // ---------------------------------------------------------------- tutorial
    private void ResetTutorial()
    {
        _tutorialActive = true;
        _tutorialStep = 0;
        _tutorialStepStartTime = 0.0;
    }

    /// <summary>
    /// Advances the coaching when the player actually performs the taught action. Called from the
    /// frame loop rather than from the input handlers so a step can depend on world state that the
    /// input handlers do not see (e.g. an object ball actually being struck).
    /// </summary>
    private void UpdateTutorial()
    {
        if (!TutorialVisible) return;
        if (_world.Time < _tutorialStepStartTime + 0.35) return;   // let the step be read

        switch (_tutorialStep)
        {
            case 0:
                // Step 1: make a strike. The prompt is the drag itself.
                if (_world.ShotUsed) AdvanceTutorial();
                break;

            case 1:
                // Step 2: rewind, and release. Both halves must happen.
                if (_world.Stats.Rewinds > 0 && !_world.Rewinding && _world.ShotUsed) AdvanceTutorial();
                break;

            case 2:
                // Step 3: use the white hole at least once (or finish, whichever comes first).
                if (_world.Stats.WhiteHoleActivations > 0 || _world.Won) AdvanceTutorial();
                break;

            case 3:
                // Closing note, dismissed by the player clearing the level or pressing Space.
                if (_world.Won) _tutorialActive = false;
                break;
        }
    }

    private void AdvanceTutorial()
    {
        _tutorialStep++;
        _tutorialStepStartTime = _world.Time;
        if (_tutorialStep >= 4) _tutorialActive = false;
        QueueRedraw();
    }

    /// <summary>
    /// The bubbles. Each is pinned to the thing it talks about rather than parked in a corner, so
    /// the lesson and the object are read together.
    /// </summary>
    private void DrawTutorial()
    {
        if (!TutorialVisible) return;

        var hasHole = _level.WhiteHoles.Count > 0;

        switch (_tutorialStep)
        {
            case 0:
                BubbleAt(CueScreen() + new Vector2(0, -58),
                    "按住白球往后拖，松手击球",
                    "这是本关唯一的一杆 —— 打完就不能再补杆了");
                break;

            case 1:
                BubbleAt(new Vector2(56, 96),
                    "现在按住 R，让白球原路倒回来",
                    "松手时它会保留当时的逆向速度，冲出一条新路线");
                break;

            case 2:
                if (hasHole)
                {
                    var h = _level.WhiteHoles[0];
                    BubbleAt(new Vector2((float)h.Center.X, (float)h.Center.Y),
                        "青色光环是白洞：白球穿过它会被加速",
                        "白球没速度了这关就结束 —— 白洞就是把它救回来的地方");
                }
                else
                {
                    BubbleAt(new Vector2(520, 120),
                        "把剩下的球都打进袋就通关",
                        "没有白洞的关卡要靠回溯与撞库来维持白球的速度");
                }

                break;

            case 3:
                BubbleAt(new Vector2(520, 110), "放开手试试看", "Esc 返回首页选关，空格重玩本关");
                break;
        }
    }

    /// <summary>One coaching bubble, anchored at a world position with a pointer down to it.</summary>
    private void BubbleAt(Vector2 anchor, string title, string body)
    {
        const float w = 430.0f;
        const float h = 76.0f;

        // Keep the bubble inside the canvas. The HUD panel occupies x < 260, so a bubble that wants
        // to be further left would be hidden behind it.
        const float minX = 268.0f;
        const float maxX = 1032.0f - w;                 // 602
        // NOTE: minX (268) < maxX (602) must hold. An earlier version passed a lower bound larger
        // than its upper bound to Math.Clamp, which silently returns the lower bound and pinned
        // every bubble to the left edge.
        var x = Math.Clamp(anchor.X - w * 0.5f, minX, maxX);

        // Prefer above the anchor, but drop below when that would collide with the top HUD band.
        var y = anchor.Y - h - 26.0f;
        if (y < 96.0f) y = Math.Min(anchor.Y + 26.0f, 470.0f);
        y = Math.Clamp(y, 96.0f, 470.0f);

        var rect = new Rect2(x, y, w, h);
        DrawRect(rect, new Color(0.02f, 0.09f, 0.10f, 0.94f));
        DrawRect(rect, Palette.SessionStripe, false, 2.0f);

        // Pointer from the bubble down to the anchor: makes clear what is being talked about.
        var tipX = Math.Clamp(anchor.X, x + 24, x + w - 24);
        var tipY = y + h;
        var target = new Vector2(anchor.X, Math.Min(anchor.Y - 14.0f, tipY + 30.0f));
        DrawLine(new Vector2(tipX, tipY), target, Palette.SessionStripe, 1.6f, true);
        DrawCircle(target, 3.0f, Palette.SessionStripe);

        DrawString(_font, new Vector2(x + 14, y + 28), title, HorizontalAlignment.Left, w - 28, 15, Palette.TextMain);
        DrawString(_font, new Vector2(x + 14, y + 52), body, HorizontalAlignment.Left, w - 28, 12, Palette.TextDim);
        DrawString(_font, new Vector2(x + 14, y + 69), $"引导 {_tutorialStep + 1} / 4", HorizontalAlignment.Left, -1, 10,
            new Color(0.45f, 0.62f, 0.58f, 1f));

        if (HasArg("--trace-bubble"))
            GD.Print($"[bubble] step={_tutorialStep} anchor=({anchor.X:F0},{anchor.Y:F0}) rect=({x:F0},{y:F0},{w:F0},{h:F0}) right={x + w:F0}");
    }

    private void Notify(string message, Color color)
    {
        _message = message;
        _messageColor = color;
        _messageTimer = 4.0;
        if (_verbose) GD.Print($"[play] {message}");
    }

    // =================================================================== draw

    public override void _Draw()
    {
        if (_world == null) return;
        var t0 = Time.GetTicksUsec();

        // The room backdrop and the warped table are both ShowBehindParent children, so they are
        // already submitted before this method runs. Drawing them again here would paint the room
        // straight over the table.
        DrawTable();

        // ---- rail impact, in TABLE space -----------------------------------------------------
        // Drawn after the table but before the balls, so a ball is never hidden by the material it
        // disturbed. The bent cushion edge and the contact flash live here; the SURFACE displacement is
        // not drawn at all -- it is the warp shader, applied to the table layer underneath.
        if (HasArg("--trace-fx") && _frames % 30 == 0)
        {
            GD.Print($"[fx] frame={_frames} effects={_effects.Count} hold={_captureHoldEffects} " +
                     $"warp={_warpActive} push={_impactPush:F1} age={_impactAge:F3} " +
                     $"contact=({_impactContact.X:F0},{_impactContact.Y:F0}) tableBox={TableBox()}");
        }

        _effects.DrawRailImpacts(this, TableBox());
        _effects.DrawPotBursts(this);

        if (_trail.ShowTrail) _trail.Draw();
        if (HasArg("--trace-draw")) GD.Print($"[draw] frame={_frames} {_trail.Stats()} show={_trail.ShowTrail} " +
            $"ranges={_trail.DebugChunkCount()} firstPt={_trail.DebugPoint(0)} lastPt={_trail.DebugPoint(_trail.CachedVertexCount - 1)}");

        if (_world.Rewinding)
        {
            DrawRewindPathOverlay();
        }

        DrawBalls();

        if (_world.Rewinding)
        {
            DrawRewindCue();
            _rewindVis.Draw(this, _trail, _font);
        }

        if (!_world.ShotUsed && !_world.Won) DrawAim();

        if (_world.Won) DrawWinBanner();

        // ---- UI space, not playfield space ------------------------------------------------
        // Keep UI coordinates equal to canvas pixels if ViewScale changes later. At the H5
        // artwork's current 1:1 scale this is the identity transform.
        DrawSetTransform(Vector2.Zero, 0.0f, new Vector2(1.0f / ViewScale, 1.0f / ViewScale));
        Hud.Draw(this, _font, BuildHudModel());
        if (_debugOverlay) Hud.DrawDebug(this, _font, BuildHudModel());
        DrawTutorial();
        DrawCardPanel();
        DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);

        // The decorative bands are drawn last, in UI space and OUTSIDE the inverse-view-scale block,
        // so they sit on top of the table and the HUD at the exact canvas coordinates they declare.
        // The top one and the bottom one are separate instances, drawn in build order.
        if (_bands != null && _showBand)
        {
            foreach (var b in _bands) b.DrawBand(this, Palette.TextMain);
        }

        _drawMs = (Time.GetTicksUsec() - t0) / 1000.0;
        if (HasArg("--trace-draw") && _frames % 60 == 0)
        {
            GD.Print($"[draw] frame={_frames} authored={_drawMs:F2}ms verts={_trail.CachedVertexCount} " +
                     $"drawCalls={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} " +
                     $"prims={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)} " +
                     $"objs={Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)} " +
                     $"proc={Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0:F2}ms");
        }
        _authoredVertexCounts = _trail.CachedVertexCount;
    }

    /// <summary>
    /// Set up the table surface's render target and the impact-warp material that displaces it.
    ///
    /// STRUCTURE: SubViewport (the table drawn transparent, at design size) -> ColorRect carrying
    /// impact_warp.gdshader -> added to this node, which is a Node2D behind the table's own draw list.
    /// Play's immediate-mode _Draw then paints balls, HUD and cards ON TOP of the warped surface.
    ///
    /// WHY NOT WARP IN THE MAIN VIEWPORT: SCREEN_TEXTURE is restricted under gl_compatibility, and
    /// sampling the whole screen would drag the HUD and the decorative bands into the distortion.
    /// A dedicated target means the warp can only ever affect the table.
    ///
    /// FAILURE MODE IS DELIBERATE: if the shader is missing the surface still renders, unwarped. The
    /// effect is decoration, so a missing resource must never cost the player the table.
    /// </summary>
    private void BuildTableWarp()
    {
        var size = new Vector2I(DesignWidth, DesignHeight);

        // ---- LAYER ORDER, which is load-bearing here ------------------------------------------
        // Both the room and the table are drawn as children with ShowBehindParent, so they land
        // BELOW everything Play's own _Draw paints (balls, aim line, HUD, cards). Children render in
        // tree order, so the room is added first and the table second.
        //
        // This nesting is the reason the room cannot simply be painted from Play._Draw: the parent's
        // own draw commands all run AFTER its ShowBehindParent children, so a room rect drawn there
        // would blanket the table. That is exactly the bug the first version of this method shipped --
        // every pixel of the table was covered by the backdrop, and because the backdrop is a flat
        // fill the table still "looked" present in a coarse diff while nothing could be seen.
        _roomLayer = new Node2D { Name = "RoomBackdrop", ShowBehindParent = true };
        _roomLayer.Draw += () => DrawRoom(_roomLayer);
        AddChild(_roomLayer);

        var shader = GD.Load<Shader>("res://shaders/impact_warp.gdshader");
        if (shader == null)
        {
            // No shader: draw the surface directly, exactly as before this feature. The effect is
            // decoration, so a missing resource must never cost the player the table.
            _tableBackground = new Node2D { Name = "TableSurface", ShowBehindParent = true };
            _tableBackground.Draw += () => DrawTableSurface(_tableBackground);
            AddChild(_tableBackground);
            GD.Print("[play] impact warp unavailable (shader missing); table drawn unwarped");
            return;
        }

        // ---- source: the table, rendered transparent into its own target ----------------------
        // A SubViewport rather than the main canvas because a UV offset needs a TEXTURE to sample.
        // Displacing each draw call's coordinates instead would move the geometry but not the artwork,
        // and could not show the surface tearing or refracting -- which is the effect that was asked
        // for ("uv偏移和扭曲").
        //
        // The target is the design size, so a dent measured in level px is the same px in texture
        // space and the two cannot drift apart.
        _warpViewport = new SubViewport
        {
            Name = "TableSurfaceTarget",
            Size = size,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            GuiDisableInput = true,
        };

        _warpSource = new Node2D { Name = "TableSurfaceSource" };
        _warpSource.Draw += () => DrawTableSurface(_warpSource);
        _warpViewport.AddChild(_warpSource);
        AddChild(_warpViewport);

        // The subtarget must render BEFORE the main viewport samples it, or the quad reads a texture
        // that has not been drawn into yet this frame and the table comes out blank or one frame stale.
        _warpViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

        // --dump-warp= writes the render target itself to disk. Without this there is no way to tell
        // "the shader is wrong" from "the target is empty": both present as a dark table, and the
        // capture only ever shows the final composite.
        var dump = Arg("--dump-warp=");
        if (dump != null) _dumpWarpPath = "res://evidence/" + dump;

        // ---- consumer: the same surface blitted back through the warp shader ------------------
        // CRITICAL: this node is a SIBLING of the viewport, never a child. A quad inside the viewport
        // it is meant to be reading is a feedback loop -- it would sample the target it is currently
        // rendering into.
        _warpMaterial = new ShaderMaterial { Shader = shader };
        _warpMaterial.SetShaderParameter("u_size", new Vector2(size.X, size.Y));
        PushWarpUniforms(idle: true);

        _tableBackground = new Node2D
        {
            Name = "TableSurfaceWarped",
            ShowBehindParent = true,
            Material = _warpMaterial,
            // Nearest: the target is rendered at exactly the design resolution, so filtering could
            // only soften an otherwise pixel-exact pass-through.
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
        };

        _tableBackground.Draw += () =>
        {
            // draw_texture_rect points the shader's TEXTURE at this viewport and puts UV at 0..1
            // across the rect, which is the mapping the shader assumes.
            _tableBackground.DrawTextureRect(
                _warpViewport.GetTexture(),
                new Rect2(0, 0, DesignWidth, DesignHeight),
                false);
        };

        AddChild(_tableBackground);
    }

    /// <summary>
    /// Everything that belongs to the table SURFACE -- cloth, rails, artwork, the faint quarter markers
    /// -- drawn into <paramref name="ci"/> so the impact can displace it.
    ///
    /// Shared by every theme. The H5 artwork branch used to paint straight into the parent, which meant
    /// the warp silently applied to the space theme only: the artwork was drawn on top of the warped
    /// quad and covered it.
    /// </summary>
    private void DrawTableSurface(Node2D ci)
    {
        var inner = new Rect2(
            (float)_level.TableLeft, (float)_level.TableTop,
            (float)_level.TableWidth, (float)_level.TableHeight);

        if (Palette.ActiveTheme == 0)
        {
            DrawSpaceTable(inner, ci);
            return;
        }

        // ---- classic / midnight-blue: the H5 prototype artwork ----
        if (_tableTexture != null)
        {
            // The H5 artwork is authored for the 1040x560 design canvas: its felt occupies
            // 65,63..975,497, i.e. exactly the default level's physics box (910x434), with the rail,
            // six pockets and rail markers outside that.
            //
            // So the art is scaled about the CENTRE of the physical box and aligned so the two
            // centres coincide. The original version anchored the art's top-left to the box's
            // top-left, which correctly overlaid the default table but pushed the side pockets and
            // both corner columns past the canvas edge (the right column landed at x=1078 on a
            // 1040-wide canvas), so the art was cropped exactly where it matters.
            //
            // For the default level this is an exact identity: scale 0.993 x 0.977 and a 0.7 px
            // nudge, because the art's felt centre sits 1.5 px above the level's box centre.
            const float artFeltW = 910.0f;
            const float artFeltH = 434.0f;
            const float artW = 1040.0f;
            const float artH = 560.0f;
            const float artFeltCx = 520.0f;
            const float artFeltCy = 278.5f;

            var sx = inner.Size.X / artFeltW;
            var sy = inner.Size.Y / artFeltH;
            var innerCx = inner.Position.X + inner.Size.X * 0.5f;
            var innerCy = inner.Position.Y + inner.Size.Y * 0.5f;

            var artPos = new Vector2(
                innerCx - artFeltCx * sx,
                innerCy - artFeltCy * sy);
            ci.DrawTextureRect(_tableTexture,
                new Rect2(artPos, new Vector2(artW * sx, artH * sy)), false);
        }
        else
        {
            DrawProceduralTable(inner, ci);
        }

        // Faint quarter markers so the player can judge angles (the prototype had nothing). They are
        // marks ON the cloth, so they belong to the surface and deform with it.
        for (var i = 1; i < 4; i++)
        {
            var x = inner.Position.X + inner.Size.X * i / 4.0f;
            ci.DrawLine(new Vector2(x, inner.Position.Y), new Vector2(x, inner.Position.Y + 10.0f),
                Palette.FeltGrid, 1.0f);
            ci.DrawLine(new Vector2(x, inner.End.Y - 10.0f), new Vector2(x, inner.End.Y),
                Palette.FeltGrid, 1.0f);
            var y = inner.Position.Y + inner.Size.Y * i / 4.0f;
            ci.DrawLine(new Vector2(inner.Position.X, y), new Vector2(inner.Position.X + 10.0f, y),
                Palette.FeltGrid, 1.0f);
            ci.DrawLine(new Vector2(inner.End.X - 10.0f, y), new Vector2(inner.End.X, y),
                Palette.FeltGrid, 1.0f);
        }
    }

    /// <summary>
    /// Feed the current impact into the warp material, or switch it off when nothing is happening.
    ///
    /// The IDLE PATH MATTERS: with strength 0 the shader takes an exact pass-through branch, so the
    /// table is byte-identical to the unwarped render while nothing is going on. That keeps the
    /// distortion from quietly softening every frame of normal play, and it means a capture with no
    /// impact is still a valid baseline.
    /// </summary>
    private void PushWarpUniforms(bool idle)
    {
        if (_warpMaterial == null) return;

        if (idle)
        {
            _warpMaterial.SetShaderParameter("u_impact", new Vector4(0, 0, 0, 0));
            return;
        }

        // --warp-push= overrides the magnitude. Used to prove the warp pipeline independently of the
        // effect's own tuning: a felt-coloured region is nearly uniform, so a small displacement of it
        // produces identical pixels and cannot be measured by differencing. An exaggerated push makes
        // the displacement unmistakable, which is what turns "the diff changed" into "the surface
        // actually moved".
        var push = _impactPush;
        var forced = DoubleArg("--warp-push=", double.NaN);
        if (!double.IsNaN(forced)) push = (float)forced;

        _warpMaterial.SetShaderParameter("u_impact",
            new Vector4(_impactContact.X, _impactContact.Y, push, _impactAge));
        _warpMaterial.SetShaderParameter("u_normal",
            new Vector2(_impactNormal.X, _impactNormal.Y));
        _warpMaterial.SetShaderParameter("u_tint",
            new Vector4(_impactTint.R, _impactTint.G, _impactTint.B, 1.0f));
    }

    /// <summary>
    /// Hand the strongest live impact to the shader. Called once per frame from _Process, before the
    /// draw, so the warp and the geometry it accompanies are computed from the same instant.
    /// </summary>
    private void UpdateWarp()
    {
        if (_warpMaterial == null) return;

        if (_suppressWarp || _world == null || !_effects.TryGetImpact(out var impact))
        {
            if (_warpActive) { PushWarpUniforms(idle: true); _warpActive = false; }
            return;
        }

        _impactContact = impact.Contact;
        _impactNormal = impact.Normal;
        _impactPush = impact.PeakPush;
        _impactAge = impact.Age;
        _impactTint = impact.Tint;
        _warpActive = true;
        PushWarpUniforms(idle: false);
    }

    /// <summary>
    /// The room backdrop, always drawn in the main canvas UNDER the warped table surface.
    ///
    /// This used to early-return when the background node existed, because that node drew the room as
    /// its first primitive. The room can no longer live there: the table now renders into a transparent
    /// SubViewport (so the warp shader has a texture to displace), and a transparent target cannot be
    /// relied on to cover the letterbox area. Drawing it here, in the parent, keeps the backdrop in the
    /// one place that is always opaque and never warped.
    /// </summary>
    /// <summary>
    /// The room backdrop, drawn into the ShowBehindParent layer UNDER the warped table surface.
    ///
    /// This used to early-return when the background node existed, because that node drew the room as
    /// its first primitive. The room can no longer live there: the table renders into a transparent
    /// SubViewport (so the warp shader has a texture to displace), and a transparent target cannot be
    /// relied on to cover the letterbox area. Keeping it in its own layer, below the table, is the one
    /// place that is always opaque and never warped.
    /// </summary>
    private void DrawRoom(Node2D ci)
    {
        // Node coordinates, scaled into the canvas by ViewScale. The rects are sized so the visible
        // window (0..928 x 0..500 at scale 1.12) is fully covered.
        //
        // The backdrop is deliberately drawn far larger than the design canvas. With
        // stretch/aspect="keep" a window whose aspect differs from 1040x560 letterboxes the
        // view, and anything outside these rects showed the engine's default clear colour as
        // a hard BLACK bar at the bottom of the screen (observed in the exported build on a
        // 1025x578 client area). Overscanning costs two triangles and makes the letterbox
        // read as part of the room instead of as a bug.
        ci.DrawRect(new Rect2(-600, -600, 2400, 2000), Palette.RoomBg);
    }

    /// <summary>
    /// The table. Two sources, in priority order:
    ///
    ///  1. the re-used H5 prototype artwork (`res://assets/h5/table.svg`), which is stitched to the
    ///     level's physical surface so alternate table sizes still line up;
    ///  2. <see cref="DrawProceduralTable"/>, a rail frame plus cushion bevel, used when that texture
    ///     is missing so a shipped build never degrades to a bare canvas.
    ///
    /// Both are drawn so the visible cushion edge coincides with the physics boundary
    /// (TableLeft/Right/Top/Bottom). Decorate the inside and balls would appear to bounce off nothing.
    /// </summary>
    private void DrawTable()
    {
        var inner = new Rect2(
            (float)_level.TableLeft, (float)_level.TableTop,
            (float)_level.TableWidth, (float)_level.TableHeight);

        // The surface is NOT drawn from here: BuildTableWarp already put it either into the warp target
        // or (with no shader) into _tableBackground, for every theme. The guard is on _tableBackground,
        // which is null only if BuildTableWarp never ran; testing for the warp source instead would draw
        // the table a second time on top of itself in the no-shader path.
        //
        // What remains here is the content that must NOT be distorted: the pocket mouths and white holes
        // are openings IN the surface, and dragging them through the warp would make the holes swim,
        // which reads as a rendering fault rather than as an impact.
        if (_tableBackground == null)
        {
            DrawTableSurface(this);
        }

        DrawPockets();
        DrawWhiteHoles();
    }

    /// <summary>
    /// The universe table.
    ///
    /// A starfield written deterministically from a fixed seed, so the sky is IDENTICAL in every run and
    /// every capture -- a random sky would make two screenshots of the same moment differ, which breaks
    /// the pixel evidence the rest of this project relies on.
    ///
    /// Layout, inside out:
    ///   * deep void cloth, with a faint nebula wash for depth;
    ///   * three star layers (dim, mid, bright) at different densities and sizes -- one uniform layer
    ///     reads as noise, whereas layered sizes read as distance;
    ///   * a subtle brighter core in the middle so the play area does not look flat;
    ///   * rails as dark metal banding with a cool inner bevel.
    /// The physics box is the CLOTH rectangle: the rail band is drawn strictly outside it, so the visible
    /// cushion edge is the same line the kernel bounces off.
    /// </summary>
    private void DrawSpaceTable(Rect2 inner, CanvasItem ci)
    {
        // ---- rails, outside the physics box
        const float railThick = 26.0f;
        var outer = inner.Grow(railThick);
        ci.DrawRect(outer, Palette.RailShadow);
        ci.DrawRect(outer.Grow(-3.0f), Palette.RailWood);
        ci.DrawRect(outer.Grow(-3.0f), Palette.RailLight, false, 1.0f);

        // ---- cloth
        ci.DrawRect(inner, Palette.Felt);

        // Nebula wash: a few large, very faint discs so the void is not a flat fill. Fixed positions.
        var nebula = new[]
        {
            (0.24f, 0.28f, 0.42f, new Color("#2b1a5e", 0.30f)),
            (0.74f, 0.62f, 0.50f, new Color("#123a63", 0.28f)),
            (0.52f, 0.86f, 0.34f, new Color("#4a1d4e", 0.22f)),
        };
        foreach (var (nx, ny, nr, col) in nebula)
        {
            ci.DrawCircle(new Vector2(inner.Position.X + inner.Size.X * nx, inner.Position.Y + inner.Size.Y * ny),
                inner.Size.X * nr, col, true, -1.0f, true);
        }

        // Starfield, deterministic. Park-Miller LCG seeded with a constant: no RNG state to leak, and
        // identical across processes.
        long seed = 20261011;
        double NextUnit()
        {
            seed = (seed * 48271) % 2147483647;
            return seed / 2147483647.0;
        }

        // (count, radius, alpha) per layer: sparse bright, mid, dense dim.
        var layers = new[]
        {
            (34, 1.7f, 0.95f),
            (95, 1.15f, 0.62f),
            (230, 0.75f, 0.34f),
        };
        var star = new Color("#dfe8ff");
        var warm = new Color("#ffe9c9");

        foreach (var (count, radius, alpha) in layers)
        {
            for (var i = 0; i < count; i++)
            {
                var x = inner.Position.X + (float)(NextUnit() * inner.Size.X);
                var y = inner.Position.Y + (float)(NextUnit() * inner.Size.Y);
                // A few stars are warm, so the field is not one flat colour.
                var col = NextUnit() < 0.12 ? warm : star;
                ci.DrawCircle(new Vector2(x, y), radius, new Color(col.R, col.G, col.B, alpha), true, -1.0f, true);
            }
        }

        // ---- cushion bevel, inside the rail band but outside the cloth
        ci.DrawRect(inner.Grow(8.0f), Palette.CushionLight, false, 3.0f);
        ci.DrawRect(inner, Palette.CushionInner, false, 2.0f);

        // ---- faint quarter markers, so angles can still be judged
        for (var i = 1; i < 4; i++)
        {
            var x = inner.Position.X + inner.Size.X * i / 4.0f;
            ci.DrawLine(new Vector2(x, inner.Position.Y), new Vector2(x, inner.Position.Y + 10.0f), Palette.FeltGrid, 1.0f);
            ci.DrawLine(new Vector2(x, inner.End.Y - 10.0f), new Vector2(x, inner.End.Y), Palette.FeltGrid, 1.0f);
            var y = inner.Position.Y + inner.Size.Y * i / 4.0f;
            ci.DrawLine(new Vector2(inner.Position.X, y), new Vector2(inner.Position.X + 10.0f, y), Palette.FeltGrid, 1.0f);
            ci.DrawLine(new Vector2(inner.End.X - 10.0f, y), new Vector2(inner.End.X, y), Palette.FeltGrid, 1.0f);
        }
    }

    /// <summary>
    /// Fallback table when the H5 artwork is unavailable: a rail frame outside the physics box plus a
    /// three-band cushion bevel. Kept so a missing texture degrades to a plain but correct table
    /// rather than a bare canvas.
    /// </summary>
    private void DrawProceduralTable(Rect2 inner, Node2D ci = null)
    {
        ci ??= this;
        const float railThick = 26.0f;
        var outer = inner.Grow(railThick);
        ci.DrawRect(outer, Palette.RailShadow);
        ci.DrawRect(outer.Grow(-3.0f), Palette.RailWood);
        ci.DrawRect(outer.Grow(-3.0f), Palette.RailLight, false, 1.0f);

        ci.DrawRect(inner.Grow(8.0f), Palette.CushionLight);
        ci.DrawRect(inner.Grow(4.0f), Palette.CushionMid);
        ci.DrawRect(inner, Palette.CushionInner);

        ci.DrawRect(inner, Palette.Felt);
    }

    /// <summary>
    /// Pocket mouths. Drawn as an opening rather than a flat disc: a dark ring of rail material
    /// around a near-black hole, plus a crescent of shadow on the table side so the hole reads as
    /// going DOWN into the table.
    /// </summary>
    private void DrawPockets()
    {
        foreach (var pocket in _level.Pockets)
        {
            var c = new Vector2((float)pocket.Center.X, (float)pocket.Center.Y);
            var r = (float)pocket.Radius;

            if (Palette.ActiveTheme == 0)
            {
                // BLACK HOLE. It must read as something that DEVOURS, and as the visual opposite of the
                // white hole that gives energy back. Three cues do that: an accretion rim (light bent
                // around the mouth), a hard event-horizon ring, and a core darker than the cloth, so the
                // eye reads a hole rather than a disc sitting on the table.
                var rim = Palette.PocketRim;
                DrawCircle(c, r + 6.0f, new Color(rim.R, rim.G, rim.B, 0.30f), true, -1.0f, true);
                DrawCircle(c, r + 3.0f, new Color(rim.R, rim.G, rim.B, 0.55f), false, 1.6f, true);
                DrawCircle(c, r, Palette.PocketMouth, true, -1.0f, true);
                DrawCircle(c, r, rim, false, 1.2f, true);
                DrawCircle(c, r * 0.82f, Palette.PocketLiner, true, -1.0f, true);
                DrawCircle(c, r * 0.34f, new Color(0, 0, 0, 0.95f), true, -1.0f, true);
                continue;
            }

            DrawCircle(c, r + 5.0f, Palette.PocketMouth, true, -1.0f, true);   // cut through the rail
            DrawCircle(c, r + 1.5f, Palette.PocketLiner, true, -1.0f, true);   // liner highlight
            DrawCircle(c, r, Palette.Pocket, true, -1.0f, true);               // the hole itself

            // Shadow crescent on the table side: an offset dark disc clipped visually by the liner.
            DrawCircle(c + new Vector2(-r * 0.18f, r * 0.18f), r * 0.86f,
                new Color(0, 0, 0, 0.35f), true, -1.0f, true);
            DrawCircle(c, r * 0.90f, new Color(0, 0, 0, 0.55f), true, -1.0f, true);
        }
    }

    /// <summary>
    /// White holes: energy-return zones. They must read as the OPPOSITE of a pocket -- a pocket
    /// swallows the ball, a white hole gives it back with interest -- so they are drawn as a bright
    /// emitting ring with a soft halo and an inward-pulsing core, never as a dark disc.
    ///
    /// The pulse is driven by the simulation clock, not by frame time, so the animation is identical
    /// in every capture and does not depend on frame rate.
    /// </summary>
    private void DrawWhiteHoles()
    {
        if (_level.WhiteHoles.Count == 0) return;
        var t = (float)_world.Time;

        foreach (var hole in _level.WhiteHoles)
        {
            var c = new Vector2((float)hole.Center.X, (float)hole.Center.Y);
            var r = (float)hole.Radius;
            var pulse = 0.5f + 0.5f * Mathf.Sin(t * 3.4f);

            // Halo: three rings that grow and fade, so it reads as emitting rather than solid.
            for (var i = 0; i < 3; i++)
            {
                var k = (pulse + i / 3.0f) % 1.0f;
                var rr = r * (0.72f + 0.55f * k);
                var a = (1.0f - k) * 0.30f;
                DrawCircle(c, rr, new Color(Palette.WhiteHoleHalo.R, Palette.WhiteHoleHalo.G,
                    Palette.WhiteHoleHalo.B, a), false, 2.0f, true);
            }

            // Body: a soft filled disc so it is unmistakably a zone, not a rim.
            DrawCircle(c, r, new Color(Palette.WhiteHoleBody.R, Palette.WhiteHoleBody.G,
                Palette.WhiteHoleBody.B, 0.22f + 0.10f * pulse), true, -1.0f, true);
            DrawCircle(c, r, Palette.WhiteHoleRing, false, 2.0f, true);

            // Core: a bright centre that breathes, reading as the energy source.
            DrawCircle(c, r * (0.20f + 0.07f * pulse), Palette.WhiteHoleCore, true, -1.0f, true);

            // Four chevrons pointing INWARD: the direction the energy flows.
            var d = r * 0.55f;
            for (var i = 0; i < 4; i++)
            {
                var ang = Mathf.Pi * 0.5f * i + t * 0.6f;
                var dirv = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                var tip = c + dirv * (r * 0.80f);
                // Not `base`: that is a reserved keyword in C#.
                var outer = c + dirv * (r * 0.80f + d * 0.42f);
                var perp = new Vector2(-dirv.Y, dirv.X) * (d * 0.22f);
                DrawLine(tip, outer + perp, Palette.WhiteHoleRing, 1.6f, true);
                DrawLine(tip, outer - perp, Palette.WhiteHoleRing, 1.6f, true);
            }
        }
    }

    private void DrawBalls()
    {
        for (var i = 0; i < _world.Balls.Length; i++)
        {
            var ball = _world.Balls[i];
            var spawn = _level.Balls.Count > i ? _level.Balls[i] : null;
            var glyph = BallStyles.GlyphFor(ball, spawn);
            var highlight = _world.Rewinding ? _rewindVis.HighlightWeight(ball.Id) : 0.0f;
            if (highlight > 0.0f)
            {
                // Pulse so "about to be hit" reads as an event, not a static decoration.
                highlight = 0.55f + 0.45f * Mathf.Sin((float)_wallClock * 9.0f);
            }

            BallStyles.Draw(this, ball, glyph, ball.IsCue ? 0.0f : highlight, _font, 13);

            if (!ball.Pocketed && ball.Speed > 1.0)
            {
                var c = new Vector2((float)ball.Position.X, (float)ball.Position.Y);
                var v = new Vector2((float)ball.Velocity.X, (float)ball.Velocity.Y);
                var dir = v.Normalized();
                var len = Mathf.Clamp((float)ball.Speed * 0.045f, 8.0f, 52.0f);
                DrawLine(c + dir * ((float)ball.Radius + 2.0f), c + dir * ((float)ball.Radius + 2.0f + len),
                    new Color(1, 1, 1, 0.55f), 1.6f, true);
            }
        }
    }

    private void DrawAim()
    {
        var pred = _aimCache.Value;
        if (_hasAim && !_world.ShotUsed && !_world.Rewinding)
        {
            if (!_aimCache.Matches(_world, _aimAngle, _aimPower))
            {
                pred = AimPredictor.Trace(_world, _aimAngle, _aimPower);
                _aimCache.Store(_world, _aimAngle, _aimPower, pred);
            }

            if (pred != null && pred.Valid) AimPredictor.Draw(this, pred, _font);
        }

        if (_dragging)
        {
            var cue = CueScreen();
            var drag = _dragPoint - cue;
            var len = drag.Length();
            var dir = len > 1e-3f ? drag / len : Vector2.Zero;
            var clamped = Math.Min(len, (float)_world.Config.PowerMax);
            var t = clamped / (float)_world.Config.PowerMax;

            // Pull-back band: the drag is measured backwards from the cue, i.e. a slingshot.
            DrawLine(cue, cue + dir * Math.Min(len, 260.0f), Palette.MeterLow.Lerp(Palette.MeterHigh, t), 3.0f, true);
            DrawCircle(cue + dir * clamped, 7.0f, Palette.MeterLow.Lerp(Palette.MeterHigh, t), false, 2.0f, true);
            if (len > (float)_world.Config.PowerMax)
            {
                DrawCircle(cue + dir * clamped, 11.0f, Palette.Warn, false, 1.4f, true);
                DrawString(_font, cue + dir * clamped + new Vector2(10, -10), "满力", HorizontalAlignment.Left, -1, 11, Palette.Warn);
            }
        }
    }

    /// <summary>Dim the not-yet-walked part of the frozen path and mark the release point.</summary>
    private void DrawRewindPathOverlay()
    {
        var track = _world.RewindTrack;
        if (track == null || track.Length < 2) return;

        var points = new Vector2[track.Length];
        for (var i = 0; i < track.Length; i++)
        {
            points[i] = new Vector2((float)track[i].Position.X, (float)track[i].Position.Y);
        }

        DrawPolyline(points, new Color(Palette.RewindRemaining.R, Palette.RewindRemaining.G, Palette.RewindRemaining.B, 0.55f), 1.2f, true);

        // The part the crawl has not reached yet: where the cue is going if the player holds R.
        var toGo = new List<Vector2>();
        for (var i = _world.RewindCursor; i >= 0; i--) toGo.Add(points[i]);
        if (toGo.Count >= 2)
        {
            DrawPolyline(toGo.ToArray(), new Color(0.85f, 0.95f, 1.0f, 0.35f), 1.6f, true);
        }
    }

    private void DrawRewindCue()
    {
        var cue = _world.Cue;
        var c = new Vector2((float)cue.Position.X, (float)cue.Position.Y);
        var r = (float)cue.Radius;
        var pulse = 0.5f + 0.5f * Mathf.Sin((float)_wallClock * 8.0f);

        DrawCircle(c, r + 5.0f + 2.0f * pulse, new Color(Palette.RewindCue.R, Palette.RewindCue.G, Palette.RewindCue.B, 0.30f), true, -1.0f, true);
        DrawCircle(c, r, Palette.RewindCue, true, -1.0f, true);
        DrawCircle(c, r, new Color("#ffe9d2"), false, 2.0f, true);
        DrawCircle(c, r * 0.36f, new Color("#fff3e0"), true, -1.0f, true);

        // Rewind chevrons: the cue is being pulled backwards, not rolling.
        var dir = _world.Cue.Velocity;
        var d = dir.LengthSquared > 1e-9 ? new Vector2((float)dir.X, (float)dir.Y).Normalized() : new Vector2(-1, 0);
        for (var i = 0; i < 3; i++)
        {
            var offset = new Vector2(-d.X, -d.Y) * (r + 8.0f + i * 9.0f);
            var p = c + offset;
            var n = new Vector2(-d.Y, d.X);
            DrawLine(p - n * 6.0f - d * 4.0f, p + d * 4.0f, new Color(1f, 0.85f, 0.6f, 0.85f - i * 0.22f), 2.0f, true);
            DrawLine(p + n * 6.0f - d * 4.0f, p + d * 4.0f, new Color(1f, 0.85f, 0.6f, 0.85f - i * 0.22f), 2.0f, true);
        }

        DrawString(_font, c + new Vector2(0, -r - 14.0f), "回溯中", HorizontalAlignment.Center, 120.0f, 12, Palette.RewindCue);
    }

    private void DrawWinBanner()
    {
        // Record the clear exactly once. Doing it here (rather than in the physics) keeps the kernel
        // free of file IO, and the guard keeps it idempotent because _Draw runs every frame.
        if (!_resultRecorded)
        {
            _resultRecorded = true;
            _earnedStars = LevelCatalog.RecordResult(
                _level.Id, _world.Stats.Rewinds, _world.Stats.WhiteHoleActivations);
        }

        var rect = new Rect2(300, 178, 440, 186);
        DrawRect(rect, new Color(0.03f, 0.08f, 0.05f, 0.92f));
        DrawRect(rect, Palette.Good, false, 3.0f);
        DrawString(_font, new Vector2(rect.Position.X, rect.Position.Y + 50), "清台成功", HorizontalAlignment.Center, rect.Size.X, 32, Palette.Good);

        // Stars earned, drawn large: this is the reward the front end records.
        for (var i = 0; i < 3; i++)
        {
            var on = i < _earnedStars;
            DrawString(_font, new Vector2(rect.Position.X + 150 + i * 46, rect.Position.Y + 92),
                on ? "★" : "☆", HorizontalAlignment.Left, -1, 30,
                on ? Palette.Accent : new Color(0.32f, 0.36f, 0.34f, 1f));
        }

        DrawString(_font, new Vector2(rect.Position.X, rect.Position.Y + 128),
            $"{_world.PocketedObjectCount}/{_world.ObjectBallCount} 颗 · 用时 {_world.Time:F2}s · 回溯 {_world.Stats.Rewinds} 次",
            HorizontalAlignment.Center, rect.Size.X, 15, Palette.TextMain);
        DrawString(_font, new Vector2(rect.Position.X, rect.Position.Y + 154),
            "Esc 返回首页 · 空格 重玩本关",
            HorizontalAlignment.Center, rect.Size.X, 13, Palette.TextDim);
    }

    // =============================================================== hud model

    private Hud.Model BuildHudModel()
    {
        var m = new Hud.Model
        {
            LevelId = _level.Id,
            LevelName = _level.Name,
            ObjectBallCount = _world.ObjectBallCount,
            PocketedObjectCount = _world.PocketedObjectCount,
            ShotUsed = _world.ShotUsed,
            Rewinding = _world.Rewinding,
            Won = _world.Won,
            Paused = _rewindVis.Paused,
            Scrubbing = _rewindVis.Scrubbing,
            Tick = _world.Tick,
            Time = _world.Time,
            AimAngle = _aimAngle,
            AimPower = _aimPower,
            HasAim = _hasAim,
            Dragging = _dragging,
            MinPower = _world.Config.MinPower,
            MaxPower = _world.Config.PowerMax,
            Speed = _world.ShotUsed ? _world.Cue.Speed : _aimPower * _world.Config.SpeedPerPower,
            Rewinds = _world.Stats.Rewinds,
            RewindSegmentsUsed = _world.RewindSegmentsUsed,
            RewindSegmentsMax = _world.Budget.MaxSegments,
            RewindDistanceUsed = _world.RewindDistanceUsed,
            RewindDistanceMax = _world.Budget.MaxDistance,
            HistoryEntries = _world.Cue.History.Count,
            TrailSessions = _trail.SessionCount,
            TurnBacks = _trail.TurnBackCount,
            TurnForwards = _trail.TurnForwardCount,
            ForecastContacts = _rewindVis.ContactCount,
            ForecastUs = _rewindVis.ForecastMicroseconds,
            Message = _messageTimer > 0.0 ? _message : "",
            MessageColor = _messageColor,
            Debug = _debugOverlay,
            ShowPanel = _showHud,
            WallBoostsRemaining = _world.Cue != null && _world.Cue.WallBoostsRemaining.HasValue
                ? _world.Cue.WallBoostsRemaining.Value
                : -1,
            World = _world,
            Trail = _trail,
            RewindVis = _rewindVis,
            Aim = _aimCache.Value,
            FrameMs = _lastFrameMs,
            MaxFrameMs = _maxFrameMs,
            DrawMs = _drawMs,
            SimMs = _simMs,
            TrailVertices = _trail.CachedVertexCount,
            Bands = _bands,
            Cards = BuildCardLabels(),
            RapidTapCount = _world.RapidTapCount,
            NextTapIsDeathThroe = _world.RapidTapCount == 1,
            BalloonArmed = _world.Cards.BalloonArmed && !_world.Cards.Lost,
            Lost = _world.Cards.Lost,
            LostReason = _world.Cards.LostReason,
        };
        return m;
    }

    /// <summary>Card names for the HUD, or null when none have been taken.</summary>
    private System.Collections.Generic.List<string> BuildCardLabels()
    {
        if (_world.Cards.Taken.Count == 0) return null;
        var list = new System.Collections.Generic.List<string>(_world.Cards.Taken.Count);
        foreach (var id in _world.Cards.Taken)
        {
            list.Add(CardPool.Get(id)?.Name ?? id.ToString());
        }

        return list;
    }

    /// <summary>
    /// Drive the REAL input handlers with synthetic events, so the strike path is exercised the way
    /// a player exercises it: press on the cue ball, drag away, release = one strike, and the second
    /// attempt must be refused. Scripted captures set the aim directly, which would leave this path
    /// untested - this closes that gap without a human.
    /// </summary>
    private void RunInputSelfTest()
    {
        GD.Print("[input] driving Play._UnhandledInput with synthetic mouse/keyboard events");
        var cue = CueScreen();

        // 1. Press on the cue ball. A press on empty felt must NOT start an aim.
        var offBall = cue + new Vector2(120, 60);
        _UnhandledInput(Press(offBall, true));
        _UnhandledInput(Press(offBall, false));
        Check("pressing away from the cue ball does not strike", !_world.ShotUsed, $"shotUsed={_world.ShotUsed}");

        // 2. Real drag: down on the cue, move, release. Pull back along +x => the cue must go +x.
        var pullBack = new Vector2(160f, 0f);
        _UnhandledInput(Press(cue, true));
        _UnhandledInput(MouseMotion(cue + pullBack * 0.5f));
        _UnhandledInput(MouseMotion(cue + pullBack));
        _UnhandledInput(Press(cue + pullBack, false));

        var expectedPower = Math.Min((double)pullBack.Length(), _world.Config.PowerMax);
        Check("a drag from the cue ball produces exactly one strike", _world.ShotUsed,
            $"shotUsed={_world.ShotUsed} angle={_aimAngle:F3} power={_aimPower:F3}");
        Check("the drag angle is the pull direction reversed", Math.Abs(_aimAngle) < 1e-3,
            $"angle={_aimAngle:F3} deg (expected 0.0 for a pull along +x)");
        Check("the drag distance is the shot power", Math.Abs(_aimPower - expectedPower) < 1e-6,
            $"power={_aimPower:F3} drag={expectedPower:F3}");
        Check("the launch speed is power x SpeedPerPower",
            Math.Abs(_world.Cue.Speed - _aimPower * _world.Config.SpeedPerPower) < 1e-6,
            $"speed={_world.Cue.Speed:F3} expected={_aimPower * _world.Config.SpeedPerPower:F3}");
        Check("the input sequence recorded exactly one shot", _world.Inputs.Count == 1,
            $"inputs={_world.Inputs.Count} kind={(_world.Inputs.Count > 0 ? _world.Inputs[0].Kind.ToString() : "none")}");

        // 3. A second drag must be refused: one strike per level.
        Settle();
        var beforeSpeed = _world.Cue.Speed;
        _UnhandledInput(Press(cue, true));
        _UnhandledInput(Press(cue + pullBack, false));
        Check("a second drag cannot strike again", _world.Inputs.Count == 1,
            $"inputs={_world.Inputs.Count} speedBefore={beforeSpeed:F3} speedNow={_world.Cue.Speed:F3}");

        // 4. Hold R = rewind, release R = release (the verb path through the real key handler).
        _UnhandledInput(KeyEvent(Key.R, true));
        Check("holding R starts a rewind", _world.Rewinding, $"rewinding={_world.Rewinding} segments={_world.RewindSegmentsUsed}");
        var guard = 0;
        while (_world.Rewinding && guard++ < 4000) StepOnceForInputTest();
        _UnhandledInput(KeyEvent(Key.R, false));
        Check("releasing R ends the rewind", !_world.Rewinding, $"rewinding={_world.Rewinding}");

        // 5. P pauses for scrubbing, arrows move the marker, Enter releases at it.
        _UnhandledInput(KeyEvent(Key.R, true));
        for (var i = 0; i < 90; i++) StepOnceForInputTest();
        _UnhandledInput(KeyEvent(Key.P, true));
        var markerBefore = _rewindVis.PlannedReleaseIndex;
        _UnhandledInput(KeyEvent(Key.Left, true));
        _UnhandledInput(KeyEvent(Key.Right, true));
        var markerAfter = _rewindVis.PlannedReleaseIndex;
        Check("P pauses the rewind and arms the scrub marker",
            _rewindVis.Scrubbing && _rewindVis.PlannedReleaseIndex >= 0,
            $"scrubbing={_rewindVis.Scrubbing} marker={markerBefore} -> {markerAfter} " +
            $"span={_rewindVis.MinReleaseIndex}..{_rewindVis.MaxReleaseIndex}");
        _UnhandledInput(KeyEvent(Key.Enter, true));
        Check("Enter releases at the scrubbed point", !_world.Rewinding && !_rewindVis.Scrubbing,
            $"rewinding={_world.Rewinding} scrubbing={_rewindVis.Scrubbing} speed={_world.Cue.Speed:F1}");

        // 6. F1 toggles the panel (and, with it, the debug readings).
        _UnhandledInput(KeyEvent(Key.F1, true));
        Check("F1 hides the left panel", !_showHud, $"showHud={_showHud}");
        Check("F1 shows the debug readings with the panel", _debugOverlay, $"debugOverlay={_debugOverlay}");
        _UnhandledInput(KeyEvent(Key.F1, true));
        Check("F1 toggles it back", _showHud && !_debugOverlay, $"showHud={_showHud} debug={_debugOverlay}");

        GD.Print($"=== M2 selftest summary: {_checksRun - _checksFailed}/{_checksRun} checks passed ===");
        GD.Print(_checksFailed == 0 ? "M2 SELFTEST PASS" : $"M2 SELFTEST FAIL: {_checksFailed} check(s) failed");
    }

    private void StepOnceForInputTest()
    {
        _world.Step();
        _trail.Sync(_world);
        _rewindVis.ArcWalked = _world.Rewinding ? ArcWalked() : 0.0;
        if (_world.Rewinding) _rewindVis.EnsureForecast(_world, _trail.TraversedRange.From, _trail.TraversedRange.To);
    }

    private static InputEventMouseButton Press(Vector2 at, bool pressed) => new InputEventMouseButton
    {
        ButtonIndex = MouseButton.Left,
        Pressed = pressed,
        Position = at,
        ButtonMask = pressed ? MouseButtonMask.Left : 0,
    };

    private static InputEventMouseMotion MouseMotion(Vector2 at) => new InputEventMouseMotion
    {
        Position = at,
        ButtonMask = MouseButtonMask.Left,
    };

    private static InputEventKey KeyEvent(Key key, bool pressed) => new InputEventKey
    {
        Keycode = key,
        PhysicalKeycode = key,
        Pressed = pressed,
        Echo = false,
    };

    // ================================================================ self test

    /// <summary>
    /// Machine-checkable claims about the view. Exits non-zero on failure so the report can quote a
    /// real exit code rather than an assertion.
    /// </summary>
    private void RunSelfTests(string mode)
    {
        if (mode == "prediction" || mode == "all") VerifyPredictionExactness();
        if (mode == "trail" || mode == "all") VerifyTrailFidelity();
        if (mode == "all" || mode == "adversarial") VerifyPredictionAdversarial();
        if (mode == "layout" || mode == "all") VerifyPanelLayout();
        GD.Print($"=== M2 selftest summary: {_checksRun - _checksFailed}/{_checksRun} checks passed ===");
        if (_checksFailed > 0)
        {
            GD.Print($"M2 SELFTEST FAIL: {_checksFailed} check(s) failed");
            GetTree().Quit(1);
            return;
        }

        GD.Print("M2 SELFTEST PASS");
        GetTree().Quit(0);
    }

    private void Check(string name, bool ok, string detail)
    {
        _checksRun++;
        if (ok) GD.Print($"  PASS  {name}  {detail}");
        else
        {
            _checksFailed++;
            GD.Print($"  FAIL  {name}  {detail}");
        }
    }

    /// <summary>
    /// The HUD is drawn with absolute coordinates on a fixed 1040x560 canvas, which is exactly the
    /// kind of layout that silently overlaps after an edit. This turns that into a check.
    /// </summary>
    private void VerifyPanelLayout()
    {
        var problems = Hud.PanelLayoutSelfCheck();
        Check("HUD blocks are ordered, on-screen and non-overlapping", problems.Count == 0,
            problems.Count == 0 ? "7 blocks 36..554 on a 1040x560 canvas" : string.Join("; ", problems));
    }

    /// <summary>
    /// Prepare a long trail and let the frame loop sample the RENDERED pixels along it. The colour
    /// gradient is a visual claim, so it is checked against the framebuffer rather than against the
    /// colour array the view thinks it submitted.
    /// </summary>
    private void StartGradientCheck()
    {
        SetAim(7.0, 170.0);
        FireShot();
        Settle();
        _gradientCheckPending = true;
        GD.Print($"[gradient] playfield scaled to {ViewScale}x; trail vertices={_world.Cue.History.Count}");
    }

    private bool _gradientCheckPending;

    /// <summary>Sample the framebuffer along the trail and report the old-to-new luminance ramp.</summary>
    private void RunGradientCheck()
    {
        var image = GetViewport().GetTexture().GetImage();
        if (image == null)
        {
            Check("gradient check could obtain the framebuffer", false, "GetImage returned null");
            return;
        }

        var samples = _world.Cue.History.Count;
        var count = 12;
        var lumas = new double[count];
        var detail = "";
        for (var i = 0; i < count; i++)
        {
            // Old end first, new end last.
            var idx = (int)Math.Round((double)i / (count - 1) * (samples - 1));
            var p = _world.Cue.History[idx].Position;
            var px = Math.Clamp((int)Math.Round(p.X * ViewScale), 0, image.GetWidth() - 1);
            var py = Math.Clamp((int)Math.Round(p.Y * ViewScale), 0, image.GetHeight() - 1);

            // The drawn line is ~2 px wide, so take the brightest pixel in a small neighbourhood.
            var best = 0.0;
            for (var dy = -3; dy <= 3; dy++)
            {
                for (var dx = -3; dx <= 3; dx++)
                {
                    var x = px + dx;
                    var y = py + dy;
                    if (x < 0 || y < 0 || x >= image.GetWidth() || y >= image.GetHeight()) continue;
                    var c = image.GetPixel(x, y);
                    var l = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                    if (l > best) best = l;
                }
            }

            lumas[i] = best;
            detail += $"{best:F3} ";
        }

        var oldest = (lumas[0] + lumas[1] + lumas[2]) / 3.0;
        var newest = (lumas[count - 1] + lumas[count - 2] + lumas[count - 3]) / 3.0;
        GD.Print($"[gradient] samples={samples} luminance(old->new)= {detail}");
        GD.Print($"[gradient] oldestAvg={oldest:F4} newestAvg={newest:F4} ratio={(newest / Math.Max(1e-9, oldest)):F2}");

        Check("the drawn trail is measurably brighter at the newest end", newest > oldest * 1.25,
            $"oldest={oldest:F4} newest={newest:F4} ratio={(newest / Math.Max(1e-9, oldest)):F2}");
        Check("both ends are drawn (neither is background)", oldest > 0.12 && newest > 0.12,
            $"oldest={oldest:F4} newest={newest:F4}");
    }

    /// <summary>Print per-step cue state so a prediction mismatch can be attributed, not guessed.</summary>
    private void TracePath(double angle, double power)
    {
        var w = new World(_level);
        w.Shot(angle, power);
        var window = IntArg("--trace-steps=", 30);
        for (var i = 0; i < window; i++)
        {
            var before = w.Cue.Position;
            var vbefore = w.Cue.Velocity;
            w.Step();
            var after = w.Cue.Position;
            var cos = vbefore.Length > 1e-9 && w.Cue.Velocity.Length > 1e-9
                ? Vec2.Dot(vbefore, w.Cue.Velocity) / (vbefore.Length * w.Cue.Velocity.Length)
                : 1.0;
            GD.Print($"[path] step={i + 1} from={before} to={after} v={w.Cue.Velocity} cos={cos:F6} " +
                     $"d={(after - before).Length:F4} pocketed={w.Cue.Pocketed} hist={w.Cue.History.Count} " +
                     $"wallBottom={_level.TableBottom - w.Cue.Radius:F4} wallTop={_level.TableTop + w.Cue.Radius:F4} " +
                     $"radius={w.Cue.Radius} intervals={w.LastIntervals} maxTravel={w.LastMaxIntervalTravel:E3}");
            if (HasArg("--trace-balls"))
            {
                var line = "        ";
                foreach (var b in w.Balls)
                {
                    line += $"b{b.Id}({b.Position.X:F3},{b.Position.Y:F3})v{b.Velocity.Length:F1} ";
                }

                GD.Print(line);
            }
        }
    }

    /// <summary>
    /// Run BOTH the plain per-step trace and the reference construction in one process on identical
    /// fresh worlds, and diff them. This is how the reference was proven wrong, not the trail.
    /// </summary>
    private void TraceCompare(double angle, double power)
    {
        var a = new World(_level);
        a.Shot(angle, power);
        var b = new World(_level);
        b.Shot(angle, power);

        var steps = IntArg("--trace-steps=", 30);
        for (var i = 0; i < steps; i++)
        {
            var va = a.Cue.Position;
            a.Step();
            b.Step();
            var same = (a.Cue.Position - b.Cue.Position).Length;
            GD.Print($"[cmp] step={i + 1} a={a.Cue.Position} b={b.Cue.Position} diff={same:E3} " +
                     $"va={a.Cue.Velocity.Length:F6} vb={b.Cue.Velocity.Length:F6} " +
                     $"tickA={a.Tick} tickB={b.Tick} histA={a.Cue.History.Count} histB={b.Cue.History.Count}");
        }
    }

    private void TraceShot(double angle, double power, int steps)
    {
        var w = new World(_level);
        w.Shot(angle, power);
        GD.Print($"[trace] angle={angle} power={power} launchV={w.Cue.Velocity}");
        for (var i = 0; i < steps; i++)
        {
            var before = w.Cue.Velocity;
            w.Step();
            GD.Print($"[trace] step={i + 1} pos={w.Cue.Position} vBefore={before} vAfter={w.Cue.Velocity} same={(before.Equals(w.Cue.Velocity))} " +
                     $"railXform={w.Stats.RailTransforms} pairContacts={w.Stats.BallBallContacts} intervals={w.LastIntervals}");
            foreach (var b in w.Balls)
            {
                if (b.IsCue) continue;
                GD.Print($"[trace]    b{b.Id} p={b.Position} v={b.Velocity} pocketed={b.Pocketed}");
            }
        }

        var pred = AimPredictor.Trace(new World(_level), angle, power);
        GD.Print($"[trace] prediction contact={pred.Contact} ballId={pred.ContactBallId} steps={pred.Steps} " +
                 $"struckSpeed={pred.StruckSpeed:F3} deflectSpeed={pred.CueDeflectSpeed:F3} reachedRest={pred.ReachedRest} " +
                 $"stop={pred.StopPoint} pathPts={pred.CuePath.Count}");
    }

    /// <summary>
    /// The prediction claims to be exact. Prove it: replay the same shot in a fresh clone for the
    /// same number of kernel steps and compare every cue position bit-for-bit.
    /// </summary>
    private void VerifyPredictionExactness()
    {
        var angles = new[] { 0.0, 12.5, 35.0, 90.0, 137.0, 180.0, -47.0, 251.0 };
        var powers = new[] { 40.0, 120.0, 170.0 };
        var worst = 0.0;
        var cases = 0;
        var stops = 0;
        var ballContacts = 0;
        var railContacts = 0;

        foreach (var angle in angles)
        {
            foreach (var power in powers)
            {
                var fresh = new World(_level);
                var pred = AimPredictor.Trace(fresh, angle, power);
                Check($"prediction valid a={angle} p={power}", pred.Valid, $"steps={pred.Steps}");

                var replay = new World(_level);
                replay.Shot(angle, power);
                var worstThis = 0.0;
                for (var i = 0; i < pred.Steps; i++)
                {
                    replay.Step();
                    var p = pred.CuePath[i + 1];
                    var rp = new Vector2((float)replay.Cue.Position.X, (float)replay.Cue.Position.Y);
                    var d = (p - rp).Length();
                    if (d > worstThis) worstThis = d;
                }

                if (worstThis > worst) worst = worstThis;
                cases++;
                if (pred.Contact == AimPredictor.ContactKind.ObjectBall) ballContacts++;
                if (pred.Contact == AimPredictor.ContactKind.CueBallRail) railContacts++;
                if (pred.ReachedRest) stops++;
                Check($"prediction matches kernel replay a={angle} p={power}", worstThis <= 1e-9,
                    $"worstCuePathDeviation={worstThis:E3}px steps={pred.Steps} us={pred.Microseconds}");
            }
        }

        GD.Print($"[selftest] prediction cases={cases} worstDeviationPx={worst:E3} ballContacts={ballContacts} " +
                 $"railContacts={railContacts} predictedRest={stops}");
        Check("prediction deviation is zero (exact, not approximate)", worst <= 1e-9, $"worst={worst:E3}px");

        // Clock-only updates must reuse a preview; real state/input changes must invalidate it.
        var idle = new World(_level);
        var cache = new AimPredictor.Cache();
        cache.Store(idle, 7.0, 170.0, AimPredictor.Trace(idle, 7.0, 170.0));
        idle.RunSteps(120);
        Check("idle physics ticks retain the aim cache", cache.Matches(idle, 7.0, 170.0), $"tick={idle.Tick}");
        Check("angle and power changes invalidate the aim cache",
            !cache.Matches(idle, 8.0, 170.0) && !cache.Matches(idle, 7.0, 169.0), "changed input");
        Check("a different world cannot reuse the aim cache",
            !cache.Matches(idle.Clone(), 7.0, 170.0), "world identity");
        cache.Store(idle, 7.0, 170.0, cache.Value);
        idle.Balls[1].Position += new Vec2(1.0, 0.0);
        Check("object movement invalidates the aim cache", !cache.Matches(idle, 7.0, 170.0), "moved object");
        idle.Reset();
        Check("reset invalidates the aim cache", !cache.Matches(idle, 7.0, 170.0), $"tick={idle.Tick}");
        var weak = AimPredictor.Trace(idle, 0.0, idle.Config.MinPower);
        Check("a resting prediction stops before the 4000-step limit",
            weak.Steps < 4000, $"steps={weak.Steps}");
    }

    /// <summary>
    /// The trail claims to draw the real path with no corner cutting. This settles it with three
    /// measurable facts about the kernel's own output instead of reconstructing the path by hand (an
    /// earlier version of this test did that and was wrong twice - and a wrong reference is exactly
    /// how a "is the drawn path the real path" test lies).
    ///
    ///  (i)   Every vertex lies ON a straight leg of the kernel's own motion. Its per-step positions
    ///        form a polyline whose straight runs ARE the motion primitives, so a vertex may sit on a
    ///        run's infinite line beyond a step node; the test allows exactly that and nothing more.
    ///  (ii)  No drawn segment is long enough to have swallowed a turn: vertices are 2.0 px of arc
    ///        length apart (locked), so a chord can never leap over a corner.
    ///  (iii) Corners are still in the data: bends between consecutive vertices are sharp, i.e. no
    ///        decimation flattened a turn into a chord.
    /// </summary>
    private void VerifyTrailFidelity()
    {
        var angles = new[] { 0.0, 23.0, 90.0, 140.0, 260.0 };
        var powers = new[] { 170.0, 120.0 };
        var worst = 0.0;
        var worstBend = 0.0;
        var totalVertices = 0;
        var totalRuns = 0;
        var gapMax = 0.0;
        var cases = 0;

        foreach (var angle in angles)
        {
            foreach (var power in powers)
            {
                var w = new World(_level);
                w.Shot(angle, power);

                // Kernel ground truth: one position per fixed step, until the cue stops or is pocketed.
                var steps = new List<Vec2> { w.Cue.Position };
                var guard = 0;
                while (!w.AtRest() && guard++ < 4000)
                {
                    w.Step();
                    steps.Add(w.Cue.Position);
                    if (w.Cue.Pocketed) break;
                }

                var runs = BuildStraightRuns(steps);
                totalRuns += runs.Count;
                var samples = w.Cue.History.Samples;
                totalVertices += samples.Count;
                cases++;

                // (i) every recorded vertex lies on some straight leg's line.
                var deviation = 0.0;
                foreach (var s in samples)
                {
                    var best = double.MaxValue;
                    foreach (var run in runs) best = Math.Min(best, DistanceToLine(s.Position, run.A, run.B));
                    if (best > deviation) deviation = best;
                }

                // (iii) bend angles between consecutive recorded vertices.
                var bend = 0.0;
                for (var j = 1; j + 1 < samples.Count; j++)
                {
                    var d1 = samples[j].Position - samples[j - 1].Position;
                    var d2 = samples[j + 1].Position - samples[j].Position;
                    if (d1.Length < 1e-9 || d2.Length < 1e-9) continue;
                    var cos = Vec2.Dot(d1, d2) / (d1.Length * d2.Length);
                    var deg = Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * 180.0 / Math.PI;
                    if (deg > bend) bend = deg;
                }

                // (ii) vertex spacing.
                var gap = 0.0;
                for (var j = 1; j < samples.Count; j++)
                {
                    var g = (samples[j].Position - samples[j - 1].Position).Length;
                    if (g > gap) gap = g;
                }

                gapMax = Math.Max(gapMax, gap);
                worst = Math.Max(worst, deviation);
                worstBend = Math.Max(worstBend, bend);
                Check($"trail vertices lie on a straight kernel leg a={angle} p={power}", deviation <= 1e-6,
                    $"maxDeviation={deviation:E3}px vertices={samples.Count} stepPts={steps.Count} runs={runs.Count}");
            }
        }

        GD.Print($"[selftest] trail cases={cases} vertices={totalVertices} straightRuns={totalRuns} " +
                 $"worstLineDeviationPx={worst:E3} maxVertexGapPx={gapMax:F4} worstBendDegrees={worstBend:F2}");
        Check("every vertex lies exactly on a straight kernel leg", worst <= 1e-6, $"worst={worst:E3}px");
        Check("no drawn segment can swallow a corner (gap <= 2 px arc length)",
            gapMax <= _world.Config.HistoryStepPx + 1e-6, $"maxGap={gapMax:F4}px");
        Check("corners are retained in the drawn data (sharp bends present)",
            worstBend >= 5.0, $"worstBend={worstBend:F2} deg");
    }

    /// <summary>Split the per-step polyline into maximal collinear runs (the motion primitives).</summary>
    private static List<(Vec2 A, Vec2 B)> BuildStraightRuns(List<Vec2> steps)
    {
        var runs = new List<(Vec2, Vec2)>();
        if (steps.Count < 2) return runs;
        var start = 0;
        for (var i = 2; i < steps.Count; i++)
        {
            var d1 = steps[i - 1] - steps[i - 2];
            var d2 = steps[i] - steps[i - 1];
            var cos = d1.Length > 1e-9 && d2.Length > 1e-9 ? Vec2.Dot(d1, d2) / (d1.Length * d2.Length) : 1.0;
            if (cos < 0.9999995)
            {
                runs.Add((steps[start], steps[i - 1]));
                start = i - 1;
            }
        }

        runs.Add((steps[start], steps[steps.Count - 1]));
        return runs;
    }

    /// <summary>Perpendicular distance from a point to the infinite line through a and b.</summary>
    private static double DistanceToLine(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var len = ab.Length;
        if (len <= 1e-9) return (p - a).Length;
        return Math.Abs(Vec2.Cross(ab, p - a)) / len;
    }

    /// <summary>Adversarial pass: predictions at grazing angles and after a rewind, where a naive predictor breaks.</summary>
    private void VerifyPredictionAdversarial()
    {
        // 1. A shot aimed straight at another ball's centre (head-on) must predict a ball contact.
        var w = new World(_level);
        var target = w.Balls[1];
        var dir = (target.Position - w.Cue.Position).Normalized();
        var headOn = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
        var p1 = AimPredictor.Trace(w, headOn, 170.0);
        Check("head-on aim predicts a ball contact", p1.Contact == AimPredictor.ContactKind.ObjectBall,
            $"contact={p1.Contact} ballId={p1.ContactBallId} struckSpeed={p1.StruckSpeed:F1}");

        // 2. A grazing aim a few degrees off must still predict the ball (continuous collision).
        var grazeOk = false;
        var grazeDetail = "";
        foreach (var power in new[] { 30.0, 60.0, 100.0, 170.0 })
        {
            var gp = AimPredictor.Trace(w, headOn + 6.0, power);
            grazeDetail += $"[p={power}: {gp.Contact}/{gp.ContactBallId}] ";
            if (gp.Contact == AimPredictor.ContactKind.ObjectBall) grazeOk = true;
        }

        Check("grazing aim at 6 deg off still predicts a ball", grazeOk, grazeDetail);

        // 3. After a strike, a prediction must be refused (the shot is used).
        var live = new World(_level);
        live.Shot(headOn, 170.0);
        var p3 = AimPredictor.Trace(live, headOn, 170.0);
        Check("no prediction is offered after the strike is used", !p3.Valid, $"valid={p3.Valid}");

        // 4. A world that has already used its strike and walked a rewind: the ONLY state in which
        //    the player can still be planning. The kernel refuses a second stroke, so this asserts
        //    the refusal, and that asking for the prediction anyway left the live world untouched.
        var w2 = new World(_level);
        w2.Shot(headOn, 170.0);
        var g = 0;
        while (!w2.AtRest() && !w2.Won && !w2.Frozen && g++ < 4000) w2.Step();
        var started = w2.StartRewind();
        for (var i = 0; i < 120; i++) w2.Step();
        w2.EndRewind();
        var cueAfterRewind = new Vector2((float)w2.Cue.Position.X, (float)w2.Cue.Position.Y);
        var tickBefore = w2.Tick;
        var historyBefore = w2.Cue.History.Count;
        var p4 = AimPredictor.Trace(w2, headOn + 180.0, 140.0);

        Check("a second stroke is refused after the rewind (one strike per level)",
            !p4.Valid && !w2.Shot(headOn, 170.0),
            $"predictionValid={p4.Valid} startedRewind={started} rewinding={w2.Rewinding} " +
            $"cue={w2.Cue.Position} v={w2.Cue.Velocity} history={historyBefore}");

        Check("asking for that prediction left the live world untouched",
            w2.Tick == tickBefore && w2.Cue.History.Count == historyBefore
            && (new Vector2((float)w2.Cue.Position.X, (float)w2.Cue.Position.Y) - cueAfterRewind).Length() == 0f
            && !w2.Rewinding && !w2.HasNonFiniteState(),
            $"tick {tickBefore}->{w2.Tick} history {historyBefore}->{w2.Cue.History.Count} cue={w2.Cue.Position}");
    }


}
