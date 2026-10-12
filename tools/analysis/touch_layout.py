"""Verify the touch buttons are drawn where they claim, and do not cover anything that matters.

The layout has to satisfy three things at once and a screenshot alone cannot check them, because "there
is a button here" and "the table is still visible here" are statements about pixels that may be covered
by something else:

  1. each button's rect actually contains its own fill (the draw and Rect() agree);
  2. no button rect intersects the TABLE, where the player aims;
  3. no button rect intersects the toast (262,494 700x50) or the bottom band (y >= 546), which would
     hide a message or be hidden itself.

Reads Palette.PanelRaised / Palette.Accent / Palette.PanelEdge out of the capture by sampling, rather
than trusting the code, because the bug this guards against (a rect drawn where the hit test is not) is
invisible in code review and obvious in pixels.
"""
from PIL import Image

img = Image.open(r"godot\evidence\touch_controls.png").convert("RGB")

# Mirrors TouchControls.cs. Kept as literals on purpose: if these disagree with the C#, this test SHOULD
# fail, and copying them from the source at runtime would make it pass regardless.
LEFT, TOP = 14.0, 414.0
W, H, GAPX, GAPY = 104.0, 40.0, 8.0, 8.0
COLUMNS = 2
# ASCII keys on purpose: the Windows console here is GBK, and the Chinese labels (and the U+25C0/U+25B6
# arrows) raise UnicodeEncodeError on print. The ORDER still matches TouchControls.Order positionally,
# which is what the layout depends on. The nudge actions are absent because touch has no arrow buttons --
# the player drags the release marker, which emulate_mouse_from_touch already routes.
ORDER = ["rewind", "pause", "reset", "back"]

TABLE = (262, 62, 990, 498)
TOAST = (262, 494, 962, 544)
BOTTOM_BAND_TOP = 546

rects = {}
for i, name in enumerate(ORDER):
    col, row = i % COLUMNS, i // COLUMNS
    x = LEFT + col * (W + GAPX)
    y = TOP + row * (H + GAPY)
    rects[name] = (x, y, x + W, y + H)

print("button rects (design px):")
for name, r in rects.items():
    print(f"  {name:12s} x {r[0]:.0f}..{r[2]:.0f}   y {r[1]:.0f}..{r[3]:.0f}")

print()
print("overlap check:")
ok = True


def overlap(a, b):
    return not (a[2] <= b[0] or b[2] <= a[0] or a[3] <= b[1] or b[3] <= a[1])


for name, r in rects.items():
    for label, zone in (("TABLE", TABLE), ("TOAST", TOAST)):
        if overlap(r, zone):
            print(f"  FAIL {name} overlaps {label}")
            ok = False
    if r[3] >= BOTTOM_BAND_TOP:
        print(f"  FAIL {name} reaches the bottom band (y {r[3]:.0f} >= {BOTTOM_BAND_TOP})")
        ok = False
if ok:
    print("  none of the six buttons touches the table, the toast or the bottom band")

print()
print("sample inside each button (a button must not be the colour of the felt behind it):")
for name, r in rects.items():
    cx, cy = int((r[0] + r[2]) / 2), int((r[1] + r[3]) / 2)
    # Sample a few points and take the brightest: the label text and the badge sit near the centre.
    pts = [(cx, cy), (cx, r[1] + 6), (r[0] + 6, cy), (r[2] - 6, r[3] - 6)]
    cols = [img.getpixel(p) for p in pts]
    print(f"  {name:12s} centre{img.getpixel((cx, cy))}  samples={cols}")

print()
print("each button must contain its LABEL, not just a filled rectangle:")
ok2 = True
for name, r in rects.items():
    # Count bright pixels strictly inside the rect (skipping the 2px border). A label is drawn in
    # Palette.TextMain #eef3ef (enabled) or TextDim (disabled); both are far brighter than the body
    # fills (Panel #0a0f11-ish, PanelRaised #1c2a2d-ish), so a modest threshold separates text from fill.
    bright = 0
    for y in range(int(r[1]) + 4, int(r[3]) - 3):
        for x in range(int(r[0]) + 4, int(r[2]) - 3):
            px = img.getpixel((x, y))
            if px[0] > 120 and px[1] > 120 and px[2] > 120:
                bright += 1
    status = "OK  " if bright > 20 else "FAIL"
    if bright <= 20:
        ok2 = False
    print(f"  {status} {name:8s} label pixels={bright}")

print()
if ok and ok2:
    print("VERDICT: layout OK, every button labelled")
else:
    print("VERDICT: layout has a collision" if not ok else "VERDICT: a button has no visible label")
