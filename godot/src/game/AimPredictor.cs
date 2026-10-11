using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// Exact aim prediction (port plan 搂4.5 "鐬勫噯棰勬祴绾?).
///
/// The physics is deterministic, so this is NOT a fixed-length ray or a cosine approximation: the
/// prediction is produced by cloning the live <see cref="World"/> and actually running the shot at
/// the candidate angle/power to the first contact, then reporting what the kernel did. Same
/// integrator, same constants, same TOI solver = the drawn prediction is the shot.
///
/// Result contents:
///  * the cue's path polyline up to the first contact (one vertex per physics step, so its chord
///    error obeys the kernel's 1.0 px sub-step travel budget);
///  * the first-contact point and whether it is a ball or a rail;
///  * the struck ball's resulting direction (the ghost-ball construction falls out of the kernel:
///    the contact normal is the struck ball's post-contact velocity direction);
///  * the cue's own post-contact deflection direction, or "stops dead";
///  * the ghost cue circle at the moment of contact;
///  * where the cue would stop if nothing else happened.
/// </summary>
public static class AimPredictor
{
    public enum ContactKind
    {
        None,
        ObjectBall,
        CueBallRail,
        BallRail,
    }

    public sealed class Prediction
    {
        public bool Valid;
        public double AngleDeg;
        public double Power;
        public double Speed;              // launch speed px/s
        public bool WouldBeRefused;       // power below the locked minimum

        public readonly List<Vector2> CuePath = new List<Vector2>();
        public Vector2 CueStart;
        public Vector2 ContactPoint;
        public ContactKind Contact = ContactKind.None;
        public int ContactBallId = -1;
        public Vector2 ContactNormal;

        public Vector2 StruckDirection;   // unit vector the struck ball leaves along
        public double StruckSpeed;
        public Vector2 CueDeflectDirection;
        public double CueDeflectSpeed;

        public Vector2 GhostCenter;       // cue centre at the moment of contact
        public Vector2 StopPoint;         // where the cue settles (if the trace ran to rest)
        public bool ReachedRest;
        public double TimeToContact;
        public int Steps;
        public ulong Microseconds;

        /// <summary>Balls whose *next* event after the first contact is also worth showing.</summary>
        public readonly List<(int Id, Vector2 From, Vector2 Dir, double Speed)> Secondary = new List<(int, Vector2, Vector2, double)>();

        /// <summary>Fill in the struck-ball / cue-deflection outcome from the post-contact state.</summary>
        public void StruckVelocity(Ball struck, Vec2 cueVel)
        {
            var n = struck.Velocity;
            var dir = new Vector2((float)n.X, (float)n.Y);
            if (dir.LengthSquared() > 1e-12)
            {
                StruckDirection = dir.Normalized();
                StruckSpeed = struck.Speed;
                ContactNormal = StruckDirection;
            }
            else
            {
                // The struck ball did not move (zero normal impulse): the normal is the cue's
                // arrival direction, which is still the right thing to draw.
                var arrival = new Vector2((float)cueVel.X, (float)cueVel.Y);
                ContactNormal = arrival.LengthSquared() > 1e-12 ? arrival.Normalized() : Vector2.Zero;
                StruckDirection = Vector2.Zero;
            }

            var cd = new Vector2((float)cueVel.X, (float)cueVel.Y);
            if (cd.LengthSquared() > 1e-9)
            {
                CueDeflectDirection = cd.Normalized();
                CueDeflectSpeed = (float)cueVel.Length;
            }
            else
            {
                CueDeflectDirection = Vector2.Zero;
                CueDeflectSpeed = 0f;
            }
        }
    }

    /// <summary>Cache key: the prediction only depends on (base state, angle, power).</summary>
    public sealed class Cache
    {
        public int HistoryCount = -1;
        public int Tick = -1;
        public double Angle = double.NaN;
        public double Power = double.NaN;
        public bool ShotUsed;
        public bool Won;
        public Prediction Value;
        private World _world;
        private int _shotsUsed;
        private bool _frozen;
        private bool _rewinding;
        private Vec2[] _positions = Array.Empty<Vec2>();
        private Vec2[] _velocities = Array.Empty<Vec2>();
        private bool[] _pocketed = Array.Empty<bool>();

        public bool Matches(World w, double angle, double power)
        {
            if (!(Value != null
                   && ReferenceEquals(_world, w)
                   && HistoryCount == w.Cue.History.Count
                   // Before the first strike, an unchanged resting world has the same future.
                   // Later shots retain the tick key because white-hole cooldowns depend on time.
                   && (Tick == w.Tick || (w.Tick > Tick && _shotsUsed == 0 && w.AtRest()))
                   && _shotsUsed == w.ShotsUsed
                   && _frozen == w.Frozen
                   && _rewinding == w.Rewinding
                   && ShotUsed == w.ShotUsed
                   && Won == w.Won
                   && Math.Abs(Angle - angle) < 1e-9
                   && Math.Abs(Power - power) < 1e-9)) return false;

            for (var i = 0; i < w.Balls.Length; i++)
            {
                var ball = w.Balls[i];
                if (!ball.Position.Equals(_positions[i]) || !ball.Velocity.Equals(_velocities[i])
                    || ball.Pocketed != _pocketed[i]) return false;
            }
            return true;
        }

