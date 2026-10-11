using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Game;

/// <summary>
/// The front end: title, level select, personalisation and settings, all in one scene.
///
/// One scene with a state enum rather than four scenes: the screens share a header, a background
/// and an input model, and four .tscn files that each need the same plumbing is more moving parts
/// than this front end is worth. Transitions between them are instant because nothing is reloaded.
///
/// Everything is drawn directly (no Control nodes) to match the play scene's drawing style and to
/// keep the whole front end in one place that is easy to review.
/// </summary>
public partial class Menu : Node2D
{
    private enum Screen { Title, Levels, Theme, Settings }

    /// <summary>Which level the play scene should load. Set here, read by Play on startup.</summary>
    public static string SelectedLevelId;

    /// <summary>Shown once on the first level a player enters, then never again.</summary>
    public static bool TutorialPending;

    // Canvas design size, matching project.godot.
    private const float W = 1040.0f;
    private const float H = 560.0f;

    private Screen _screen = Screen.Title;
    private Font _font;

    private List<LevelEntry> _levels = new();
    private int _selected;                 // index of the highlighted level on the level screen
    private int _themeIndex;
    private int _hoverButton = -1;

    // Settings, mirrored into the save file so they persist.
    private bool _showTrail = true;
    private bool _showGhost = true;
    private bool _autoAimHint = true;

    private readonly List<(Rect2 Rect, string Id)> _hitAreas = new();

    // Deterministic screenshot support, mirroring the play scene's --out= path. Added because
    // grabbing the desktop window is unreliable here: the capture keeps landing on whichever window
    // happens to be on top, and a screenshot that shows the wrong app proves nothing.
    private string _outPath;
    private string _screenArg;
    private int _frames;

    public override void _Ready()
    {
        _font = LoadUiFont();
        _levels = LevelCatalog.All();
        _selected = LevelCatalog.NextUnclearedIndex();
        LoadSettings();

        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--out=", StringComparison.Ordinal)) _outPath = a.Substring(6);
            if (a.StartsWith("--screen=", StringComparison.Ordinal)) _screenArg = a.Substring(9);
        }

