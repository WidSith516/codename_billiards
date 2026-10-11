extends SceneTree

# Blank-frame detector. Loads a PNG and proves whether it contains real drawn content.
# Usage: godot_console.exe --headless --path <proj> --script res://diag/verify_png.gd -- <abs.png> [minDistinct]
# Exit code: 0 = non-blank (content proven), 4 = blank/uniform, 5 = load failed.
#
# Why this exists: a screenshot that "saved successfully" proves nothing. A capture taken
# against a dummy/absent rendering device is a valid PNG of a blank frame - the file
# exists, has the right dimensions, and is worthless. This makes blankness a hard failure.

func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.is_empty():
		print("[verify] usage: -- <abs.png> [minDistinctColors]")
		quit(2)
		return

	var path: String = args[0]
	var min_distinct := 8
	if args.size() > 1:
		min_distinct = int(args[1])

	var img := Image.load_from_file(path)
	if img == null:
		print("[verify] FAIL load_from_file returned null: ", path)
		quit(5)
		return

	var w := img.get_width()
	var h := img.get_height()
	print("[verify] file=", path)
	print("[verify] size=", w, "x", h, " format=", img.get_format())

	# Histogram of RGB triples, plus a "differs from top-left pixel" count.
	var hist := {}
	var bg := img.get_pixel(0, 0)
	var non_bg := 0
	var total := w * h
	for y in h:
		for x in w:
			var c := img.get_pixel(x, y)
			var key := (int(c.r * 255.0) << 16) | (int(c.g * 255.0) << 8) | int(c.b * 255.0)
			hist[key] = hist.get(key, 0) + 1
			if absf(c.r - bg.r) > 0.02 or absf(c.g - bg.g) > 0.02 or absf(c.b - bg.b) > 0.02:
				non_bg += 1

	var distinct := hist.size()
	var non_bg_ratio := float(non_bg) / float(total)
	print("[verify] distinct_rgb=", distinct)
	print("[verify] background(top-left)=", bg)
	print("[verify] non_background_px=", non_bg, " (", "%.2f%%" % (non_bg_ratio * 100.0), ")")

	# Top 6 colors by pixel count, so the report can quote what was actually drawn.
	var pairs := []
	for k in hist.keys():
		pairs.append([k, hist[k]])
	pairs.sort_custom(func(a, b): return a[1] > b[1])
	print("[verify] top_colors:")
	for i in mini(6, pairs.size()):
		var k: int = pairs[i][0]
		print("    #%02x%02x%02x  %d px (%.2f%%)" % [(k >> 16) & 255, (k >> 8) & 255, k & 255, pairs[i][1], 100.0 * float(pairs[i][1]) / float(total)])

	var blank := distinct < min_distinct or non_bg_ratio < 0.005
	if blank:
		print("[verify] VERDICT=BLANK (distinct=", distinct, " < ", min_distinct, " or non_bg<0.5%)")
		quit(4)
	else:
		print("[verify] VERDICT=NONBLANK (distinct=", distinct, ", non_bg=", "%.2f%%" % (non_bg_ratio * 100.0), ")")
		quit(0)
