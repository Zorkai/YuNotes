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

namespace YuNotes.Controls;

/// <summary>
/// Live-stroke renderer backed by a CanvasVirtualImageSource (a virtualized
/// DComp surface) instead of retained-mode XAML shapes. Pixels persist between
/// pointer samples, so each sync clears and redraws ONLY the union of the old
/// and new active-tail bounds — the composition visual itself never changes and
/// DWM recomposites just the dirty region, instead of the whole window as it
/// does when a XAML Path/Polyline's geometry is swapped every sample (measured
/// at +3-5 GPU points of dwm.exe and ~2x app CPU; see prototypes/InkPerfLab).
///
/// The surface is created on stroke start and dropped on stroke end, so tile
/// memory only ever covers one in-flight stroke. Chunking semantics carry over
/// from the retained-XAML predecessor: opaque strokes freeze ChunkPoints-sample
/// chunks (kept as geometries for dirty-region overlap redraws — their pixels
/// are already on the surface); translucent strokes stay one geometry rebuilt
/// per sync (chunk-boundary caps would double-blend), with the redraw region
/// still limited to the samples that actually changed.
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
    private Rect _tailBounds;           // chunkable: full tail bounds; else last changed region
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

            var tail = BuildRibbon(pts, _chunkStart, count - 1, predict: true);
            var bounds = Inflate(tail.ComputeBounds(), 3);
            _tail?.Dispose();
            _tail = tail;
            var region = _hasTailBounds ? Union(_tailBounds, bounds) : bounds;
            _tailBounds = bounds;
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

    // ── Uniform-width polyline (pen without pressure, highlighter) ─────────
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
            while (count - _chunkStart >= ChunkPoints)
            {
                int end = _chunkStart + ChunkPoints - 1;
                var g = BuildPolyline(pts, _chunkStart, end, predict: false);
                _frozen.Add((g, Inflate(g.ComputeStrokeBounds(_uniformWidth, _roundStroke), 2)));
                _chunkStart = end;
            }

            var tail = BuildPolyline(pts, _chunkStart, count - 1, predict: true);
            var bounds = Inflate(tail.ComputeStrokeBounds(_uniformWidth, _roundStroke), 2);
            _tail?.Dispose();
            _tail = tail;
            var region = _hasTailBounds ? Union(_tailBounds, bounds) : bounds;
            _tailBounds = bounds;
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
        for (int i = from; i < count; i++)
        {
            var p = pts[i];
            float r = pressureRadii ? _baseHalfWidth * Math.Max(0.2f, p.Pressure) : _uniformWidth * 0.5f;
            Extend(p.X, p.Y, r);
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
        }
        return Inflate(new Rect(minX, minY, maxX - minX, maxY - minY), 3);
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

    // Open polyline through samples [first..last], plus the same capped
    // prediction lead as the XAML Polyline path.
    private CanvasGeometry BuildPolyline(List<InkPoint> pts, int first, int last, bool predict)
    {
        using var pb = new CanvasPathBuilder(_device);
        pb.BeginFigure(new Vector2(pts[first].X, pts[first].Y));
        for (int i = first + 1; i <= last; i++)
            pb.AddLine(new Vector2(pts[i].X, pts[i].Y));
        if (predict && last >= 1)
        {
            var pn = pts[last];
            var pn1 = pts[last - 1];
            float dx = pn.X - pn1.X;
            float dy = pn.Y - pn1.Y;
            float k = PageCanvas.PredictionScale(dx, dy);
            pb.AddLine(new Vector2(pn.X + dx * k, pn.Y + dy * k));
        }
        pb.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(pb);
    }

    // Variable-width ribbon outline over samples [first..last] (plus one
    // extrapolated sample when predict): per-point half-widths from pressure,
    // averaged per-point normals so the edges stay continuous, semicircle end
    // caps, Winding fill so tight curls fill solid instead of punching
    // even-odd holes. Matches the committed-stroke renderer's ribbon shape.
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

        var lastDir = new Vector2(1, 0);
        for (int i = 0; i < total - 1; i++)
        {
            var d = pos[i + 1] - pos[i];
            float len = d.Length();
            if (len > 1e-3f) lastDir = d / len;
            segN[i] = new Vector2(-lastDir.Y, lastDir.X);
        }
        norm[0] = segN[0];
        if (total >= 2)
        {
            norm[total - 1] = segN[total - 2];
            for (int i = 1; i < total - 1; i++)
            {
                var avg = segN[i - 1] + segN[i];
                float len = avg.Length();
                norm[i] = len > 0.5f ? avg / len : segN[i];
            }
        }

        using var pb = new CanvasPathBuilder(_device);
        pb.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        var start = pos[0] + norm[0] * half[0];
        pb.BeginFigure(start);
        for (int i = 1; i < total; i++)
            pb.AddLine(pos[i] + norm[i] * half[i]);
        int e = total - 1;
        pb.AddArc(pos[e] - norm[e] * half[e], half[e], half[e], 0,
            CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
        for (int i = total - 2; i >= 0; i--)
            pb.AddLine(pos[i] - norm[i] * half[i]);
        pb.AddArc(start, half[0], half[0], 0,
            CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
        pb.EndFigure(CanvasFigureLoop.Closed);
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
    }

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
