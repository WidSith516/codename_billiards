extends SceneTree

# M2 non-blank checker. A saved PNG proves nothing on its own: a capture taken against a
# dummy rendering device is a valid PNG of a blank frame. This turns blankness into a hard,
# programmatic failure and prints the numbers the M2 report quotes.
#
# Usage:
#   tools/godot/godot_console.exe --headless --path godot \
#       res://src/ui/verify_png.gd -- <abs.png> [minDistinctColors] [--min-content=0.02]
#
# Exit codes: 0 = non-blank, 4 = blank, 5 = load failed, 2 = bad usage.
#
# Why the background is the MODE colour rather than the top-left pixel (the idiom used by
# tools/godotprobe/diag/verify_png.gd): Godot's viewport capture is not letterboxed here, but
# a screenshot whose top-left pixel happens to sit on a drawn element would report a
# falsely high "non-background" fraction. The mode colour is the felt in every M2 capture.

func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.is_empty():
		print("[verify] usage: -- <abs.png> [minDistinctColors] [--min-content=0.02]")
		quit(2)
		return

	var path: String = args[0]
	var min_distinct := 8
	var min_content := 0.02
	for i in range(1, args.size()):
		var a: String = args[i]
		if a.begins_with("--min-content="):
			min_content = float(a.substr(14))
		elif a.is_valid_int():
			min_distinct = int(a)

	var img := Image.load_from_file(path)
	if img == null:
		print("[verify] FAIL load_from_file returned null: ", path)
		quit(5)
		return
	if img.is_empty():
		print("[verify] FAIL image is empty: ", path)
		quit(5)
		return
	if img.get_format() != Image.FORMAT_RGBA8:
		img.convert(Image.FORMAT_RGBA8)

	var w := img.get_width()
	var h := img.get_height()
	print("[verify] file=", path)
	print("[verify] size=", w, "x", h, " format=", img.get_format())

	# Single pass: RGB histogram + per-quadrant content bookkeeping.
	var hist := {}
	var quad_content := [0, 0, 0, 0]
	var total := w * h
	var data := img.get_data()
	for y in h:
		var row := y * w * 4
		for x in w:
			var o := row + x * 4
			var r: int = data[o]
			var g: int = data[o + 1]
			var b: int = data[o + 2]
			var key := (r << 16) | (g << 8) | b
			hist[key] = hist.get(key, 0) + 1

	# Mode colour = the "background" (the felt).
	var bg_key := -1
	var bg_count := 0
	for k in hist.keys():
		if hist[k] > bg_count:
			bg_count = hist[k]
			bg_key = k
	var bg := Color8((bg_key >> 16) & 255, (bg_key >> 8) & 255, bg_key & 255)
	var bg_r := (bg_key >> 16) & 255
	var bg_g := (bg_key >> 8) & 255
	var bg_b := bg_key & 255

	# Second pass over the same buffer: content = NOT within tolerance of the mode colour.
	var content := 0
	var tolerance := 6
	for y in h:
		var row := y * w * 4
		for x in w:
			var o := row + x * 4
			var dr: int = data[o] - bg_r
			var dg: int = data[o + 1] - bg_g
			var db: int = data[o + 2] - bg_b
			if dr < 0: dr = -dr
			if dg < 0: dg = -dg
			if db < 0: db = -db
			if dr > tolerance or dg > tolerance or db > tolerance:
				content += 1
				var q := (0 if y < h / 2 else 2) + (0 if x < w / 2 else 1)
				quad_content[q] += 1

	var distinct := hist.size()
	var content_ratio := float(content) / float(total)
	print("[verify] distinct_rgb=", distinct)
	print("[verify] background_mode_color=", bg, " rgb=(", bg_r, ",", bg_g, ",", bg_b, ") px=", bg_count,
		" (", "%.2f%%" % (100.0 * float(bg_count) / float(total)), ")")
	print("[verify] content_px=", content, " (", "%.2f%%" % (content_ratio * 100.0), ")  tolerance=", tolerance)
	print("[verify] content_by_quadrant(TL,TR,BL,BR)=", quad_content)

	# Top 8 colours by pixel count: what was actually drawn.
	var pairs := []
	for k in hist.keys():
		pairs.append([k, hist[k]])
	pairs.sort_custom(func(a, b): return a[1] > b[1])
	print("[verify] top_colors:")
	for i in mini(8, pairs.size()):
		var k: int = pairs[i][0]
		print("    #%02x%02x%02x  %d px (%.2f%%)" % [(k >> 16) & 255, (k >> 8) & 255, k & 255, pairs[i][1], 100.0 * float(pairs[i][1]) / float(total)])

	var blank := distinct < min_distinct or content_ratio < min_content
	if blank:
		print("[verify] VERDICT=BLANK (distinct=", distinct, " minDistinct=", min_distinct,
			" content=", "%.2f%%" % (content_ratio * 100.0), " minContent=", "%.2f%%" % (min_content * 100.0), ")")
		quit(4)
	else:
		print("[verify] VERDICT=NONBLANK (distinct=", distinct, ", content=", "%.2f%%" % (content_ratio * 100.0), ")")
		quit(0)
