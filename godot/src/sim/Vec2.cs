using System;

namespace Rewind.Sim;

/// <summary>
/// Double-precision 2D vector. The whole simulation uses <c>double</c> and this struct
/// only: no Godot types, no float, no engine math. That keeps the kernel deterministic
/// and replayable across processes (see docs/DIFFERENCES.md "determinism hygiene").
/// </summary>
public readonly struct Vec2 : IEquatable<Vec2>
{
    public readonly double X;
    public readonly double Y;

    public Vec2(double x, double y)
    {
        X = x;
        Y = y;
    }

    public static readonly Vec2 Zero = new Vec2(0.0, 0.0);

    public double LengthSquared => X * X + Y * Y;

    public double Length => Math.Sqrt(X * X + Y * Y);

    /// <summary>True when both components are finite (no NaN, no Infinity).</summary>
    public bool IsFinite => !double.IsNaN(X) && !double.IsInfinity(X) && !double.IsNaN(Y) && !double.IsInfinity(Y);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);

    public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);

    public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);

    public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);

    public static Vec2 operator *(double s, Vec2 a) => new Vec2(a.X * s, a.Y * s);

    public static Vec2 operator /(Vec2 a, double s) => new Vec2(a.X / s, a.Y / s);

    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public Vec2 Normalized()
    {
        var len = Length;
        return len > 0.0 ? new Vec2(X / len, Y / len) : Zero;
    }

    public static Vec2 FromAngleDegrees(double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return new Vec2(Math.Cos(rad), Math.Sin(rad));
    }

    /// <summary>
    /// Length-preserving rescale to <paramref name="newLength"/>. Returns Zero for a zero vector.
    /// </summary>
    public Vec2 WithLength(double newLength)
    {
        var len = Length;
        if (len <= 0.0) return Zero;
        var k = newLength / len;
        return new Vec2(X * k, Y * k);
    }

    public bool Equals(Vec2 other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object obj) => obj is Vec2 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    /// <summary>Round-trippable, culture-invariant ("R" always round-trips on .NET Core).</summary>
    public override string ToString() =>
        X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
        Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
