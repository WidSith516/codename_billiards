extends SceneTree

# Focused user:// writability proof (Lead priority #3).
# Run: godot_console.exe --headless --path <proj> --script res://diag/probe_user.gd

func _initialize() -> void:
	var dir := OS.get_user_data_dir()
	print("[user] OS.get_user_data_dir() = ", dir)
	print("[user] globalize user:// = ", ProjectSettings.globalize_path("user://"))

	# 1. subdirectory
	var sub := "user://savedata"
	var e1 := DirAccess.make_dir_recursive_absolute(sub)
	print("[user] mkdir user://savedata err=", e1, " exists=", DirAccess.dir_exists_absolute(sub))

	# 2. a real save payload via ConfigFile (what settings/star-ratings would use)
	var cfg := ConfigFile.new()
	cfg.set_value("progress", "stars", 17)
	cfg.set_value("progress", "cue_unlocked", true)
	cfg.set_value("settings", "master_volume", 0.8)
	var e2 := cfg.save("user://savedata/progress.cfg")
	print("[user] ConfigFile.save err=", e2, " exists=", FileAccess.file_exists("user://savedata/progress.cfg"))

	# 3. read it back - a save that cannot be reloaded is not a save
	var cfg2 := ConfigFile.new()
	var e3 := cfg2.load("user://savedata/progress.cfg")
	print("[user] ConfigFile.load err=", e3, " stars=", cfg2.get_value("progress", "stars", -1),
		" cue=", cfg2.get_value("progress", "cue_unlocked", null),
		" vol=", cfg2.get_value("settings", "master_volume", -1.0))

	# 4. plain JSON round-trip too
	var f := FileAccess.open("user://savedata/payload.json", FileAccess.WRITE)
	if f == null:
		print("[user] JSON write FAILED err=", FileAccess.get_open_error())
	else:
		f.store_string(JSON.stringify({"stars": [3, 2, 1]}))
		f.close()
		print("[user] JSON write OK exists=", FileAccess.file_exists("user://savedata/payload.json"))

	print("[user] RESULT=", "WRITABLE" if (e1 == OK and e2 == OK and e3 == OK) else "BROKEN")
	quit(0)
