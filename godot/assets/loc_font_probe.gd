extends SceneTree

# CJK glyph-coverage probe for the Chinese localization.
#
# Why this file exists: a translated HUD drawn with a font that lacks CJK glyphs renders as
# tofu boxes (□□□).  A screenshot of tofu boxes is still "non-blank", so verify_png.gd cannot
# detect the failure.  This probe turns "does the font actually have these glyphs" into a hard,
# reproducible check over the REAL shipped source: it harvests every non-ASCII codepoint that
# appears anywhere in godot/src/game/*.cs (i.e. every character the localization can draw) and
# asks FreeType, through Godot's own FontFile.has_char(), whether the candidate font covers it.
#
# Usage:
#   tools/godot/godot_console.exe --headless --path godot \
#       --script res://assets/loc_font_probe.gd -- [extra sample text ...]
#
# Exit codes: 0 = the project font covers every codepoint, 6 = coverage hole, 5 = font load failed.

const ProjectFont := "res://assets/DroidSansFallback.ttf"
const SourceDir := "res://src/game"

# Windows system CJK fonts, kept as documented alternates.  Deliberately includes .ttc
# collections: they need a face index, which is one of the reasons the project font is a .ttf.
const Alternates := [
	"C:/Windows/Fonts/simhei.ttf",
	"C:/Windows/Fonts/Deng.ttf",
	"C:/Windows/Fonts/msyh.ttc",
	"C:/Windows/Fonts/simsun.ttc",
	"C:/Program Files/Epic Games/Launcher/Portal/Content/Font/NotoSansTC-Regular.otf",
]


func _initialize() -> void:
	var needed := {}

	# 1. Every non-ASCII codepoint in the sourcing .cs files (= every char the game can draw).
	var dir := DirAccess.open(SourceDir)
	if dir == null:
		print("[loc-font] FAIL cannot open ", SourceDir)
		quit(5)
		return
	var files := 0
	for f in dir.get_files():
		if not f.ends_with(".cs"):
			continue
		files += 1
		_harvest(FileAccess.get_file_as_string(SourceDir + "/" + f), needed)
	print("[loc-font] harvested ", needed.size(), " distinct non-ASCII codepoints from ", files, " file(s) in ", SourceDir)

	# 2. Explicit sample text: catches characters held in data (levels, docs) or typed on the CLI.
	for a in OS.get_cmdline_user_args():
		_harvest(a, needed)
	print("[loc-font] checking ", needed.size(), " codepoints")

	# 3. Report per candidate.
	var project_missing := -1
	for path in [ProjectFont] + Alternates:
		var res := _check(path, needed)
		print("[loc-font] ", res["line"])
		if path == ProjectFont:
			project_missing = res["missing"]

	var project_ok := project_missing == 0
	if project_ok:
		print("[loc-font] VERDICT=COVERED ", ProjectFont, " has every glyph the game can draw")
		quit(0)
	else:
		print("[loc-font] VERDICT=TOFU-RISK ", ProjectFont, " is missing glyphs; swap the font before shipping")
		quit(6)


func _harvest(text: String, into: Dictionary) -> void:
	for i in text.length():
		var c := text.unicode_at(i)
		if c > 0x7F:
			into[c] = true


func _check(path: String, needed: Dictionary) -> Dictionary:
	var ff := FontFile.new()
	var err := ff.load_dynamic_font(path)
	if err != OK:
		return {"missing": -1, "line": "ERR  %s  load_dynamic_font err=%d" % [path, err]}
	var missing := PackedInt32Array()
	for c in needed.keys():
		if not ff.has_char(c):
			missing.append(c)
	var sample := ""
	for i in mini(24, missing.size()):
		sample += char(missing[i])
	var note := ff.get_font_name()
	if missing.is_empty():
		return {"missing": 0, "line": "OK   %-46s %s  all %d glyphs present" % [path, note, needed.size()]}
	return {"missing": missing.size(),
		"line": "MISS %-46s %s  missing %d/%d [%s]" % [path, note, missing.size(), needed.size(), sample]}
