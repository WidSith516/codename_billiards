using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// Rewind presentation (port plan §4.4 "撞击预告高亮" + "回溯可暂停 / scrubbing").
///
/// Two jobs:
///
/// 1. **Visualisation.** While rewinding, the cue is drawn as a rewind-coloured object (not "a
///    white ball that happens to be moving"), the traversed part of the path is highlighted, and
///    the still-to-be-walked part is drawn dim. The planned end of the path is marked.
///
/// 2. **Contact forecast.** Before the player releases, the balls the rewind is about to push are
///    highlighted so cause precedes effect visibly instead of surprising. The forecast walks the
///    frozen snapshot at half a history node (1 px) and fires a contact when the cue-to-ball
///    distance reaches the contact radius (2r). Object balls do not move during a rewind unless
///    the cue pushes them, so a first-pass forecast is exact; anything the forecast itself would
///    push is marked as such in the debug overlay rather than re-simulated.
///
/// Scrubbing: a rewind can be paused and the planned release point moved *backwards* along the
/// path. Moving it forwards is refused by design - a rewind that could skip ahead would not be a
/// rewind - and the UI says so instead of silently ignoring the input.
/// </summary>
public sealed class RewindVisualizer
{
    public enum ContactKind
    {
        Ball,
        Arrival,
    }

    public sealed class Forecast
    {
        public ContactKind Kind;
        public int BallId = -1;
        public double TrackIndex;
        public double ArcFromCursor;   // px of crawl from the start of the rewind
        public Vector2 At;             // cue centre when the contact happens
        public double Seconds;         // crawl time from the rewind start
    }

    /// <summary>Crawl distance the forecast looks ahead. 2.5 s at the locked 240 px/s crawl.</summary>
    public double LookaheadPx = 600.0;

    public int ContactCount => _ballContacts.Count;
    public IReadOnlyList<Forecast> BallContacts => _ballContacts;
    public double ForecastMicroseconds { get; private set; }

    private readonly List<Forecast> _ballContacts = new List<Forecast>();
    private Forecast _arrival;
    private int _forecastCursor = -1;
    private bool _hasForecast;

    /// <summary>Live-history sample range the crawl has already walked (for the scrub clamp).</summary>
    public int TraversedFrom { get; private set; } = -1;
    public int TraversedTo { get; private set; } = -1;

    /// <summary>Planned release point as a live-history sample index, or -1 when not scrubbing.</summary>
    public int PlannedReleaseIndex { get; private set; } = -1;
    public bool Scrubbing { get; private set; }
    public bool Paused { get; set; }

    /// <summary>Arc length already crawled in this rewind, px. Set by <see cref="Play"/> each frame.</summary>
    public double ArcWalked { get; set; }

    /// <summary>How far ahead the highlight starts fading in, in px of remaining crawl.</summary>
    public double FadeInPx = 260.0;

    public void Reset()
    {
        _ballContacts.Clear();
        _arrival = null;
        _forecastCursor = -1;
        _hasForecast = false;
        TraversedFrom = -1;
        TraversedTo = -1;
        PlannedReleaseIndex = -1;
        Scrubbing = false;
        Paused = false;
        ArcWalked = 0.0;
    }

    public void BeginScrub()
    {
        if (TraversedFrom < 0) return;
        Scrubbing = true;
        Paused = true;
        PlannedReleaseIndex = TraversedFrom;
    }

    public void EndScrub(bool keepPaused)
    {
        Scrubbing = false;
        PlannedReleaseIndex = -1;
        Paused = keepPaused;
    }

    public int MinReleaseIndex => TraversedFrom;
    public int MaxReleaseIndex => TraversedTo;

    public void SetPlannedRelease(int index) => PlannedReleaseIndex = Math.Clamp(index, TraversedFrom, TraversedTo);

    public void NudgePlannedRelease(int delta)
    {
        if (PlannedReleaseIndex < 0) PlannedReleaseIndex = TraversedTo;
        PlannedReleaseIndex = Math.Clamp(PlannedReleaseIndex + delta, TraversedFrom, TraversedTo);
    }

