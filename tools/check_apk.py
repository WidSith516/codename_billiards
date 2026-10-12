"""Assert what a shippable APK must contain, straight from the zip.

Reporting via aapt is unreliable here (it fails on this manifest's modern attributes), so the checks
that actually gate shipping read the archive directly. Three things must be true:

  * the arm64-v8a Godot native library is present -- the preset ships arm64 ONLY, so an APK without it
    installs on no real phone;
  * the C# assembly is present -- a missing assembly still yields a structurally valid APK and then
    dies at launch, which is the most expensive place to discover it;
  * launcher icon resources exist -- the export refuses without an icon, but that proves the preset
    pointed at one, not that it made it into the package.
"""
import sys
import zipfile

path = sys.argv[1] if len(sys.argv) > 1 else r"dist\REWIND.apk"
z = zipfile.ZipFile(path)
names = z.namelist()

print(f"{path}: {len(names)} entries, {__import__('os').path.getsize(path):,} bytes")

checks = [
    ("arm64 native lib", "lib/arm64-v8a/libgodot_android.so"),
    # Godot publishes the C# app into assets/.godot/mono/publish/<abi>/ and the assembly lands there,
    # NOT at assets/RewindKernel.dll. An earlier version of this check looked for the latter, reported
    # MISS on a complete APK, and briefly made a good build look broken -- so match on the entry
    # SUFFIX rather than a full path that a future engine version may relocate again.
    ("C# assembly", "mono/publish/arm64/RewindKernel.dll"),
    ("C# runtimeconfig", "mono/publish/arm64/RewindKernel.runtimeconfig.json"),
]
ok = True
for label, needle in checks:
    hit = [n for n in names if n.endswith(needle)]
    status = "OK  " if hit else "MISS"
    if not hit:
        ok = False
    print(f"  {status} {label:18s} {hit[0] if hit else needle}")

abis = sorted({n.split("/")[1] for n in names if n.startswith("lib/") and n.count("/") > 1})
print(f"  ABIs: {abis}")

icons = [n for n in names if "mipmap" in n and "icon" in n]
print(f"  launcher icon resources: {len(icons)}")

# The AndroidManifest is binary in a packaged APK; confirm it exists at all.
print(f"  AndroidManifest.xml: {'OK' if 'AndroidManifest.xml' in names else 'MISS'}")

if "arm64-v8a" not in abis:
    print("FAIL: no arm64-v8a native library -- this APK would not install on a real phone")
    ok = False
if not icons:
    print("FAIL: no launcher icon resources")
    ok = False

print("APK CONTENTS OK" if ok else "APK CONTENTS INCOMPLETE")
sys.exit(0 if ok else 1)
