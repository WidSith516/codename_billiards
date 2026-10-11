using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Game;

using Rewind.Sim;

/// <summary>
/// The time-layered history trail (port plan §4.4 "时间分层轨迹").
///
/// What this class guarantees, and why it is not just a polyline:
///  * **No corner cutting.** The drawn geometry is one vertex per recorded history sample, in
///    order, with no decimation and no resampling. The kernel records a vertex every 2.0 px of arc
///    length (locked), so the drawn path IS the recorded path. The old prototype subsampled its
///    polyline and cut corners; that is structurally impossible here, and the fidelity is measured
///    (see Play.VerifyTrailFidelity).
///  * **Time layering.** Colour = age: the newest samples are bright/saturated, older ones fade to
///    a dim, desaturated green. Both hue and alpha carry the gradient, so the ordering survives
///    greyscale.
///  * **Rewind sessions.** The live history is segmented at every rewind press; a stripe marks each
///    fold, so the player can see how many times the path doubled back.
///  * **Turn-around points.** Local extrema of the walk direction are marked: turn-back = diamond,
///    turn-forward = ring.
///  * **Ghost.** The previous rewind's frozen path stays on screen at very low alpha, so "where did
///    I go last time" is comparable at a glance.
///
/// Performance: per-frame work is O(new samples), not O(n). Vertices, colours and the frozen-index
/// mapping are cached and only appended, so a 50k-sample trail costs the same per frame as a
/// 100-sample one.
/// </summary>
public sealed class TimelineTrail
{
    /// <summary>How many of the most recent samples span the full brightness gradient.</summary>
    public int FadeWindow = 1400;

    /// <summary>Maximum vertices per DrawPolylineColors call (gradient resolution vs draw calls).</summary>
    public int ChunkVertices = 2048;

    public bool ShowGhost = true;
    public bool ShowSessionStripes = true;
    public bool ShowTurnMarkers = true;
    public bool ShowTrail = true;

    /// <summary>Diagnostic switch: print what the trail pass is actually about to submit.</summary>
    public bool DebugDraw;

    /// <summary>Draw a dark halo under the gradient so a thin line cannot dissolve into the felt.</summary>
    public bool HaloPass = true;

    /// <summary>
    /// Font for the on-table session labels. Injected rather than taken from ThemeDB.FallbackFont,
    /// which has no CJK glyphs and would draw them as tofu boxes.
    /// </summary>
    public Font LabelFont;

    private readonly CanvasItem _ci;

    // Cached geometry, index-aligned with the kernel's history samples.
    private Vector2[] _points = new Vector2[1024];
    private Color[] _colors = new Color[1024];
    private int _cachedCount;
    private int _coloredFrom;

    // Frozen-track index per live sample (monotone walk; refreshed per rewind session).
    private int[] _frozenIndex = new int[1024];
    private int _mapBase;      // live index represented by _mapCursor
    private int _mapCursor;    // frozen index for _mapBase
    private int _lastFrozen = -1;
    private int _lastDelta;

    private readonly List<int> _turnBacks = new List<int>();
    private readonly List<int> _turnForwards = new List<int>();
    private readonly List<int> _sessionStarts = new List<int>();
    private bool _wasRewinding;

    public int SessionCount => _sessionStarts.Count;
    public int TurnBackCount => _turnBacks.Count;
    public int TurnForwardCount => _turnForwards.Count;
    public int CachedVertexCount => _cachedCount;
    public IReadOnlyList<int> TurnBacks => _turnBacks;
    public IReadOnlyList<int> TurnForwards => _turnForwards;
    public IReadOnlyList<int> SessionStarts => _sessionStarts;

    /// <summary>Frozen snapshot of the previous rewind; drawn at very low alpha.</summary>
    public HistorySample[] GhostTrack;

    /// <summary>Traversed span of the live history while rewinding, or (-1,-1) when not rewinding.</summary>
    public (int From, int To) TraversedRange { get; private set; } = (-1, -1);

    /// <summary>Highest frozen-track index reached during the current rewind (for the "ahead" test).</summary>
    public int RewindHighWater { get; private set; } = -1;

    public TimelineTrail(CanvasItem ci)
    {
        _ci = ci;
    }

    public void Reset()
    {
        _cachedCount = 0;
        _coloredFrom = 0;
        _mapBase = 0;
        _mapCursor = 0;
        _lastFrozen = -1;
        _lastDelta = 0;
        _turnBacks.Clear();
        _turnForwards.Clear();
        _sessionStarts.Clear();
        _wasRewinding = false;
        GhostTrack = null;
        TraversedRange = (-1, -1);
        RewindHighWater = -1;
    }

