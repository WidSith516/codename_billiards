using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// HUD and debug overlay. Drawn with immediate-mode primitives on the same Node2D as the table, so
/// there is exactly one thing to screenshot and no Control-tree layout to reason about.
///
/// Readability requirements (M2 brief item 7) are answered by:
///  * a persistent top bar: balls left (n/N), STRIKE AVAILABLE / STRIKE USED - REWIND ONLY, REWINDING;
///  * a left panel: power meter with numeric px/s, strike lock, rewind budget (segments + px);
///  * ball identity by number + glyph, never colour alone (see <see cref="BallStyles"/>);
///  * the debug overlay (F1): live positions, velocities, history/forecast counts and a non-finite
///    check - the numbers a verifier needs to check the view against the kernel.
/// </summary>
public static class Hud
{
    public sealed class Model
    {
        public string LevelId;
        public string LevelName;
        public int ObjectBallCount;
        public int PocketedObjectCount;
        public bool ShotUsed;
        public bool Rewinding;
        public bool Won;
        public bool Paused;
        public bool Scrubbing;
        public int Tick;
        public double Time;

        public double AimAngle;
        public double AimPower;
        public bool HasAim;
        public bool Dragging;
        public double MinPower;
        public double MaxPower;
        public double Speed;

        public int Rewinds;
        public int RewindSegmentsUsed;
        public int RewindSegmentsMax;
        public double RewindDistanceUsed;
        public double RewindDistanceMax;

        public int HistoryEntries;
        public int TrailSessions;
        public int TurnBacks;
        public int TurnForwards;
        public int ForecastContacts;
        public double ForecastUs;

        public string Message;
        public Color MessageColor = Palette.TextMain;
        public bool Debug;

        /// <summary>
        /// Show the whole left panel (rules, power, rewind budget, controls). F1 turns it off so
        /// the playfield can be seen clean for recording and screenshots. The top bar stays: it is
        /// one thin line and carries the numbers that actually matter while playing.
        /// </summary>
        public bool ShowPanel = true;

        /// <summary>True when the on-screen buttons are active, which suppresses the keyboard legend:
        /// the buttons are drawn into the same block, and the legend describes keys a touch device does
        /// not have. Set from Play.</summary>
        public bool TouchControls;

        /// <summary>Remaining rail speed boosts for the cue ball; a negative value means unlimited.</summary>
        public int WallBoostsRemaining = -1;

        /// <summary>Names of the event cards this run has taken, in acquisition order.</summary>
        public System.Collections.Generic.List<string> Cards;

        /// <summary>Rapid taps used in the current cluster (0..2). Shown so the player can see that
        /// mashing R has stopped paying.</summary>
        public int RapidTapCount;

        /// <summary>True while the next tap would be the death-throe kick.</summary>
        public bool NextTapIsDeathThroe;

        /// <summary>True while the balloon card is armed (the next dead stop loses the run).</summary>
        public bool BalloonArmed;

        /// <summary>True once the run has been lost.</summary>
        public bool Lost;

        public string LostReason;

        public World World;
        public TimelineTrail Trail;
        public RewindVisualizer RewindVis;
        public AimPredictor.Prediction Aim;

        /// <summary>The decorative sine bands (top + bottom), for the debug overlay only. Null when
        /// disabled.</summary>
        public ReplayBand[] Bands;

        public double FrameMs;
        public double MaxFrameMs;
        public double DrawMs;
        public double SimMs;
        public int TrailVertices;
    }

    private const float Pad = 10.0f;

    public static void Draw(CanvasItem ci, Font font, Model m)
    {
        DrawTopBar(ci, font, m);

        // F1 hides the entire left panel, not just the numeric block: for a gameplay slice the
        // panel is the single biggest thing covering the table.
        if (m.ShowPanel) DrawLeftPanel(ci, font, m);

        // ON TOUCH, THE PANEL GETS AN ON-SCREEN FOLD TAB, because F1 does not exist on a phone. It is
        // drawn OUTSIDE the ShowPanel check -- collapsed it is the ONLY way back, so making it
        // conditional on the thing it controls would strand the player with no panel and no way to
        // recall it. Same reasoning as the button grid's tab.
        if (m.TouchControls)
        {
            TouchControls.DrawChevronTab(ci, PanelFoldRect(), !m.ShowPanel,
                m.ShowPanel ? TouchControls.Chevron.Left : TouchControls.Chevron.Right);
        }

        // The toast is drawn LAST and OUTSIDE DrawLeftPanel. It used to live inside that method, which
        // meant folding the panel away also hid every error and rule message -- the one thing on screen
        // that is not decoration. It is the table column's overlay, not part of the panel.
        if (!string.IsNullOrEmpty(m.Message))
        {
            DrawMessage(ci, font, m);
        }
    }

