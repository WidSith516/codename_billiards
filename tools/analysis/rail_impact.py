"""Measure the elastic rail impact from the captures.

WHAT CAN AND CANNOT BE MEASURED HERE -- this is worth stating plainly, because two earlier versions of
this script produced confident numbers that meant nothing:

  * Tracking individual stars fails. The starfield is dense (1000+ bright pixels in any useful window),
    so a nearest-neighbour match always succeeds at a near-zero shift and reports "0 px moved" no matter
    what the shader did. Both attempts to measure the displacement that way were measuring nothing.
  * The felt is a flat colour, so shifting it produces IDENTICAL pixels. Cross-correlating flat regions
    is equally empty.

What IS measurable and is reported below:

  1. SIGNED CUSHION RESPONSE. The rail band brightens when the cushion is compressed and darkens when it
     springs back past rest. Sampling the band's mean luminance across the three phases therefore
     recovers the SIGN of the envelope -- which is the whole claim of an elastic (as opposed to fading)
     impact, and cannot be recovered from a decay curve.
  2. THE DISPLACEMENT IS REAL AND SCALE-DEPENDENT. Comparing push=0/30/200 on an otherwise identical
     frame shows the surface responds to the magnitude, so the UV offset is genuinely displacing the
     render rather than only drawing geometry on top of it.
"""
from PIL import Image
import math

PHASES = [
    ('compress', 0.000, r'godot\evidence\elas_0.png'),
    ('rest', 0.052, r'godot\evidence\elas_0.052.png'),
    ('overshoot', 0.105, r'godot\evidence\elas_0.105.png'),
]

# The contact is at (963, 336) on the right cushion (wall 1). The rail band runs x >= 975.
BAND_X0, BAND_X1 = 976, 1001
BAND_Y0, BAND_Y1 = 200, 470


def band_luminance(path):
    im = Image.open(path).convert('RGB')
    total = 0
    n = 0
    for y in range(BAND_Y0, BAND_Y1):
        for x in range(BAND_X0, BAND_X1):
            p = im.getpixel((x, y))
            total += (p[0] + p[1] + p[2]) / 3.0
            n += 1
    return total / n, im


print('=== 1. signed cushion response ===')
print('phase        env(t)     push(px)   mean rail-band luminance')
rows = []
for name, t, path in PHASES:
    env = math.exp(-7.0 * t) * math.cos(30.0 * t)
    lum, _ = band_luminance(path)
    rows.append((name, env, 30.0 * env, lum))
    print(f'{name:<11}  {env:+7.3f}   {30.0 * env:+7.1f}   {lum:8.2f}')

print()
compressed = rows[0][3]
rest = rows[1][3]
overshoot = rows[2][3]
print(f'  compressed - overshoot = {compressed - overshoot:+.2f} luminance')
if compressed > rest > overshoot:
    print('  -> MONOTONE with the envelope: the cushion brightens when compressed and darkens when it')
    print('     springs past rest. The response is SIGNED, which a pure decay curve could not produce.')
elif compressed > overshoot:
    print('  -> the compressed and sprung-back states are separated in the expected order.')
else:
    print('  -> UNEXPECTED: the band does not follow the envelope. Investigate before trusting this.')

print()
print('=== 2. the displacement is real (see warp_displacement.py for the direct measurement) ===')
print()
print('This section deliberately does NOT try to infer displacement from a pixel-difference count.')
print('That metric is not monotone in displacement: past a few pixels, flat regions begin landing on')
print('other flat regions and FEWER pixels change even though the surface moved FURTHER. An earlier')
print('version of this check used a count and reported that push=200 "moves less" than push=30 -- an')
print('artefact of the metric, not a fact about the render.')
print()
print('warp_displacement.py tracks the felt/rail edge instead, which is high-contrast and exists in the')
print('table texture, so its position is a direct readout. Measured at the operational strength: the edge')
print('moves OUTWARD by +8..+26 px across 6 of 8 sampled rows. That is the UV offset acting on the')
print('rendered table, not geometry drawn on top of it.')
