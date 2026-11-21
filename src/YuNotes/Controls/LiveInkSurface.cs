using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using YuNotes.Models;
using YuNotes.Rendering;

namespace YuNotes.Controls;

/// <summary>
/// Live-stroke renderer backed by a CanvasVirtualImageSource (a virtualized
/// DComp surface) instead of retained-mode XAML shapes. Pixels persist between
/// pointer samples, so each sync clears and redraws ONLY the area around the
/// samples whose outline changed (plus the previous sync's area, which held the
/// stale prediction) — the composition visual itself never changes and
/// DWM recomposites just the dirty region, instead of the whole window as it
/// does when a XAML Path/Polyline's geometry is swapped every sample (measured
/// at +3-5 GPU points of dwm.exe and ~2x app CPU; see prototypes/InkPerfLab).
///
/// The surface is created on stroke start and dropped on stroke end, so tile
/// memory only ever covers one in-flight stroke. Chunking semantics carry over
/// from the retained-XAML predecessor: opaque strokes freeze ChunkPoints-sample
/// chunks (kept as geometries for dirty-region overlap redraws — their pixels
/// are already on the surface); translucent strokes stay one geometry rebuilt
/// per sync (chunk-boundary caps would double-blend). Either way the redraw
/// region is limited to the samples that actually changed.
///
/// Known live-only compromise: ink is clipped to the page surface while the
/// pen is down, so a stroke dragged past the page edge shows its overflow only
/// after commit (the XAML shapes rendered it unclipped). Committed rendering
/// is unchanged either way.
/// </summary>
internal sealed class LiveInkSurface
{
    // Device pixels per page unit ceiling. rasterizationScale (2 on typical
    // HiDPI laptops) x DpiScale tier (1..4) tops out at 8 — matching the
    // committed CanvasVirtualControl's backing resolution at any zoom.
    private const float MaxPixelScale = 8f;
    // Samples per frozen chunk of an opaque live stroke; only the ≤64-sample
    // active tail re-tessellates per sync.
    private const int ChunkPoints = 64;

    private readonly Canvas _overlay;

    private Image? _image;
    private CanvasVirtualImageSource? _vsis;
    private CanvasDevice? _device;
    private float _scale = 2f;          // applied when the next stroke starts
    private float _pageW, _pageH;

    // ── Active stroke state ────────────────────────────────────────────────
    private string? _strokeId;
    private bool _pressure;
    private bool _chunkable;            // opaque → frozen chunks; translucent → one geometry
    private Color _color;
    private float _uniformWidth;
    private float _baseHalfWidth;
    private CanvasStrokeStyle? _roundStroke;
    private readonly List<(CanvasGeometry Geo, Rect Bounds)> _frozen = new();
    private CanvasGeometry? _tail;
    private Rect _tailBounds;           // last sync's changed region (covers its stale prediction)
    private bool _hasTailBounds;
    private int _chunkStart;
    private int _synced;
    private bool _dotRendered;

    // Scratch arrays for ribbon outline math (grown on demand for long
    // translucent strokes, which tessellate the full ribbon each sync).
    private Vector2[] _pos = new Vector2[128];
    private float[] _half = new float[128];
    private Vector2[] _norm = new Vector2[128];
    private Vector2[] _segN = new Vector2[128];
    private float[] _segLen = new float[128];
    private float[] _mPlus = new float[128];
    private float[] _mMinus = new float[128];

    // cos(30°): joints turning more than this get an explicit round join.
    private const float SharpJoinCos = 0.866f;

    public LiveInkSurface(Canvas overlay) => _overlay = overlay;

    public bool IsActive => _strokeId is not null;

    /// <summary>
    /// Sets the surface density (device pixels per page unit). Takes effect on
    /// the next stroke — a surface mid-stroke keeps its resolution.
    /// </summary>
    public void SetScale(float pixelsPerPageUnit) =>
        _scale = Math.Clamp(pixelsPerPageUnit, 1f, MaxPixelScale);