    /// <summary>
    /// Where the side panel's fold tab sits.
    ///
    /// ONE FIXED RECT, IDENTICAL IN BOTH STATES. The obvious alternative -- letting the tab travel with
    /// the drawer, to the screen edge when folded -- moves the control 200+ px the instant it is tapped.
    /// A control whose position is only correct in the state you just left is a control you have to hunt
    /// for, and this one is the ONLY way to bring the panel back. So it stays where the finger left it.
    ///
    /// WHY NOT THE LITERAL TOP-LEFT CORNER: the panel's own text starts at x=16/y=50 ("击球" and its
    /// status line), so a tab at the corner would cover the panel's first readout in the state where the
    /// panel is visible. This sits in the panel's top-right, which is empty (the panel's content stops at
    /// x=242, and the only text near y 40..72 begins at x=16), and it stays clear of the top bar (y<28)
    /// and of the toast (y 494). Folded, it floats alone in the same place.
    /// </summary>
    public static Rect2 PanelFoldRect()
        => new Rect2(214.0f, 38.0f, 34.0f, 34.0f);

    // --------------------------------------------------------------- top bar

    private static void DrawTopBar(CanvasItem ci, Font font, Model m)
    {
        var rect = new Rect2(0, 0, 1040, 28);
        ci.DrawRect(rect, Palette.PanelBg);
        ci.DrawLine(new Vector2(0, 28), new Vector2(1040, 28), Palette.PanelEdge, 1.0f);

        var x = 8.0f;
        // The friendly level name (Chinese, from level data) instead of the raw id, which stays
        // visible in the F1 panel. Ellipsized because the name is data: a long one must never push
        // the chips into the right-aligned readout.
        var title = string.IsNullOrWhiteSpace(m.LevelName) ? m.LevelId : m.LevelName;
        x = Text(ci, font, x, 19, $"回溯  |  {Ellipsize(font, title, 15, 300.0f)}", 15, Palette.TextMain);
        x += 10;

        var ballsLeft = m.ObjectBallCount - m.PocketedObjectCount;
        x = Chip(ci, font, x, "目标球", $"{ballsLeft} / {m.ObjectBallCount}", ballsLeft == 0 ? Palette.Good : Palette.Accent);

        var strikeText = m.ShotUsed ? "已击出 · 仅可回溯" : "可击球（每关一杆）";
        x = Chip(ci, font, x, "击球", strikeText, m.ShotUsed ? Palette.Bad : Palette.Good);

        if (m.Rewinding)
        {
            var label = m.Scrubbing ? "回溯暂停（微调中）" : "回溯中";
            x = Chip(ci, font, x, "时间", label, Palette.RewindCue);
        }
        else if (m.Won)
        {
            x = Chip(ci, font, x, "时间", "已清台", Palette.Good);
        }
        else
        {
            x = Chip(ci, font, x, "时间", "正放", Palette.TextDim);
        }

        var t = $"t={m.Time:F2}s   步={m.Tick}   回溯={m.Rewinds}   轨迹={m.HistoryEntries}";
        // Anchored to the VISIBLE right edge, not to 1040: the Play view scales the node by
        // ViewScale while the canvas stays 1040 px wide, so a readout right-aligned at local
        // x=1030 is rasterised at 1076 and silently drops its last field off-screen. (The English
        // build had the same bug: "trail=1" rendered as "tra".)
        var rightLocal = VisibleWidth(ci) - Pad;
        Text(ci, font, rightLocal - Measure(font, t, 12), 19, t, 12, Palette.TextDim);
    }

    /// <summary>
    /// Canvas width expressed in the coordinates this CanvasItem actually draws in. The HUD lives on
    /// a scaled Node2D, so a physical edge must be divided back through that scale before anything
    /// is anchored to it.
    /// </summary>
    private static float VisibleWidth(CanvasItem ci)
    {
        var sx = ci.GetGlobalTransform().Scale.X;
        return sx > 0.001f ? 1040.0f / sx : 1040.0f;
    }

    private static float Chip(CanvasItem ci, Font font, float x, string key, string value, Color color)
    {
        var kw = Measure(font, key, 11);
        var vw = Measure(font, value, 13);
        var w = kw + vw + 18.0f;
        ci.DrawRect(new Rect2(x, 4, w, 20), new Color(color.R, color.G, color.B, 0.16f));
        ci.DrawLine(new Vector2(x + 6, 6), new Vector2(x + 6, 22), color, 2.0f);
        Text(ci, font, x + 11, 18, key, 11, Palette.TextDim);
        Text(ci, font, x + 11 + kw + 5, 18, value, 13, color);
        return x + w + 8;
    }

    // ------------------------------------------------------------ left panel

