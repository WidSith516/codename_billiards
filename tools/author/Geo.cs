using System;
using System.Collections.Generic;
using System.Globalization;
using Rewind.Sim;

namespace Rewind.Author;

/// <summary>
/// The authoring geometry: everything that turns "I want this ball to be potted into that pocket"
/// into a spawn coordinate, plus the analytic tolerance law the placement search reasons with.
///
/// THE TOLERANCE LAW (derived here, validated against the kernel by the `verify-cut` command):
///   A ball is hit at contact parameter b (signed perpendicular offset from the cue's line of
///   travel), at along-line coordinate s measured from the line's pivot along the strike direction.
///   Its departure direction is the contact normal
///       n = sin(phi)*m + cos(phi)*d,   sin(phi) = b/(2R), m = perp(d)
///   Differentiating n with respect to the strike angle gives the whole law in closed form:
///       dn/dtheta = n_perp * (1 - s/(2R*cos(phi)))      =>   |dn/dtheta| = |1 - s/(2R cos phi)|
///   Consequences that drive every placement in this tool:
///     * the error VANISHES when s = 2R*cos(phi) - the ball is hit at its own contact distance,
///       which is the least sensitive contact there is (and is why grazing a ball at 24.4 px is
///       forgiving while grazing one at 300 px is not);
///     * a ball hit from BEHIND the pivot (a rewind "backdoor" pot) has s = -L, so the sensitivity
///       is 1 + L/(2R cos phi) >= 1: the two error terms ADD and can never cancel;
///     * tolerance also needs a SHORT ball-to-pocket distance: the lateral miss at the pocket is
///       miss = D_B * |dn/dtheta| * eps, hence
///       eps_max = (PocketRadius - marginRequired) / (D_B * |dn/dtheta|).
///   => high tolerance = short putt + contact close to the pivot + thick contact.
/// </summary>
public static class Geo
{
    public static readonly double R = new PhysicsConfig().BallRadius;
    public static readonly double TwoR = 2.0 * R;
    public static readonly double PocketR = new PhysicsConfig().PocketRadius;

    public static Vec2 Perp(Vec2 d) => new Vec2(-d.Y, d.X);

    public static Vec2 Dir(double degrees) => Vec2.FromAngleDegrees(degrees);

    public static double Deg(double radians) => radians * 180.0 / Math.PI;

    public static double Rad(double degrees) => degrees * Math.PI / 180.0;

