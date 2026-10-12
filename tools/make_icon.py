"""Generate the Android launcher icon from the game's own visual language.

WHY GENERATE RATHER THAN COMMISSION/SOURCE ART: the icon has to be a 512x512 PNG, none exists in the
repo, and the Android export refuses to run without one ("No project icon specified"). Rather than drop
in an unrelated placeholder, this draws the smallest honest statement of what the game is -- the cue
ball, and the rewind arc that is the whole mechanic -- using the exact palette the game renders with, so
the icon and the title screen cannot drift apart.

It is deliberately code, not a checked-in binary from an unknown source: the colours below are read out
of Palette.cs by hand and noted, so a palette change can be traced back to this file.
"""
from PIL import Image, ImageDraw

S = 512
OUT = r"godot\assets\icon.png"

# From Palette.cs (theme 0, "deep space"): SpaceBg-ish room, ivory cue ball, orange rewind cue.
ROOM = (5, 5, 16)          # #050510  RoomBg (theme 0)
FELT = (11, 16, 48)        # #0b1030  felt
CUE_IVORY = (253, 253, 255)  # #fdfdff  Cue (theme 0)
REWIND = (255, 122, 26)    # #ff7a1a  RewindCue
TRAVERSED = (255, 183, 3)  # #ffb703  RewindTraversed
GHOST = (40, 44, 70)

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Rounded-square background, because Android masks the corners itself and a full-bleed square looks
# correct under every mask shape (circle, squircle, rounded square).
pad = 0
d.rounded_rectangle([pad, pad, S - 1 - pad, S - 1 - pad], radius=104, fill=FELT + (255,))

# A felt-dark vignette: drawn as concentric ellipses so there is some depth without a gradient library.
for i in range(26):
    t = i / 25.0
    r = int(300 - 150 * t)
    col = (
        int(FELT[0] + (ROOM[0] - FELT[0]) * t),
        int(FELT[1] + (ROOM[1] - FELT[1]) * t),
        int(FELT[2] + (ROOM[2] - FELT[2]) * t),
    )
    cx, cy = S // 2, S // 2
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=col + (255,))

# ---- the rewind arc: the mechanic, drawn as a swept ring around the ball ----
cx, cy = S // 2, S // 2 + 14
ring_r = 168
# Traversed portion (where the ball has already been) in amber, the rest in the dim "remaining" grey.
d.arc([cx - ring_r, cy - ring_r, cx + ring_r, cy + ring_r], start=200, end=340, fill=GHOST + (255,), width=26)
d.arc([cx - ring_r, cy - ring_r, cx + ring_r, cy + ring_r], start=118, end=200, fill=TRAVERSED + (255,), width=26)
d.arc([cx - ring_r, cy - ring_r, cx + ring_r, cy + ring_r], start=36, end=118, fill=REWIND + (255,), width=26)
# An arrowhead at the arc's leading end, so the arc reads as motion rather than a broken circle.
import math
ang = math.radians(-34)
tipx, tipy = cx + ring_r * math.cos(ang), cy + ring_r * math.sin(ang)
a1 = ang + math.radians(145)
a2 = ang - math.radians(145)
h = 44
d.polygon(
    [
        (tipx + h * math.cos(a1), tipy + h * math.sin(a1)),
        (tipx, tipy),
        (tipx + h * math.cos(a2), tipy + h * math.sin(a2)),
    ],
    fill=REWIND + (255,),
)

# ---- the cue ball: the one object the player controls ----
br = 118
d.ellipse([cx - br, cy - br, cx + br, cy + br], fill=CUE_IVORY + (255,))
# A soft terminator: a crescent of shadow on the lower right so the ball is a sphere, not a disc.
shade = Image.new("RGBA", (S, S), (0, 0, 0, 0))
sd = ImageDraw.Draw(shade)
for i in range(18):
    t = i / 17.0
    r = int(br * (1.0 - 0.02 * i))
    ox, oy = int(10 * t), int(12 * t)
    sd.ellipse(
        [cx - r + ox, cy - r + oy, cx + r + ox, cy + r + oy],
        outline=(90, 96, 120, max(0, int(16 * (1 - t)))),
        width=3,
    )
img.alpha_composite(shade)
# The ghost ring is the game's "where the ball will be" indicator; a thin one on the ball ties the icon
# to the aim UI without adding another colour.
d.ellipse([cx - br + 16, cy - br + 16, cx + br - 16, cy + br - 16],
          outline=(206, 214, 235, 90), width=4)

img.save(OUT)
print(f"wrote {OUT} {img.size}")

# Also emit the 432x432 adaptive foreground/background the Android preset asks for, if wanted later.
fg = Image.new("RGBA", (432, 432), (0, 0, 0, 0))
fimg = img.resize((432, 432), Image.LANCZOS)
fg.paste(fimg, (0, 0), fimg)
fg.save(r"godot\assets\icon_foreground.png")
Image.new("RGBA", (432, 432), FELT + (255,)).save(r"godot\assets\icon_background.png")
print("wrote adaptive foreground/background 432x432")