    /// <summary>Renders the live stroke; called once per input event.</summary>
    public void Sync(Stroke s, Color color, float pageW, float pageH)
    {
        if (_strokeId != s.Id)
            Begin(s, color, pageW, pageH);
        if (_vsis is null) return;

        try
        {
            if (_pressure) SyncPressure(s);
            else SyncUniform(s);
        }
        catch (Exception e) when (_device is not null && _device.IsDeviceLost(e.HResult))
        {
            // Drop the live overlay; the model still has the stroke, and the
            // committed redraw renders it once the device recovers. The next
            // stroke re-creates everything on a fresh shared device.
            Clear();
        }
    }

    /// <summary>Removes all live ink (stroke committed, cancelled, or handed off).</summary>
    public void Clear()
    {
        if (_vsis is not null)
        {
            _vsis.RegionsInvalidated -= OnRegionsInvalidated;
            _vsis = null;
        }
        _device = null;
        if (_image is not null) _image.Source = null;
        foreach (var (g, _) in _frozen) g.Dispose();
        _frozen.Clear();
        _tail?.Dispose();
        _tail = null;
        _hasTailBounds = false;
        _strokeId = null;
    }

    private void Begin(Stroke s, Color color, float pageW, float pageH)
    {
        Clear();
        _pageW = pageW;
        _pageH = pageH;
        _device = CanvasDevice.GetSharedDevice();
        _vsis = new CanvasVirtualImageSource(_device, pageW, pageH, 96f * _scale, CanvasAlphaMode.Premultiplied);
        _vsis.RegionsInvalidated += OnRegionsInvalidated;

        if (_image is null)
        {
            _image = new Image
            {
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            // Bottom of the overlay: live ink sits above the committed canvas
            // but below the marquee/selection/handle visuals.
            _overlay.Children.Insert(0, _image);
        }
        _image.Width = pageW;
        _image.Height = pageH;
        _image.Source = _vsis.Source;

        _strokeId = s.Id;
        _pressure = s.PressureMode;
        _color = color;
        _chunkable = color.A == 255;
        _uniformWidth = s.Width;
        _baseHalfWidth = s.Width * 0.5f;
        _roundStroke ??= new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round
        };
        _chunkStart = 0;
        _synced = 0;
        _dotRendered = false;
        _hasTailBounds = false;
    }

    // The system raises this for visible regions it has no content for (first
    // display after creation, surface memory reclaimed, device loss). Redraw
    // whatever stroke content intersects; everything else just clears.
    private void OnRegionsInvalidated(CanvasVirtualImageSource sender, CanvasRegionsInvalidatedEventArgs args)
    {
        if (!ReferenceEquals(sender, _vsis)) return;
        try
        {
            foreach (var region in args.InvalidatedRegions)
                Redraw(region);
        }
        catch (Exception e) when (_device is not null && _device.IsDeviceLost(e.HResult))
        {
            Clear();
        }
    }

    // ── Pressure ribbon (variable width from per-point pressure) ───────────
    private void SyncPressure(Stroke s)
    {
        var pts = s.Points;
        int count = pts.Count;
        if (count <= _synced) return;
        int prevSynced = _synced;
        _synced = count;

        if (count >= 1 && !_dotRendered)
        {
            var p = pts[0];
            float r = _baseHalfWidth * Math.Max(0.2f, p.Pressure);
            var dot = CanvasGeometry.CreateCircle(_device, p.X, p.Y, r);
            var b = Inflate(dot.ComputeBounds(), 2);
            _frozen.Add((dot, b));
            Redraw(b);
            _dotRendered = true;
        }
        if (count < 2) return;

        if (_chunkable)
        {
            while (count - _chunkStart >= ChunkPoints)
            {
                int end = _chunkStart + ChunkPoints - 1;
                // Pixels for the frozen range are already on the surface from
                // earlier tail draws; the geometry is kept only so dirty-region
                // redraws can repaint it.
                var g = BuildRibbon(pts, _chunkStart, end, predict: false);
                _frozen.Add((g, Inflate(g.ComputeBounds(), 3)));
                _chunkStart = end;
            }

            // Redraw only what changed (see ChangedPointBounds), not the whole
            // tail: the tail can span a word, and repainting its box at up to
            // 8 px/unit on every sample was most of the live GPU/DWM cost. A
            // freeze always lands at or after the last previously-synced
            // sample, so its seam is inside the changed region too.
            var tail = BuildRibbon(pts, _chunkStart, count - 1, predict: true);
            _tail?.Dispose();
            _tail = tail;
            var changed = ChangedPointBounds(s, prevSynced, pressureRadii: true);
            var region = _hasTailBounds ? Union(_tailBounds, changed) : changed;
            _tailBounds = changed;
            _hasTailBounds = true;
            Redraw(region);
        }
        else
        {
            // Translucent: one ribbon over the whole stroke (chunk-boundary
            // caps would double-blend), but the redraw region only covers the
            // samples whose outline actually changed plus the old prediction.
            var changed = ChangedPointBounds(s, prevSynced, pressureRadii: true);
            var tail = BuildRibbon(pts, 0, count - 1, predict: true);
            _tail?.Dispose();
            _tail = tail;
            var region = _hasTailBounds ? Union(_tailBounds, changed) : changed;
            _tailBounds = changed;
            _hasTailBounds = true;
            Redraw(region);
        }
    }

