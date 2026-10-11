extends SceneTree

# Matrix diagnostic: where can the Godot process actually create a directory/file?

func _try_dir(label: String, path: String) -> void:
	var before := DirAccess.dir_exists_absolute(path)
	var err := DirAccess.make_dir_recursive_absolute(path)
	var after := DirAccess.dir_exists_absolute(path)
	print("[mx] DIR  %-22s %-58s err=%d exists_before=%s exists_after=%s" % [label, path, err, str(before), str(after)])

func _try_file(label: String, path: String) -> void:
	var f := FileAccess.open(path, FileAccess.WRITE)
	if f == null:
		print("[mx] FILE %-22s %-58s FAILED open_err=%d" % [label, path, FileAccess.get_open_error()])
	else:
		f.store_string("x")
		f.close()
		print("[mx] FILE %-22s %-58s OK exists=%s" % [label, path, str(FileAccess.file_exists(path))])

func _initialize() -> void:
	print("[mx] env USERNAME=", OS.get_environment("USERNAME"))
	print("[mx] env TEMP=", OS.get_environment("TEMP"))
	print("[mx] env LOCALAPPDATA=", OS.get_environment("LOCALAPPDATA"))
	print("[mx] OS.get_temp_dir()=", OS.get_temp_dir())
	print("[mx] OS.get_user_data_dir()=", OS.get_user_data_dir())
	print("[mx] globalize res:// =", ProjectSettings.globalize_path("res://"))

	_try_dir("workspace-probe", "D:/codename/codename_billiards/tools/godotprobe/.mx_probe")
	_try_dir("workspace-root", "D:/codename/codename_billiards/tools/godotprobe_mx")
	_try_dir("drive-d-root", "D:/_godot_mx_root")
	_try_dir("os-temp", OS.get_temp_dir().path_join("godot_mx_temp"))
	_try_dir("user-data", OS.get_user_data_dir().path_join("godot_mx_user"))
	_try_dir("saint-ruin-ref", "D:/saint_ruin/_godot_mx_ref")

	_try_file("workspace-probe", "D:/codename/codename_billiards/tools/godotprobe/.mx_probe/f.txt")
	_try_file("os-temp", OS.get_temp_dir().path_join("godot_mx_temp/f.txt"))
	_try_file("user-data", OS.get_user_data_dir().path_join("godot_mx_user/f.txt"))
	quit(0)
