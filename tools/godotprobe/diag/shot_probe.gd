extends Node2D

# Quick de-risk: does a NON-headless run render and produce a real PNG here?
# Usage: godot_console.exe --path <proj> --resolution 1040x560 res://diag/shot_probe.gd

var _frames := 0
var _out := "user://shot_probe.png"

func _ready() -> void:
	print("[shot] display_driver=", DisplayServer.get_name())
	print("[shot] rendering_driver=", RenderingServer.get_video_adapter_name())
	print("[shot] window_size=", DisplayServer.window_get_size())
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--out="):
			_out = a.substr("--out=".length())
	print("[shot] out=", _out, " abs=", ProjectSettings.globalize_path(_out))
	queue_redraw()

func _draw() -> void:
	# Deliberately colourful + patterned so a blank capture is obvious.
	draw_rect(Rect2(0, 0, 1040, 560), Color(0.07, 0.09, 0.14))
	for i in 13:
		draw_circle(Vector2(70 + i * 75, 130), 30.0, Color.from_hsv(i / 13.0, 0.85, 0.95), true, -1.0, true)
	# AA polyline trail (the billiards aim/trail use-case)
	var pts := PackedVector2Array()
	for i in 200:
		var t := i / 199.0
		pts.append(Vector2(30 + t * 980, 300 + sin(t * 9.0) * 70))
	draw_polyline(pts, Color(0.35, 0.9, 1.0), 4.0, true)
	draw_string(ThemeDB.fallback_font, Vector2(32, 500), "GODOT 4.7 RENDER PROBE", HORIZONTAL_ALIGNMENT_LEFT, -1, 34, Color(1, 1, 1))
	draw_string(ThemeDB.fallback_font, Vector2(32, 535), Time.get_datetime_string_from_system(), HORIZONTAL_ALIGNMENT_LEFT, -1, 18, Color(0.8, 0.85, 0.95))

func _process(_delta: float) -> void:
	_frames += 1
	if _frames < 6:
		return
	var img := get_viewport().get_texture().get_image()
	if img == null:
		print("[shot] FAILED: viewport texture image is null")
		get_tree().quit(2)
		return
	print("[shot] image=", img.get_width(), "x", img.get_height(), " format=", img.get_format())
	var err := img.save_png(_out)
	print("[shot] save_png err=", err)
	get_tree().quit(0 if err == OK else 3)
