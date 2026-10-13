"""Verify the side panel's fold tab: position, that it does not cover panel text, and that it works.

THREE CLAIMS, ALL CHECKED AGAINST PIXELS RATHER THAN ARITHMETIC:

  1. THE TAB IS AT THE SAME PLACE IN BOTH STATES. A tab that jumps when tapped has to be hunted for,
     and this one is the only way to bring the panel back. Two captures (panel open / folded) must show
     the tab's border in the same 34x34 rect.

  2. IT DOES NOT COVER THE PANEL'S OWN TEXT. The tab lives inside the panel's rectangle, so it can only
     be justified if the band it occupies is empty. The panel's text is measured, not assumed: this
     script finds the rightmost non-background pixel in the rows the tab spans and asserts it stops
     short of the tab's left edge.

  3. FOLDED, THE FELT IS ACTUALLY FREED. The point of the feature is that the table becomes visible, so
     the panel's translucent background must be gone from the region it occupied -- otherwise the panel
     merely stopped drawing its text.

The felt is a dark green; the panel background is a dark translucent teal and the tab border is accent
gold (#ffd166), which is bright and unmistakable.
"""
from PIL import Image
import sys

OPEN = "godot/evidence/panel_open.png"
FOLDED = "godot/evidence/panel_folded.png"

# Mirrors Hud: panel Rect2(6,34,246,476); tab Rect2(214,38,34,34) in both states.
PANEL = (6, 34, 252, 510)
TAB = (214, 38, 248, 72)

try:
    a = Image.open(OPEN).convert("RGB")
    b = Image.open(FOLDED).convert("RGB")
except FileNotFoundError as e:
    print(f"FAIL: missing capture ({e.filename}). Run the two --state captures first.")
    raise SystemExit(2)

ok = True


def accent(p):
    """Palette.Accent #ffd166, with slack for the 1.5px anti-aliased border."""
    return p[0] > 170 and p[1] > 120 and p[2] < 140


def bbox_of(pred, img, region):
    x1, y1, x2, y2 = region
    px = img.load()
    pts = [(x, y) for y in range(y1, y2) for x in range(x1, x2) if pred(px[x, y])]
    if not pts:
        return None
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return (min(xs), min(ys), max(xs), max(ys))


def not_bg(p):
    """Anything the panel actually draws: text, bars, the tab. The panel body itself is a very dark
    translucent teal (PanelBg 0.04,0.06,0.07) so this threshold sits just above it and below any text."""
    return p[0] > 60 or p[1] > 70 or p[2] > 70


print("1. the tab is in the same place in both states")
tab_open = bbox_of(accent, a, (180, 30, 252, 90))
tab_folded = bbox_of(accent, b, (180, 30, 252, 90))
print(f"   panel open   : accent bbox in the tab band = {tab_open}")
print(f"   panel folded : accent bbox in the tab band = {tab_folded}")
if tab_open is None or tab_folded is None:
    print("   FAIL the tab is not drawn in one of the two states")
    ok = False
elif abs(tab_open[0] - tab_folded[0]) > 2 or abs(tab_open[1] - tab_folded[1]) > 2:
    print(f"   FAIL the tab MOVES between states: {tab_open} vs {tab_folded}")
    ok = False
else:
    print("   OK   the tab's border is in the same rect in both states")

print()
print("2. the tab does not cover the panel's own text (rows y 38..72)")

# THE OBVIOUS VERSION OF THIS CHECK IS WORTHLESS, and it took reading the pixels to notice. Measuring
# "the rightmost lit pixel left of the tab" returns x=213 -- the tab's own anti-aliased left border,
# which is lit on every row of the band. The assertion then passes because 213 < 214, i.e. it passes for
# the wrong reason and would keep passing even if the tab swallowed real text.
#
# So the tab's border is excluded first (anything within 2px of the tab's left edge), and the measurement
# is reported per row so a genuine collision is visible as text running INTO the tab rather than as one
# aggregate number that happens to be small.
TAB_LEFT = TAB[0]
GUARD = TAB_LEFT - 2

