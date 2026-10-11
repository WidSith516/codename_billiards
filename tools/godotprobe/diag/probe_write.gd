extends SceneTree

# Diagnostic-only GDScript probe: can the Godot PROCESS create directories/files?
# Run: godot_console.exe --headless --path <proj> --script res://probe_write.gd

func _initialize() -> void:
	var targets := [
		"D:/codename/codename_billiards/tools/godotprobe/.wtest_abs",
		"res://.wtest_res",
		"user://wtest_user",
	]
	for t in targets:
		var err := DirAccess.make_dir_recursive_absolute(t)
		print("[wtest] mkdir_absolute ", t, " -> err=", err)

	# file write via absolute path
	var ap := "D:/codename/codename_billiards/tools/godotprobe/.wtest_abs/hello.txt"
	var f := FileAccess.open(ap, FileAccess.WRITE)
	if f == null:
		print("[wtest] abs file write FAILED err=", FileAccess.get_open_error())
	else:
		f.store_string("godot wrote this\n")
		f.close()
		print("[wtest] abs file write OK")

	# file write via res://
	var fr := FileAccess.open("res://.wtest_res/hello.txt", FileAccess.WRITE)
	print("[wtest] res file write -> ", "OK" if fr != null else "FAILED err=%d" % FileAccess.get_open_error())

	print("[wtest] res:// resolves to: ", ProjectSettings.globalize_path("res://"))
	print("[wtest] user:// resolves to: ", ProjectSettings.globalize_path("user://"))
	print("[wtest] OS.get_user_data_dir(): ", OS.get_user_data_dir())
	print("[wtest] temp dir: ", OS.get_temp_dir())
	print("[wtest] cwd: ", OS.get_executable_path())
	quit(0)