    // ── Uniform-width curve (pen without pressure, highlighter) ────────────
    private void SyncUniform(Stroke s)
    {
        var pts = s.Points;
        int count = pts.Count;
        if (count <= _synced) return;
        int prevSynced = _synced;
        _synced = count;
        if (count < 2) return;   // single sample renders nothing, same as the XAML Polyline

        if (_chunkable)
        {
            // Strictly greater: the chunk's last curve segment needs the real
            // sample after it as its Catmull-Rom neighbour, or the frozen
            // geometry would disagree with the pixels the tail already drew.
            while (count - _chunkStart > ChunkPoints)
            {
                int end = _chunkStart + ChunkPoints - 1;
                var g = BuildPolyline(pts, _chunkStart, end, predict: false);
                _frozen.Add((g, Inflate(g.ComputeStrokeBounds(_uniformWidth, _roundStroke), 2)));
                _chunkStart = end;
            }

            var tail = BuildPolyline(pts, _chunkStart, count - 1, predict: true);
            _tail?.Dispose();
            _tail = tail;
            var changed = ChangedPointBounds(s, prevSynced, pressureRadii: false);
            var region = _hasTailBounds ? Union(_tailBounds, changed) : changed;
            _tailBounds = changed;
            _hasTailBounds = true;
            Redraw(region);
        }
        else
        {
            var changed = ChangedPointBounds(s, prevSynced, pressureRadii: false);
            var tail = BuildPolyline(pts, 0, count - 1, predict: true);
            _tail?.Dispose();
            _tail = tail;
            var region = _hasTailBounds ? Union(_tailBounds, changed) : changed;
            _tailBounds = changed;
            _hasTailBounds = true;
            Redraw(region);
        }
    }

    /// <summary>
    /// Bounding box of the samples whose rendering changed this sync: the new
    /// samples plus two earlier ones (their averaged normals / join shape shift
    /// when a neighbour arrives) plus the fresh prediction point. The previous
    /// sync's region (held in _tailBounds) covers the stale prediction.
    /// </summary>
    private Rect ChangedPointBounds(Stroke s, int prevSynced, bool pressureRadii)
    {
        var pts = s.Points;
        int count = pts.Count;
        int from = Math.Max(0, prevSynced - 2);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        void Extend(float x, float y, float r)
        {
            if (x - r < minX) minX = x - r;
            if (y - r < minY) minY = y - r;
            if (x + r > maxX) maxX = x + r;
            if (y + r > maxY) maxY = y + r;
        }
        // Longest segment touching the changed range: Catmull-Rom control
        // points sit up to |P[i+1]-P[i-1]|/6 ≤ 2·maxSeg/6 outside the samples.
        float maxSegSq = 0f;
        for (int i = from; i < count; i++)
        {
            var p = pts[i];
            float r = pressureRadii ? _baseHalfWidth * Math.Max(0.2f, p.Pressure) : _uniformWidth * 0.5f;
            Extend(p.X, p.Y, r);
            if (i > 0)
            {
                var q = pts[i - 1];
                float sx = p.X - q.X, sy = p.Y - q.Y;
                maxSegSq = Math.Max(maxSegSq, sx * sx + sy * sy);
            }
        }
        if (count >= 2)
        {
            var pn = pts[count - 1];
            var pn1 = pts[count - 2];
            float dx = pn.X - pn1.X;
            float dy = pn.Y - pn1.Y;
            float k = PageCanvas.PredictionScale(dx, dy);
            float r = pressureRadii ? _baseHalfWidth * Math.Max(0.2f, pn.Pressure) : _uniformWidth * 0.5f;
            Extend(pn.X + dx * k, pn.Y + dy * k, r);
            maxSegSq = Math.Max(maxSegSq, (dx * dx + dy * dy) * k * k);
        }
        double pad = 3 + (pressureRadii ? 0 : MathF.Sqrt(maxSegSq) / 3f);
        return Inflate(new Rect(minX, minY, maxX - minX, maxY - minY), pad);
    }

