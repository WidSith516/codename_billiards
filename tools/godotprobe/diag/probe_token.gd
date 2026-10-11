extends SceneTree

# Ask the OS what token Godot's own process tree carries.
# If the Godot process is confined (AppContainer / restricted token), this shows it.

func _initialize() -> void:
	var out := []
	var code := OS.execute("whoami", ["/all"], out, true)
	print("[tok] whoami /all exit=", code)
	for line in out:
		print("[tok] ", line)

	var out2 := []
	var code2 := OS.execute("cmd.exe", ["/c", "echo test > D:\\codename\\codename_billiards\\tools\\godotprobe\\.childwrite_test.txt && echo WROTE || echo NOWRITE"], out2, true)
	print("[tok] child-cmd write test exit=", code2)
	for line in out2:
		print("[tok] ", line)

	var out3 := []
	var code3 := OS.execute("cmd.exe", ["/c", "echo test > D:\\saint_ruin\\_childwrite_test.txt && echo WROTE || echo NOWRITE"], out3, true)
	print("[tok] child-cmd write to saint_ruin exit=", code3)
	for line in out3:
		print("[tok] ", line)

	quit(0)
