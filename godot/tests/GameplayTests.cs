using System;
using Godot;
using Rewind.Sim;

namespace Rewind.Tests;

/// <summary>
/// The three gameplay changes requested together, each of which is a rule rather than a look:
///
///   R  the cue strikes OPPOSITE the pull (a cue stick, not a push)
///   P  the cue ball's pocket mouth is wider than an object ball's
///   W  EVERY rail contact is recorded, for every ball, with its own wall and contact point
///
/// WHY R AND W ARE TESTED IN THE KERNEL-VIEW BOUNDARY AND NOT BY A SCREENSHOT: both were invisible in
/// every capture this project already had. The direction bug survived because the SCRIPTED captures go
/// through SetAim (which had the direction right) while live play went through the mouse handler (which
/// did not) -- so the recorded frames looked correct. The rail bug survived because captures were taken
/// on shots where the cue hit the rail, the one case the old cue-indexed reconstruction handled.
///
/// A silent failure here is invisible without a device in hand, which is exactly why these are asserted
/// rather than eyeballed.
/// </summary>
public static class GameplayTests
{
    /// <summary>
    /// R01: the ball leaves opposite the pull, on both axes, and the aim angle is the reversed pull.
    ///
    /// The specific bug: Play.HandleMouse set the aim angle from the raw drag vector, so pulling
    /// down-left sent the ball down-left. SetAim -- used by every scripted capture -- placed the stick
    /// behind the ball instead, so the two disagreed and only the captured path was ever checked.
    /// </summary>
    public static void R01_StrikeIsOppositeThePull(TestRunner t)
    {
        t.Section("R01 - the cue strikes opposite the pull, on both axes");

        // The four cardinal pulls. The launched velocity must be the NEGATIVE of the pull direction.
        foreach (var (label, pull, expectX, expectY) in new (string, Vec2, double, double)[]
                 {
                     ("pull +x -> leave -x", new Vec2(1, 0), -1, 0),
                     ("pull -x -> leave +x", new Vec2(-1, 0), 1, 0),
                     ("pull +y -> leave -y", new Vec2(0, 1), 0, -1),
                     ("pull -y -> leave +y", new Vec2(0, -1), 0, 1),
                 })
        {
            var level = TestSupport.Table(pockets: false);
            level.AddCue(300, 280);
            var w = new World(level);

            // Mirror what the mouse handler computes: angle = pull + 180.
            var pullDeg = Math.Atan2(pull.Y, pull.X) * 180.0 / Math.PI;
            var aim = pullDeg + 180.0;
            t.Check($"{label}: the shot is accepted", w.Shot(aim, 170.0), "refused");

            var v = w.Cue.Velocity.Normalized();
            t.Check($"{label}: the launch is the reversed pull",
                Math.Abs(v.X - expectX) < 1e-9 && Math.Abs(v.Y - expectY) < 1e-9,
                $"pull=({pull.X},{pull.Y}) launched=({v.X:F6},{v.Y:F6})");
            t.Check($"{label}: it is not the pull direction itself",
                !(Math.Abs(v.X - pull.X) < 1e-6 && Math.Abs(v.Y - pull.Y) < 1e-6),
                $"v=({v.X:F6},{v.Y:F6}) -- the ball is still following the hand");
        }
    }