    /// <summary>
    /// Left panel. The layout is hand-computed on a fixed grid because there is no Control tree to
    /// resolve constraints; the y coordinate of every block is written next to it, and
    /// <see cref="PanelLayoutSelfCheck"/> asserts the blocks stay ordered and on screen.
    /// </summary>
    private static void DrawLeftPanel(CanvasItem ci, Font font, Model m)
    {
        var rect = new Rect2(6, 34, 246, 476);
        ci.DrawRect(rect, Palette.PanelBg);
        ci.DrawRect(rect, Palette.PanelEdge, false, 1.0f);

        var x = rect.Position.X + 10;

        // --- STRIKE --------------------------------------------------------- y 50 .. 81
        Text(ci, font, x, 50, "击球", 12, Palette.TextDim);
        Text(ci, font, x, 66, m.ShotUsed ? "已击出 · 永久锁定" : "可击球（每关仅一杆）", 14,
            m.ShotUsed ? Palette.Bad : Palette.Good);
        Text(ci, font, x, 81, m.ShotUsed ? "剩余操作：仅可回溯" : "从白球向后拖动", 11, Palette.TextDim);

        // --- POWER --------------------------------------------------------- y 104 .. 150
        DrawPowerMeter(ci, font, x, 104, m);

        // --- REWIND BUDGET ------------------------------------------------- y 160 .. 206
        Text(ci, font, x, 168, "回溯预算", 12, Palette.TextDim);
        var segMax = m.RewindSegmentsMax > 0 ? m.RewindSegmentsMax.ToString(CultureInfo.InvariantCulture) : "无限";
        Text(ci, font, x, 185, $"段数 {m.RewindSegmentsUsed} / {segMax}", 12,
            m.RewindSegmentsMax > 0 && m.RewindSegmentsUsed >= m.RewindSegmentsMax ? Palette.Bad : Palette.TextMain);
        DrawBudgetBar(ci, font, x, 192, m);

        // --- TRAIL / numbers: DEBUG ONLY ----------------------------------- y 216 .. 300
        // Hidden by default so a first-time player sees rules and controls, not counters. "采样点"
        // and "折返" are diagnostic readings with no in-game meaning; the rewind budget and the
        // transport hints above stay visible because they ARE the rules.
        if (m.Debug)
        {
            Text(ci, font, x, 224, "轨迹", 12, Palette.TextDim);
            var rows = new (string Key, string Value, Color C)[]
            {
                ("采样点", m.HistoryEntries.ToString(CultureInfo.InvariantCulture), Palette.TextMain),
                ("回溯段", m.TrailSessions.ToString(CultureInfo.InvariantCulture), Palette.TextMain),
                ("折返", m.TurnBacks.ToString(CultureInfo.InvariantCulture), Palette.TurnMarker),
                ("回正", m.TurnForwards.ToString(CultureInfo.InvariantCulture), Palette.TurnForwardMarker),
            };
            var ry = 241.0f;
            foreach (var row in rows)
            {
                Text(ci, font, x, ry, row.Key, 12, Palette.TextDim);
                Text(ci, font, x + 108, ry, row.Value, 12, row.C);
                ry += 15;
            }

            // Per-ball rail-boost allowance: this is a real rule now (capped by count), so it is
            // worth showing while tuning.
            var boosts = m.WallBoostsRemaining;
            var boostsText = boosts < 0 ? "无限" : boosts.ToString(CultureInfo.InvariantCulture);
            Text(ci, font, x, ry + 5, "撞库加速", 12, Palette.TextDim);
            Text(ci, font, x + 108, ry + 5, boostsText, 12,
                boosts == 0 ? Palette.Bad : Palette.Good);

            if (m.ForecastContacts > 0)
            {
                Text(ci, font, x, Math.Max(ry + 22, 291), $"待碰撞 {m.ForecastContacts}", 12, Palette.ContactSoon);
            }
        }
        else if (!m.TouchControls)
        {
            // The F1 hint is a KEYBOARD hint. On a phone there is no F1, and the panel is folded with
            // the on-screen tab instead, so printing it would advertise a key the player does not have.
            Text(ci, font, x, 228, "F1  显示调试面板", 11, Palette.TextDim);
        }

        // --- event cards + the two run-ending risks ------------------------ y 296 .. 336
        // Shown OUTSIDE the debug switch on purpose: which cards a run holds is not debug
        // information, it is the run's state, and the player must be able to read it at any time.
        var cy2 = 300.0f;
        if (m.Cards != null && m.Cards.Count > 0)
        {
            Text(ci, font, x, cy2, "事件卡", 12, Palette.TextDim);
            var cx = x + 62;
            foreach (var name in m.Cards)
            {
                var w = font.GetStringSize(name, HorizontalAlignment.Left, -1, 12).X + 12.0f;
                if (cx + w > x + 226.0f)
                {
                    cx = x + 62;
                    cy2 += 19.0f;
                }

                ci.DrawRect(new Rect2(cx, cy2 - 11, w, 16), Palette.PanelRaised);
                Text(ci, font, cx + 6, cy2, name, 12, Palette.Accent);
                cx += w + 5.0f;
            }

            cy2 += 21.0f;
        }

        // The rapid-tap COUNTER used to be printed here ("R 连击 1/2 · 下一击 = 垂死挣扎"). It is gone:
        // it described a keyboard gesture (repeatedly pressing R) and its wording was an announcement of
        // a rule, not a readout of the run's state -- the panel's job. The rule itself is unchanged and
        // still enforced by the kernel; see CardTests for the tap-counter assertions. The two lines
        // below stay because they ARE state: whether the run is already lost, and why.
        if (m.BalloonArmed)
        {
            Text(ci, font, x, cy2, "⚠ 气球已充气：速度归零即失败", 12, Palette.Bad);
            cy2 += 18.0f;
        }

        if (m.Lost)
        {
            Text(ci, font, x, cy2, "本关失败", 14, Palette.Bad);
            if (!string.IsNullOrEmpty(m.LostReason))
            {
                Text(ci, font, x, cy2 + 17, m.LostReason, 11, Palette.TextDim);
            }
        }

        // --- transport hint ------------------------------------------------ y 306 .. 322
        //
        // KEYBOARD ONLY. These lines name the R and P keys, which a phone does not have; on touch the
        // equivalent actions are the four labelled buttons in the bottom-right corner, and the rewind
        // button already says "按住回溯 · 松开保留速度" in TouchControls. Printing the key form on a phone
        // would describe a control the player cannot use -- the same reason the CONTROLS legend below
        // switches to touch wording.
        if (m.ShotUsed && !m.Won && !m.TouchControls)
        {
            Text(ci, font, x, 342, m.Rewinding ? (m.Scrubbing ? "P  在标记处松开" : "P  暂停 + 微调") : "按住 R  回溯",
                12, Palette.RewindCue);
            Text(ci, font, x, 358, m.Rewinding ? "松开 R  继续" : "松开 R  保留当时速度", 12, Palette.TextDim);
        }

        // --- CONTROLS, pinned to the panel bottom --------------------------- y 436 .. 540
        //
        // ON TOUCH THE KEYBOARD LEGEND IS REPLACED BY TOUCH INSTRUCTIONS. The legend lists R / P /
        // Space / F1 -- keys a phone does not have -- so printing it would describe controls the player
        // cannot use. (The four transport buttons used to live in this block; they now sit in the
        // bottom-right corner of the felt, where a thumb rests, so this space is free again and is used
        // to tell the player how to aim and where the buttons went.)
        if (m.TouchControls)
        {
            var ty = rect.Position.Y + rect.Size.Y - 108;
            ci.DrawLine(new Vector2(x, ty - 10), new Vector2(x + 226, ty - 10), Palette.PanelEdge, 1.0f);
            Text(ci, font, x, ty + 4, "操作（触屏）", 12, Palette.TextDim);
            var touch = new (string Key, string Desc)[]
            {
                ("拖动白球", "瞄准（向后拉再松开）"),
                ("右下角按钮", "回溯 / 暂停 / 重置 / 选关"),
                ("按住 回溯", "松开才结算速度"),
                ("小三角", "收起按钮，看全台面"),
                ("点事件卡", "选择效果"),
            };
            var tky = ty + 21;
            foreach (var line in touch)
            {
                Text(ci, font, x, tky, line.Key, 11, Palette.TextMain);
                Text(ci, font, x + 78, tky, line.Desc, 11, Palette.TextDim);
                tky += 14;
            }

            // The toast is NOT drawn here (see Draw()): it must survive the panel being folded away.
            return;
        }

        var cy = rect.Position.Y + rect.Size.Y - 108;
        ci.DrawLine(new Vector2(x, cy - 10), new Vector2(x + 226, cy - 10), Palette.PanelEdge, 1.0f);
        Text(ci, font, x, cy + 4, "操作", 12, Palette.TextDim);
        // Two real columns instead of one space-padded string: the English layout leaned on a
        // fixed-width grid that a proportional CJK face cannot reproduce, so the columns are now
        // positioned by pixel instead of by counting spaces.
        var controls = new (string Key, string Desc)[]
        {
            ("拖动后松开", "瞄准 / 击球"),
            ("R（按住）", "回溯"),
            ("P", "暂停 + 微调"),
            ("← / →", "移动释放点"),
            ("空格", "重置（调试）"),
            ("F1 / F2 / F3", "隐藏面板 / 残影 / 轨迹"),
        };
        var ky = cy + 21;
        foreach (var line in controls)
        {
            Text(ci, font, x, ky, line.Key, 11, Palette.TextMain);
            Text(ci, font, x + 78, ky, line.Desc, 11, Palette.TextDim);
            ky += 14;
        }

        // The toast is NOT drawn here (see Draw()): it must survive the panel being folded away.
    }

