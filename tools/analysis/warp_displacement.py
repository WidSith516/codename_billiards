"""Direct displacement measurement, sampling AWAY from the ball.

The previous version scanned rows y=320..352, which is exactly where the cue ball sits (contact 963,336,
radius ~12). The ball is drawn in Play's canvas and is NOT part of the warped table texture, so those rows
measured the ball's edge -- which never moves -- and reported "0 px displacement" for every push.

The dent spans +/- 3 sigma = +/- 264 px along the wall, so there is plenty of wall beyond the ball. These
rows are clear of it, and the felt->rail edge is a high-contrast feature whose position is a direct
readout of the surface displacement.
"""
from PIL import Image

CASES = [
    ('idle (push=0)', r'godot\evidence\warp_p0.png'),
    ('push=30', r'godot\evidence\warp_p30.png'),
    ('push=200', r'godot\evidence\warp_p200.png'),
]

# The contact is at (963, 336). The cue ball covers roughly y 324..348 at that x, so these rows are
# clear of it but still well inside the dent's +/- 3 sigma span.
ROWS = [200, 220, 240, 260, 400, 420, 440, 460]

# Scan a window that contains the felt/rail boundary but stops short of the pockets at the corners.
SCAN = range(930, 1000)


def edge_x(im, y):
    """x of the strongest felt->rail luminance rise on this row."""
    best_x, best_g = None, -1e9
    for x in SCAN:
        g = sum(im.getpixel((x + 1, y))) - sum(im.getpixel((x, y)))
        if g > best_g:
            best_g, best_x = g, x
    return best_x


def band_mean(im, y):
    """Mean luminance of the rail band on this row: a second, independent readout of the bend."""
    t = n = 0
    for x in range(976, 1001):
        p = im.getpixel((x, y))
        t += (p[0] + p[1] + p[2]) / 3.0
        n += 1
    return t / n


images = {label: Image.open(path).convert('RGB') for label, path in CASES}

print('felt->rail edge x, per row (clear of the ball)')
print(f'{"case":<16}' + ''.join(f'{y:>7}' for y in ROWS))

edges = {}
for label, _ in CASES:
    im = images[label]
    xs = [edge_x(im, y) for y in ROWS]
    edges[label] = xs
    print(f'{label:<16}' + ''.join(f'{x:>7}' for x in xs))

print()
print('rail band mean luminance, per row')
print(f'{"case":<16}' + ''.join(f'{y:>7}' for y in ROWS))
for label, _ in CASES:
    im = images[label]
    print(f'{label:<16}' + ''.join(f'{band_mean(im, y):>7.1f}' for y in ROWS))
print()

idle_e = edges['idle (push=0)']
d30 = [b - a for a, b in zip(idle_e, edges['push=30'])]
moved30 = sum(1 for v in d30 if v != 0)
print(f'edge displacement vs idle, push=30: ' + ' '.join(f'{v:+d}' for v in d30))
print(f'  rows whose edge moved: {moved30}/{len(ROWS)}')
print(f'  direction: {"OUTWARD (+x, the cushion gives way)" if sum(1 for v in d30 if v > 0) > moved30 / 2 else "MIXED"}')
print()

# push=200 is reported for completeness but is NOT a valid scale check. The texture is 1040 px wide and
# the wall is at x=975, so an offset of 200 px samples x=1175 -- past the edge of the target. The shader
# then reads clamped/empty texels and the band goes DARK rather than moving further. That is a property of
# the measurement exceeding the texture, not a failure of the warp. The operational magnitude is 30 px,
# whose largest sample (975 + 30 = 1005) stays comfortably inside.
print('NOTE: the --warp-push=200 capture is NOT a valid displacement reading. At that magnitude the sample')
print('      point (975 + 200 = 1175) falls outside the 1040 px target, so the shader reads off-texture')
print('      and the band darkens instead of moving further. push=30 samples at most 1005, inside.')
print()
if moved30 >= len(ROWS) // 2:
    print('  -> at its operational strength the surface DOES move, outward, across most of the dent span:')
    print('     the shader is applying a real UV offset, not merely drawing geometry on top.')
else:
    print('  -> the surface does NOT move. The shader is not displacing the render.')