    /// <summary>
    /// R02: the angle the handler derives from a drag is exactly the reversed drag direction, and is
    /// wrapped so that a pull near +/-180 does not produce an un-normalised angle.
    ///
    /// The wrap matters for more than tidiness: an aim of 180.0001 and one of -179.9999 are the same
    /// shot, and a comparison or a cache keyed on the raw value would treat them as different.
    /// </summary>
    public static void R02_AimAngleIsTheReversedPull(TestRunner t)
    {
        t.Section("R02 - the derived aim angle is the reversed, wrapped pull direction");

        foreach (var (label, pull, expected) in new (string, Vec2, double)[]
                 {
                     ("straight right", new Vec2(10, 0), 180.0),
                     ("straight left", new Vec2(-10, 0), 0.0),
                     ("straight up", new Vec2(0, 10), -90.0),
                     ("straight down", new Vec2(0, -10), 90.0),
                 })
        {
            var deg = Math.Atan2(pull.Y, pull.X) * 180.0 / Math.PI;
            var aim = Angles.Wrap180(deg + 180.0);
            t.Check($"{label}: aim is {expected} deg", Math.Abs(aim - expected) < 1e-9,
                $"got {aim}");
        }

        // Wrapping: every input must land in (-180, 180].
        foreach (var raw in new[] { 540.0, -540.0, 720.0, -180.0, 180.0, 359.0, -359.0 })
        {
            var wrapped = Angles.Wrap180(raw);
            t.Check($"wrap({raw}) is in (-180, 180] and equivalent",
                wrapped > -180.0 && wrapped <= 180.0
                && Math.Abs(Math.IEEERemainder(raw - wrapped, 360.0)) < 1e-9,
                $"wrap({raw}) = {wrapped}");
        }
    }

    /// <summary>
    /// P01: the cue's capture radius is wider than an object ball's, and the locked shared radius is
    /// untouched -- the object-ball circle defines the win condition, so widening it would quietly make
    /// every authored level easier.
    /// </summary>
    public static void P01_CueHasAWiderMouthOnly(TestRunner t)
    {
        t.Section("P01 - only the cue ball gets the wider pocket mouth");

        var cfg = new PhysicsConfig();
        t.Check("the locked shared radius is unchanged at 24 px",
            Math.Abs(cfg.PocketRadius - 24.0) < 1e-9, $"PocketRadius={cfg.PocketRadius}");
        t.Check("the cue multiplier is above 1", cfg.CuePocketRadiusScale > 1.0,
            $"scale={cfg.CuePocketRadiusScale}");
        t.Check("the widening is modest -- around 30%, not a circus mouth",
            cfg.CuePocketRadiusScale >= 1.15 && cfg.CuePocketRadiusScale <= 1.5,
            $"scale={cfg.CuePocketRadiusScale}");

        var level = TestSupport.Table();
        level.AddCue(300, 280);
        level.AddObject(700, 280);
        var w = new World(level);

        // A point just outside the shared circle but inside the cue's widened one must capture the cue
        // and NOT an object ball. Distances are measured from a pocket centre.
        var pocket = w.Level.Pockets[0];
        var scale = w.Config.CuePocketRadiusScale;
        var probe = 24.0 * (1.0 + scale) * 0.5;   // midway between the two radii
        t.Check("the probe distance really does separate the two radii",
            probe > 24.0 && probe < 24.0 * scale, $"probe={probe:F2}");

        // Place the cue on the probe ring, moving TOWARD the pocket so the dot-product condition passes.
        var cueBall = w.Cue;
        var dir = (pocket.Center - new Vec2(pocket.Center.X, pocket.Center.Y + probe)).Normalized();
        cueBall.Position = pocket.Center - dir * probe;
        cueBall.Velocity = dir * 400.0;
        t.Check("the cue IS captured on the widened ring", w.InPocketCapture(cueBall),
            $"probe={probe:F2} radius={24.0 * scale:F2}");

        var obj = w.Balls[1];
        obj.Position = cueBall.Position;
        obj.Velocity = cueBall.Velocity;
        t.Check("an object ball at the SAME spot is NOT captured",
            !w.InPocketCapture(obj),
            "the win condition's circle was widened too, which would make every level easier");
    }

