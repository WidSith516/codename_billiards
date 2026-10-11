# REWIND — 简体中文本地化 (Simplified Chinese localization)

Status: **done and visually verified**. Every player-facing string in `godot/src/game/**` is
Simplified Chinese, a CJK font is vendored into the project and applied to every draw path, and the
result was screenshotted and checked for tofu boxes rather than assumed.

Owner: `loc-zh`. Write scope: `godot/src/game/**` (string localization), `godot/assets/**`,
`godot/evidence/loc_*`, this file.

---

## 1. Font

| | |
|---|---|
| **File** | `godot/assets/DroidSansFallback.ttf` |
| **Family (from the font's own name table)** | Droid Sans Fallback |
| **Size / SHA-256** | 3,939,852 bytes / `05D71B179EF97B82CF1BB91CEF290C600A510F77F39B4964359E3EF88378C79D` |
| **Copied from** | `C:\Program Files\Epic Games\Launcher\Engine\Content\Slate\Fonts\DroidSansFallback.ttf` (hash-verified identical before and after the copy) |
| **Licence** | **Apache License 2.0** — permissive, redistributable |
| **Licence text vendored at** | `godot/assets/DroidSansFallback.LICENSE.txt` (full Apache-2.0 text + attribution + provenance + hash) |

### Why this font

Godot's built-in font (`ThemeDB.FallbackFont`) contains **no CJK glyphs at all**. Translating the
strings without changing the font produces a screen full of `□` while every "is this frame
non-blank?" check still passes — the exact silent failure this task warned about.

The font selection was made by *testing candidates*, not by picking the prettiest name:

| Candidate | Path | Licence | Coverage of the 215 non-ASCII codepoints this game can draw |
|---|---|---|---|
| **Droid Sans Fallback** ✅ | `godot/assets/` (vendored) | **Apache-2.0** | **215 / 215 — 0 missing** |
| SimHei (黑体) | `C:/Windows/Fonts/simhei.ttf` | Microsoft proprietary | 215 / 215 |
| DengXian (等线) | `C:/Windows/Fonts/Deng.ttf` | Microsoft proprietary | 215 / 215 |
| Microsoft YaHei (微软雅黑) | `C:/Windows/Fonts/msyh.ttc` | Microsoft proprietary | 215 / 215 |
| SimSun (宋体) | `C:/Windows/Fonts/simsun.ttc` | Microsoft proprietary | 215 / 215 |
| Noto Sans TC | `C:/Program Files/Epic Games/Launcher/Portal/Content/Font/NotoSansTC-Regular.otf` | SIL OFL 1.1 | **167 / 215 — 48 missing** ❌ |

Two conclusions worth recording:

* **The Microsoft faces render this localization correctly but may not be redistributed.** They are
  fine for a local build; shipping them with the project is a licence violation. Droid Sans Fallback
  is the only permissively-licensed CJK face present on this machine, so it is the one vendored.
* **The "obviously permissive" alternative on this machine is a trap.** Noto Sans TC is SIL OFL and
  has its own licence file sitting right next to it — but it is a *Traditional Chinese* subset and is
  missing 48 of the 215 codepoints used here, **including simplified-only everyday characters**:
  `击 关 暂 调 时 轨 锁 预 数 记 开 继 续 当 释 试 残 冻 结` (full list and command in §5). Had it been
  chosen on licence grounds alone, roughly a quarter of the HUD would have been tofu boxes.

### A `.ttf`, deliberately — not a `.ttc`

`msyh.ttc` / `simsun.ttc` are TrueType *collections*: they need a face index, and picking the wrong
one is another way this fails silently. The vendored font is a plain single-face `.ttf`, so there is
no face index to get wrong. (The probe in §5 does load the `.ttc` candidates and reports the family
name it actually got, as a cross-check.)

### How it is applied

`godot/src/game/Play.cs`:

* `LoadUiFont()` loads `res://assets/DroidSansFallback.ttf` — first through `ResourceLoader` (the
  imported path, which is what an exported build has), then via `FontFile.LoadDynamicFont()` (works
  on a fresh checkout before the import cache exists), and only then falls back to
  `ThemeDB.FallbackFont` **with a `GD.PushError`**, so the tofu case is loud instead of silent.
* `_font` is passed to every draw path: the HUD, the ball ID labels, the aim predictor, the rewind
  visualizer and (new) the trail's session labels.
* `GetTree().Root.Theme = new Theme { DefaultFont = _font, DefaultFontSize = 13 }` — so a `Control`
  added later inherits the CJK font instead of quietly reintroducing tofu.
* `TimelineTrail` used to hard-code `ThemeDB.FallbackFont` for its `S1/S2/...` labels; it now takes
  an injected `LabelFont` set from `Play`.

---

## 2. String table (English → 简体中文)

`godot/src/game/Play.cs` sets `_font`; everything below is drawn with it. Format specifiers and
units are preserved exactly; `px`, `px/s`, `ms`, `us`, `s` are left as-is because they are read as
units, not as words.

### 2.1 Top bar — `Hud.DrawTopBar`

| English | 简体中文 |
|---|---|
| `REWIND  \|  {levelId}` | `回溯  \|  {levelName}` — see §2.7 |
| `BALLS` (chip label) | `目标球` |
| `{left} / {total}` | *(unchanged)* |
| `STRIKE` (chip label) | `击球` |
| `STRIKE AVAILABLE` | `可击球（每关一杆）` |
| `STRIKE USED - REWIND ONLY` | `已击出 · 仅可回溯` |
| `TIME` (chip label) | `时间` |
| `REWINDING` | `回溯中` |
| `REWIND PAUSED (scrub)` | `回溯暂停（微调中）` |
| `CLEARED` | `已清台` |
| `FORWARD` | `正放` |
| `t={t}s   tick={n}   rewinds={n}   trail={n}` | `t={t}s   步={n}   回溯={n}   轨迹={n}` |

### 2.2 Left panel — `Hud.DrawLeftPanel` / `DrawPowerMeter` / `DrawBudgetBar`

| English | 简体中文 |
|---|---|
| `STRIKE` | `击球` |
| `AVAILABLE (1 per level)` | `可击球（每关仅一杆）` |
| `USED - LOCKED FOREVER` | `已击出 · 永久锁定` |
| `drag back from the cue ball` | `从白球向后拖动` |
| `verb left: REWIND only` | `剩余操作：仅可回溯` |
| `POWER` | `力度` |
| `{v} px/s` | *(unchanged)* |
| `min` | `最小` |
| `max` | `最大` |
| `angle {a} deg  drag {p} / {m} px` | `角度 {a}°  拖动 {p} / {m} px` |
| `REWIND BUDGET` | `回溯预算` |
| `segments {u} / {m}` | `段数 {u} / {m}` |
| `inf` | `无限` |
| `distance {u} / {m} px` | `距离 {u} / {m} px` |
| `distance {u} px (no limit)` | `距离 {u} px（无上限）` |
| `TRAIL` | `轨迹` |
| `points` | `采样点` |
| `sessions` | `回溯段` |
| `turn-backs` | `折返` |
| `turn-forwards` | `回正` |
| `hits pending {n}` | `待碰撞 {n}` |
| `hold R  rewind` | `按住 R  回溯` |
| `P  pause + scrub` | `P  暂停 + 微调` |
| `P  release at marker` | `P  在标记处松开` |
| `release R  keep stored v` | `松开 R  保留当时速度` |
| `release R  resume` | `松开 R  继续` |
| `CONTROLS` | `操作` |
| `drag + release   aim / strike` | `拖动后松开` ┃ `瞄准 / 击球` |
| `R (hold)         rewind` | `R（按住）` ┃ `回溯` |
| `P                pause + scrub` | `P` ┃ `暂停 + 微调` |
| `left / right     move marker` | `← / →` ┃ `移动释放点` |
| `Space            reset (debug)` | `空格` ┃ `重置（调试）` |
| `F1 debug F2 ghost F3 trail` | `F1 / F2 / F3` ┃ `调试 / 残影 / 轨迹` |

The CONTROLS block is the one place the layout was changed, and for a concrete reason: the English
version aligned its two columns by padding with spaces, which only works in a fixed-width grid. A
proportional CJK face cannot reproduce that, so the six lines are now drawn as two real pixel
columns (`x` and `x + 78`) instead of one space-padded string. The y positions, the separator line,
the row height and the block bounds are all unchanged.

### 2.3 Debug overlay (F1) — `Hud.DrawDebug`

Kept in scope because it is drawn on screen, even though it is a developer panel. Technical tokens
(`true`/`false`, `us`, `u`, ids, numbers) are kept readable rather than translated.

| English | 简体中文 |
|---|---|
| `DEBUG OVERLAY (F1)` | `调试面板（F1）` |
| `level {id}  dt {ms} ms  engineTicks {n}` | `关卡 {id}  步长 {ms} ms  引擎刻 {n}` |
| `tick` / `time` | `步数` / `时间` |
| `shotUsed {a}   won {b}   frozen {c}` | `已击球 {a}   已清台 {b}   冻结 {c}` |
| `rewinding {a}   paused {b}   scrub {c}` | `回溯中 {a}   暂停 {b}   微调 {c}` |
| `nonfinite   {YES *** / no}` | `非有限值    {是 *** / 否}` |
| `history     {n} entries  gap {g} px` | `历史        {n} 条  间隔 {g} px` |
| `samples+    {v} px` | `采样累加    {v} px` |
| `snapshot    {n} pts  cursor {c}  u {f}` | `快照        {n} 点  游标 {c}  u {f}` |
| `BALLS  id  x      y      \|v\|     state` | `球  id  x      y      \|v\|     状态` |
| `POCKETED` / `moving` / `rest` | `已入袋` / `运动` / `静止` |
| `(cue)` | `（白球）` |
| `VIEW` | `视图` |
| `frame {a} ms (max {b})` | `帧耗时  {a} ms（峰值 {b}）` |
| `draw/sim {a} / {b} ms` | `绘制/模拟 {a} / {b} ms` |
| `verts {n}  sessions {s}` | `顶点    {n}  回溯段 {s}` |
| `turns back {a}  fwd {b}` | `折返    {a} 次  回正 {b} 次` |
| `ghost {n} pts  segments {a}/{b}` | `残影    {n} 点  段数 {a}/{b}` |
| `forecast {n} hits  {t} us` | `预测    {n} 次碰撞  {t} us` |
| `traversed {a}..{b}` | `已走过  {a}..{b}` |
| `PREDICTION` | `瞄准预测` |
| `contact {k}  ball {id}` | `接触    {k}  球 {id}` |
| `toContact {t} ms  steps {n}` | `到接触  {t} ms  步数 {n}` |
| `struck {v} px/s` | `被撞球  {v} px/s` |
| `cue next {v} px/s` | `白球随后 {v} px/s` |
| `trace {us} us` | `解算用时 {us} us` |
| `rest at {x},{y} reached={b}` | `静止于  {x},{y} 已到达={b}` |
| `REWIND FORECAST (before contact)` | `回溯预测（碰撞前）` |
| `ball {id} in {d} px / {t}s` | `球 {id} 还需 {d} px / {t}s` |

`Hud.Wrap()` gained a per-character break fallback. It split on spaces only, which is fine for
English but would let a long space-free Chinese clause overflow the 268 px debug panel.

### 2.4 Gameplay messages — `Play.cs` (`Notify`, message box)

| English | 简体中文 |
|---|---|
| `STRIKE  angle {a} deg  power {p} px  speed {v} px/s` | `击球  角度 {a}°  力度 {p} px  速度 {v} px/s` |
| `REWIND refused (no path / budget exhausted / nothing to rewind)` | `无法回溯（没有可走路径 / 预算已耗尽 / 没有历史）` |
| `REWINDING - hold R, release R to keep the stored velocity` | `回溯中 · 按住 R 倒退，松开保留当时的逆向速度` |
| `REWIND resumed` | `继续回溯` |
| `PAUSED - drag or use arrows to move the release point backwards, R to release there` | `已暂停 · 拖动或用 ← → 把释放点向后移，按 R 在该点松开` |
| `nothing to rewrite yet (strike first, then hold R)` | `还没有可回溯的历史（先击球，再按住 R）` |
| `cannot reset while rewinding` | `回溯中无法重置` |
| `ghost path on / off` | `残影路径：开 / 关` |
| `history trail on / off` | `历史轨迹：开 / 关` |
| `level already won` | `本关已清台` |
| `cannot aim while rewinding` | `回溯中无法瞄准` |
| `ONE STRIKE PER LEVEL - it has been used; the only verb left is REWIND` | `每关仅一杆 · 已经用掉，剩余唯一操作是回溯` |
| `click ON the cue ball, then drag back and release` | `请点中白球，再向后拖动并松开` |
| `drag at least {m} px (this was {l})` | `至少要拖动 {m} px（本次只有 {l}）` |
| `a rewind cannot skip forward along the path` | `回溯不能沿路径向前跳` |
| `release point moved back {n} samples` | `释放点后移了 {n} 个采样点` |
| `RELEASED - velocity is the stored v at this history point ({v} px/s)` | `已松开 · 速度取该历史点存储的 v（{v} px/s）` |
| `RELEASED at the scrubbed point - stored v was {v} px/s` | `已在微调点松开 · 存储的 v 为 {v} px/s` |
| `level reset - the strike is available again (this is a debug key, not a rule)` | `关卡已重置 · 击球重新可用（调试键，不是规则）` |
| `AUTOPLAY stroke {i}/{n}: angle {a} power {p}` | `自动击球 {i}/{n}：角度 {a}°  力度 {p}` |

### 2.5 On the table — `Play.cs`, `AimPredictor.cs`, `RewindVisualizer.cs`, `TimelineTrail.cs`

These sit next to the geometry the player is looking at, and they were in the original brief's
out-of-scope set. The Lead extended the scope to cover them after this file reported them; keeping
`HIT 1.2s` / `PATH START` / `RELEASE` / `S1` on the felt would not be 完整本地化.

| English | 简体中文 | Where |
|---|---|---|
| `MAX` | `满力` | aim drag past max power |
| `speed {v} px/s  t={ms} ms` | `速度 {v} px/s  接触 {ms} ms` | aim prediction label |
| `cue stops` | `白球停住` | aim prediction, cue dies on contact |
| `HIT {t}s` | `命中 {t}s` | rewind contact forecast ring |
| `PATH START {t}s` | `起点 {t}s` | rewind path start |
| `RELEASE` | `释放` | scrub release marker |
| `REWINDING` | `回溯中` | label above the rewinding cue ball |
| `S{n}` | `第{n}段` | time-layered trail session boundary |
| `TABLE CLEARED` | `清台成功` | win banner |
| `{n}/{N} object balls in {t}s` | `{n}/{N} 颗目标球 · 用时 {t}s` | win banner, line 2 |
| `rewinds {n}   distance {d}px   trail {n} pts` | `回溯 {n} 次   距离 {d} px   轨迹 {n} 点` | win banner, line 3 |

### 2.6 The top-bar level title

The top bar used to print the raw level **id** (`corridor`). That was the last English word on the
HUD, and it left the localization visibly incomplete. `m2-levels` has since put a Chinese title in
the level data (`corridor.json`: `"name": "一杆清台 · 演示关（L1：中库回程）"`, UTF-8), so the top
bar now prints the level **name** and the raw id moved to where a developer wants it — the F1 debug
panel still shows `关卡 {levelId}`.

Because the name is *data*, `Hud` cannot assume it is short, so it goes through a new
`Hud.Ellipsize(font, title, 15, 300.0f)` which trims to a 300 px budget and appends `…`. A future
40-glyph level name therefore degrades to an ellipsis instead of running into the stat chips.
Level data itself is **not** edited by this task.

### 2.7 Deliberately NOT translated

* **`GD.Print` / console output** — `[play] level=…`, `[play] CAPTURE file=…`, the M2 selftest
  lines, `KERNEL_VIEW_READY`, and the `Check(...)` names. These are machine- and verifier-facing,
  they are grepped by `run_gates.ps1` and by existing reports, and translating them would break
  tooling and evidence chains for no player-visible benefit.
* **`palette` colour hex strings and every code comment.** `Palette.cs` holds no player-facing
  strings at all — only `Color` hex literals and comments. Its `BallStyles` sibling likewise draws
  glyphs, not words. Both files were left functionally untouched.
* **Level *ids*** — `corridor` still appears, but only in the F1 debug panel (`关卡 corridor`),
  where a stable handle is what a verifier wants. The player-facing top bar shows the Chinese level
  *name* from the level data (§2.6).
* **Units** `px`, `px/s`, `ms`, `us`, `s`, and the technical tokens `true`/`false`, `u`, ids.

---

## 3. How the glyphs were verified (not tofu)

A non-blank check is **not** sufficient: a screenshot full of `□` is non-blank. Four independent
checks were run; the first two are programmatic, the last two are the ones that actually rule out
tofu.

### 3.1 Glyph coverage in the font — `godot/assets/loc_font_probe.gd`

Harvests **every non-ASCII codepoint that appears anywhere in `godot/src/game/*.cs`** (so it is
derived from the real shipped sources, not from a hand-written list) and asks Godot's own
`Font.has_char()` — i.e. FreeType — whether the font covers each one.

```
tools\godot\godot_console.exe --headless --path godot --script res://assets/loc_font_probe.gd
```

Result (exit 0):

```
[loc-font] harvested 215 distinct non-ASCII codepoints from 10 file(s) in res://src/game
[loc-font] checking 215 codepoints
[loc-font] OK   res://assets/DroidSansFallback.ttf   Droid Sans Fallback  all 215 glyphs present
[loc-font] MISS C:/Program Files/.../NotoSansTC-Regular.otf  missing 48/215 [击关暂调时轨锁预数记开继续当释试残冻结]
[loc-font] VERDICT=COVERED res://assets/DroidSansFallback.ttf has every glyph the game can draw
```

Caveat, stated honestly: the harvest is deliberately **over-inclusive** — it also picks up
non-ASCII characters that only occur in code comments (a few mojibake bytes in pre-existing
comments, and `§`). Covering *more* than the UI needs cannot cause a false pass, so this is safe
for the claim it supports. The claim it supports is exactly: *no character in the shipped sources
can render as tofu with this font.*

### 3.2 Tofu test on the rendered pixels — `godot/evidence/loc_render_check.py`

Every tofu box in a run is the **same bitmap**. Real Chinese is a run of *different* bitmaps. The
checker isolates a text band, finds its connected components, and counts how many distinct component
bitmaps they collapse to:

```
python godot\evidence\loc_render_check.py
```

| Capture | Band | components (≥5 px) | distinct bitmaps | verdict |
|---|---|---|---|---|
| `loc_aim.png` | top-bar chips | 48 | 48 | REAL-GLYPHS |
| `loc_aim.png` | left panel STRIKE block | 22 | 22 | REAL-GLYPHS |
| `loc_aim.png` | left panel CONTROLS block | 56 | 55 | REAL-GLYPHS |
| `loc_aim.png` | top-bar right readout | 16 | 16 | REAL-GLYPHS |
| `loc_debug.png` | debug overlay block | 257 | 230 | REAL-GLYPHS |
| `loc_rewind.png` | message band | 22 | 22 | REAL-GLYPHS |
| `loc_win.png` | win banner text | 42 | 42 | REAL-GLYPHS |

Fifty-odd different glyph shapes in one small band, each distinct, is the signature of real text.
A tofu band produces *one* distinct bitmap repeated.

### 3.3 Eyes on cropped regions

The HUD text regions were cropped out of the PNGs and read directly. Legible, correct Simplified
Chinese was read back from every one of them, for example:

* top bar: `回溯 | 一杆清台 · 演示关（L1：中库回程）  |目标球 2 / 2  |击球 可击球（每关一杆）  |时间 正放`
* top-bar right readout (was clipped, now complete): `t=3.12s  步=374  回溯=1  轨迹=779`
* power meter: `力度 1258 px/s`, `最小`, `最大`, `角度 7.0°  拖动 170 / 170 px`
* controls: `拖动后松开/瞄准 / 击球`, `R（按住）/回溯`, `P/暂停 + 微调`, `← / →/移动释放点`,
  `空格/重置（调试）`, `F1 / F2 / F3/调试 / 残影 / 轨迹`
* debug panel: `调试面板（F1）`, `非有限值 否`, `历史 539 条 间隔 2.00 px`, `快照 450 点 游标 358`,
  `球 0 … 运动（白球）`, `帧耗时 15.79 ms（峰值 86.83）`
* win banner: `清台成功` / `4/4 颗目标球 · 用时 0.01s` / `回溯 0 次  距离 0 px  轨迹 1 点`
* on the felt: `速度 1258 px/s 接触 400 ms`, `起点 2.6s`, `命中 …`, `释放`, `第1段`, `回溯中`

### 3.4 Non-blank check for the record

`godot/src/ui/verify_png.gd` was read for its idiom (mode colour as the background, per-channel
tolerance 6, `distinct_rgb >= 8`, `content >= 2%`). **It was launched but is impractically slow
here** — a GDScript per-pixel loop over a 582,400-pixel image had not finished after 10 minutes for
a *single* frame, so the runs were cancelled rather than left to block for over an hour. The
identical arithmetic was therefore done in numpy inside `loc_render_check.py`, with the same two
metrics and the same thresholds, and it reproduces the same modal background the checker documents
(`#14402f` = `Palette.Felt`):

| Capture | distinct_rgb | modal bg | content | verdict |
|---|---|---|---|---|
| `loc_aim.png` | 4180 | `#14402f` (56.8%) | 43.14% | NONBLANK |
| `loc_strike.png` | 5756 | `#14402f` (55.8%) | 44.06% | NONBLANK |
| `loc_rewind.png` | 7364 | `#14402f` (55.5%) | 44.39% | NONBLANK |
| `loc_rewind-contact.png` | 6437 | `#14402f` (55.5%) | 44.42% | NONBLANK |
| `loc_scrub.png` | 7653 | `#14402f` (55.4%) | 44.43% | NONBLANK |
| `loc_debug.png` | 7306 | `#14402f` (34.6%) | 65.37% | NONBLANK |
| `loc_win.png` | 3824 | `#14402f` (44.9%) | 55.08% | NONBLANK |

Recorded because the deliverable format asks for it, **not** because it proves anything about CJK —
§3.1–§3.3 are the checks that do. To be explicit about the limit: this table would read exactly the
same if every string had rendered as `□`.

---

## 4. Screenshots

All 1040×560, captured with the mandated recipe (note `--rendering-driver d3d12`; Vulkan is broken
on this machine and the fallback is silent):

```
tools\godot\godot_console.exe --path godot --resolution 1040x560 --rendering-driver d3d12 \
    res://scenes/play.tscn -- --out=<abs>.png --state=<state>
```

| File | State | What it shows in Chinese |
|---|---|---|
| `godot/evidence/loc_aim.png` | `aim` | top bar, chips, power meter, aim prediction label `速度 … 接触 … ms`, CONTROLS block |
| `godot/evidence/loc_strike.png` | `strike` | locked strike `已击出 · 永久锁定`, `剩余操作：仅可回溯` |
| `godot/evidence/loc_rewind.png` | `rewind` | `回溯中`, `起点 2.6s`, `松开 R  继续`, full message box |
| `godot/evidence/loc_rewind-contact.png` | `rewind-contact` | contact forecast `命中` |
| `godot/evidence/loc_scrub.png` | `scrub` | `P  在标记处松开`, `释放` marker, `第…段` session label |
| `godot/evidence/loc_debug.png` | `debug` | the whole F1 debug overlay in Chinese |
| `godot/evidence/loc_win.png` | `win` | `清台成功` banner + both stat lines |

### Verification provenance

The captures above are from the **current** sources, not an earlier revision. `Play.cs` was changed
under me twice by the Lead (the main-loop fixes at 14:45/14:47), so the whole battery — capture,
clip measurement, tofu test — was re-run after those commits rather than reported from the earlier
frames. Before each capture round the built assembly's timestamp was compared against the newest
`.cs` in `godot/src/game/` to rule out the stale-assembly trap this project has hit repeatedly
(final state: assembly 14:54:07 > newest source 14:45:13; build rc 0, 0 warnings, 0 errors).

---

## 5. Overflow / clipping found, and what was done

### 5.1 Fixed: the top-bar right readout was being cut off (pre-existing, not caused by Chinese)

`Hud` draws in the Play node's **local** coordinates, but `Play` sets `Scale = 1.045` while the
canvas is a fixed 1040 px wide. A readout right-aligned to local `x = 1030` is therefore rasterised
at physical `x ≈ 1076` and silently loses its last field.

Measured, not guessed — rightmost ink column inside the top bar, canvas is 1040 wide:

| | before | after |
|---|---|---|
| English build (`evidence/m2_6_debug_overlay.png`) | **1039 — touching the edge**, `trail=1` rendered as `tra` | — |
| Chinese, before fix (`loc_*.png`, first capture) | **1039 — touching the edge**, `轨迹=1` entirely off-screen | — |
| Chinese, after fix (current `loc_*.png`) | — | 1025 / 1027 — **clear**, 13 px margin |

Fix: `Hud.VisibleWidth(ci)` converts the physical edge back through the CanvasItem's global
transform, and the readout is anchored to that. The English build had the identical bug, so this is
a genuine (and now fixed) defect rather than a localization regression.
`godot/evidence/loc_render_check.py` reports this as a hard number, and it is the check that caught it.

### 5.2 Verified OK: the narrow left panel

The left panel is 246 px wide and was the stated overflow risk. Checked at pixel level — no ink in
the dead strip left of the panel (`x 0..4` = 0 ink pixels in every capture), and every Chinese label
is shorter than the English it replaced in the same box:

| Block | English width | Chinese width |
|---|---|---|
| `USED - LOCKED FOREVER` → `已击出 · 永久锁定` | clearly wider | narrower, fits 14 px box |
| `drag back from the cue ball` → `从白球向后拖动` | ~150 px @11px | ~88 px @11px |
| `turn-forwards` → `回正` | 13 chars in a 108 px column | 2 glyphs |
| `F1 debug F2 ghost F3 trail` → `F1 / F2 / F3 ┃ 调试 / 残影 / 轨迹` | 27 chars | fits the two-column layout |

### 5.3 Found but NOT changed (out of this task's remit — reported, not touched)

* **The message box's bottom border is clipped.** `Rect2(262, 494, 700, 50)` is drawn after the
  1.045 scale, so its bottom edge lands at physical y ≈ 568 on a 560 px canvas. The **text is fully
  visible** (baseline ≈ y 549); only the box's bottom border is cut. Pre-existing, identical in
  English, and fixing it means moving a HUD block and updating `PanelLayoutSelfCheck`'s hard-coded
  numbers — a layout change, not a localization change, so it was left alone.
* **On-table labels overlap in the scrub corner.** At the scrub state the release marker, the
  session stripe and the cue ball all land in the same corner, so `释放` / `第1段` / `回溯中`
  overlap each other and the ball. **This is pre-existing geometry, and Chinese made it no worse:**
  the English control (`evidence/m2_5_scrub.png`, same crop) shows `REWINDING` / `RELEASE` / `S2`
  overlapping in exactly the same place, and the English labels are *wider* (`REWINDING` ≈ 90 px and
  `RELEASE` ≈ 55 px versus `回溯中` ≈ 36 px and `释放` ≈ 24 px). `第1段` is wider than `S2`
  (≈ 33 px vs ≈ 13 px), which is the only regression and it was a deliberate clarity trade the Lead
  signed off on. The English labels also overflowed the right edge; the Chinese ones no longer do.
* **Pre-existing mojibake in code comments** (`godot/src/game/Palette.cs:8`, `AimPredictor.cs:10`
  contain mis-decoded `§4.5` / `瞄准预测` bytes). Comments only, no rendering impact, not touched.

---

## 6. UNVERIFIED / BLOCKED

Stated plainly rather than glossed:

1. **Level content is not owned by this task, and it moved under me.** `godot/levels/corridor.json`
   was replaced mid-task by `m2-levels` (4 object balls → 2, budget `8 / 2400 px` → `1 / unlimited`
   → back to `8 / 2400 px`, and the `name` field became Chinese). The title is now displayed (§2.6)
   and the screenshots reflect the current file, but **any future level name, hint or subtitle added
   to level data will appear in English unless someone adds it in Chinese** — the HUD only renders
   what the data provides. Non-Chinese *new* strings introduced by other teammates after this pass
   are outside what could be verified here.
2. **No human native-speaker review.** The translations are natural-register game Chinese and were
   chosen for tone (e.g. `可击球（每关仅一杆）`, `已击出 · 永久锁定`, `剩余操作：仅可回溯`), but
   "reads naturally to a Chinese player" is not something a screenshot can prove. Two choices are
   worth a native check: `正放` for `TIME FORWARD` (compact, slightly technical) and `回正` for
   `turn-forwards` (the counterpart of `折返`; a player may need one session to learn it).
3. **Only the `corridor` level was captured.** Every other level file shares the same HUD code, and
   the font coverage check is per-codepoint so it is level-independent — but per-level screenshots
   were not taken. If a level ever supplies its own on-screen text, that text is not covered above.
4. **Not checked: font rendering at other DPI / window sizes, and in an exported build.** All
   evidence is a 1040×560 dev-run capture. The `ResourceLoader` branch of `LoadUiFont()` is the
   branch an export would take; the direct `LoadDynamicFont` fallback was exercised only implicitly
   (the `.ttf.import` file exists, so the primary branch is the one that ran here). **An export smoke
   test was not run** — `run_gates.ps1` and heavier regression were explicitly suspended for this
   fast-iteration phase by the user, so compile + screenshot was the agreed check.
5. **`run_gates.ps1` was NOT run** (per the task's hard rule). The C# build was verified the same way
   the gate script does it (`--headless --import` then `dotnet build … -c Debug --nologo`): **rc 0,
   0 warnings, 0 errors**, with the built assembly confirmed newer than the last source edit so that
   the stale-assembly trap cannot apply. The kernel test suites were not executed.
6. **One transient team-wide blocker, now cleared.** Mid-task, `godot/tests/InvariantTests.cs`
   (another teammate's scope) failed to compile — 4 errors around LINQ over `corridor.Balls` — which
   silently froze every teammate's assembly at the 14:10 build. It was reported to the Lead and to
   `m2-levels` with the raw compiler output and has since been fixed on their side (they replaced the
   LINQ with explicit loops). The build is green and every piece of evidence above comes from an
   assembly built from all the changes in this document — the assembly timestamp was checked against
   the last source edit before each capture round, precisely because this project has hit the
   stale-assembly trap repeatedly.
7. **`src/ui/verify_png.gd` was not run to completion** (see §3.4) — it is too slow on this machine
   to be usable per-screenshot. Its metric was replicated in numpy instead, with the same thresholds
   and the same modal-background idiom read out of the checker's own source. If a maintainer wants
   the original script's verdict on these files as well, it needs to run for roughly an hour per
   image batch; nothing in this task's conclusions depends on it, since a tofu-filled frame would
   also pass it.
8. **Concurrent writers.** Two other teammates committed to this tree while this pass was running
   (`m2-levels` on `godot/levels/**` and `godot/tests/**`, the Lead on `godot/src/game/Play.cs`).
   Anything they changed in `godot/src/game/**` *after* the final capture round above would not be
   reflected in these screenshots. The re-verification in §4 was run at the end of this task and was
   clean; a later edit by someone else to a string in `Play.cs`/`Hud.cs` would need a fresh capture
   to be covered by this evidence.