        public void Store(World w, double angle, double power, Prediction p)
        {
            HistoryCount = w.Cue.History.Count;
            Tick = w.Tick;
            Angle = angle;
            Power = power;
            ShotUsed = w.ShotUsed;
            Won = w.Won;
            Value = p;
            _world = w;
            _shotsUsed = w.ShotsUsed;
            _frozen = w.Frozen;
            _rewinding = w.Rewinding;
            if (_positions.Length != w.Balls.Length)
            {
                _positions = new Vec2[w.Balls.Length];
                _velocities = new Vec2[w.Balls.Length];
                _pocketed = new bool[w.Balls.Length];
            }
            for (var i = 0; i < w.Balls.Length; i++)
            {
                _positions[i] = w.Balls[i].Position;
                _velocities[i] = w.Balls[i].Velocity;
                _pocketed[i] = w.Balls[i].Pocketed;
            }
        }

        public void Invalidate() => Value = null;
    }

    private const int MaxSteps = 4000;
    private const double MaxSeconds = 40.0;

    /// <summary>
    /// Run the candidate shot in a cloned world and describe the outcome exactly.
    /// Never touches the live world.
    /// </summary>
    public static Prediction Trace(World live, double angleDeg, double power, int maxSteps = MaxSteps)
    {
        var t0 = Time.GetTicksUsec();
        var result = new Prediction { AngleDeg = angleDeg, Power = power };

        if (live.Won || live.ShotUsed || live.Rewinding || live.Frozen) return result;

        var clamped = Math.Min(power, live.Config.PowerMax);
        result.WouldBeRefused = power < live.Config.MinPower;
        result.Speed = Math.Min(clamped * live.Config.SpeedPerPower, live.Config.SpeedCap);

        var ghost = live.Clone();
        if (!ghost.Shot(angleDeg, power))
        {
            result.Microseconds = Time.GetTicksUsec() - t0;
            return result;
        }

        result.Valid = true;
        result.CueStart = new Vector2((float)ghost.Cue.Position.X, (float)ghost.Cue.Position.Y);
        result.CuePath.Add(result.CueStart);
        result.GhostCenter = result.CueStart;

        var prevCue = ghost.Cue.Position;
        var prevVel = ghost.Cue.Velocity;
        var prevObjectVel = SnapshotObjectVelocities(ghost);

        var contactTick = -1;

        // NOTE on the contact test. Velocity *magnitude* changes every step (friction is applied
        // once per step to every ball), so "the velocity changed" is NOT a contact. A contact is a
        // direction discontinuity: the cue's heading turns by more than a degree within one step
        // (rail reflection), or another ball starts moving. Friction cannot turn a ball.
        for (var step = 0; step < maxSteps; step++)
        {
            ghost.Step();
            result.Steps = step + 1;

            var cuePos = ghost.Cue.Position;
            result.CuePath.Add(new Vector2((float)cuePos.X, (float)cuePos.Y));

            var cueVel = ghost.Cue.Velocity;
            var struck = FindStruckBall(ghost, prevObjectVel);
            var railHit = struck < 0 && HeadingChanged(prevVel, cueVel);

            if (struck >= 0 || railHit)
            {
                contactTick = step + 1;
                result.Contact = struck >= 0 ? ContactKind.ObjectBall : ContactKind.CueBallRail;
                result.ContactBallId = struck;

                // The contact itself happened inside this step. For a ball-ball contact the cue is
                // still travelling along its incoming line when it touches, so the last position
                // before the turn is the contact point; for a rail reflection it is the clamped
                // position after the step.
                result.ContactPoint = struck >= 0
                    ? new Vector2((float)prevCue.X, (float)prevCue.Y)
                    : new Vector2((float)cuePos.X, (float)cuePos.Y);
                result.GhostCenter = result.ContactPoint;
                result.TimeToContact = contactTick * ghost.Config.SimulationStep;

                if (struck >= 0)
                {
                    var b = ghost.Balls[struck];
                    result.StruckVelocity(b, cueVel);
                }
                else
                {
                    var d = new Vector2((float)cueVel.X, (float)cueVel.Y);
                    result.ContactNormal = d.LengthSquared() > 1e-9 ? -d.Normalized() : Vector2.Zero;
                    result.CueDeflectDirection = d.LengthSquared() > 1e-9 ? d.Normalized() : Vector2.Zero;
                    result.CueDeflectSpeed = (float)cueVel.Length;
                }

                break;
            }

            prevCue = cuePos;
            prevVel = cueVel;
            for (var i = 0; i < ghost.Balls.Length; i++) prevObjectVel[i] = ghost.Balls[i].Velocity;

            // A weak shot may stop without contacting anything. There is no remaining path to trace.
            if (ghost.AtRest() || ghost.Won || ghost.Frozen) break;
        }

        // Where does the cue stop? Continue the same clone: the clone is the future.
        var extra = 0;
        while (extra < maxSteps * 4 && !ghost.Won && !ghost.Frozen && !ghost.AtRest())
        {
            ghost.Step();
            extra++;
            if (ghost.AtRest()) break;
            if (ghost.Time > MaxSeconds) break;
        }

        result.ReachedRest = ghost.AtRest();
        result.StopPoint = new Vector2((float)ghost.Cue.Position.X, (float)ghost.Cue.Position.Y);
        result.Microseconds = Time.GetTicksUsec() - t0;
        return result;
    }