    /// <summary>Synchronise the cache with the world's live history. Call once per frame, before Draw.</summary>
    public void Sync(World world)
    {
        var history = world.Cue.History;
        var count = history.Count;

        if (count < _cachedCount) Reset();

        if (count > _cachedCount)
        {
            EnsureCapacity(count);
            for (var i = _cachedCount; i < count; i++)
            {
                var s = history[i];
                _points[i] = new Vector2((float)s.Position.X, (float)s.Position.Y);
            }

            if (world.Rewinding && world.RewindTrack != null)
            {
                MapFrozenIndices(world, _cachedCount, count);
            }
            else
            {
                // Outside a rewind the history is the forward walk: index == sample index.
                for (var i = _cachedCount; i < count; i++) _frozenIndex[i] = i;
            }

            DetectTurns(_cachedCount, count);
            _cachedCount = count;
        }

        // Colour refresh: only the head window changes while the trail grows.
        var from = Math.Max(0, _cachedCount - FadeWindow - 1);
        if (from < _coloredFrom) _coloredFrom = from;
        for (var i = _coloredFrom; i < _cachedCount; i++) _colors[i] = AgeColor(_cachedCount - i);
        if (_cachedCount > 0) _coloredFrom = Math.Max(0, _cachedCount - 1);

        if (world.Rewinding)
        {
            if (!_wasRewinding)
            {
                if (_sessionStarts.Count == 0) _sessionStarts.Add(0);
                if (_sessionStarts[_sessionStarts.Count - 1] != _cachedCount) _sessionStarts.Add(_cachedCount);
                RewindHighWater = -1;
                _mapBase = 0;
                _mapCursor = 0;
            }

            if (world.RewindTrack != null) RewindHighWater = Math.Max(RewindHighWater, world.RewindCursor);
        }
        else if (_wasRewinding)
        {
            // The rewind ended: its frozen snapshot becomes the ghost path.
            GhostTrack = world.RewindTrack;
        }

        _wasRewinding = world.Rewinding;
        UpdateTraversedRange(world);
    }

    private void DetectTurns(int from, int to)
    {
        for (var i = Math.Max(1, from); i < to; i++)
        {
            var idx = _frozenIndex[i];
            var delta = idx - _lastFrozen;
            if (_lastFrozen >= 0 && delta != 0 && _lastDelta != 0 && Math.Sign(delta) != Math.Sign(_lastDelta))
            {
                if (_lastDelta > 0) _turnBacks.Add(i - 1);
                else _turnForwards.Add(i - 1);
            }

            if (delta != 0) _lastDelta = delta;
            _lastFrozen = idx;
        }
    }

    /// <summary>
    /// Map newly appended live samples onto the frozen track. The crawl walks the track
    /// monotonically backwards (index never increases), so a monotone pointer is exact and keeps
    /// this O(total) per session instead of O(n^2).
    /// </summary>
    private void MapFrozenIndices(World world, int from, int to)
    {
        var track = world.RewindTrack;
        if (track == null || track.Length == 0) return;
        for (var i = from; i < to; i++)
        {
            var p = world.Cue.History[i].Position;
            var best = _mapCursor;
            var bestD = double.MaxValue;
            var lo = Math.Max(0, _mapCursor - 3);
            var hi = Math.Min(track.Length - 1, _mapCursor + 3);
            for (var k = lo; k <= hi; k++)
            {
                var d = (track[k].Position - p).LengthSquared;
                if (d < bestD)
                {
                    bestD = d;
                    best = k;
                }
            }

            _frozenIndex[i] = best;
            _mapCursor = best;
            _mapBase = i;
        }
    }

    private void UpdateTraversedRange(World world)
    {
        if (!world.Rewinding)
        {
            TraversedRange = (-1, -1);
            return;
        }

        // The traversal is the contiguous tail of the live history whose frozen index is
        // non-increasing. Walk back from the head while the mapping stays monotone.
        var to = _cachedCount - 1;
        if (to < 1)
        {
            TraversedRange = (-1, -1);
            return;
        }

        var from = to;
        var limit = Math.Max(1, _mapBase);
        while (from > limit && _frozenIndex[from - 1] <= _frozenIndex[from]) from--;
        TraversedRange = (from, to);
    }

