"""Reproduces the numeric claims in 策划案/REWIND_port_plan_v1.md section 2.2.

All constants are copied verbatim from h5/index.html (the frozen reference).
Run: python const_audit.py
"""
import math

# --- constants copied from h5/index.html ---
R = 12.2                      # ball radius
STEP = 1.0 / 120              # fixed physics step
DAMP_K = 1.07                 # damping = exp(-1.07*dt)
STOP_SPEED = 9.0              # speed below this is snapped to 0
RAIL_FACTOR_CUE = 1.5         # cue ball rail boost (unbounded, multiplicative)
RAIL_FACTOR_OBJ = 0.85
MAX_DRAG = 170.0              # power clamp in releaseShot()
SPEED_PER_POWER = 7.4         # cue.vx = (dx/len)*power*7.4
T_LEFT, T_RIGHT = 65.0, 975.0
T_TOP, T_BOTTOM = 63.0, 497.0
PLAY_LEN = T_RIGHT - T_LEFT   # 910 px of travel along x
POCKET_CAP = 24.0             # inPocket(): hypot < 24
V0 = MAX_DRAG * SPEED_PER_POWER


def decay_behaviour():
    t = math.log(V0 / STOP_SPEED) / DAMP_K
    dist = (V0 - STOP_SPEED) / DAMP_K
    return t, dist, dist / PLAY_LEN


def rail_recursion(v0=V0, first_flight=204.8, flight=409.6, n_max=8, factor=RAIL_FACTOR_CUE):
    """Exact rail-boost recursion, including the damping that happens DURING each flight.

    Per bounce:  v_out = factor * v_in * exp(-k * L / v_in)

    A naive `v(n) = v0 * factor**n` list is WRONG: it ignores the damping over the flight
    and therefore overstates how fast the divergence happens (an earlier revision of the
    design doc made exactly that mistake). The recursion has a fixed point at
    v* = k*L / ln(factor); above it the sequence diverges, below it it decays.
    """
    out = []
    v = v0
    L = first_flight
    for n in range(1, n_max + 1):
        v_leave = v * math.exp(-DAMP_K * L / v)
        v_out = factor * v_leave
        out.append((n, v, L, v_leave, v_out, v_out * STEP))
        v = v_out
        L = flight
        if not math.isfinite(v) or v > 1e9:
            break
    return out


def fixed_point(L, factor=RAIL_FACTOR_CUE):
    """v* = k*L / ln(factor). Flight length and stability threshold are PROPORTIONAL."""
    return DAMP_K * L / math.log(factor)


def safety_bounds():
    axis = 2 * R / STEP
    diag = (R * math.sqrt(2)) / STEP
    pocket = POCKET_CAP / STEP
    return axis, diag, pocket


def rack():
    """Verbatim reproduction of init()'s rack generation."""
    balls = [(0, 292.0, 280.0)]
    bid = 1
    for col in range(4):
        for row in range(col + 1):
            x = 675 + col * R * 1.82
            y = 280 + (row - col / 2.0) * R * 2.12
            balls.append((bid, x, y))
            bid += 1
    overlaps = []
    for i in range(1, len(balls)):
        for j in range(i + 1, len(balls)):
            _, ax, ay = balls[i]
            _, bx, by = balls[j]
            d = math.hypot(bx - ax, by - ay)
            if d < 2 * R - 1e-3:
                overlaps.append((balls[i][0], balls[j][0], d, 2 * R - d))
    # Neighbour spacing: the true nearest neighbours are "adjacent column + half a row
    # offset", i.e. hypot(1.82R, 2.12R/2). An earlier revision used hypot(1.82R/2, 2.12R),
    # which mixes components of two DIFFERENT pairs and describes the diagonal, not the
    # nearest neighbour (it gave 28.146 instead of the correct 25.695).
    nearest = math.hypot(R * 1.82, R * 2.12 / 2)
    same_col = R * 2.12
    gap = nearest - 2 * R
    tight = 2 * R * math.cos(math.pi / 6)
    used = R * 1.82
    return balls, overlaps, gap, tight, used, nearest, same_col