    /// <summary>True when the heading turned by more than 1 degree inside one step (a reflection).</summary>
    private static bool HeadingChanged(Vec2 before, Vec2 after)
    {
        var a = before.Length;
        var b = after.Length;
        if (a <= 1e-9 || b <= 1e-9) return false;
        var cos = Vec2.Dot(before, after) / (a * b);
        return cos < 0.99985; // cos(1 degree)
    }

    private static int FindStruckBall(World w, Vec2[] before)
    {
        for (var i = 1; i < w.Balls.Length; i++)
        {
            var b = w.Balls[i];
            var v = b.Velocity;
            var pv = before[i];
            if (b.Pocketed) continue;
            var d = (v - pv).Length;
            if (d > 1e-9) return i;
        }

        return -1;
    }

    private static Vec2[] SnapshotObjectVelocities(World w)
    {
        var a = new Vec2[w.Balls.Length];
        for (var i = 0; i < w.Balls.Length; i++) a[i] = w.Balls[i].Velocity;
        return a;
    }

    /// <summary>
    /// Plot the prediction. Ghost-ball construction: the cue ball is drawn where it would be at the
    /// moment of contact, the struck ball's leave direction is drawn from the impact normal.
    /// </summary>
    public static void Draw(CanvasItem ci, Prediction p, Font font)
    {
        if (p == null || !p.Valid || p.CuePath.Count < 2) return;

        // 1. The exact cue path to the first contact, drawn from the kernel's own steps.
        var pts = p.CuePath.ToArray();
        ci.DrawPolyline(pts, p.WouldBeRefused ? Palette.AimBlocked : Palette.AimLine, 1.6f, true);

        // 2. Ghost cue ball at the contact moment.
        var r = 12.2f;
        ci.DrawCircle(p.GhostCenter, r, Palette.GhostBall, false, 1.6f, true);
        ci.DrawCircle(p.GhostCenter, r, new Color(Palette.GhostBall.R, Palette.GhostBall.G, Palette.GhostBall.B, 0.10f), true, -1.0f, true);

        // 3. First contact marker.
        if (p.Contact != ContactKind.None)
        {
            ci.DrawCircle(p.ContactPoint, 4.0f, Palette.AimFirstContact, true, -1.0f, true);
            ci.DrawCircle(p.ContactPoint, 7.5f, Palette.AimFirstContact, false, 1.4f, true);
        }

        // 4. The struck ball's leave direction: the collision normal through the ball centre.
        if (p.Contact == ContactKind.ObjectBall && p.StruckDirection.LengthSquared() > 1e-9)
        {
            var from = p.ContactPoint;
            var to = from + p.StruckDirection * 90.0f;
            ci.DrawDashedLine(from, to, Palette.AimTargetDir, 2.0f, 6.0f, true);
            var tip = to + p.StruckDirection * 8.0f;
            var n = new Vector2(-p.StruckDirection.Y, p.StruckDirection.X);
            ci.DrawColoredPolygon(new[] { to + p.StruckDirection * 9.0f, tip + n * 6.0f - p.StruckDirection * 9.0f, tip - n * 6.0f - p.StruckDirection * 9.0f }, Palette.AimTargetDir);
        }

        // 5. Cue deflection after contact (tangent-ish), or "stops dead".
        if (p.CueDeflectDirection.LengthSquared() > 1e-9 && p.CueDeflectSpeed > 1.0f)
        {
            var to = p.ContactPoint + p.CueDeflectDirection * 55.0f;
            ci.DrawLine(p.ContactPoint, to, Palette.AimCueDeflect, 1.2f, true);
        }
        else if (p.Contact != ContactKind.None)
        {
            ci.DrawString(font, p.ContactPoint + new Vector2(8, 16), "白球停住", HorizontalAlignment.Left, -1, 11, Palette.TextDim);
        }

        // 6. Where the cue would settle.
        ci.DrawCircle(p.StopPoint, 3.0f, Palette.AimTargetDir, false, 1.2f, true);

        if (font != null)
        {
            var label = $"速度 {p.Speed:F0} px/s  接触 {p.TimeToContact * 1000.0:F0} ms";
            ci.DrawString(font, p.CueStart + new Vector2(14, -14), label, HorizontalAlignment.Left, -1, 11, Palette.TextDim);
        }
    }
}