    /// <summary>
    /// The notification toast. Shared by both control layouts so the two cannot drift apart in
    /// position or styling.
    ///
    /// ON TOUCH IT IS NARROWER, AND ONLY NARROWER. The buttons sit at x 754..1028, y 396..514, so a
    /// 700px-wide toast (right edge 962) would have its right-hand end covered by them -- and the toast
    /// is where errors and rule explanations appear, i.e. exactly the text the player must read.
    /// Pulling the right edge in to 732 clears the grid horizontally, which is enough on its own.
    ///
    /// An earlier attempt ALSO lifted the toast to y 434. That was wrong twice over: it bought nothing
    /// (the buttons are cleared horizontally), and it pushed the toast up into the touch instructions
    /// in the side panel -- caught by PanelLayoutSelfCheck, which is exactly why that check exists. A
    /// floating toast does not have to be ordered against panel blocks; it only has to be on screen,
    /// clear of the bottom band, and clear of whatever shares its corner.
    /// </summary>
    private static void DrawMessage(CanvasItem ci, Font font, Model m)
    {
        var rect2 = m.TouchControls
            ? new Rect2(262, 494, 470, 50)
            : new Rect2(262, 494, 700, 50);
        ci.DrawRect(rect2, new Color(0.03f, 0.05f, 0.05f, 0.92f));
        ci.DrawRect(rect2, m.MessageColor, false, 2.0f);
        Text(ci, font, rect2.Position.X + 12, rect2.Position.Y + 31, m.Message, 14, m.MessageColor);
    }

