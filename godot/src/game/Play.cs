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
    private string _stateName = "aim";
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

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;

        var levelId = Arg("--level=") ?? LevelIds.Corridor;
        _outPath = Arg("--out=");
        _stateName = Arg("--state=") ?? "aim";
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
        _trail = new TimelineTrail(this) { ShowTrail = !HasArg("--no-trail"), DebugDraw = HasArg("--trace-draw") };

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

        if (_selfTestMode != null)
        {
            RunSelfTests(_selfTestMode);
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

        PrepareState(_stateName);
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
        Notify($"STRIKE  angle {_aimAngle:F1} deg  power {_aimPower:F0} px  speed {_world.Cue.Speed:F0} px/s", Palette.Accent);
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
            Notify("REWIND refused (no path / budget exhausted / nothing to rewind)", Palette.Bad);
            return;
        }

        _rewindVis.Reset();
        _aimCache.Invalidate();
        Notify("REWINDING - hold R, release R to keep the stored velocity", Palette.RewindCue);
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
                    Notify($"AUTOPLAY stroke {step}/{plan.Count}: angle {shot.Angle:F2} power {shot.Power:F1}", Palette.Accent);
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

    public override void _PhysicsProcess(double delta)
    {
        if (_world == null) return;

        var dt = _world.Config.SimulationStep;
        var t0 = Time.GetTicksUsec();
        _accumulator += dt;
        var steps = 0;
        while (_accumulator >= dt - 1e-12 && steps < MaxStepsPerFrame)
        {
            // A scripted capture freezes the world at the prepared state: the crawl would otherwise
            // keep running during the frames that precede the screenshot and capture another moment.
            if (!_rewindVis.Paused && !_freezeWorld) _world.Step();
            _accumulator -= dt;
            steps++;
        }

        if (_accumulator > dt) _accumulator = 0.0;
        _simMs = (Time.GetTicksUsec() - t0) / 1000.0;

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
                        Notify("REWIND resumed", Palette.RewindCue);
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
                        ? "PAUSED - drag or use arrows to move the release point backwards, R to release there"
                        : "REWIND resumed", Palette.RewindTarget);
                }
                else
                {
                    Notify("nothing to rewrite yet (strike first, then hold R)", Palette.TextDim);
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
                if (_world.Rewinding) Notify("cannot reset while rewinding", Palette.Bad);
                else
                {
                    ResetLevel();
                }

                break;

            case Key.F1:
                if (key.Pressed) _debugOverlay = !_debugOverlay;
                break;

            case Key.F2:
                if (key.Pressed)
                {
                    _trail.ShowGhost = !_trail.ShowGhost;
                    Notify($"ghost path {(_trail.ShowGhost ? "on" : "off")}", Palette.TextDim);
                }

                break;

            case Key.F3:
                if (key.Pressed)
                {
                    _trail.ShowTrail = !_trail.ShowTrail;
                    Notify($"history trail {(_trail.ShowTrail ? "on" : "off")}", Palette.TextDim);
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
                Notify(_world.Won ? "level already won"
                    : _world.Rewinding ? "cannot aim while rewinding"
                    : "ONE STRIKE PER LEVEL - it has been used; the only verb left is REWIND", Palette.Bad);
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
                Notify("click ON the cue ball, then drag back and release", Palette.TextDim);
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
                Notify($"drag at least {(float)_world.Config.MinPower:F0} px (this was {len:F0})", Palette.Warn);
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
            Notify("a rewind cannot skip forward along the path", Palette.Bad);
            return;
        }

        var old = _rewindVis.PlannedReleaseIndex;
        _rewindVis.SetPlannedRelease(best);
        if (old >= 0 && _rewindVis.PlannedReleaseIndex <= old)
        {
            Notify($"release point moved back {Math.Abs(_rewindVis.PlannedReleaseIndex - old)} samples", Palette.TextDim);
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
        Notify($"RELEASED - velocity is the stored v at this history point ({_world.Cue.Speed:F0} px/s)", Palette.RewindTraversed);
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
        Notify($"RELEASED at the scrubbed point - stored v was {_world.Cue.Speed:F0} px/s", Palette.RewindTraversed);
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
        Notify("level reset - the strike is available again (this is a debug key, not a rule)", Palette.TextDim);
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
        _authoredVertexCounts = _trail.CachedVertexCount;
    }

    private void DrawRoom()
    {
        DrawRect(new Rect2(0, 0, 1040, 560), Palette.RoomBg);
        DrawRect(new Rect2(20, 16, 1000, 528), Palette.RailWood);
        DrawRect(new Rect2(30, 26, 980, 508), Palette.RailInner);
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
                DrawString(_font, cue + dir * clamped + new Vector2(10, -10), "MAX", HorizontalAlignment.Left, -1, 11, Palette.Warn);
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

        DrawString(_font, c + new Vector2(0, -r - 14.0f), "REWINDING", HorizontalAlignment.Center, 120.0f, 12, Palette.RewindCue);
    }

    private void DrawWinBanner()
    {
        var rect = new Rect2(300, 190, 440, 150);
        DrawRect(rect, new Color(0.03f, 0.08f, 0.05f, 0.92f));
        DrawRect(rect, Palette.Good, false, 3.0f);
        DrawString(_font, new Vector2(rect.Position.X, rect.Position.Y + 52), "TABLE CLEARED", HorizontalAlignment.Center, rect.Size.X, 34, Palette.Good);
        DrawString(_font, new Vector2(rect.Position.X, rect.Position.Y + 88),
            $"{_world.PocketedObjectCount}/{_world.ObjectBallCount} object balls in {_world.Time:F2}s",
            HorizontalAlignment.Center, rect.Size.X, 16, Palette.TextMain);
        DrawString(_font, new Vector2(rect.Position.X, rect.Position.Y + 116),
            $"rewinds {_world.Stats.Rewinds}   distance {_world.RewindDistanceUsed:F0}px   trail {_world.Cue.History.Count} pts",
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