        // The window size is a display setting, not a scene setting, so set it here. Needed because
        // the exported GUI build ignores a resolution request made only on the command line and came
        // up at the size the compositor chose, which then affected the layout.
        if (HasArg("--size="))
        {
            var spec = ArgValue("--size=");
            var parts = spec.Split('x');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var vw) && int.TryParse(parts[1], out var vh)
                && vw > 0 && vh > 0)
            {
                DisplayServer.WindowSetSize(new Vector2I(vw, vh));
            }
        }

        if (_screenArg != null)
        {
            _screen = _screenArg switch
            {
                "levels" => Screen.Levels,
                "theme" => Screen.Theme,
                "settings" => Screen.Settings,
                _ => Screen.Title,
            };
        }

        QueueRedraw();

        // Boot marker, mirroring the play scene's KERNEL_VIEW_READY. The gate asserts on this so a
        // front end that failed to construct, or found no levels, cannot pass as a clean boot.
        GD.Print($"[menu] MENU_READY levels={_levels.Count} screen={_screen} " +
                 $"font={(_font != ThemeDB.FallbackFont ? "bundled" : "fallback")}");
        if (_levels.Count == 0) GD.PushError("[menu] no playable levels were discovered");
    }

    private static Font LoadUiFont()
    {
        const string path = "res://assets/DroidSansFallback.ttf";
        if (ResourceLoader.Exists(path))
        {
            var f = ResourceLoader.Load<FontFile>(path);
            if (f != null) return f;
        }

        return ThemeDB.FallbackFont;
    }

    // ---------------------------------------------------------------- args

    private static bool HasArg(string prefix)
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string ArgValue(string prefix)
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length);
        return null;
    }

    // ---------------------------------------------------------------- config

    private const string SavePath = "user://progress.cfg";
    private const string SettingsSection = "settings";

    private void LoadSettings()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SavePath) != Error.Ok) return;
        _themeIndex = (int)cfg.GetValue(SettingsSection, "theme", 0);
        _showTrail = (bool)cfg.GetValue(SettingsSection, "show_trail", true);
        _showGhost = (bool)cfg.GetValue(SettingsSection, "show_ghost", true);
        _autoAimHint = (bool)cfg.GetValue(SettingsSection, "aim_hint", true);
    }

    private void SaveSettings()
    {
        var cfg = new ConfigFile();
        cfg.Load(SavePath);
        cfg.SetValue(SettingsSection, "theme", _themeIndex);
        cfg.SetValue(SettingsSection, "show_trail", _showTrail);
        cfg.SetValue(SettingsSection, "show_ghost", _showGhost);
        cfg.SetValue(SettingsSection, "aim_hint", _autoAimHint);
        cfg.Save(SavePath);
    }

    /// <summary>Theme index the play scene reads when it starts.</summary>
    public static int ThemeIndex
    {
        get
        {
            var cfg = new ConfigFile();
            if (cfg.Load(SavePath) != Error.Ok) return 0;
            return (int)cfg.GetValue(SettingsSection, "theme", 0);
        }
    }

    // ---------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.Escape:
                    GoBack();
                    return;
                case Key.Left:
                    if (_screen == Screen.Levels) { _selected = Math.Max(0, _selected - 1); QueueRedraw(); }
                    return;
                case Key.Right:
                    if (_screen == Screen.Levels)
                    {
                        _selected = Math.Min(_levels.Count - 1, _selected + 1);
                        QueueRedraw();
                    }

                    return;
                case Key.Enter:
                case Key.KpEnter:
                    ActivateCurrent();
                    return;
            }
        }

        if (@event is InputEventMouseMotion motion)
        {
            var hover = HitTest(motion.Position);
            if (hover != _hoverButton)
            {
                _hoverButton = hover;
                QueueRedraw();
            }
        }

        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            var hit = HitTest(mb.Position);
            if (hit >= 0) Activate(hit);
        }
    }

    private int HitTest(Vector2 p)
    {
        for (var i = 0; i < _hitAreas.Count; i++)
            if (_hitAreas[i].Rect.HasPoint(p)) return i;
        return -1;
    }

    private void GoBack()
    {
        if (_screen == Screen.Title)
        {
            GetTree().Quit();
            return;
        }

        _screen = Screen.Title;
        QueueRedraw();
    }

    private void ActivateCurrent()
    {
        if (_screen == Screen.Levels) StartLevel(_selected);
        else if (_screen == Screen.Title) _screen = Screen.Levels;
        QueueRedraw();
    }

    private void Activate(int index)
    {
        var id = _hitAreas[index].Id;

        if (id == "quit") { GetTree().Quit(); return; }
        if (id == "back") { GoBack(); return; }
        if (id == "start") { StartLevel(Math.Max(0, _selected)); return; }
        if (id == "levels") { _screen = Screen.Levels; QueueRedraw(); return; }
        if (id == "theme") { _screen = Screen.Theme; QueueRedraw(); return; }
        if (id == "settings") { _screen = Screen.Settings; QueueRedraw(); return; }

        if (id.StartsWith("level:", StringComparison.Ordinal))
        {
            var n = int.Parse(id.AsSpan(6));
            _selected = n;
            QueueRedraw();
            return;
        }

        if (id.StartsWith("pick:", StringComparison.Ordinal))
        {
            var n = int.Parse(id.AsSpan(5));
            if (_levels.Count > n && _levels[n].Unlocked) StartLevel(n);
            return;
        }

        if (id.StartsWith("theme:", StringComparison.Ordinal))
        {
            _themeIndex = int.Parse(id.AsSpan(6));
            SaveSettings();
            QueueRedraw();
            return;
        }

        if (id.StartsWith("toggle:", StringComparison.Ordinal))
        {
            switch (id.Substring(7))
            {
                case "trail": _showTrail = !_showTrail; break;
                case "ghost": _showGhost = !_showGhost; break;
                case "aim": _autoAimHint = !_autoAimHint; break;
            }

            SaveSettings();
            QueueRedraw();
            return;
        }

        if (id == "reset_progress")
        {
            LevelCatalog.ResetProgress();
            _levels = LevelCatalog.All();
            _selected = 0;
            QueueRedraw();
        }
    }

    private void StartLevel(int index)
    {
        if (index < 0 || index >= _levels.Count) return;
        var entry = _levels[index];
        if (!entry.Unlocked) return;

        SelectedLevelId = entry.Id;
        // The tutorial bubble shows on the first level the player ever opens, and only if they have
        // not already cleared anything (i.e. a returning player is not re-taught).
        TutorialPending = LevelCatalog.TotalStars() == 0;
        GetTree().ChangeSceneToFile("res://scenes/play.tscn");
    }

    // ---------------------------------------------------------------- draw

    public override void _Draw()
    {
        _hitAreas.Clear();
        DrawBackground();

        switch (_screen)
        {
            case Screen.Title: DrawTitle(); break;
            case Screen.Levels: DrawLevels(); break;
            case Screen.Theme: DrawTheme(); break;
            case Screen.Settings: DrawSettings(); break;
        }

        if (_outPath != null) CaptureIfReady();
    }

    /// <summary>Saves one frame to <c>--out=</c> then quits, so the front end has real evidence.</summary>
    private void CaptureIfReady()
    {
        _frames++;
        if (_frames < 3) return;

        var image = GetViewport().GetTexture().GetImage();
        var err = image.SavePng(_outPath);
        GD.Print($"[menu] CAPTURE file={_outPath} err={err} screen={_screen} size={image.GetWidth()}x{image.GetHeight()}");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    private void DrawBackground()
    {
        DrawRect(new Rect2(-400, -400, W + 800, H + 800), Palette.RoomBg);

        // Felt panel behind the whole front end, so the menu already looks like the game.
        var rect = new Rect2(70, 46, W - 140, H - 92);
        DrawRect(rect, new Color(0.055f, 0.13f, 0.10f, 1.0f));
        DrawRect(rect, Palette.FeltEdge, false, 2.0f);

        // A few decorative rail dots, echoing the table.
        for (var i = 0; i < 9; i++)
        {
            var x = rect.Position.X + 26 + i * (rect.Size.X - 52) / 8.0f;
            DrawCircle(new Vector2(x, rect.Position.Y + 13), 2.6f, Palette.PocketRim);
            DrawCircle(new Vector2(x, rect.Position.Y + rect.Size.Y - 13), 2.6f, Palette.PocketRim);
        }
    }

    private void DrawTitle()
    {
        var cx = W * 0.5f;

        Text(cx, 126, "REWIND", 58, Palette.TextMain, HorizontalAlignment.Center);
        Text(cx, 172, "一 杆 清 台", 24, Palette.Accent, HorizontalAlignment.Center);
        Text(cx, 206, "你只能打一杆 —— 但可以让白球沿它走过的路倒回来", 15,
            Palette.TextDim, HorizontalAlignment.Center);

        // Vertical budget: the exit button must clear the two info lines at the bottom, and the
        // info lines must clear the panel edge. The first version put them on top of each other.
        var y = 244.0f;
        Button(new Rect2(cx - 150, y, 300, 42), "start", "▶  开始游戏");
        y += 52;
        Button(new Rect2(cx - 150, y, 300, 42), "levels", "关卡选择");
        y += 52;
        Button(new Rect2(cx - 150, y, 146, 40), "theme", "个性化");
        Button(new Rect2(cx + 4, y, 146, 40), "settings", "设置");
        y += 50;
        Button(new Rect2(cx - 150, y, 300, 38), "quit", "退出");

        var all = _levels.Count;
        var cleared = 0;
        foreach (var e in _levels) if (e.Stars > 0) cleared++;

        // Below the last button, above the panel's bottom edge (panel spans y 46..514).
        Text(cx, 466, $"已通关 {cleared} / {all}     星星 {LevelCatalog.TotalStars()} / {LevelCatalog.MaxStars()}",
            13, Palette.TextDim, HorizontalAlignment.Center);
        Text(cx, 490, "← →  选择      Enter  确定      Esc  返回", 12,
            Palette.TextDim, HorizontalAlignment.Center);
    }

    private void DrawLevels()
    {
        Text(90, 86, "关卡选择", 26, Palette.TextMain);
        Text(90, 110, "← →  切换      Enter  开始      Esc  返回", 12, Palette.TextDim);

        if (_levels.Count == 0)
        {
            Text(W * 0.5f, H * 0.5f, "没有找到可用关卡", 20, Palette.Bad, HorizontalAlignment.Center);
            return;
        }

        // Card grid, 3 per row.
        const int perRow = 3;
        var cardW = 268.0f;
        var cardH = 118.0f;
        var gap = 22.0f;
        var totalW = perRow * cardW + (perRow - 1) * gap;
        var x0 = (W - totalW) * 0.5f;
        var y0 = 152.0f;

        for (var i = 0; i < _levels.Count; i++)
        {
            var e = _levels[i];
            var col = i % perRow;
            var row = i / perRow;
            var rect = new Rect2(x0 + col * (cardW + gap), y0 + row * (cardH + gap), cardW, cardH);

            var isSel = i == _selected;
            var bg = e.Unlocked
                ? (isSel ? new Color(0.10f, 0.24f, 0.18f, 1f) : new Color(0.07f, 0.17f, 0.13f, 1f))
                : new Color(0.06f, 0.09f, 0.09f, 1f);
            DrawRect(rect, bg);
            DrawRect(rect, isSel ? Palette.Accent : Palette.PanelEdge, false, isSel ? 2.5f : 1.0f);

            _hitAreas.Add((rect, $"pick:{i}"));

            if (!e.Unlocked)
            {
                Text(rect.Position.X + rect.Size.X * 0.5f, rect.Position.Y + 56,
                    "🔒  未解锁", 18, Palette.TextDim, HorizontalAlignment.Center);
                Text(rect.Position.X + rect.Size.X * 0.5f, rect.Position.Y + 84,
                    "通关上一关后开启", 12, Palette.TextDim, HorizontalAlignment.Center);
                continue;
            }

            // Index + title, ellipsized so a long name cannot spill out of the card.
            Text(rect.Position.X + 16, rect.Position.Y + 30, $"{i + 1}. {Ellipsize(e.Title, 13)}",
                15, Palette.TextMain);

            Text(rect.Position.X + 16, rect.Position.Y + 54, e.Subtitle, 12, Palette.TextDim);
            if (e.HasWhiteHole)
                Text(rect.Position.X + 16, rect.Position.Y + 74, "◉ 含白洞", 12, Palette.WhiteHoleRing);

            // Difficulty dots from the measured clearing window.
            for (var d = 0; d < 3; d++)
            {
                var c = new Vector2(rect.Position.X + 18 + d * 14, rect.Position.Y + 96);
                var on = d < e.Difficulty;
                DrawCircle(c, 5.0f, on ? Palette.Warn : new Color(0.3f, 0.35f, 0.33f, 1f));
            }

            DrawStars(rect.Position.X + rect.Size.X - 74, rect.Position.Y + 96, e.Stars);

            var best = LevelCatalog.BestRewinds(e.Id);
            if (best >= 0)
                Text(rect.Position.X + rect.Size.X - 16, rect.Position.Y + 30,
                    $"最短回溯 {best}", 12, Palette.Good, HorizontalAlignment.Right);
        }
    }

    private void DrawStars(float x, float y, int stars)
    {
        for (var i = 0; i < 3; i++)
        {
            var on = i < stars;
            Text(x + i * 20, y, on ? "★" : "☆", 18,
                on ? Palette.Accent : new Color(0.32f, 0.36f, 0.34f, 1f));
        }
    }

    private void DrawTheme()
    {
        Text(90, 86, "个性化", 26, Palette.TextMain);
        Text(90, 110, "只更换配色与表现，不影响任何物理数值。Esc 返回", 12, Palette.TextDim);

        // Names and swatches live in Palette so the screen and the actual theme cannot drift apart:
        // they were hard-coded here and disagreed with the palettes (this screen offered a "霓虹" theme
        // that no longer existed, and did not mention the space theme that is the default).
        var names = Palette.ThemeNames;
        var swatches = Palette.ThemeSwatches;

        var y = 156.0f;
        for (var i = 0; i < names.Length; i++)
        {
            var rect = new Rect2(140, y, 420, 54);
            var sel = i == _themeIndex;
            DrawRect(rect, sel ? new Color(0.10f, 0.24f, 0.18f, 1f) : new Color(0.07f, 0.15f, 0.12f, 1f));
            DrawRect(rect, sel ? Palette.Accent : Palette.PanelEdge, false, sel ? 2.5f : 1.0f);

            DrawRect(new Rect2(rect.Position.X + 12, rect.Position.Y + 12, 30, 30), swatches[i]);
            DrawRect(new Rect2(rect.Position.X + 12, rect.Position.Y + 12, 30, 30), Palette.PanelEdge, false, 1.0f);

            Text(rect.Position.X + 56, rect.Position.Y + 34, names[i], 15,
                sel ? Palette.TextMain : Palette.TextDim);
            if (sel) Text(rect.Position.X + rect.Size.X - 14, rect.Position.Y + 34, "已选", 12,
                Palette.Accent, HorizontalAlignment.Right);

            _hitAreas.Add((rect, $"theme:{i}"));
            y += 66;
        }

        Text(140, y + 16, "说明：换肤只改配色表，不改质量/摩擦/弹性等任何物理参数。", 12, Palette.TextDim);
    }

    private void DrawSettings()
    {
        Text(90, 86, "设置", 26, Palette.TextMain);
        Text(90, 110, "Esc 返回", 12, Palette.TextDim);

        var y = 156.0f;
        Toggle(new Rect2(140, y, 420, 48), "toggle:trail", "显示历史轨迹", _showTrail);
        y += 58;
        Toggle(new Rect2(140, y, 420, 48), "toggle:ghost", "显示残影", _showGhost);
        y += 58;
        Toggle(new Rect2(140, y, 420, 48), "toggle:aim", "显示瞄准预测线", _autoAimHint);
        y += 58;

        Button(new Rect2(140, y, 420, 46), "reset_progress", "清空进度（开发用）");
        y += 66;

        Text(140, y, "操作：拖动后松开 = 瞄准/击球    按住 R = 回溯    松开 R = 保留当时速度",
            12, Palette.TextDim);
        Text(140, y + 22, "P = 暂停并微调释放点    F1 = 隐藏左侧面板    F2/F3 = 残影/轨迹    空格 = 重置本关",
            12, Palette.TextDim);
    }

    // ---------------------------------------------------------------- widgets

    private void Button(Rect2 rect, string id, string label)
    {
        var hover = _hoverButton >= 0 && _hoverButton < _hitAreas.Count && _hitAreas[_hoverButton].Id == id;
        var bg = hover ? new Color(0.16f, 0.30f, 0.24f, 1f) : new Color(0.09f, 0.19f, 0.15f, 1f);
        DrawRect(rect, bg);
        DrawRect(rect, hover ? Palette.Accent : Palette.PanelEdge, false, hover ? 2.0f : 1.0f);
        Text(rect.Position.X + rect.Size.X * 0.5f, rect.Position.Y + rect.Size.Y * 0.5f + 6,
            label, 16, Palette.TextMain, HorizontalAlignment.Center);
        _hitAreas.Add((rect, id));
    }

    private void Toggle(Rect2 rect, string id, string label, bool value)
    {
        DrawRect(rect, new Color(0.07f, 0.15f, 0.12f, 1f));
        DrawRect(rect, Palette.PanelEdge, false, 1.0f);
        Text(rect.Position.X + 16, rect.Position.Y + 30, label, 15, Palette.TextMain);

        var box = new Rect2(rect.Position.X + rect.Size.X - 74, rect.Position.Y + 12, 58, 24);
        DrawRect(box, value ? new Color(0.20f, 0.46f, 0.34f, 1f) : new Color(0.14f, 0.18f, 0.17f, 1f));
        DrawRect(box, value ? Palette.Good : Palette.PanelEdge, false, 1.5f);
        var knobX = value ? box.Position.X + box.Size.X - 13 : box.Position.X + 13;
        DrawCircle(new Vector2(knobX, box.Position.Y + box.Size.Y * 0.5f), 9.0f, Palette.TextMain);

        _hitAreas.Add((rect, id));
    }

    // ---------------------------------------------------------------- text

    private void Text(float x, float y, string s, int size, Color color,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var width = align == HorizontalAlignment.Center ? -1 : -1;
        DrawString(_font, new Vector2(x, y), s, align, width, size, color);
    }

    /// <summary>Truncates by display width so a long level name cannot overflow its card.</summary>
    private string Ellipsize(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
