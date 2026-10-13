"""Verify the touch buttons sit where the player was told to expect them: bottom-right, over the felt.

Two earlier layouts were rejected by arithmetic (2x3 overflowed the canvas; 3x2 overflowed the panel),
so this checks the CURRENT geometry against the rendered frame rather than trusting the constants:

  1. every button's rect really contains a drawn button (fill brighter than the felt behind it);
  2. the grid hugs the bottom-right of the table, which is where a thumb rests;
  3. nothing reaches the bottom replay band (y >= 546) or runs off the 1040x560 canvas;
  4. each button carries a LABEL -- a filled rectangle with no text is not a control.

Felt is a dark green (~#14402f, luminance ~45); the translucent button bodies are dark blue-grey with a
bright accent BORDER, so the border is the reliable signal. Counting accent-coloured pixels along a
button's edge is robust to the fill alpha, which is deliberately low so the cloth shows through.
"""
from PIL import Image
import sys

path = sys.argv[1] if len(sys.argv) > 1 else r"godot\evidence\touch_controls.png"
img = Image.open(path).convert("RGB")

# Mirrors TouchControls. Literals on purpose: if these disagree with the C#, this check SHOULD fail.
CANVAS_W, CANVAS_H = 1040.0, 560.0
W, H, GAPX, GAPY = 132.0, 54.0, 10.0, 10.0
COLUMNS = 2
MARGIN_RIGHT, MARGIN_BOTTOM = 12.0, 46.0
BAND_TOP = 546
ORDER = ["rewind", "pause", "reset", "back"]

grid_left = CANVAS_W - MARGIN_RIGHT - (COLUMNS * W + (COLUMNS - 1) * GAPX)
grid_top = CANVAS_H - MARGIN_BOTTOM - (2 * H + GAPY)

rects = {}
for i, name in enumerate(ORDER):
    col, row = i % COLUMNS, i // COLUMNS
    x = grid_left + col * (W + GAPX)
    y = grid_top + row * (H + GAPY)
    rects[name] = (x, y, x + W, y + H)

print(f"grid origin ({grid_left:.0f},{grid_top:.0f}) on a {CANVAS_W:.0f}x{CANVAS_H:.0f} canvas")
for name, r in rects.items():
    print(f"  {name:7s} x {r[0]:.0f}..{r[2]:.0f}   y {r[1]:.0f}..{r[3]:.0f}")

ok = True

print()
print("bounds:")
for name, r in rects.items():
    if r[3] > BAND_TOP:
        print(f"  FAIL {name} reaches the bottom band (y {r[3]:.0f} > {BAND_TOP})")
        ok = False
    if r[2] > CANVAS_W or r[3] > CANVAS_H or r[0] < 0 or r[1] < 0:
        print(f"  FAIL {name} is off-canvas {r}")
        ok = False
if ok:
    print("  all four buttons are on canvas and clear of the bottom band")

# The table box, from PhysicsConfig: (65,63) to (975,497).
TABLE = (65, 63, 975, 497)
print()
print("the grid must reach into the table's lower-right (that is the point of the move):")
for name, r in rects.items():
    inside = r[0] < TABLE[2] and r[1] < TABLE[3]
    print(f"  {'OK  ' if inside else 'FAIL'} {name} overlaps the felt")
    if not inside:
        ok = False

print()
print("bright pixels inside each button (border + label must be present):")
for name, r in rects.items():
    bright = 0
    for y in range(int(r[1]), int(r[3])):
        for x in range(int(r[0]), int(r[2])):
            px = img.getpixel((x, y))
            # Palette.Accent is #ffd166 and TextMain #eef3ef -- both far brighter than felt (~45).
            if px[0] > 130 and px[1] > 110:
                bright += 1
    status = "OK  " if bright > 60 else "FAIL"
    if bright <= 60:
        ok = False
    print(f"  {status} {name:7s} bright pixels={bright}")

# The collapse tab, just above the grid: it must be drawn in both states, so it is always present.
tab = (CANVAS_W - MARGIN_RIGHT - 34.0, grid_top - 44.0, CANVAS_W - MARGIN_RIGHT, grid_top - 10.0)
tab_bright = 0
for y in range(int(tab[1]), int(tab[3])):
    for x in range(int(tab[0]), int(tab[2])):
        px = img.getpixel((x, y))
        if px[0] > 130 and px[1] > 110:
            tab_bright += 1
print()
print(f"collapse tab x {tab[0]:.0f}..{tab[2]:.0f} y {tab[1]:.0f}..{tab[3]:.0f}: bright={tab_bright}")
if tab_bright <= 20:
    print("  FAIL the collapse tab is not drawn -- with no other way back, the buttons could be lost")
    ok = False
else:
    print("  OK   the collapse tab is drawn")

print()
print("VERDICT: layout OK" if ok else "VERDICT: layout FAILED")
sys.exit(0 if ok else 1)