    /// <summary>The toast's rect, so the layout check and the touch-overlap check agree on one value
    /// rather than each hard-coding a copy that can drift.</summary>
    public static Rect2 MessageRect(bool touchControls)
        => touchControls ? new Rect2(262, 494, 470, 50) : new Rect2(262, 494, 700, 50);

    /// <summary>
    /// The panel is drawn with absolute coordinates, so this asserts the blocks stay inside the
    /// screen and in order: a later edit cannot silently reintroduce the overlap this layout
    /// replaced.
    ///
    /// BOTH LAYOUTS ARE CHECKED, because the toast is NARROWER on touch (it stops at x 732 so the
    /// bottom-right buttons do not cover it). Checking only the desktop width would let the touch
    /// layout drift into an overlap unnoticed -- and the toast is where errors appear, so covering it
    /// is the failure the player would notice first.
    ///
    /// THE TOAST IS CHECKED SEPARATELY, NOT AS A PANEL BLOCK. It floats over the TABLE column (x
    /// 262..732), while every block in the list above lives in the SIDE panel (x 6..252). They cannot
    /// overlap horizontally, so requiring them to be vertically ordered is meaningless -- and asserting
    /// it is how an earlier version of this check produced a false failure when the toast was (need-
    /// lessly) lifted. The toast gets its own rules: on screen, above the bottom band, clear of the
    /// on-screen buttons.
    /// </summary>
    public static IReadOnlyList<string> PanelLayoutSelfCheck(bool touchControls = false)
    {
        var problems = new List<string>();

        // The controls block is the same rectangle in both layouts; only its contents differ.
        var controlsTop = 34 + 476 - 108 - 10;
        var controlsBottom = 34 + 476 - 108 + 21 + 5 * 14;

        var blocks = new (string Name, float Top, float Bottom)[]
        {
            ("strike", 50, 81),
            ("power", 104, 154),
            ("budget", 168, 214),
            ("trail", 224, 293),
            ("transport", 342, 358),
            ("controls", controlsTop, controlsBottom),
        };
        for (var i = 0; i < blocks.Length; i++)
        {
            if (blocks[i].Top < 36 || blocks[i].Bottom > 560)
                problems.Add($"{blocks[i].Name} outside the screen ({blocks[i].Top}..{blocks[i].Bottom})");
            if (i > 0 && blocks[i].Top < blocks[i - 1].Bottom)
                problems.Add($"{blocks[i].Name} overlaps {blocks[i - 1].Name} ({blocks[i].Top} < {blocks[i - 1].Bottom})");
        }

        // The toast: its own rules, because it lives in a different column.
        var messageRect = MessageRect(touchControls);
        if (messageRect.Position.X < 262)
            problems.Add($"the toast starts left of the table column ({messageRect.Position.X})");
        if (messageRect.Position.Y < 36 || messageRect.Position.Y + messageRect.Size.Y > 560)
            problems.Add($"the toast is off screen ({messageRect})");
        // The bottom replay band starts at 546, so the toast must end above it.
        if (messageRect.Position.Y + messageRect.Size.Y > 546)
            problems.Add($"the toast reaches the bottom band ({messageRect.Position.Y + messageRect.Size.Y} > 546)");

        // It must also clear the side panel's blocks horizontally -- a toast that widened leftwards
        // would land on the readouts.
        if (messageRect.Position.X < 6 + 246)
            problems.Add($"the toast overlaps the side panel's column ({messageRect.Position.X} < 252)");

        // And the on-screen buttons, which share its corner.
        if (touchControls)
        {
            foreach (var a in System.Enum.GetValues<TouchControls.Action>())
            {
                if (Overlaps(messageRect, TouchControls.Rect(a)))
                {
                    problems.Add($"the notification toast is covered by the {a} button ({messageRect} vs {TouchControls.Rect(a)})");
                }
            }

            if (Overlaps(messageRect, TouchControls.CollapseToggleRect()))
            {
                problems.Add($"the notification toast is covered by the collapse tab ({messageRect} vs {TouchControls.CollapseToggleRect()})");
            }
        }

        return problems;
    }

