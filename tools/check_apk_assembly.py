"""Assert the touch feature is really inside the APK's C# assembly.

WHY THIS IS NOT PARANOIA: an APK with a STALE assembly installs and launches perfectly -- it just plays
the previous build. Every content check that looks only at file names passes. The only way to know the
shipped binary contains the new code is to look for the new code's symbols in the assembly that is
actually inside the APK.

It is the exact counterpart of the earlier trap in this project: a .exe that was rebuilt from source
looked fine, but an unchanged result after an edit meant the DLL had never been rebuilt. Checking the
file list would not have caught that; looking for the symbol does.
"""
import zipfile
import sys

APK = sys.argv[1] if len(sys.argv) > 1 else r"dist\REWIND.apk"

z = zipfile.ZipFile(APK)
names = [n for n in z.namelist() if n.endswith("RewindKernel.dll")]
if not names:
    print(f"FAIL: no RewindKernel.dll inside {APK}")
    sys.exit(2)

entry = names[0]
blob = z.read(entry)
print(f"{APK}")
print(f"  assembly: {entry}")
print(f"  size:     {len(blob):,} bytes")
print()

# Symbols that must be present if this assembly is the build that contains the touch work.
#
# NOTE ON ENCODING: type and method NAMES appear as plain ASCII in the metadata, but string LITERALS
# ("--touch-controls") live in the #US heap as UTF-16LE. Matching only the ASCII form reported a false
# MISSING on the flag. So each entry is searched in both encodings.
must = [
    b"TouchControls",           # the new type
    b"OnTouchPress",            # the button press handler
    b"ShouldShow",              # the touchscreen-detection gate
    b"--touch-controls",        # the desktop override flag (a literal -> UTF-16 in the #US heap)
    b"T01_GridFitsThePanel",    # the layout regression test
    b"T05_EveryActionIsLabelled",
]
# A symbol that must NOT be present: the nudge actions were removed from the touch grid because touch
# players drag the release marker instead. If they are still here, the assembly is stale.
must_not = [b"NudgeForward", b"Action.NudgeBack"]


def present(blob: bytes, needle: bytes) -> bool:
    return needle in blob or needle.decode("ascii").encode("utf-16-le") in blob


ok = True
for needle in must:
    hit = present(blob, needle)
    if not hit:
        ok = False
    print(f"  {'OK     ' if hit else 'MISSING'} {needle.decode()}")
for needle in must_not:
    hit = present(blob, needle)
    if hit:
        ok = False
    print(f"  {'STALE  ' if hit else 'OK     '} {needle.decode()} absent")

print()
print("ASSEMBLY IS CURRENT" if ok else "ASSEMBLY CHECK FAILED")
sys.exit(0 if ok else 1)
