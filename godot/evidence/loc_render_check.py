"""Localization render checks: no font library, just PIL + numpy.

Three questions, each answered from pixels rather than from a return code:

1. TOFU TEST.  A missing CJK glyph renders as a hollow rectangle.  Every tofu box in a run is
   the *same* bitmap, so a row of tofu has a very low count of distinct connected-component
   shapes.  Real Chinese has many distinct shapes per run.  We report, per text band, the number
   of connected components and how many DISTINCT component bitmaps they collapse to.  All-equal
   components => tofu; many distinct => real glyphs.

2. CLIP TEST.  Is there ink touching the last few pixel columns of the canvas?  (Right-edge
   overflow of a right-aligned readout.)

3. PANEL OVERFLOW.  Does HUD ink appear to the right of the left panel's edge, or below/above its
   declared band?
"""

import sys
from pathlib import Path

import numpy as np
from PIL import Image

INK = 60.0  # per-channel distance from the band's modal colour to count as ink


def band_arrays(img, box):
    x0, y0, x1, y1 = box
    a = np.asarray(img.convert("RGB"), dtype=np.float32)[y0:y1, x0:x1]
    flat = a.reshape(-1, 3)
    # modal colour = background of this band
    q = (flat // 4).astype(np.int32)
    keys = q[:, 0] * 4096 + q[:, 1] * 64 + q[:, 2]
    vals, counts = np.unique(keys, return_counts=True)
    best = vals[np.argmax(counts)]
    bg = flat[keys == best].mean(axis=0)
    d = np.abs(a - bg).max(axis=2)
    return a, d > INK, bg


def components(mask):
    """4-connected components, iterative flood fill (images here are tiny)."""
    h, w = mask.shape
    seen = np.zeros_like(mask, dtype=bool)
    out = []
    for sy in range(h):
        for sx in range(w):
            if not mask[sy, sx] or seen[sy, sx]:
                continue
            stack = [(sy, sx)]
            seen[sy, sx] = True
            ys, xs = [], []
            while stack:
                y, x = stack.pop()
                ys.append(y)
                xs.append(x)
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        stack.append((ny, nx))
            y0, y1 = min(ys), max(ys)
            x0, x1 = min(xs), max(xs)
            sub = np.zeros((y1 - y0 + 1, x1 - x0 + 1), dtype=bool)
            for y, x in zip(ys, xs):
                sub[y - y0, x - x0] = True
            out.append((sub, len(ys), (x0, y0, x1, y1)))
    return out


def tofu_report(path, name, box):
    img = Image.open(path)
    a, mask, bg = band_arrays(img, box)
    comps = components(mask)
    # ignore specks (antialiasing dust, leaders) and merge nothing: compare the big ones
    big = [c for c in comps if c[1] >= 4]
    sigs = {}
    for sub, n, bb in big:
        h, w = sub.shape
        if h < 5 or w < 3:  # too small to be a glyph body
            continue
        sigs.setdefault(sub.tobytes(), []).append(bb)
    shapes = len(sigs)
    total = sum(len(v) for v in sigs.values())
    verdict = "TOFU?" if (total >= 4 and shapes <= max(1, total // 3)) else "REAL-GLYPHS"
    print(f"  [{name}] box={box} ink_px={int(mask.sum())} components>=5px={total} "
          f"distinct_bitmaps={shapes} -> {verdict}")
    return total, shapes, verdict


def ink_extent(path, box):
    img = Image.open(path)
    a, mask, bg = band_arrays(img, box)
    cols = np.where(mask.any(axis=0))[0]
    rows = np.where(mask.any(axis=1))[0]
    if len(cols) == 0:
        return None
    x0, y0 = box[0], box[1]
    return (x0 + int(cols.min()), x0 + int(cols.max()), y0 + int(rows.min()), y0 + int(rows.max()))


def nonblank(path, min_distinct=8, min_content=0.02, tolerance=6):
    """The same two quantities src/ui/verify_png.gd computes, with the same thresholds.

    verify_png.gd is a GDScript per-pixel loop over a 582,400-pixel image; a single 1040x560 frame
    had not finished after 10 minutes on this machine, so the identical arithmetic is done here in
    numpy.  Recorded because the deliverable asks for the non-blank evidence -- NOT because it says
    anything about CJK: a screenshot full of tofu boxes is non-blank too.
    """
    img = Image.open(path).convert("RGB")
    a = np.asarray(img, dtype=np.int32)  # int32, not uint16: (r << 16) overflows uint16
    keys = (a[:, :, 0] << 16) | (a[:, :, 1] << 8) | a[:, :, 2]
    vals, counts = np.unique(keys, return_counts=True)
    distinct = len(vals)
    top = int(np.argmax(counts))
    bg_key, bg_count = int(vals[top]), int(counts[top])
    bg = np.array([(bg_key >> 16) & 255, (bg_key >> 8) & 255, bg_key & 255], dtype=np.int32)
    diff = np.abs(a - bg).max(axis=2)
    content = int((diff > tolerance).sum())
    total = a.shape[0] * a.shape[1]
    ratio = content / total
    blank = distinct < min_distinct or ratio < min_content
    return dict(file=Path(path).name, distinct=distinct,
                bg="#%02x%02x%02x" % ((bg_key >> 16) & 255, (bg_key >> 8) & 255, bg_key & 255),
                bg_pct=100.0 * bg_count / total, content_px=content, content_pct=100.0 * ratio,
                verdict="BLANK" if blank else "NONBLANK")


def main():
    ev = Path(r"D:\codename\codename_billiards\godot\evidence")

    print("non-blank check (same metrics + thresholds as src/ui/verify_png.gd):")
    for f in sorted(ev.glob("loc_*.png")):
        m = nonblank(f)
        print(f"  {m['file']:24s} distinct_rgb={m['distinct']:<5d} bg={m['bg']} ({m['bg_pct']:.1f}%) "
              f"content={m['content_px']}px ({m['content_pct']:.2f}%)  VERDICT={m['verdict']}")

    print("\ntofu / glyph-distinctness check:")
    checks = [
        ("loc_aim.png", "top-bar right readout", (820, 4, 1040, 26)),
        ("loc_aim.png", "left panel STRIKE block", (14, 42, 250, 90)),
        ("loc_aim.png", "left panel CONTROLS block", (14, 390, 250, 540)),
        ("loc_aim.png", "top-bar chips", (0, 4, 700, 26)),
        ("loc_debug.png", "debug overlay block", (700, 36, 962, 470)),
        ("loc_rewind.png", "messages band", (262, 494, 962, 544)),
        ("loc_win.png", "win banner text", (300, 190, 740, 340)),
    ]
    for fname, label, box in checks:
        p = ev / fname
        if not p.exists():
            print(f"  [{label}] MISSING {p}")
            continue
        tofu_report(p, label, box)

    print("\nRight-edge clipping of the top-bar readout (canvas is 1040 wide):")
    for f in ["loc_aim.png", "loc_strike.png", "loc_rewind.png", "loc_debug.png", "loc_win.png"]:
        p = ev / f
        if not p.exists():
            continue
        img = Image.open(p).convert("RGB")
        a = np.asarray(img, dtype=np.float32)[4:26, :]
        flat = a.reshape(-1, 3)
        q = (flat // 4).astype(np.int32)
        keys = q[:, 0] * 4096 + q[:, 1] * 64 + q[:, 2]
        vals, counts = np.unique(keys, return_counts=True)
        bg = flat[keys == vals[np.argmax(counts)]].mean(axis=0)
        m = (np.abs(a - bg).max(axis=2) > INK)[:, 700:]
        cols = np.where(m.any(axis=0))[0]
        last = 700 + int(cols.max()) if len(cols) else -1
        touch = "TOUCHES-EDGE(1039)" if last >= 1039 else "clear"
        print(f"  {f:22s} rightmost ink column in y4..26 = {last}  {touch}")

    print("\nLeft-panel bounds (panel is x6..252, y34..510):")
    for f in ["loc_aim.png", "loc_debug.png", "loc_rewind.png"]:
        p = ev / f
        if not p.exists():
            continue
        img = Image.open(p).convert("RGB")
        a = np.asarray(img, dtype=np.float32)
        # far-left dead strip x0..5 must be untouched (panel starts at 6)
        strip = a[34:510, 0:5]
        print(f"  {f:22s} ink in x0..4 strip = {int((np.abs(strip - strip.reshape(-1,3)[0]).max(axis=1) > INK).sum())}")


if __name__ == "__main__":
    main()