    /// <summary>
    /// W01: every ball's rail contact is recorded, with its own wall and a contact point that lies ON
    /// that wall -- not reconstructed from the cue.
    ///
    /// The reported symptom was "a bounce effect only appears when a boost happens". The cause was that
    /// the view rebuilt the geometry from the CUE ball, so an object ball's contact (cue parked in the
    /// middle of the table) produced no wall at all and nothing was drawn. Since the cue's allowance is
    /// unlimited and object balls have none, that presented exactly as "only boosted hits show".
    /// </summary>
    public static void W01_EveryBallRecordsItsOwnContact(TestRunner t)
    {
        t.Section("W01 - rail contacts are recorded per ball, with a real wall and contact point");

        // The cue is driven into the LEFT rail so its own contact is unambiguous. The object ball is
        // parked adjacent so it gets struck and sent off as well.
        var level = TestSupport.Table(pockets: false);
        level.AddCue(300, 280);
        level.AddObject(180, 280);
        var w = new World(level);

        var before = w.RailEventsTotal;
        t.Check("nothing is recorded before a shot", before == 0, $"total={before}");

        w.Shot(180.0, 170.0);           // leave along -x, i.e. toward the left rail
        for (var i = 0; i < 4000 && !w.AtRest(); i++) w.Step();

        var total = w.RailEventsTotal;
        t.Check("contacts were recorded", total > 0, $"total={total}");
        t.Check("one event per rail transform, exactly",
            total == w.Stats.RailTransforms,
            $"events={total} transforms={w.Stats.RailTransforms}");

        var events = new System.Collections.Generic.List<World.RailHitEvent>();
        w.CopyRailEventsSince(0, events);
        t.Check("the events can be read back", events.Count > 0, $"count={events.Count}");

        var left = w.Level.TableLeft;
        var right = w.Level.TableRight;
        var top = w.Level.TableTop;
        var bottom = w.Level.TableBottom;

        var badWall = 0;
        var offWall = 0;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Wall < 0 || e.Wall > 3) badWall++;