    /// <summary>True when a planned release is behind where the crawl is standing (reachable).</summary>
    public bool PlannedReleaseIsReachable => PlannedReleaseIndex >= 0 && TraversedFrom >= 0 && PlannedReleaseIndex <= TraversedTo;

    /// <summary>Recompute the forecast when a rewind starts (the snapshot is frozen, so it is stable).</summary>
    public void EnsureForecast(World world, int traversedFrom, int traversedTo)
    {
        TraversedFrom = traversedFrom;
        TraversedTo = traversedTo;
        if (!world.Rewinding || world.RewindTrack == null) return;
        if (_hasForecast && _forecastCursor == world.RewindCursor) return;

        var t0 = Time.GetTicksUsec();
        _ballContacts.Clear();
        _arrival = null;
        _hasForecast = true;
        _forecastCursor = world.RewindCursor;

        var track = world.RewindTrack;
        if (track.Length < 2) return;

        var step = Math.Max(1.0, world.Config.HistoryStepPx * 0.5);
        var contactDist = 2.0 * world.Config.BallRadius;

        var cursorArc = ArcAt(track, world.RewindCursor, world.RewindFraction);
        var budgetArc = Math.Min(LookaheadPx, cursorArc);

        var walked = 0.0;
        var i = world.RewindCursor;
        var u = world.RewindFraction;
        var pos = SampleAt(track, i, u);
        var blocked = new bool[world.Balls.Length];

        while (walked < budgetArc && i > 0)
        {
            var segLen = (track[i].Position - track[i - 1].Position).Length;
            var available = u * segLen;
            var take = Math.Min(step, available);
            if (take <= 1e-9)
            {
                i -= 1;
                u = 1.0;
                continue;
            }

            u -= take / segLen;
            if (u < 0.0) u = 0.0;
            walked += take;
            pos = SampleAt(track, i, u);

            for (var b = 1; b < world.Balls.Length; b++)
            {
                if (blocked[b]) continue;
                var ball = world.Balls[b];
                if (ball.Pocketed) continue;
                if ((ball.Position - new Vec2(pos.X, pos.Y)).Length > contactDist) continue;
                blocked[b] = true;
                _ballContacts.Add(new Forecast
                {
                    Kind = ContactKind.Ball,
                    BallId = b,
                    TrackIndex = i - 1.0 + u,
                    ArcFromCursor = walked,
                    At = new Vector2((float)pos.X, (float)pos.Y),
                    Seconds = walked / Math.Max(1e-9, world.Config.RewindCrawlPxPerS),
                });
            }

            if (i <= 0) break;
        }

        _arrival = new Forecast
        {
            Kind = ContactKind.Arrival,
            TrackIndex = 0.0,
            ArcFromCursor = cursorArc,
            At = new Vector2((float)track[0].Position.X, (float)track[0].Position.Y),
            Seconds = cursorArc / Math.Max(1e-9, world.Config.RewindCrawlPxPerS),
        };

        ForecastMicroseconds = Time.GetTicksUsec() - t0;
    }

    /// <summary>Highlight weight 0..1 for a ball that the rewind is about to push (0 = not forecast).</summary>
    public float HighlightWeight(int ballId)
    {
        foreach (var f in _ballContacts)
        {
            if (f.BallId != ballId) continue;
            var toGo = f.ArcFromCursor - ArcWalked;
            if (toGo < 0.0) return 0.0f;
            return 1.0f - Mathf.Clamp((float)(toGo / Math.Max(1.0, FadeInPx)), 0f, 1f);
        }

        return 0f;
    }

    /// <summary>Remaining crawl distance until the ball with this id is pushed, or double.NaN.</summary>
    public double DistanceUntil(int ballId)
    {
        foreach (var f in _ballContacts)
        {
            if (f.BallId == ballId) return f.ArcFromCursor - ArcWalked;
        }

        return double.NaN;
    }

    private static Vector2 SampleAt(HistorySample[] track, int index, double u)
    {
        index = Math.Clamp(index, 0, track.Length - 1);
        if (u >= 1.0 || index == 0) return new Vector2((float)track[index].Position.X, (float)track[index].Position.Y);
        var p = Vec2.Lerp(track[index - 1].Position, track[index].Position, u);
        return new Vector2((float)p.X, (float)p.Y);
    }