    private void Redraw(Rect region)
    {
        if (_vsis is null) return;
        region.Intersect(new Rect(0, 0, _pageW, _pageH));
        if (region.IsEmpty || region.Width <= 0 || region.Height <= 0) return;

        using var ds = _vsis.CreateDrawingSession(Color.FromArgb(0, 0, 0, 0), region);
        foreach (var (g, b) in _frozen)
        {
            if (Intersects(b, region))
                DrawGeo(ds, g);
        }
        if (_tail is not null)
            DrawGeo(ds, _tail);
    }

    private void DrawGeo(CanvasDrawingSession ds, CanvasGeometry g)
    {
        if (_pressure) ds.FillGeometry(g, _color);
        else ds.DrawGeometry(g, _color, _uniformWidth, _roundStroke);
    }

    // Catmull-Rom curve through samples [first..last] (plus the capped
    // prediction lead), identical to the committed renderer's smoothing so the
    // stroke doesn't change shape on pen-up. Neighbours come from the WHOLE
    // stroke (clamped only at its real ends, like the committed curve), so a
    // frozen chunk and the tail meet with matching tangents. The prediction
    // point acts as one extra virtual sample after the last real one.
    private CanvasGeometry BuildPolyline(List<InkPoint> pts, int first, int last, bool predict)
    {
        int count = pts.Count;
        Vector2 pred = default;
        bool doPredict = predict && last >= 1;
        if (doPredict)
        {
            var pn = pts[last];
            var pn1 = pts[last - 1];
            float dx = pn.X - pn1.X;
            float dy = pn.Y - pn1.Y;
            float k = PageCanvas.PredictionScale(dx, dy);
            pred = new Vector2(pn.X + dx * k, pn.Y + dy * k);
        }
        // Virtual sample sequence: real samples, then (tail only) the
        // prediction. The tail always ends at the last real sample.
        int vLast = doPredict ? last + 1 : count - 1;
        Vector2 V(int j)
        {
            if (j < 0) j = 0;
            else if (j > vLast) j = vLast;
            if (doPredict && j > last) return pred;
            var p = pts[j];
            return new Vector2(p.X, p.Y);
        }

        int segEnd = doPredict ? last + 1 : last;   // last vertex index drawn
        using var pb = new CanvasPathBuilder(_device);
        pb.BeginFigure(V(first));
        for (int i = first; i < segEnd; i++)
        {
            var p0 = V(i - 1);
            var p1 = V(i);
            var p2 = V(i + 1);
            var p3 = V(i + 2);
            pb.AddCubicBezier(p1 + (p2 - p0) / 6f, p2 - (p3 - p1) / 6f, p2);
        }
        pb.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(pb);
    }