    private void EnsureCapacity(int n)
    {
        if (_points.Length >= n) return;
        var cap = _points.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref _points, cap);
        Array.Resize(ref _colors, cap);
        Array.Resize(ref _frozenIndex, cap);
    }

    /// <summary>Colour for a sample <paramref name="age"/> samples behind the head.</summary>
    private Color AgeColor(int age)
    {
        var t = 1.0f - Mathf.Clamp((float)age / Mathf.Max(1, FadeWindow), 0f, 1f);
        var c = Palette.TrailOld.Lerp(Palette.TrailNew, t * t);
        c.A = 0.16f + 0.84f * t;
        return c;
    }

    private Color TraversedColor(int age)
    {
        var t = 1.0f - Mathf.Clamp((float)age / Mathf.Max(1, FadeWindow), 0f, 1f);
        var c = Palette.RewindRemaining.Lerp(Palette.RewindTraversed, t);
        c.A = 0.45f + 0.55f * t;
        return c;
    }

    public void Draw()
    {
        if (!ShowTrail) return;

        if (_cachedCount >= 2)
        {
            if (ShowGhost && GhostTrack != null && GhostTrack.Length >= 2)
            {
                var ghostPts = new Vector2[GhostTrack.Length];
                for (var i = 0; i < GhostTrack.Length; i++)
                {
                    ghostPts[i] = new Vector2((float)GhostTrack[i].Position.X, (float)GhostTrack[i].Position.Y);
                }

                _ci.DrawPolyline(ghostPts, new Color(0.75f, 0.95f, 1.0f, 0.12f), 1.0f, true);
            }

            var (tf, tt) = TraversedRange;
            var traversing = tf >= 0 && tt - tf >= 1;

            // Base pass: time gradient, chunked (one DrawPolylineColors call per chunk).
            var chunks = ChunkRanges(0, _cachedCount - 1);
            if (DebugDraw)
            {
                GD.Print($"[trail-draw] verts={_cachedCount} chunks={chunks.Count} first={_points[0]} " +
                         $"color0={_colors[0]} colorMid={_colors[_cachedCount / 2]} colorLast={_colors[_cachedCount - 1]} " +
                         $"hasTrail={ShowTrail}");
            }

            foreach (var (a, b) in chunks)
            {
                if (traversing && a >= tf && b <= tt)
                {
                    // Recolour the traversed span so the highlight is part of the same geometry:
                    // no second overdrawn polyline, and the gradient stays continuous.
                    for (var i = a; i <= b; i++) _colors[i] = TraversedColor(_cachedCount - i);
                }

                if (DebugDraw)
                {
                    GD.Print($"[trail-draw]   chunk {a}..{b} n={b - a + 1} colorA={_colors[a]} colorB={_colors[b]}");
                }

                // ARRAY overload on purpose: the ReadOnlySpan overload silently produced no geometry
                // in this build (verified by a debug pass that logged the exact submission), so the
                // trail is drawn through scratch arrays that are REUSED across frames. Allocating
                // fresh arrays per chunk per frame measured 267 ms/frame at 50k vertices - the GC
                // churn, not the drawing, was the cost.
                EnsureScratch(ChunkVertices);
                var n = b - a + 1;
                Array.Copy(_points, a, _scratchPts, 0, n);
                Array.Copy(_colors, a, _scratchCols, 0, n);

                // Two passes: a dark halo under the gradient. Without it a 2 px line at 16-70% alpha
                // dissolves into the felt - a readability bug, not a style choice.
                if (HaloPass)
                {
                    _ci.DrawPolyline(new ReadOnlySpan<Vector2>(_scratchPts, 0, n),
                        new Color(0.02f, 0.05f, 0.04f, 0.55f), (traversing ? 2.6f : 2.0f) + 2.4f, true);
                }

                _ci.DrawPolylineColors(new ReadOnlySpan<Vector2>(_scratchPts, 0, n),
                    new ReadOnlySpan<Color>(_scratchCols, 0, n), traversing ? 2.6f : 2.0f, true);
            }

            if (ShowSessionStripes) DrawSessionStripes();
            if (ShowTurnMarkers) DrawTurnMarkers();
        }
        else if (_cachedCount == 1)
        {
            _ci.DrawCircle(_points[0], 2.5f, Palette.TrailNew, true, -1.0f, true);
        }
    }

    /// <summary>Split [from,to] into draw-call-sized chunks that never straddle a session break.</summary>
    private List<(int A, int B)> ChunkRanges(int from, int to)
    {
        var list = new List<(int, int)>();
        if (to - from < 1) return list;
        var a = from;
        while (a < to)
        {
            var b = Math.Min(to, a + ChunkVertices - 1);
            for (var s = 0; s < _sessionStarts.Count; s++)
            {
                var start = _sessionStarts[s];
                if (start > a && start <= b)
                {
                    b = start - 1;
                    break;
                }
            }

            if (b <= a) b = Math.Min(to, a + 1);
            list.Add((a, b));
            a = b;
        }

        return list;
    }

    private void DrawSessionStripes()
    {
        for (var s = 1; s < _sessionStarts.Count; s++)
        {
            var i = Math.Clamp(_sessionStarts[s], 1, _cachedCount - 1);
            var p = _points[i];
            var d = (_points[i] - _points[i - 1]).Normalized();
            var n = new Vector2(-d.Y, d.X);
            _ci.DrawLine(p - n * 9.0f, p + n * 9.0f, Palette.SessionStripe, 2.2f, true);
            _ci.DrawCircle(p, 3.4f, Palette.SessionStripe, true, -1.0f, true);
            _ci.DrawString(LabelFont ?? ThemeDB.FallbackFont, p + new Vector2(6, -6), $"第{s + 1}段", HorizontalAlignment.Left, -1, 11,
                Palette.SessionStripe);
        }
    }

    private void DrawTurnMarkers()
    {
        foreach (var i in _turnBacks)
        {
            if (i < 0 || i >= _cachedCount) continue;
            DrawDiamond(_points[i], 6.0f, Palette.TurnMarker);
        }

        foreach (var i in _turnForwards)
        {
            if (i < 0 || i >= _cachedCount) continue;
            _ci.DrawCircle(_points[i], 4.0f, Palette.TurnForwardMarker, false, 2.0f, true);
            _ci.DrawCircle(_points[i], 1.6f, Palette.TurnForwardMarker, true, -1.0f, true);
        }
    }

    private void DrawDiamond(Vector2 c, float r, Color color)
    {
        var pts = new[]
        {
            c + new Vector2(0, -r),
            c + new Vector2(r, 0),
            c + new Vector2(0, r),
            c + new Vector2(-r, 0),
        };
        _ci.DrawColoredPolygon(pts, color);
        _ci.DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[0] }, Palette.BallRingDark, 1.0f, true);
    }

    /// <summary>Point on the recorded path at live sample index i (clamped).</summary>
    public Vector2 PointAt(int i) => _points[Math.Clamp(i, 0, Math.Max(0, _cachedCount - 1))];

    public bool HasGeometry => _cachedCount >= 2;

    /// <summary>Number of draw-call chunks the current trail would be split into (diagnostics).</summary>
    public int DebugChunkCount() => _cachedCount < 2 ? 0 : ChunkRanges(0, _cachedCount - 1).Count;

    public Vector2 DebugPoint(int i) => _points[Math.Clamp(i, 0, Math.Max(0, _cachedCount - 1))];

    /// <summary>
    /// Fill the trail cache with <paramref name="count"/> vertices by tiling the recorded path, so
    /// the 50k-point target from the port plan can be measured through the real render path. The
    /// number of laps is logged so the report can state exactly what was measured.
    /// </summary>
    public int SeedSyntheticTrail(int count)
    {
        count = Math.Max(2, count);
        var recorded = _cachedCount;
        EnsureCapacity(count);
        _colors = new Color[_points.Length];

        var laps = 1;
        if (recorded < 2)
        {
            for (var i = 0; i < count; i++)
            {
                var t = i * 0.02f;
                _points[i] = new Vector2(520.0f + Mathf.Cos(t) * (60.0f + 6.0f * Mathf.Sin(t * 0.3f)),
                    280.0f + Mathf.Sin(t) * (60.0f + 6.0f * Mathf.Cos(t * 0.3f)));
            }
        }
        else
        {
            var basePoints = new Vector2[recorded];
            Array.Copy(_points, basePoints, recorded);
            for (var i = 0; i < count; i++)
            {
                var p = basePoints[i % recorded];
                var lap = i / recorded;
                laps = Math.Max(laps, lap + 1);
                var shift = (lap % 7) * 30.0f - 90.0f;
                var drop = (lap % 5) * 24.0f - 48.0f;
                _points[i] = new Vector2(
                    Mathf.Clamp(p.X + shift, 70.0f, 970.0f),
                    Mathf.Clamp(p.Y + drop, 70.0f, 490.0f));
            }
        }

        for (var i = 0; i < count; i++) _colors[i] = AgeColor(count - i);

        _sessionStarts.Clear();
        _sessionStarts.Add(0);
        var stride = Math.Max(2, count / 8);
        for (var s = stride; s < count; s += stride) _sessionStarts.Add(s);
        _cachedCount = count;
        _coloredFrom = Math.Max(0, count - 1);
        return laps;
    }

    private Vector2[] _scratchPts = new Vector2[1];
    private Color[] _scratchCols = new Color[1];

    private void EnsureScratch(int n)
    {
        if (_scratchPts.Length >= n) return;
        _scratchPts = new Vector2[n];
        _scratchCols = new Color[n];
    }

    public string Stats()
    {
        return $"trail verts={_cachedCount} sessions={_sessionStarts.Count} turnBack={_turnBacks.Count} " +
               $"turnFwd={_turnForwards.Count} ghost={(GhostTrack?.Length ?? 0)} traversed={TraversedRange.From}..{TraversedRange.To}";
    }
}