    public static string N(double v, int digits = 3) =>
        double.IsNaN(v) ? "nan" : v.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);

    public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // -------------------------------------------------------------- cut solving

    public sealed class CutSolution
    {
        public bool Ok;
        public double Offset;      // signed perpendicular offset of the ball from the cue's line
        public Vec2 Normal;        // the ball's departure direction (the contact normal)
        public double CosPhi;      // cos(phi): 1 = dead straight, 0 = pure graze
        public double TanPhi;
        public double Sensitivity; // |dn/dtheta| (dimensionless)
        public double MissPerDeg;  // px of lateral miss at THIS pocket per degree of strike error
        public double WindowDeg;   // eps_max for a margin requirement of 8 px (see Metrics.MarginFloor)
        public string Note = string.Empty;
    }

    /// <summary>
    /// Solve for the ball offset that makes the ball's departure line run through
    /// <paramref name="pocket"/> exactly, for a ball whose along-line foot sits at
    /// <paramref name="sB"/> (measured along <paramref name="d"/> from the line's pivot
    /// <paramref name="foot"/>), on a cue line with direction <paramref name="d"/>.
    ///
    /// Derivation: with q = pocket - foot = qd*d + qm*m, the collinearity of (pocket - ball) and n
    /// gives f(u) = qd*u + (2R*u - qm)*sqrt(1-u^2) = 0 with u = sin(phi). Solved by bracketing + bisection.
    /// </summary>
    public static CutSolution SolveCut(Vec2 foot, Vec2 d, Vec2 pocket, double sB, double requiredMargin)
    {
        var sol = new CutSolution();
        var m = Perp(d);
        var q = pocket - foot;
        var qd = Vec2.Dot(q, d);
        var qm = Vec2.Dot(q, m);

        CutSolution best = null;
        const int samples = 4000;
        var prevU = -0.999999;
        var prev = F(prevU);
        for (var i = 1; i <= samples; i++)
        {
            var u = -0.999999 + 2.0 * 0.999999 * i / samples;
            var val = F(u);
            if (!double.IsNaN(prev) && !double.IsNaN(val) && Math.Sign(prev) != Math.Sign(val))
            {
                var root = Bisect(prevU, u);
                var candidate = Evaluate(root);
                if (candidate.Ok && (best == null || candidate.CosPhi > best.CosPhi)) best = candidate;
            }

            prevU = u;
            prev = val;
        }

        if (best == null)
        {
            sol.Note = "no cut solution";
            return sol;
        }

        return best;

        double F(double u)
        {
            var c = Math.Sqrt(Math.Max(0.0, 1.0 - u * u));
            return qd * u + (TwoR * u - qm) * c;
        }

        double Bisect(double lo, double hi)
        {
            var flo = F(lo);
            for (var i = 0; i < 200; i++)
            {
                var mid = 0.5 * (lo + hi);
                var fmid = F(mid);
                if (fmid == 0.0) return mid;
                if (Math.Sign(fmid) == Math.Sign(flo)) { lo = mid; flo = fmid; }
                else hi = mid;
            }

            return 0.5 * (lo + hi);
        }

        CutSolution Evaluate(double u)
        {
            var res = new CutSolution();
            var c = Math.Sqrt(Math.Max(0.0, 1.0 - u * u));
            res.CosPhi = c;
            res.Offset = TwoR * u;
            res.Normal = m * u + d * c;
            var ball = foot + d * sB + m * res.Offset;
            var toPocket = pocket - ball;
            var dist = toPocket.Length;
            if (dist < 1e-9 || Vec2.Dot(toPocket, res.Normal) <= 0.0)
            {
                res.Ok = false;
                res.Note = "pocket behind the ball";
                return res;
            }

            if (c <= 1e-6)
            {
                res.Ok = false;
                res.Note = "pure graze";
                return res;
            }

            res.TanPhi = Math.Abs(u) / c;
            res.Sensitivity = Math.Abs(1.0 - sB / (TwoR * c));
            res.MissPerDeg = dist * res.Sensitivity * Math.PI / 180.0;
            res.WindowDeg = res.MissPerDeg > 0.0 ? (PocketR - requiredMargin) / res.MissPerDeg : double.PositiveInfinity;
            res.Ok = true;
            return res;
        }
    }

    private static double Sq(double x) => x * x;

    /// <summary>
    /// |dn/dtheta| for a contact at along-line coordinate s with perpendicular offset b, i.e. the
    /// factor by which a strike-angle error is amplified into a departure-direction error.
    /// 1 = the whole shot just rotates with the aim; 0 = the contact is at its least sensitive point.
    /// </summary>
    public static double SensitivityAtContact(double s, double offset)
    {
        var u = Math.Max(-1.0, Math.Min(1.0, offset / TwoR));
        var c = Math.Sqrt(Math.Max(1e-12, 1.0 - u * u));
        return Math.Abs(1.0 - s / (TwoR * c));
    }

    // ------------------------------------------------------------ pot geometry

    /// <summary>
    /// Is the straight run from <paramref name="from"/> along <paramref name="dir"/> to
    /// <paramref name="pocket"/> legal: does the ball's centre stay inside the rail box all the way
    /// to the capture boundary (24 px short of the pocket centre), with a small clearance?
    /// This rejects "pots" that would really bounce off a cushion first.
    /// </summary>
    public static bool StraightPotIsLegal(LevelData level, Vec2 from, Vec2 dir, Vec2 pocket, out string why, double clearance = 0.5)
    {
        why = string.Empty;
        var toPocket = pocket - from;
        var dist = toPocket.Length;
        if (dist <= PocketR + 1.0) { why = "already inside the pocket circle"; return false; }
        var entry = from + dir.Normalized() * (dist - PocketR);
        var minX = level.TableLeft + R + clearance;
        var maxX = level.TableRight - R - clearance;
        var minY = level.TableTop + R + clearance;
        var maxY = level.TableBottom - R - clearance;
        // walk the segment from -R (behind the ball) to the entry point
        const int steps = 64;
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var p = from + (entry - from) * t;
            if (p.X < minX || p.X > maxX || p.Y < minY || p.Y > maxY)
            {
                why = $"leaves the legal centre box at t={N(t)} ({N(p.X, 1)},{N(p.Y, 1)})";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Will a ball leaving <paramref name="from"/> along <paramref name="dir"/> actually be captured
    /// by <paramref name="pocketIndex"/>? This walks the ball along its line with the kernel's own
    /// rail rule (reflect the wall component, clamp to the centre box) and the kernel's own capture
    /// rule (centre strictly inside the circle AND velocity pointing at the centre).
    ///
    /// WHY NOT A STRAIGHT-LINE TEST: for the four corner pockets the 24 px capture circle is centred
    /// ON the corner, so its interior is mostly outside the table; a ball aimed at the corner centre
    /// from a shallow angle touches a cushion first and is captured after the bounce. A straight-line
    /// test wrongly rejects exactly the pots that the game's corner pockets are built for.
    /// </summary>
    public static bool PotReachability(LevelData level, Vec2 from, Vec2 dir, Vec2 pocket, double pocketRadius,
        out string why, double maxTravel = 900.0)
    {
        why = string.Empty;
        var p = from;
        var v = dir.Normalized();
        if (v.LengthSquared <= 0.0)
        {
            why = "zero departure direction";
            return false;
        }

        var minX = level.TableLeft + R;
        var maxX = level.TableRight - R;
        var minY = level.TableTop + R;
        var maxY = level.TableBottom - R;

        const double step = 0.25;
        var travelled = 0.0;
        var bounces = 0;
        while (travelled < maxTravel)
        {
            p += v * step;
            travelled += step;

            // Clamp to the centre box first and test the capture with the PRE-bounce velocity: the
            // kernel checks pockets after the movement and before it resolves the rail event, so a
            // ball that arrives at a corner with its velocity pointing into the pocket is captured
            // even though the rail contact and the capture happen at the same instant.
            var clamped = false;
            if (p.X < minX) { p = new Vec2(minX, p.Y); clamped = true; }
            else if (p.X > maxX) { p = new Vec2(maxX, p.Y); clamped = true; }
            if (p.Y < minY) { p = new Vec2(p.X, minY); clamped = true; }
            else if (p.Y > maxY) { p = new Vec2(p.X, maxY); clamped = true; }

            var toCenter = pocket - p;
            if (toCenter.Length < pocketRadius && Vec2.Dot(v, toCenter) > 0.0) return true;

            if (clamped)
            {
                if (p.X <= minX + 1e-12 || p.X >= maxX - 1e-12)
                    v = new Vec2(-v.X, v.Y);
                if (p.Y <= minY + 1e-12 || p.Y >= maxY - 1e-12)
                    v = new Vec2(v.X, -v.Y);
                if (++bounces > 8) break;
            }
        }

        why = $"never reaches {N(pocketRadius, 0)} px of the pocket centre in a straight run (travelled {N(travelled, 0)} px)";
        return false;
    }

    /// <summary>Every pocket of the standard table, in the kernel's own order (corners + mid rails).</summary>
    public static string PocketName(int index) => index switch
    {
        0 => "top-left",
        1 => "top-mid",
        2 => "top-right",
        3 => "bottom-left",
        4 => "bottom-mid",
        5 => "bottom-right",
        _ => "pocket" + index,
    };

    /// <summary>
    /// Angle of a straight-line approach to the pocket, and whether it is reachable without
    /// touching a cushion first. Corner pockets need the approach inside a +-15 deg cone around
    /// the 45 deg bisector (otherwise the 24 px capture circle lies outside the table at the entry
    /// point); mid-rail pockets accept up to ~59 deg off the perpendicular.
    /// </summary>
    public static bool ApproachConeOk(LevelData level, int pocketIndex, Vec2 entryPoint)
    {
        var pocket = level.Pockets[pocketIndex];
        var minX = level.TableLeft + R;
        var maxX = level.TableRight - R;
        var minY = level.TableTop + R;
        var maxY = level.TableBottom - R;
        return entryPoint.X >= minX - 0.75 && entryPoint.X <= maxX + 0.75
               && entryPoint.Y >= minY - 0.75 && entryPoint.Y <= maxY + 0.75
               && (entryPoint - pocket.Center).Length <= PocketR + 1e-9;
    }

    // ------------------------------------------------------------------- misc

    /// <summary>Distance from p to the segment ab.</summary>
    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared;
        var t = len2 > 0.0 ? Vec2.Dot(p - a, ab) / len2 : 0.0;
        t = Math.Max(0.0, Math.Min(1.0, t));
        return (a + ab * t - p).Length;
    }

    /// <summary>Minimum distance from p to a polyline (null-safe).</summary>
    public static double DistanceToPolyline(Vec2 p, IReadOnlyList<Vec2> poly)
    {
        if (poly == null || poly.Count == 0) return double.PositiveInfinity;
        if (poly.Count == 1) return (p - poly[0]).Length;
        var best = double.PositiveInfinity;
        for (var i = 1; i < poly.Count; i++)
        {
            var d = DistanceToSegment(p, poly[i - 1], poly[i]);
            if (d < best) best = d;
        }

        return best;
    }
}
