"""Verify the cue stick is drawn BEHIND the ball, opposite the shot direction.

WHY THE EARLIER ATTEMPTS FAILED: this check went through three wrong versions before this one.

  1. It searched for wood-coloured pixels and reported a "shaft centroid" 86-238 px from the ball.
     That measured the BACKGROUND, not the stick: the felt, the cushions and the level's warm starfield
     all contain wood-toned pixels. A centroid over a background-coloured mask is meaningless.
  2. It sampled a 1-pixel-wide probe along the expected axis. The shaft is only 7 px wide (three
     overlapping strokes), so a 3 px perpendicular drift -- from rounding, or from the probe's own
     discretisation -- returns "not wood" for the entire length and the check reports a false failure.
     The first version reported 17/110 on a stick that was in fact drawn correctly.
  3. It assumed a y convention without testing it.

WHAT THIS VERSION DOES INSTEAD:

  * probes a PERPENDICULAR WINDOW (+/-6 px) at each step, which is wider than the 7 px stroke;
  * tests BOTH candidate y conventions and reports which one the image actually matches, rather than
    hard-coding one and failing opaquely;
  * compares against the mirrored span in FRONT of the ball, so a probe that simply covers the whole
    table cannot pass.

GROUND TRUTH: corridor.json spawns the cue at (520,280), the aim is +7 degrees, and Play.DrawAim pulls
the stick back along -aimDir by `r + 6 + t*54` then extends it 300 px, with r = 12.2 and t = 1 at full
power. Those numbers are read from the source; they are not re-derived here.
"""
from PIL import Image
import math

path = "godot/evidence/cue_stick.png"
im = Image.open(path).convert("RGB")
b = im.load()
W, H = im.size

BX, BY = 520.0, 280.0        # corridor.json cue spawn
ANG = math.radians(7.0)
UX, UY = math.cos(ANG), math.sin(ANG)    # +x, +y component of the aim direction

R = 12.2
T0 = R + 6.0 + 54.0          # standoff at full power (t = 1)
T1 = T0 + 300.0              # shaft length from Play.DrawAim
STEPS = 120


def warm(p):
    """Cue wood. CueShaftDark #8a6534 and CueShaft #c99a5b both pass; the felt is green (g dominant)
    and the starfield is faint, so neither is mistaken for the shaft."""
    return p[0] > 110 and p[0] > p[1] + 20 and p[1] > p[2] + 15


def coverage(sign_x, sign_y, perpendicular=6.0):
    """Fraction of the span with wood somewhere inside a perpendicular window.

    The two signs give the four candidate axes. Only the correct convention will light up.
    """
    hits = 0
    for i in range(STEPS):
        t = T0 + (T1 - T0) * i / (STEPS - 1.0)
        cx = BX + sign_x * UX * t
        cy = BY + sign_y * UY * t
        found = False
        for o in range(-int(perpendicular), int(perpendicular) + 1):
            # Offset perpendicular to the axis, in canvas space.
            px = int(round(cx - o * sign_y * UY))
            py = int(round(cy + o * sign_x * UX))
            if 0 <= px < W and 0 <= py < H and warm(b[px, py]):
                found = True
                break
        if found:
            hits += 1
    return hits / STEPS


# "back" is the far side of the ball from the shot; "front" is where the ball travels.
back_minus_y = coverage(-1, -1)   # up and to the left: the maths-convention result
back_plus_y = coverage(-1, +1)    # down and to the left: the canvas-convention result
front_minus_y = coverage(+1, -1)
front_plus_y = coverage(+1, +1)

print(f"{path}")
print(f"  cue spawn {BX:.0f},{BY:.0f}   aim +7 deg   probe {T0:.0f}..{T1:.0f} px from the centre")
print()
print("  shaft coverage (wood within +/-6 px of the axis):")
print(f"    BEHIND the ball, up-left   (-x,-y): {back_minus_y:6.1%}")
print(f"    BEHIND the ball, down-left (-x,+y): {back_plus_y:6.1%}")
print(f"    IN FRONT,       up-right   (+x,-y): {front_minus_y:6.1%}")
print(f"    IN FRONT,       down-right (+x,+y): {front_plus_y:6.1%}")
print()

best_back = max(back_minus_y, back_plus_y)
best_front = max(front_minus_y, front_plus_y)

if best_back < 0.5:
    print(f"VERDICT: FAILED - no shaft found behind the ball (best {best_back:.0%}). "
          "Either the stick is not drawn in this state, or its geometry changed.")
    raise SystemExit(1)

if best_back <= best_front * 2:
    print(f"VERDICT: FAILED - the shaft is not clearly behind the ball "
          f"(behind {best_back:.0%} vs front {best_front:.0%}).")
    raise SystemExit(1)

# WHICH Y CONVENTION THE IMAGE MATCHES, stated in the terms that matter:
#
#   +90 degrees aims at the BOTTOM rail, and FromAngleDegrees(90) = (0, +1), so level +y grows DOWNWARD.
#   The level file agrees: ball id 2 sits at y=150 (nearer the top rail) and ball id 1 at y=310.
#   Canvas +y also grows downward. So the two spaces MATCH, and the stick -- being behind a shot aimed
#   down-right -- must appear UP-left, i.e. (-x, -y).
#
# If the (+x,+y) or (-x,+y) probes had won instead, that would mean a vertical flip somewhere and would
# be a real bug rather than a labelling question. The earlier version of this message had the branch
# inverted, so it reported a flip when the measurement actually showed agreement.
if back_minus_y >= back_plus_y:
    which = "up-left (-x,-y), which is the canvas convention matching the level file"
else:
    which = ("down-left (-x,+y), i.e. canvas y is FLIPPED relative to the level file -- "
             "that would be a real bug, not a labelling detail")

print(f"VERDICT: the shaft lies BEHIND the ball, opposite the shot -- {best_back:.0%} coverage, "
      f"vs {best_front:.0%} in front. It points {which}.")

if back_minus_y < back_plus_y:
    print("         (and the y convention therefore disagrees with every other rect in this project)")
    raise SystemExit(1)

raise SystemExit(0)
