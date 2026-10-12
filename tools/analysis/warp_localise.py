"""Localise the regression: is the warp SOURCE wrong, or the blit through the shader?

Three images:
  old        the committed capture from the last build that had NO warp at all
  target     the SubViewport contents this build (--dump-warp)
  composite  what the player actually sees this build

If target matches old but composite does not, the source is fine and the shader/blit path is at fault.
If target does not match old, the source drawing itself broke.
"""
from PIL import Image

OLD = r'godot\evidence\rail_ripple.png'
TARGET = r'godot\evidence\warp_target.png'
COMPOSITE = r'godot\evidence\warp_composite.png'

X0, X1, Y0, Y1 = 100, 940, 100, 470


def mean_lum(im, box):
    x0, x1, y0, y1 = box
    t = n = 0
    for y in range(y0, y1, 2):
        for x in range(x0, x1, 2):
            p = im.getpixel((x, y))
            t += (p[0] + p[1] + p[2]) / 3.0
            n += 1
    return t / n


def diff_pct(a, b, box, thr=30):
    x0, x1, y0, y1 = box
    d = n = 0
    for y in range(y0, y1, 2):
        for x in range(x0, x1, 2):
            p, q = a.getpixel((x, y)), b.getpixel((x, y))
            if abs(p[0] - q[0]) + abs(p[1] - q[1]) + abs(p[2] - q[2]) > thr:
                d += 1
            n += 1
    return d * 100.0 / n


old = Image.open(OLD).convert('RGB')
target = Image.open(TARGET).convert('RGB')
comp = Image.open(COMPOSITE).convert('RGB')

box = (X0, X1, Y0, Y1)
print(f'table interior mean luminance (x {X0}..{X1}, y {Y0}..{Y1}):')
print(f'  old (no warp, committed)  {mean_lum(old, box):7.2f}')
print(f'  target (SubViewport)      {mean_lum(target, box):7.2f}')
print(f'  composite (on screen)     {mean_lum(comp, box):7.2f}')
print()
print('pixels differing by >30:')
print(f'  old  vs target   {diff_pct(old, target, box):5.1f}%')
print(f'  old  vs composite{diff_pct(old, comp, box):5.1f}%')
print(f'  target vs comp   {diff_pct(target, comp, box):5.1f}%')
print()
print('This decides the branch:')
print('  old≈target  -> the source is correct, the shader/blit path is broken')
print('  old≠target  -> the source drawing itself changed')
print()
print('target, coarse ASCII:')
small = target.resize((60, 26))
chars = ' .:-=+*#%@'
for y in range(26):
    print('  ' + ''.join(chars[min(9, int(sum(small.getpixel((x, y))) / 3 / 25.6))] for x in range(60)))