            // The contact point must lie ON the wall it names, within a ball radius of slop (the ball's
            // radius is added along the normal, so the tangential coordinate is what has to match).
            var onWall = e.Wall switch
            {
                0 => Math.Abs(e.Contact.X - left) <= 13.0,
                1 => Math.Abs(e.Contact.X - right) <= 13.0,
                2 => Math.Abs(e.Contact.Y - top) <= 13.0,
                _ => Math.Abs(e.Contact.Y - bottom) <= 13.0,
            };
            if (!onWall) offWall++;
        }

        t.Check("every event names a real wall", badWall == 0, $"badWall={badWall}");
        t.Check("every contact point lies on the wall it names", offWall == 0, $"offWall={offWall}");

        // The cue was launched at the left rail, so a contact on the left wall must be present.
        var sawLeft = false;
        foreach (var e in events)
        {
            if (e.Wall == 0) sawLeft = true;
        }

        t.Check("the left rail contact is among them", sawLeft, "no wall=0 event");
    }

    /// <summary>
    /// W02: a contact made by an OBJECT ball is recorded too -- the exact case the old view dropped.
    ///
    /// Arranged so the cue is NOT the ball that reaches the cushion, which is the only way to catch a
    /// regression back to a cue-indexed reconstruction: if the view ever goes back to inferring the wall
    /// from the cue, this event still exists in the kernel and the count check below still passes, so
    /// the failure would show up as the object ball's contact having no effect rather than as a wrong
    /// number. The assertion that matters is therefore that the event names the object ball.
    /// </summary>
    public static void W02_ObjectBallContactsAreRecorded(TestRunner t)
    {
        t.Section("W02 - an object ball's own contact is recorded (the case the old view dropped)");

        // The cue points AWAY from the rails; the object ball is placed so the cue drives it into the
        // right rail. The cue then follows into the middle of the table and does eventually stop.
        var level = TestSupport.Table(pockets: false);
        level.AddCue(400, 280);
        level.AddObject(600, 280);
        var w = new World(level);

        w.Shot(0.0, 170.0);             // leave along +x: into the object ball, toward the right rail
        for (var i = 0; i < 4000 && !w.AtRest(); i++) w.Step();

        var events = new System.Collections.Generic.List<World.RailHitEvent>();
        w.CopyRailEventsSince(0, events);
        t.Check("contacts were recorded", events.Count > 0, $"count={events.Count}");

        var cueIndex = -1;
        var objectIndex = -1;
        for (var i = 0; i < w.Balls.Length; i++)
        {
            if (w.Balls[i].IsCue) cueIndex = i;
            else if (objectIndex < 0) objectIndex = i;
        }

        t.Check("the two roles are identifiable", cueIndex >= 0 && objectIndex >= 0,
            $"cue={cueIndex} object={objectIndex}");

        var objectContacts = 0;
        var cueContacts = 0;
        foreach (var e in events)
        {
            if (e.BallIndex == objectIndex) objectContacts++;
            if (e.BallIndex == cueIndex) cueContacts++;
        }

        t.Check("the OBJECT ball's contact is present, named to the object ball",
            objectContacts > 0,
            $"objectContacts={objectContacts} cueContacts={cueContacts} -- an object ball hit a rail and " +
            "nothing was recorded for it");
        t.Check("every event names one of the two balls",
            objectContacts + cueContacts == events.Count,
            $"object={objectContacts} cue={cueContacts} total={events.Count}");
    }

    /// <summary>
    /// W03: reading is incremental -- asking twice must not replay the same contacts, or one bounce
    /// would spawn its effect on every subsequent frame until it aged out.
    /// </summary>
    public static void W03_ReadingIsIncremental(TestRunner t)
    {
        t.Section("W03 - rail events are consumed once, never replayed");

        var level = TestSupport.Table(pockets: false);
        level.AddCue(300, 280);
        var w = new World(level);
        w.Shot(180.0, 170.0);

        // Walk in two stages so there is genuinely something new to read the second time. A level allows
        // one shot, so a second Shot() is refused; stepping the SAME shot further is both the only way to
        // produce new contacts and the case the frame loop actually performs.
        for (var i = 0; i < 30 && !w.AtRest(); i++) w.Step();

        var first = new System.Collections.Generic.List<World.RailHitEvent>();
        var cursor = w.CopyRailEventsSince(0, first);
        t.Check("the first read returns everything so far", first.Count == w.RailEventsTotal,
            $"read={first.Count} total={w.RailEventsTotal}");
        t.Check("some contacts had already happened by the first read", first.Count > 0,
            $"read={first.Count} -- the fixture needs a contact this early, otherwise the replay check " +
            "below cannot fail");

        var second = new System.Collections.Generic.List<World.RailHitEvent>();
        var cursor2 = w.CopyRailEventsSince(cursor, second);
        t.Check("reading again with the new cursor returns NOTHING", second.Count == 0,
            $"replayed {second.Count} events");
        t.Check("the cursor did not move when nothing was added", cursor2 == cursor,
            $"{cursor} -> {cursor2}");

        // Now let the rally continue and read again: the new contacts must arrive exactly once.
        for (var i = 0; i < 4000 && !w.AtRest(); i++) w.Step();

        var third = new System.Collections.Generic.List<World.RailHitEvent>();
        var cursor3 = w.CopyRailEventsSince(cursor2, third);
        t.Check("contacts produced after the second read are delivered", third.Count > 0,
            $"count={third.Count} cursor={cursor2} total={w.RailEventsTotal} transforms={w.Stats.RailTransforms}");
        t.Check("the delivered contacts are exactly the ones not yet seen",
            third.Count == w.RailEventsTotal - cursor2,
            $"delivered={third.Count} unseen={w.RailEventsTotal - cursor2}");

        var fourth = new System.Collections.Generic.List<World.RailHitEvent>();
        w.CopyRailEventsSince(cursor3, fourth);
        t.Check("and they are not delivered a second time", fourth.Count == 0,
            $"replayed {fourth.Count}");
    }
}