    private static bool Overlaps(Rect2 a, Rect2 b)
        => !(a.Position.X + a.Size.X <= b.Position.X
             || b.Position.X + b.Size.X <= a.Position.X
             || a.Position.Y + a.Size.Y <= b.Position.Y
             || b.Position.Y + b.Size.Y <= a.Position.Y);

    private static void DrawPowerMeter(CanvasItem ci, Font font, float x, float y, Model m)
    {
        Text(ci, font, x, y, "力度", 12, Palette.TextDim);
        var speed = m.Dragging || !m.ShotUsed ? m.AimPower * 7.4 : 0.0;
        if (m.ShotUsed) speed = m.World.Cue.Speed;
        Text(ci, font, x + 150, y, $"{speed:F0} px/s", 12, Palette.TextMain);

        var bar = new Rect2(x, y + 6, 230, 16);
        ci.DrawRect(bar, new Color(0.10f, 0.13f, 0.13f, 1.0f));
        var t = m.MaxPower > 0 ? Mathf.Clamp((float)(m.AimPower / m.MaxPower), 0f, 1f) : 0f;
        if (!m.ShotUsed)
        {
            var fill = Palette.MeterLow.Lerp(Palette.MeterHigh, t);
            ci.DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * t, bar.Size.Y)), fill);
        }
        else
        {
            // After the strike the meter becomes a live cue-speed readout, so "the strike is spent"
            // is visible in the same place the player used to aim.
            var live = Mathf.Clamp((float)(m.World.Cue.Speed / 1600.0), 0f, 1f);
            ci.DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * live, bar.Size.Y)), Palette.TextDim);
        }

        // Minimum-power tick: below this the kernel refuses the shot (locked MinPower = 12 px).
        var minX = bar.Position.X + bar.Size.X * (float)(m.MinPower / Math.Max(1.0, m.MaxPower));
        ci.DrawLine(new Vector2(minX, bar.Position.Y - 3), new Vector2(minX, bar.Position.Y + bar.Size.Y + 3), Palette.Warn, 2.0f);
        Text(ci, font, minX - 12, bar.Position.Y + bar.Size.Y + 12, "最小", 10, Palette.Warn);
        Text(ci, font, bar.Position.X + bar.Size.X - 24, bar.Position.Y + bar.Size.Y + 12, "最大", 10, Palette.TextDim);

        ci.DrawRect(bar, Palette.PanelEdge, false, 1.0f);
        if (m.HasAim && !m.ShotUsed)
        {
            Text(ci, font, x, y + 46, $"角度 {m.AimAngle:F1}°  拖动 {m.AimPower:F0} / {m.MaxPower:F0} px", 11, Palette.TextMain);
        }
        else if (!m.ShotUsed)
        {
            Text(ci, font, x, y + 46, "从白球向后拖动", 11, Palette.TextDim);
        }
    }

    private static void DrawBudgetBar(CanvasItem ci, Font font, float x, float y, Model m)
    {
        var bar = new Rect2(x, y, 230, 12);
        ci.DrawRect(bar, new Color(0.10f, 0.13f, 0.13f, 1.0f));
        if (m.RewindDistanceMax > 0)
        {
            var t = Mathf.Clamp((float)(m.RewindDistanceUsed / m.RewindDistanceMax), 0f, 1f);
            ci.DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * t, bar.Size.Y)),
                t > 0.99f ? Palette.Bad : Palette.RewindTraversed);
            ci.DrawRect(bar, Palette.PanelEdge, false, 1.0f);
            Text(ci, font, x, y + 22, $"距离 {m.RewindDistanceUsed:F0} / {m.RewindDistanceMax:F0} px", 12, Palette.TextMain);
        }
        else
        {
            ci.DrawRect(bar, Palette.PanelEdge, false, 1.0f);
            Text(ci, font, x, y + 22, $"距离 {m.RewindDistanceUsed:F0} px（无上限）", 12, Palette.TextMain);
        }
    }

    // --------------------------------------------------------- debug overlay

    /// <summary>
    /// The debug overlay's line for the decorative bands. Both instances are reported on one row
    /// because they are one effect: if the two ever disagree, that is the bug this line exists to
    /// make visible.
    /// </summary>
    private static (string Text, Color Color, int Size) BandRow(Model m)
    {
        if (m.Bands == null || m.Bands.Length == 0) return ("底带    关闭", Palette.TextDim, 11);
        var sb = new System.Text.StringBuilder("底带    ");
        for (var i = 0; i < m.Bands.Length; i++)
        {
            if (i > 0) sb.Append(" | ");
            sb.Append(m.Bands[i].BandName).Append(' ').Append(m.Bands[i].Stats());
        }

        return (sb.ToString(), Palette.TextDim, 11);
    }

    public static void DrawDebug(CanvasItem ci, Font font, Model m)
    {
        var rect = new Rect2(696, 34, 268, 476);
        ci.DrawRect(rect, new Color(0.02f, 0.03f, 0.035f, 0.92f));
        ci.DrawRect(rect, Palette.SessionStripe, false, 1.0f);

        var x = rect.Position.X + Pad;
        var y = rect.Position.Y + 20;
        var w = m.World;
        var nonFinite = w.HasNonFiniteState();

        Text(ci, font, x, y, "调试面板（F1）", 13, Palette.SessionStripe);
        y += 8;
        Text(ci, font, x, y + 12, $"关卡 {m.LevelId}  步长 {w.Config.SimulationStep * 1000:F3} ms  引擎刻 {Engine.PhysicsTicksPerSecond}", 11, Palette.TextDim);
        y += 30;
        Text(ci, font, x, y, $"步数        {w.Tick}", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"时间        {w.Time:F4} s", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"已击球 {w.ShotUsed}   已清台 {w.Won}   冻结 {w.Frozen}", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"回溯中 {w.Rewinding}   暂停 {m.Paused}   微调 {m.Scrubbing}", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"非有限值    {(nonFinite ? "是 ***" : "否")}", 12, nonFinite ? Palette.Bad : Palette.Good);
        y += 15;
        Text(ci, font, x, y, $"历史        {w.Cue.History.Count} 条  间隔 {w.Cue.History.Interval:F2} px", 12, Palette.TextMain);
        y += 15;
        Text(ci, font, x, y, $"采样累加    {w.Cue.History.DistanceSinceSample:F3} px", 12, Palette.TextMain);
        y += 15;
        if (w.RewindTrack != null)
        {
            Text(ci, font, x, y, $"快照        {w.RewindTrack.Length} 点  游标 {w.RewindCursor}  u {w.RewindFraction:F3}", 12, Palette.TextMain);
            y += 15;
        }

        y += 12;
        Text(ci, font, x, y + 6, "球  id  x      y      |v|     状态", 12, Palette.TextDim);
        y += 24;
        foreach (var b in w.Balls)
        {
            var state = b.Pocketed ? "已入袋" : b.Speed > 0.0 ? "运动" : "静止";
            var line = string.Format(CultureInfo.InvariantCulture,
                "  {0,2}  {1,6:F1} {2,6:F1}  {3,7:F1}  {4}{5}", b.Id, b.Position.X, b.Position.Y, b.Speed, state,
                b.IsCue ? "（白球）" : "");
            var color = b.Pocketed ? Palette.TextDim : b.IsCue ? Palette.Cue : Palette.TextMain;
            Text(ci, font, x, y, line, 12, color);
            y += 15;
        }

        y += 8;
        Text(ci, font, x, y, "视图", 12, Palette.TextDim);
        y += 16;

        // Left panel. The layout is hand-computed on a fixed grid because there is no Control tree to
        // resolve constraints; the y of every block is written next to it and
        // <see cref="PanelLayoutSelfCheck"/> asserts the blocks stay ordered and on screen.
        var lineH = 11;
        var x0 = rect.Position.X + 10;
        var x1 = rect.Position.X + rect.Size.X - 10;
        var rows2 = new List<(string Text, Color Color, int Size)>
        {
            ($"帧耗时  {m.FrameMs:F2} ms（峰值 {m.MaxFrameMs:F2}）", Palette.TextDim, 11),
            ($"绘制/模拟 {m.DrawMs:F2} / {m.SimMs:F2} ms", Palette.TextDim, 11),
            ($"顶点    {m.TrailVertices}  回溯段 {m.TrailSessions}", Palette.TextDim, 11),
            ($"折返    {m.TurnBacks} 次  回正 {m.TurnForwards} 次", Palette.TextDim, 11),
            ($"残影    {(m.Trail == null ? 0 : GhostLength(m.Trail))} 点  段数 {m.RewindSegmentsUsed}/{m.RewindSegmentsMax}", Palette.TextDim, 11),
            ($"预测    {m.ForecastContacts} 次碰撞  {m.ForecastUs} us", Palette.TextDim, 11),
            BandRow(m),
            ($"已走过  {m.Trail?.TraversedRange.From}..{m.Trail?.TraversedRange.To}", Palette.TextDim, 11),
        };
        foreach (var row in rows2)
        {
            foreach (var line in Hud.Wrap(font, row.Text, row.Size, x1 - x0))
            {
                Text(ci, font, x0, y, line, row.Size, row.Color);
                y += lineH;
            }
        }

        if (m.Aim != null && m.Aim.Valid)
        {
            y += 6;
            Text(ci, font, x, y, "瞄准预测", 12, Palette.TextDim);
            y += 16;
            var a = new[]
            {
                $"接触    {m.Aim.Contact}  球 {m.Aim.ContactBallId}",
                $"到接触  {m.Aim.TimeToContact * 1000:F1} ms  步数 {m.Aim.Steps}",
                $"被撞球  {m.Aim.StruckSpeed:F0} px/s",
                $"白球随后 {m.Aim.CueDeflectSpeed:F0} px/s",
                $"解算用时 {m.Aim.Microseconds} us",
                $"静止于  {m.Aim.StopPoint.X:F0},{m.Aim.StopPoint.Y:F0} 已到达={m.Aim.ReachedRest}",
            };
            foreach (var line in a)
            {
                foreach (var wrapped in Wrap(font, line, 11, x1 - x0))
                {
                    if (y > rect.Position.Y + rect.Size.Y - 12) return;
                    Text(ci, font, x0, y, wrapped, 11, Palette.TextDim);
                    y += 13;
                }
            }
        }

        if (m.RewindVis != null && m.RewindVis.BallContacts.Count > 0)
        {
            y += 6;
            if (y > rect.Position.Y + rect.Size.Y - 30) return;
            Text(ci, font, x, y, "回溯预测（碰撞前）", 12, Palette.ContactSoon);
            y += 16;
            foreach (var f in m.RewindVis.BallContacts)
            {
                var toGo = f.ArcFromCursor - m.RewindVis.ArcWalked;
                Text(ci, font, x, y, $"  球 {f.BallId} 还需 {toGo:F0} px / {f.Seconds:F2}s", 11,
                    toGo < 0 ? Palette.TextDim : Palette.ContactSoon);
                y += 14;
                if (y > rect.Position.Y + rect.Size.Y - 20) break;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static float Text(CanvasItem ci, Font font, float x, float y, string text, int size, Color color)
    {
        if (string.IsNullOrEmpty(text)) return x;
        ci.DrawString(font, new Vector2(x, y), text, HorizontalAlignment.Left, -1, size, color);
        return x + font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
    }

    private static float Measure(Font font, string text, int size)
    {
        return font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
    }

    /// <summary>
    /// Trim to a pixel budget, appending an ellipsis. Level names are data, so the top bar cannot
    /// assume they are short: without this a 40-glyph name would run into the stat chips.
    /// </summary>
    private static string Ellipsize(Font font, string text, int size, float maxWidth)
    {
        if (Measure(font, text, size) <= maxWidth) return text;
        var trimmed = text;
        while (trimmed.Length > 1 && Measure(font, trimmed + "…", size) > maxWidth)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed + "…";
    }

    /// <summary>
    /// Greedy word wrap to a pixel width: the debug panel must not spill outside itself. Chinese
    /// has no spaces, so a single "word" can be a whole clause; when that happens it is broken
    /// per character rather than allowed to overflow the panel.
    /// </summary>
    private static List<string> Wrap(Font font, string text, int size, float maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ');
        var current = "";
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Measure(font, candidate, size) <= maxWidth || current.Length == 0)
            {
                if (current.Length == 0 && Measure(font, candidate, size) > maxWidth)
                {
                    // Unbreakable-by-space token wider than the panel (typical for CJK): split it.
                    foreach (var ch in word)
                    {
                        if (current.Length > 0 && Measure(font, current + ch, size) > maxWidth)
                        {
                            lines.Add(current);
                            current = "";
                        }

                        current += ch;
                    }

                    continue;
                }

                current = candidate;
            }
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private static int GhostLength(TimelineTrail trail)
    {
        return trail.GhostTrack?.Length ?? 0;
    }
}