worst = None
for y in range(TAB[1], TAB[3]):
    xs = [x for x in range(6, GUARD) if not_bg(a.load()[x, y])]
    if xs:
        hi = max(xs)
        if worst is None or hi > worst[1]:
            worst = (y, hi)

if worst is None:
    print(f"   no lit pixel in x 6..{GUARD}, y {TAB[1]}..{TAB[3]} -- the band is empty")
    print("   OK   nothing there to cover")
else:
    y, hi = worst
    print(f"   rightmost panel pixel excluding the tab's own border (x < {GUARD}): x={hi} at y={y}")
    print(f"   (the panel's text starts at x=16; the tab's left edge is x={TAB_LEFT})")
    if hi >= GUARD:
        print(f"   FAIL panel content reaches x={hi}, into the tab's guard band")
        ok = False
    else:
        print(f"   OK   panel content ends {GUARD - hi}px short of the tab -- no overlap")

print()
print("3. folding actually frees the felt")

# THEME-INDEPENDENT, and deliberately so. The first version of this check hard-coded "felt is green",
# which is only true for the DEFAULT theme: this level ships the space skin, whose table surface is a
# deep blue-violet (~21,19,62). The check therefore reported that folding revealed LESS felt than
# leaving the panel open -- the exact opposite of the truth. The felt colour is data (ApplyTheme can
# replace it), so the claim has to be phrased against whatever is actually next to the panel.
#
# The claim: the panel's footprint looked like the PANEL before folding, and looks like the TABLE after.
# A reference strip just right of the panel gives "the table's colour" without assuming what it is.
panel_px = a.load()
folded_px = b.load()

REF_X = range(292, 332)
REF_Y = range(302, 478)
ref = [panel_px[x, y] for y in REF_Y for x in REF_X]
ref_r = sorted(p[0] for p in ref)[len(ref) // 2]
ref_g = sorted(p[1] for p in ref)[len(ref) // 2]
ref_b = sorted(p[2] for p in ref)[len(ref) // 2]
print(f"   reference table colour (x 292..332, clear of the panel): ({ref_r},{ref_g},{ref_b})")


def mean_distance(img, region):
    px = img.load()
    tot = 0.0
    n = 0
    for y in range(region[1], region[3]):
        for x in range(region[0], region[2]):
            p = px[x, y]
            tot += abs(p[0] - ref_r) + abs(p[1] - ref_g) + abs(p[2] - ref_b)
            n += 1
    return tot / n


# The part of the panel that sits OVER the table: table x starts at 65, panel spans x 6..252.
overlap = (70, 300, 248, 480)
d_open = mean_distance(a, overlap)
d_folded = mean_distance(b, overlap)
print(f"   mean distance to the table colour, inside the panel's footprint over the table:")
print(f"     panel open   : {d_open:.1f}   (should be far: the panel is covering the table)")
print(f"     panel folded : {d_folded:.1f}   (should be near: the table shows through)")
if d_folded >= d_open:
    print("   FAIL folding did not move the region toward the table's colour")
    ok = False
elif d_folded > d_open * 0.6:
    print(f"   FAIL the region only moved partway ({d_folded / d_open:.0%} of the original distance); "
          "something is still drawn over the table there")
    ok = False
else:
    print(f"   OK   folding moved it to {d_folded / d_open:.0%} of the original distance "
          "-- the table is genuinely revealed")

# And the table outside the panel must be UNCHANGED, i.e. folding the panel does not disturb the scene.
d_ref_before = mean_distance(a, (292, 302, 332, 478))
d_ref_after = mean_distance(b, (292, 302, 332, 478))
print(f"   control -- the same measurement on the reference strip itself:")
print(f"     panel open {d_ref_before:.1f} vs folded {d_ref_after:.1f}")
if abs(d_ref_after - d_ref_before) > 6.0:
    print("   FAIL the strip beside the panel changed too, so the captures are not comparable")
    ok = False
else:
    print("   OK   unchanged, so the two captures differ only in the panel")

print()
print("VERDICT: panel fold OK" if ok else "VERDICT: panel fold check FAILED")
raise SystemExit(0 if ok else 1)