    /// <summary>Arc length from the start of the track up to (index,u).</summary>
    public static double ArcAt(HistorySample[] track, int index, double u)
    {
        if (track == null || track.Length < 2) return 0.0;
        index = Math.Clamp(index, 0, track.Length - 1);
        var arc = 0.0;
        for (var k = 1; k <= index; k++) arc += (track[k].Position - track[k - 1].Position).Length;
        if (index >= 1) arc -= (1.0 - u) * (track[index].Position - track[index - 1].Position).Length;
        return arc;
    }

    /// <summary>Draw the rewind overlay: forecast marks, contact rings, planned release marker.</summary>
    public void Draw(CanvasItem ci, TimelineTrail trail, Font font)
    {
        foreach (var f in _ballContacts)
        {
            var toGo = f.ArcFromCursor - ArcWalked;
            if (toGo < 0.0) continue;
            var w = 1.0f - Mathf.Clamp((float)(toGo / FadeInPx), 0f, 1f);
            var a = 0.3f + 0.7f * w;
            var color = new Color(Palette.ContactSoon.R, Palette.ContactSoon.G, Palette.ContactSoon.B, a);
            ci.DrawCircle(f.At, 7.0f, color, false, 2.0f, true);
            ci.DrawLine(f.At + new Vector2(-9, -9), f.At + new Vector2(9, 9), color, 1.6f, true);
            ci.DrawLine(f.At + new Vector2(-9, 9), f.At + new Vector2(9, -9), color, 1.6f, true);
            if (font != null)
            {
                ci.DrawString(font, f.At + new Vector2(10, -12), $"HIT {f.Seconds:F1}s", HorizontalAlignment.Left, -1, 11, color);
            }
        }

        if (_arrival != null)
        {
            var toGo = _arrival.ArcFromCursor - ArcWalked;
            if (toGo >= 0.0)
            {
                var a = 0.35f + 0.65f * (1.0f - Mathf.Clamp((float)(toGo / FadeInPx), 0f, 1f));
                var color = new Color(Palette.SessionStripe.R, Palette.SessionStripe.G, Palette.SessionStripe.B, a);
                ci.DrawCircle(_arrival.At, 6.0f, color, false, 1.6f, true);
                if (font != null)
                {
                    ci.DrawString(font, _arrival.At + new Vector2(8, 16), $"PATH START {_arrival.Seconds:F1}s", HorizontalAlignment.Left, -1, 10, color);
                }
            }
        }

        if (Scrubbing && trail.HasGeometry && PlannedReleaseIndex >= 0)
        {
            var p = trail.PointAt(PlannedReleaseIndex);
            ci.DrawLine(p + new Vector2(-16, 0), p + new Vector2(-8, 0), Palette.RewindTarget, 2.0f, true);
            ci.DrawLine(p + new Vector2(8, 0), p + new Vector2(16, 0), Palette.RewindTarget, 2.0f, true);
            ci.DrawLine(p + new Vector2(0, -16), p + new Vector2(0, -8), Palette.RewindTarget, 2.0f, true);
            ci.DrawLine(p + new Vector2(0, 8), p + new Vector2(0, 16), Palette.RewindTarget, 2.0f, true);
            ci.DrawCircle(p, 10.0f, Palette.RewindTarget, false, 2.2f, true);
            ci.DrawCircle(p, 3.0f, Palette.RewindTarget, true, -1.0f, true);
            if (font != null)
            {
                ci.DrawString(font, p + new Vector2(14, -14), "RELEASE", HorizontalAlignment.Left, -1, 12, Palette.RewindTarget);
            }
        }
    }

    public string Stats()
    {
        return $"forecast contacts={_ballContacts.Count} lookahead={LookaheadPx:F0}px computedIn={ForecastMicroseconds}us " +
               $"walked={ArcWalked:F1}px scrub={Scrubbing} paused={Paused} planned={PlannedReleaseIndex}";
    }
}
