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
    private const float ViewScale = 1.045f;

    private World _world;
    private LevelData _level;
    private readonly AimPredictor.Cache _aimCache = new AimPredictor.Cache();
    private TimelineTrail _trail;
    private readonly RewindVisualizer _rewindVis = new RewindVisualizer();

    private Font _font;
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
    private bool _verbose;
    private double _messageTimer;
    private string _message = "";
    private Color _messageColor = Palette.TextMain;

    // ---------------------------------------------------------------- timing
    private readonly List<double> _frameMs = new List<double>();
    private double _lastFrameAt;
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

        // Player-facing text is Simplified Chinese and Godot's built-in fallback font has no CJK
        // glyphs at all, so anything that draws without an explicit font would render as tofu boxes.
        // Installing the same font as the window theme default makes that failure mode unreachable
        // for Controls added later, not just for the immediate-mode draw calls below.
        GetTree().Root.Theme = new Theme { DefaultFont = _font, DefaultFontSize = 13 };

        // The table is 910x434 px inside a 1040x560 canvas, which left a dead strip at the bottom and
        // made the playfield read cramped. Scaling the whole view (kernel coordinates are untouched)
        // fills the canvas without cropping playable geometry: at 1.045 the rails span
        // 25*1.045=26 .. 1014*1.045=1060 horizontally and 20*1.045=21 .. 505*1.045=528 vertically, so
        // only decorative wood falls outside the frame. The HUD is drawn in node coordinates and its
        // panels were resized for this scale.
        Scale = new Vector2(ViewScale, ViewScale);

        var levelId = Arg("--level=") ?? LevelIds.Corridor;
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

        var levelPath = levelId.StartsWith("res://") ? levelId : $"res://levels/{levelId}.json";
        _level = LevelLoader.Load(levelPath);
        _world = new World(_level);
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
        if (!_world.StartRewind()) { GD.Print("[selftest] FAIL: rewind refused"); fail++; }
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
        if (!_world.Won) { GD.Print("[selftest] FAIL: the level did not clear"); fail++; }

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

            default:
                GD.Print($"[play] unknown state '{state}', falling back to aim");
                SetAim(7.0, 170.0);
                break;
        }

        _freezeWorld = true; // keeps the captured frame identical to the prepared state
        GD.Print($"[play] state={state} ticks={_world.Tick} time={_world.Time:F3} shotUsed={_world.ShotUsed} " +
                 $"rewinding={_world.Rewinding} won={_world.Won} pocketed={_world.PocketedObjectCount}/{_world.ObjectBallCount} " +
                 $"history={_world.Cue.History.Count}");
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
        if (mode == "idle")
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
            if (!_rewindVis.Paused && !_freezeWorld) _world.Step();
        }
        _simMs = (Time.GetTicksUsec() - t0) / 1000.0;

        // Live telemetry. Absence of this was half of why two dead-main-loop bugs survived: in live
        // mode the game printed nothing at all, so there was no signal to read.
        if (_stateName == null && _frames > 0 && _frames % 120 == 0)
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
    }

    public override void _Process(double delta)
    {
        var now = Time.GetTicksUsec() / 1000.0;
        if (_lastFrameAt > 0.0)
        {
            var ms = now - _lastFrameAt;
            _frameMs.Add(ms);
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

            case Key.Escape:
                if (key.Pressed) GetTree().Quit(0);
                break;
        }
    }

    private void HandleMouse(InputEventMouseButton button)
    {
        if (button.ButtonIndex != MouseButton.Left) return;

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
        Notify("关卡已重置 · 击球重新可用（调试键，不是规则）", Palette.TextDim);
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

        DrawRoom();
        DrawTable();

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

        Hud.Draw(this, _font, BuildHudModel());
        if (_debugOverlay) Hud.DrawDebug(this, _font, BuildHudModel());

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

    private void DrawRoom()
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
        DrawRect(new Rect2(-600, -600, 2400, 2000), Palette.RoomBg);
        DrawRect(new Rect2(18, 14, 900, 478), Palette.RailWood);
        DrawRect(new Rect2(27, 24, 882, 460), Palette.RailInner);
    }

    private void DrawTable()
    {
        var felt = new Rect2(
            (float)_level.TableLeft, (float)_level.TableTop,
            (float)_level.TableWidth, (float)_level.TableHeight);
        DrawRect(felt, Palette.Felt);
        DrawRect(felt, Palette.FeltEdge, false, 3.0f);

        // Faint quarter markers so the player can judge angles (the prototype had nothing).
        for (var i = 1; i < 4; i++)
        {
            var x = felt.Position.X + felt.Size.X * i / 4.0f;
            DrawLine(new Vector2(x, felt.Position.Y), new Vector2(x, felt.Position.Y + 10.0f), Palette.FeltGrid, 1.0f);
            DrawLine(new Vector2(x, felt.Position.Y + felt.Size.Y - 10.0f), new Vector2(x, felt.Position.Y + felt.Size.Y), Palette.FeltGrid, 1.0f);
            var y = felt.Position.Y + felt.Size.Y * i / 4.0f;
            DrawLine(new Vector2(felt.Position.X, y), new Vector2(felt.Position.X + 10.0f, y), Palette.FeltGrid, 1.0f);
            DrawLine(new Vector2(felt.Position.X + felt.Size.X - 10.0f, y), new Vector2(felt.Position.X + felt.Size.X, y), Palette.FeltGrid, 1.0f);
        }

        foreach (var pocket in _level.Pockets)
        {
            var c = new Vector2((float)pocket.Center.X, (float)pocket.Center.Y);
            DrawCircle(c, (float)pocket.Radius + 3.5f, Palette.PocketRim, true, -1.0f, true);
            DrawCircle(c, (float)pocket.Radius, Palette.Pocket, true, -1.0f, true);
        }

        DrawWhiteHoles();
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
        var rect = new Rect2(300, 190, 440, 150);
        DrawRect(rect, new Color(0.03f, 0.08f, 0.05f, 0.92f));
        DrawRect(rect, Palette.Good, false, 3.0f);
        DrawString(_font, new Vector2(rect.Position.X, rect.Position.Y + 52), "清台成功", HorizontalAlignment.Center, rect.Size.X, 34, Palette.Good);
        DrawString(_font, new Vector2(rect.Position.X, rect.Position.Y + 88),
            $"{_world.PocketedObjectCount}/{_world.ObjectBallCount} 颗目标球 · 用时 {_world.Time:F2}s",
            HorizontalAlignment.Center, rect.Size.X, 16, Palette.TextMain);
        DrawString(_font, new Vector2(rect.Position.X, rect.Position.Y + 116),
            $"回溯 {_world.Stats.Rewinds} 次   距离 {_world.RewindDistanceUsed:F0} px   轨迹 {_world.Cue.History.Count} 点",
            HorizontalAlignment.Center, rect.Size.X, 14, Palette.TextDim);
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
            FrameMs = _frameMs.Count > 0 ? _frameMs[_frameMs.Count - 1] : 0.0,
            MaxFrameMs = _maxFrameMs,
            DrawMs = _drawMs,
            SimMs = _simMs,
            TrailVertices = _trail.CachedVertexCount,
        };
        return m;
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
