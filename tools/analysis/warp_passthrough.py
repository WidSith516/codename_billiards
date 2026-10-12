"""Is the idle warp an exact pass-through?

The warp shader's idle branch is supposed to reproduce the table unchanged. If it does not, then every
measurement that compares two captures through the warp is contaminated, and worse, the table would look
wrong in normal play whenever nothing is happening.

The committed capture `rail_ripple.png` was taken by the LAST build that had no warp at all, and the
starfield is written from a fixed seed, so its table content is a valid unwarped reference.
"""
from PIL import Image

OLD = r'godot\evidence\rail_ripple.png'          # no warp existed in this build
NEW = r'godot\evidence\warp_p0.png'              # warp present, push forced to 0 (pass-through)


def load(p):
    return Image.open(p).convert('RGB')


def stats(im, x0, x1, y0, y1, label):
    total = 0
    n = 0
    dark = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            p = im.getpixel((x, y))
            lum = (p[0] + p[1] + p[2]) / 3.0
            total += lum
            n += 1
            if lum < 8:
                dark += 1
    print(f'  {label:<28} mean={total / n:7.2f}  near-black={dark * 100.0 / n:5.1f}%')
    return total / n


old, new = load(OLD), load(NEW)

# Table interior only: inside the cushions on all four sides.
X0, X1, Y0, Y1 = 100, 940, 100, 470

print('table interior statistics (x 100..940, y 100..470)')
a = stats(old, X0, X1, Y0, Y1, 'no warp (committed)')
b = stats(new, X0, X1, Y0, Y1, 'warp present, push=0')

print()
print(f'  mean luminance ratio new/old = {b / a:.3f}')

# Count pixels differing materially.
diff = 0
n = 0
for y in range(Y0, Y1, 2):
    for x in range(X0, X1, 2):
        p, q = old.getpixel((x, y)), new.getpixel((x, y))
        if abs(p[0] - q[0]) + abs(p[1] - q[1]) + abs(p[2] - q[2]) > 30:
            diff += 1
        n += 1

print(f'  pixels differing by >30: {diff}/{n} = {diff * 100.0 / n:.1f}%')
print()
if diff * 100.0 / n > 20:
    print('  -> NOT pass-through. The warp path is altering the table even when idle, so every')
    print('     captured comparison is contaminated and normal play looks wrong. Fix before tuning.')
else:
    print('  -> pass-through is clean: the idle warp reproduces the unwarped table.')