    // Variable-width ribbon outline over samples [first..last] (plus one
    // extrapolated sample when predict): per-point half-widths from pressure,
    // averaged per-point normals so the edges stay continuous, semicircle end
    // caps, Winding fill so tight curls fill solid instead of punching
    // even-odd holes. Inner-side miters and round joins at sharp turns make
    // it match the committed renderer's quads-plus-circles union.
    private CanvasGeometry BuildRibbon(List<InkPoint> pts, int first, int last, bool predict)
    {
        int n = last - first + 1;
        bool doPredict = predict && last >= 1;
        int total = doPredict ? n + 1 : n;
        EnsureScratch(total);
        var pos = _pos; var half = _half; var norm = _norm; var segN = _segN;

        for (int i = 0; i < n; i++)
        {
            var p = pts[first + i];
            pos[i] = new Vector2(p.X, p.Y);
            half[i] = _baseHalfWidth * Math.Max(0.2f, p.Pressure);
        }
        if (doPredict)
        {
            var pn = pts[last];
            var pn1 = pts[last - 1];
            float dx = pn.X - pn1.X;
            float dy = pn.Y - pn1.Y;
            float k = PageCanvas.PredictionScale(dx, dy);
            pos[n] = new Vector2(pn.X + dx * k, pn.Y + dy * k);
            half[n] = half[n - 1];
        }

        var segLen = _segLen;
        var lastDir = new Vector2(1, 0);
        for (int i = 0; i < total - 1; i++)
        {
            var d = pos[i + 1] - pos[i];
            float len = d.Length();
            segLen[i] = len;
            if (len > 1e-3f) lastDir = d / len;
            segN[i] = new Vector2(-lastDir.Y, lastDir.X);
        }
        var mPlus = _mPlus; var mMinus = _mMinus;
        norm[0] = segN[0];
        mPlus[0] = mMinus[0] = 1f;
        if (total >= 2)
        {
            norm[total - 1] = segN[total - 2];
            mPlus[total - 1] = mMinus[total - 1] = 1f;
            for (int i = 1; i < total - 1; i++)
            {
                var avg = segN[i - 1] + segN[i];
                float len = avg.Length();
                norm[i] = len > 0.5f ? avg / len : segN[i];
                mPlus[i] = mMinus[i] = 1f;

                // Inner side of a turn: push the edge out to the miter point
                // (where the two segments' offset edges actually meet) instead
                // of cutting a notch — the averaged normal alone narrows the
                // stroke by cos(θ/2) at every bend. Limited so the miter never
                // reaches past either neighbouring segment (short, jittery
                // segments would otherwise spike out of the inner edge). The
                // outer side keeps the bisector point, which lies on the
                // committed renderer's round join.
                float c = Vector2.Dot(norm[i], segN[i]);
                if (c < 0.9999f && c > 1e-3f)
                {
                    float s = MathF.Sqrt(1f - c * c);
                    float h = Math.Max(half[i], 1e-3f);
                    float m = Math.Min(1f / c, Math.Min(segLen[i - 1], segLen[i]) / (h * s));
                    m = Math.Clamp(m, 1f, 3f);
                    if (Cross(segN[i - 1], segN[i]) > 0) mPlus[i] = m;
                    else mMinus[i] = m;
                }
            }
        }

        using var pb = new CanvasPathBuilder(_device);
        pb.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        var start = pos[0] + norm[0] * half[0];
        pb.BeginFigure(start);
        for (int i = 1; i < total; i++)
            pb.AddLine(pos[i] + norm[i] * (half[i] * mPlus[i]));
        int e = total - 1;
        pb.AddArc(pos[e] - norm[e] * half[e], half[e], half[e], 0,
            CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
        for (int i = total - 2; i >= 0; i--)
            pb.AddLine(pos[i] - norm[i] * (half[i] * mMinus[i]));
        pb.AddArc(start, half[0], half[0], 0,
            CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
        pb.EndFigure(CanvasFigureLoop.Closed);

        // Round joins where the pen turns sharply (> ~30°): the outer edge's
        // chord through the bisector point would otherwise show a visible
        // corner, and hairpins fold the ribbon. Same circles the committed
        // renderer puts at every joint, so the stroke doesn't change on
        // pen-up — but only where they're visible, keeping tessellation cheap.
        for (int i = 1; i < total - 1; i++)
        {
            if (Vector2.Dot(segN[i - 1], segN[i]) < SharpJoinCos)
                PageRenderer.AddCircleFigure(pb, pos[i].X, pos[i].Y, half[i]);
        }
        return CanvasGeometry.CreatePath(pb);
    }

    private void EnsureScratch(int total)
    {
        if (_pos.Length >= total) return;
        int size = Math.Max(total, _pos.Length * 2);
        _pos = new Vector2[size];
        _half = new float[size];
        _norm = new Vector2[size];
        _segN = new Vector2[size];
        _segLen = new float[size];
        _mPlus = new float[size];
        _mMinus = new float[size];
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static Rect Union(Rect a, Rect b)
    {
        a.Union(b);
        return a;
    }

    private static Rect Inflate(Rect r, double d) =>
        new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    private static bool Intersects(Rect a, Rect b)
    {
        a.Intersect(b);
        return !a.IsEmpty && a.Width > 0 && a.Height > 0;
    }
}