if __name__ == "__main__":
    t, dist, ratio = decay_behaviour()
    print("== rolling decay (exponential damping, k=1.07) ==")
    print(f"  break speed           : {V0:.0f} px/s")
    print(f"  time to reach {STOP_SPEED:.0f} px/s : {t:.2f} s")
    print(f"  travel distance       : {dist:.0f} px  = {ratio:.2f} x playable length ({PLAY_LEN:.0f} px)")

    axis, diag, pocket = safety_bounds()
    print("\n== discrete-collision safety bounds (no CCD, STEP=1/120) ==")
    print(f"  axis-aligned ball-ball : v <= {axis:.0f} px/s   (step <= 2r = {2*R:.1f})")
    print(f"  worst-case diagonal    : v <= {diag:.0f} px/s   (step ~ r*sqrt2)")
    print(f"  pocket point test      : v <= {pocket:.0f} px/s   (cap radius {POCKET_CAP:.0f})")
    print(f"  break speed is {V0/diag*100:.0f}% of the worst-case bound -> break itself does NOT tunnel")

    print("\n== unbounded x1.5 rail boost: EXACT recursion (with in-flight damping) ==")
    print("   per bounce: v_out = 1.5 * v_in * exp(-k*L/v_in)")
    print(f"  {'n':>3} {'v_in':>10} {'L px':>8} {'v_leave':>10} {'v_out':>11} {'step px':>9}  verdict")
    for n, v_in, L, v_leave, v_out, step in rail_recursion():
        if v_out <= V0:
            verdict = "below break speed"
        elif v_out <= diag:
            verdict = "over r, under worst-case bound"
        else:
            verdict = "TUNNELING-PRONE"
        print(f"  {n:>3} {v_in:>10,.1f} {L:>8.1f} {v_leave:>10,.1f} {v_out:>11,.1f} {step:>9.1f}  {verdict}")
    print("  NOTE: a naive v(n)=v0*1.5**n series is WRONG (ignores in-flight damping)")
    print(f"  fixed point for L=409.6 : v* = k*L/ln1.5 = {fixed_point(409.6):.1f} px/s")
    print(f"  fixed point for L=204.8 : v* = k*L/ln1.5 = {fixed_point(204.8):.1f} px/s")
    print(f"  break speed {V0:.0f} is ABOVE both -> straight-down/up shots DIVERGE")

    print("\n== stability vs flight length (v* = k*L/ln1.5, proportional to L) ==")
    for L, label in ((204.8, "short-axis flight from spawn"),
                     (409.6, "full table height"),
                     (670.8, "spawn -> right rail"),
                     (885.6, "long-axis flight"),
                     (910.0, "playable length")):
        vstar = fixed_point(L)
        flag = "UNSTABLE at break speed" if V0 > vstar else "stable at break speed"
        print(f"  L={L:>7.1f} ({label:<28}) v*={vstar:>8.1f} px/s -> {flag}")
    print("  -> the SHORTER the flight, the more dangerous: hugging a rail is the worst case")

    balls, overlaps, gap, tight, used, nearest, same_col = rack()
    print("\n== rack geometry (verbatim init()) ==")
    for bid, x, y in balls:
        print(f"  ball {bid:>2}: x={x:8.2f} y={y:8.2f}")
    print(f"  overlapping ball pairs at spawn : {len(overlaps)}  -> rack does NOT start interpenetrating")
    print(f"  nearest-neighbour distance      : {nearest:.3f} px  hypot(1.82R, 2.12R/2)")
    print(f"  same-column neighbour distance  : {same_col:.3f} px  (2.12R)")
    print(f"  touching distance               : {2*R:.1f} px")
    print(f"  GAP per nearest neighbour       : {gap:.3f} px  (a properly racked triangle = 0)")
    print(f"  column spacing used             : {used:.3f} px; tight triangle needs {tight:.3f} px")
    print(f"  -> rack is {used/tight:.4f}x too wide ({(used/tight-1)*100:.1f}% over)")
    print("  CAUTION: an earlier revision reported 28.146 px / 3.75 px from hypot(1.82R/2, 2.12R),")
    print("           which mixes components of two different pairs. Correct value is above.")

    print("\n== cue ball start vs head string ==")
    print("  cue x=292.0, head string x=294.0 -> cue starts 2 px INSIDE the kitchen (head side)")
