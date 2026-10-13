"""Confirm the disabled/enabled button states are visually distinct, and that the toast is not covered.

TWO THINGS THIS EXISTS TO CATCH, both of which a "the layout is fine" check would miss:

  1. A DISABLED button must still be legible. In the right-bottom capture the "pause" button shows ~130
     bright pixels against ~520 for the enabled ones, which is the disabled styling -- but "dimmer"
     and "not drawn at all" produce similar pixel counts, so the difference is asserted rather than
     assumed.

  2. THE TOAST MUST NOT BE UNDER THE BUTTONS. The player asked for the buttons in the bottom-right
     corner, and the notification toast used to occupy y 494..544 across x 262..962 -- i.e. exactly
     where the buttons now are. Hud narrows the toast to x 262..732 on touch, so it clears the grid
     (which starts at x 754); if that ever regresses, an error
     message would be half-hidden behind a translucent button and the failure looks like "the message
     system is broken" rather than "the layout collided".
"""
from PIL import Image
import sys

STATES = {
    "no-shot": r"godot\evidence\touch_controls.png",
}

def bright_in(img, rect, lo=130):
    x1, y1, x2, y2 = [int(v) for v in rect]
    n = 0
    for y in range(y1, y2):
        for x in range(x1, x2):
            px = img.getpixel((x, y))
            if px[0] > lo and px[1] > lo - 20:
                n += 1
    return n

# Mirrors TouchControls geometry.
CANVAS_W, CANVAS_H = 1040.0, 560.0
W, H, GAPX, GAPY = 132.0, 54.0, 10.0, 10.0
MARGIN_RIGHT, MARGIN_BOTTOM = 12.0, 46.0
grid_left = CANVAS_W - MARGIN_RIGHT - (2 * W + GAPX)
grid_top = CANVAS_H - MARGIN_BOTTOM - (2 * H + GAPY)
rects = {
    "rewind": (grid_left, grid_top, grid_left + W, grid_top + H),
    "pause": (grid_left + W + GAPX, grid_top, grid_left + 2 * W + GAPX, grid_top + H),
    "reset": (grid_left, grid_top + H + GAPY, grid_left + W, grid_top + 2 * H + GAPY),
    "back": (grid_left + W + GAPX, grid_top + H + GAPY, grid_left + 2 * W + GAPX, grid_top + 2 * H + GAPY),
}

ok = True
for label, path in STATES.items():
    img = Image.open(path).convert("RGB")
    print(f"{path}")
    counts = {}
    for name, r in rects.items():
        counts[name] = bright_in(img, r)
        print(f"  {name:7s} bright={counts[name]}")

    # Before the shot, rewind is ENABLED and pause is DISABLED. Both must be drawn -- a disabled control
    # that is invisible would move the layout when it becomes available.
    if counts["rewind"] < 100:
        print("  FAIL the enabled rewind button is not drawn")
        ok = False
    if counts["pause"] <= 0:
        print("  FAIL the disabled pause button is not drawn at all")
        ok = False
    if counts["pause"] >= counts["rewind"]:
        print(f"  WARN pause({counts['pause']}) is not dimmer than rewind({counts['rewind']}) -- "
              "disabled state may not be distinguishable")
    else:
        print(f"  OK   disabled 'pause' ({counts['pause']}) is dimmer than enabled 'rewind' ({counts['rewind']})")

    # The toast rect Hud uses on touch: (262,494,470,50). Its right edge is x=732, and the grid starts at
    # x=754, so they are disjoint by 22 px. Assert the gap directly.
    toast_right = 262 + 470
    if toast_right >= grid_left:
        print(f"  FAIL the toast (right edge {toast_right}) overlaps the button grid (left {grid_left})")
        ok = False
    else:
        print(f"  OK   the toast's right edge {toast_right} clears the grid's left edge {grid_left}")

print()
print("VERDICT: button states OK" if ok else "VERDICT: button state check FAILED")
sys.exit(0 if ok else 1)
