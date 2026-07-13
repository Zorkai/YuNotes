using System;
using System.IO;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using YuNotes.Models;
using YuNotes.Services;
using YuNotes.Tools;

namespace YuNotes.Rendering;

public sealed class PageRenderer
{
    private readonly TemplateService _templates;
    public PageRenderer(TemplateService templates) => _templates = templates;

    // Reused across DrawStroke calls — allocating a new style per stroke shows up
    // in profiles when the live overlay re-renders on every pen sample.
    private static readonly CanvasStrokeStyle s_strokeStyle = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round
    };

    // Baked tessellations of committed strokes. Without this, every draw of a
    // stroke re-runs geometry building (one COM call per segment) AND D2D
    // tessellation — and a pen-lift's bbox redraw re-draws every neighbouring
    // stroke its clip touches, so fast handwriting on an inked page stalls the
    // UI thread mid-word re-tessellating the same neighbours over and over.
    // CanvasCachedGeometry pays that cost once; replaying it is trivial.
    //
    // Entries are stamped with point count + endpoints + width: all in-place
    // stroke mutations (drag, resize, rotate, pixel-erase splits) change one
    // of those. The device is compared too — a session on another device
    // (device-lost recovery, offscreen export) rebuilds lazily rather than
    // replaying a dead resource. Color is NOT baked; it's applied at draw.
    private readonly record struct StrokeGeoStamp(
        int Count, float X0, float Y0, float X1, float Y1, float Width, bool Pressure);
    private sealed record StrokeGeoEntry(
        StrokeGeoStamp Stamp, CanvasDevice Device, CanvasCachedGeometry Geo);
    private readonly System.Collections.Generic.Dictionary<string, StrokeGeoEntry> _strokeGeoCache = new();
    private const int StrokeGeoCacheMax = 4096;

    private static StrokeGeoStamp StampOf(Stroke s)
    {
        var first = s.Points[0];
        var last = s.Points[^1];
        return new StrokeGeoStamp(s.Points.Count, first.X, first.Y, last.X, last.Y, s.Width, s.PressureMode);
    }

    private CanvasCachedGeometry GetOrBakeStrokeGeometry(CanvasDrawingSession ds, Stroke s)
    {
        var stamp = StampOf(s);
        var device = ds.Device;
        if (_strokeGeoCache.TryGetValue(s.Id, out var hit)
            && hit.Stamp == stamp && ReferenceEquals(hit.Device, device))
            return hit.Geo;

        using var geo = s.PressureMode
            ? BuildVariableWidthGeometry(ds, s)
            : BuildCatmullRomGeometry(ds, s);
        var baked = s.PressureMode
            ? CanvasCachedGeometry.CreateFill(geo)
            : CanvasCachedGeometry.CreateStroke(geo, s.Width, s_strokeStyle);

        if (_strokeGeoCache.Count >= StrokeGeoCacheMax && !_strokeGeoCache.ContainsKey(s.Id))
        {
            // Blunt eviction: the cache only grows past this on huge documents;
            // dropping everything and re-baking visible strokes is a one-frame cost.
            foreach (var e in _strokeGeoCache.Values) e.Geo.Dispose();
            _strokeGeoCache.Clear();
        }
        if (_strokeGeoCache.TryGetValue(s.Id, out var old)) old.Geo.Dispose();
        _strokeGeoCache[s.Id] = new StrokeGeoEntry(stamp, device, baked);
        return baked;
    }

    // `clip`: the dirty region being redrawn (canvas space). Elements whose
    // rendered bounds can't intersect it are skipped — the GPU is already
    // clipped to the region's tiles, but geometry building (one COM call per
    // Bezier segment) is pure CPU and otherwise scales with TOTAL page ink on
    // every partial redraw (stroke commit, eraser pass, newly-scrolled tile).
    // `hiResBackground`/`hiResRect`: optional re-rasterization of the visible
    // crop of the PDF background at zoomed-in resolution (see PageCanvas.
    // UpdateHiResBackground) — drawn over the base bitmap, under the template
    // and ink.
    public void DrawPage(CanvasDrawingSession ds, ICanvasResourceCreator dev, NotePage page, TemplateSettings template,
                         CanvasBitmap? backgroundBitmap, System.Collections.Generic.IDictionary<string, CanvasBitmap>? imageCache = null,
                         string? skipTextId = null, bool overlayOnly = false,
                         float previewExtLeft = 0, float previewExtRight = 0,
                         Rect? clip = null,
                         CanvasBitmap? hiResBackground = null, Rect? hiResRect = null,
                         System.Collections.Generic.ISet<string>? skipElementIds = null)
    {
        // Elements are drawn shifted right during a left-extension preview, so
        // cull against the clip shifted the opposite way.
        Rect? cullClip = clip;
        if (clip is { } c && previewExtLeft > 0)
            cullClip = new Rect(c.X - previewExtLeft, c.Y, c.Width, c.Height);
        // totalWidth grows for extension previews but stays at page.Width for
        // reduction previews (we draw an overlay instead of shrinking the canvas).
        float totalWidth = (float)page.Width + Math.Max(0, previewExtLeft) + Math.Max(0, previewExtRight);

        if (!overlayOnly)
        {
            ds.FillRectangle(0, 0, totalWidth, (float)page.Height, Colors.White);

            // Extension preview: semi-transparent blue strip + dashed boundary.
            if (previewExtLeft > 0)
            {
                ds.FillRectangle(0, 0, previewExtLeft, (float)page.Height,
                    Color.FromArgb(40, 91, 107, 255));
                var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
                ds.DrawLine(previewExtLeft, 0, previewExtLeft, (float)page.Height,
                    Color.FromArgb(140, 91, 107, 255), 1.5f, dash);
            }
            if (previewExtRight > 0)
            {
                float stripX = (float)page.Width + previewExtLeft;
                ds.FillRectangle(stripX, 0, previewExtRight, (float)page.Height,
                    Color.FromArgb(40, 91, 107, 255));
                var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
                ds.DrawLine(stripX, 0, stripX, (float)page.Height,
                    Color.FromArgb(140, 91, 107, 255), 1.5f, dash);
            }

            // Reduction preview: red overlay on the area that will be removed + dashed new edge.
            if (previewExtLeft < 0)
            {
                // Left reduction: the leftmost |previewExtLeft| px will be removed.
                float removeW = -previewExtLeft;
                ds.FillRectangle(0, 0, removeW, (float)page.Height,
                    Color.FromArgb(70, 220, 60, 60));
                var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
                ds.DrawLine(removeW, 0, removeW, (float)page.Height,
                    Color.FromArgb(200, 200, 60, 60), 1.5f, dash);
            }
            if (previewExtRight < 0)
            {
                // Right reduction: the rightmost |previewExtRight| px will be removed.
                float newRight = (float)page.Width + previewExtRight; // page.Width - |delta|
                float removeW  = -previewExtRight;
                ds.FillRectangle(newRight, 0, removeW, (float)page.Height,
                    Color.FromArgb(70, 220, 60, 60));
                var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
                ds.DrawLine(newRight, 0, newRight, (float)page.Height,
                    Color.FromArgb(200, 200, 60, 60), 1.5f, dash);
            }

            if (backgroundBitmap is not null)
            {
                float bgLeft = (float)page.BackgroundLeft + previewExtLeft;
                float bgW    = page.BackgroundContentWidth > 0
                                   ? (float)page.BackgroundContentWidth
                                   : (float)page.Width - (float)page.BackgroundLeft;
                // The hi-res crop is opaque (rasterized on a white background), so
                // when it fully covers the dirty region the base blit underneath is
                // entirely painted over — skip the expensive cubic upscale of the
                // full-page bitmap.
                bool hiResCoversClip = hiResBackground is not null && hiResRect is { } cover &&
                                       clip is { } cr &&
                                       cover.X <= cr.X + 0.01 && cover.Y <= cr.Y + 0.01 &&
                                       cover.Right >= cr.Right - 0.01 && cover.Bottom >= cr.Bottom - 0.01;
                if (!hiResCoversClip)
                {
                    // Cubic resampling keeps PDF text edges noticeably cleaner than
                    // the default linear filter when the bitmap is scaled.
                    ds.DrawImage(backgroundBitmap, new Rect(bgLeft, 0, bgW, page.Height),
                                 backgroundBitmap.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
                }
                if (hiResBackground is not null && hiResRect is { } hr)
                {
                    // The crop is rendered at the backing-store scale, so this blit
                    // is normally ~1:1, where linear is visually identical to (and
                    // far cheaper than) two-pass cubic. Keep cubic when the 16 MP
                    // cap or display scaling made it a real upscale.
                    double devicePxPerBitmapPx =
                        hr.Width * ds.Dpi / 96.0 / hiResBackground.SizeInPixels.Width;
                    var interp = Math.Abs(devicePxPerBitmapPx - 1.0) <= 0.05
                        ? CanvasImageInterpolation.Linear
                        : CanvasImageInterpolation.HighQualityCubic;
                    ds.DrawImage(hiResBackground, hr, hiResBackground.Bounds, 1f, interp);
                }
            }
            _templates.DrawTemplate(ds, totalWidth, page.Height, template);
        }

        // When previewing a left extension, shift all element drawing right so
        // elements appear in their correct position within the wider canvas.
        var prevTransform = ds.Transform;
        if (previewExtLeft > 0)
            ds.Transform = Matrix3x2.CreateTranslation(previewExtLeft, 0) * prevTransform;

        // Images — only draw if a pre-loaded bitmap is in the cache
        if (imageCache is not null)
        {
            foreach (var img in page.Images)
            {
                if (skipElementIds?.Contains(img.Id) == true) continue;
                // Reject non-finite / non-positive geometry before DrawImage's Rect
                // constructor (negative w/h throws) or Win2D (NaN throws) can crash.
                if (!IsFinite(img.X) || !IsFinite(img.Y) ||
                    !IsFinite(img.Width) || !IsFinite(img.Height) ||
                    img.Width <= 0 || img.Height <= 0)
                    continue;
                if (img.Rotation == 0 && cullClip is { } cc1 &&
                    !RectIntersects(cc1, (float)img.X, (float)img.Y,
                                    (float)(img.X + img.Width), (float)(img.Y + img.Height)))
                    continue;
                if (!imageCache.TryGetValue(img.Id, out var bmp)) continue;
                var prev = ds.Transform;
                if (img.Rotation != 0)
                {
                    var cx = (float)(img.X + img.Width * 0.5);
                    var cy = (float)(img.Y + img.Height * 0.5);
                    ds.Transform = Matrix3x2.CreateRotation((float)(img.Rotation * Math.PI / 180.0), new Vector2(cx, cy)) * prev;
                }
                ds.DrawImage(bmp, new Rect(img.X, img.Y, img.Width, img.Height));
                ds.Transform = prev;
            }
        }

        // Shapes (drawn below strokes so ink can annotate over them)
        foreach (var sh in page.Shapes)
        {
            if (skipElementIds?.Contains(sh.Id) == true) continue;
            if (cullClip is { } cc2 && !ShapeIntersects(cc2, sh)) continue;
            DrawShape(ds, sh);
        }

        // Strokes (highlighters first so pen sits on top)
        foreach (var s in page.Strokes)
            if (s.Kind == StrokeKind.Highlighter && skipElementIds?.Contains(s.Id) != true
                && (cullClip is not { } h || StrokeIntersects(h, s)))
                DrawStroke(ds, s);
        foreach (var s in page.Strokes)
            if (s.Kind == StrokeKind.Pen && skipElementIds?.Contains(s.Id) != true
                && (cullClip is not { } pc || StrokeIntersects(pc, s)))
                DrawStroke(ds, s);

        // Text — skip the element currently being inline-edited
        foreach (var t in page.Texts)
        {
            if (t.Id == skipTextId) continue;
            if (skipElementIds?.Contains(t.Id) == true) continue;
            if (t.Rotation == 0 && cullClip is { } cc3 &&
                !RectIntersects(cc3, (float)t.X, (float)t.Y,
                                (float)(t.X + t.Width), (float)(t.Y + t.Height)))
                continue;
            var prev = ds.Transform;
            if (t.Rotation != 0)
            {
                var cx = (float)(t.X + t.Width * 0.5);
                var cy = (float)(t.Y + t.Height * 0.5);
                ds.Transform = Matrix3x2.CreateRotation((float)(t.Rotation * Math.PI / 180.0), new Vector2(cx, cy)) * prev;
            }
            DrawText(ds, t);
            ds.Transform = prev;
        }

        // Restore transform to what it was before the optional preview offset.
        ds.Transform = prevTransform;
    }

    // Draws ONLY the given elements, in DrawPage's z-order (images, shapes,
    // highlighter strokes, pen strokes, texts). Used to render the drag ghost:
    // the selection is rasterized once into an overlay image, then moved with
    // composition transforms instead of re-rendering the page per pointer move.
    public void DrawElements(CanvasDrawingSession ds, NotePage page,
        System.Collections.Generic.ICollection<string> strokeIds,
        System.Collections.Generic.ICollection<string> shapeIds,
        System.Collections.Generic.ICollection<string> textIds,
        System.Collections.Generic.ICollection<string> imageIds,
        System.Collections.Generic.IDictionary<string, CanvasBitmap>? imageCache)
    {
        if (imageCache is not null && imageIds.Count > 0)
        {
            foreach (var img in page.Images)
            {
                if (!imageIds.Contains(img.Id)) continue;
                if (!IsFinite(img.X) || !IsFinite(img.Y) ||
                    !IsFinite(img.Width) || !IsFinite(img.Height) ||
                    img.Width <= 0 || img.Height <= 0)
                    continue;
                if (!imageCache.TryGetValue(img.Id, out var bmp)) continue;
                var prev = ds.Transform;
                if (img.Rotation != 0)
                {
                    var cx = (float)(img.X + img.Width * 0.5);
                    var cy = (float)(img.Y + img.Height * 0.5);
                    ds.Transform = Matrix3x2.CreateRotation((float)(img.Rotation * Math.PI / 180.0), new Vector2(cx, cy)) * prev;
                }
                ds.DrawImage(bmp, new Rect(img.X, img.Y, img.Width, img.Height));
                ds.Transform = prev;
            }
        }

        foreach (var sh in page.Shapes)
            if (shapeIds.Contains(sh.Id)) DrawShape(ds, sh);

        foreach (var s in page.Strokes)
            if (s.Kind == StrokeKind.Highlighter && strokeIds.Contains(s.Id)) DrawStroke(ds, s);
        foreach (var s in page.Strokes)
            if (s.Kind == StrokeKind.Pen && strokeIds.Contains(s.Id)) DrawStroke(ds, s);

        foreach (var t in page.Texts)
        {
            if (!textIds.Contains(t.Id)) continue;
            var prev = ds.Transform;
            if (t.Rotation != 0)
            {
                var cx = (float)(t.X + t.Width * 0.5);
                var cy = (float)(t.Y + t.Height * 0.5);
                ds.Transform = Matrix3x2.CreateRotation((float)(t.Rotation * Math.PI / 180.0), new Vector2(cx, cy)) * prev;
            }
            DrawText(ds, t);
            ds.Transform = prev;
        }
    }

    private static bool RectIntersects(in Rect clip, float minX, float minY, float maxX, float maxY)
        => maxX >= clip.X && minX <= clip.X + clip.Width &&
           maxY >= clip.Y && minY <= clip.Y + clip.Height;

    // Win2D draw calls run inside a native callback: any managed exception (e.g.
    // a Rect built with negative/NaN dimensions, or a non-finite coordinate fed
    // to CanvasPathBuilder) propagates through native code and hard-terminates
    // the process with no catchable stack. Corrupt or edge-case persisted data
    // (a flipped resize leaving negative width, a divide-by-zero saved as NaN)
    // must therefore be rejected BEFORE it reaches Win2D.
    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    // Conservative bounds test for a stroke's RENDERED extent: point hull,
    // padded by half the stroke width plus the Catmull-Rom overshoot bound —
    // the curve stays inside the hull of its control points, which sit at most
    // max|P[i+1]-P[i-1]|/6 outside the sample hull.
    private static bool StrokeIntersects(in Rect clip, Stroke s)
    {
        var pts = s.Points;
        if (pts.Count == 0) return false;

        // Early out: any sample inside the clip padded by the width alone
        // proves intersection without finishing the scan.
        float halfW = s.Width * 0.5f;
        double fastL = clip.X - halfW, fastT = clip.Y - halfW;
        double fastR = clip.X + clip.Width + halfW, fastB = clip.Y + clip.Height + halfW;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        float maxSpanSq = 0f;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (p.X >= fastL && p.X <= fastR && p.Y >= fastT && p.Y <= fastB) return true;
            if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            if (!s.PressureMode && i >= 2)
            {
                float dx = pts[i].X - pts[i - 2].X;
                float dy = pts[i].Y - pts[i - 2].Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > maxSpanSq) maxSpanSq = d2;
            }
        }
        float pad = halfW + (s.PressureMode ? 0f : MathF.Sqrt(maxSpanSq) / 6f) + 2f;
        return RectIntersects(clip, minX - pad, minY - pad, maxX + pad, maxY + pad);
    }

    private static bool ShapeIntersects(in Rect clip, ShapeElement s)
    {
        float minX = Math.Min(s.X1, s.X2);
        float minY = Math.Min(s.Y1, s.Y2);
        float maxX = Math.Max(s.X1, s.X2);
        float maxY = Math.Max(s.Y1, s.Y2);
        if (s.Kind == ShapeKind.Triangle)
        {
            minX = Math.Min(minX, s.X3); minY = Math.Min(minY, s.Y3);
            maxX = Math.Max(maxX, s.X3); maxY = Math.Max(maxY, s.Y3);
        }
        else if (s.Rotation != 0f)
        {
            // Rotation is about the bbox center, so the drawn extent is the
            // axis-aligned bounds of the rotated corners.
            Bbox rb = Bbox.RotatedAabb(minX, minY, maxX, maxY, s.Rotation);
            minX = rb.X; minY = rb.Y; maxX = rb.Right; maxY = rb.Bottom;
        }
        float pad = s.StrokeWidth * 0.5f + 2f;
        return RectIntersects(clip, minX - pad, minY - pad, maxX + pad, maxY + pad);
    }

    public void DrawShape(CanvasDrawingSession ds, ShapeElement s)
    {
        if (s is null) return;
        // Non-finite endpoints would throw inside CanvasPathBuilder / DrawGeometry.
        if (!IsFinite(s.X1) || !IsFinite(s.Y1) || !IsFinite(s.X2) || !IsFinite(s.Y2) ||
            (s.Kind == ShapeKind.Triangle && (!IsFinite(s.X3) || !IsFinite(s.Y3))))
            return;
        if (!IsFinite(s.StrokeWidth) || s.StrokeWidth < 0) return;
        float x = Math.Min(s.X1, s.X2);
        float y = Math.Min(s.Y1, s.Y2);
        float w = Math.Abs(s.X2 - s.X1);
        float h = Math.Abs(s.Y2 - s.Y1);

        // Round caps/joins so thin strokes look clean — shared instance, same
        // settings as s_strokeStyle; allocating one per shape per redraw shows
        // up on pages with many shapes.
        var style = s_strokeStyle;

        // Rotated rect/ellipse: prepend a rotation about the bbox center to
        // whatever page transform the session already carries. Only these two
        // kinds ever carry Rotation (Line/Triangle vertices are free-form).
        var savedTransform = ds.Transform;
        bool rotated = s.Rotation != 0f && IsFinite(s.Rotation)
            && s.Kind is ShapeKind.Rectangle or ShapeKind.Ellipse;
        if (rotated)
            ds.Transform = Matrix3x2.CreateRotation(
                s.Rotation * MathF.PI / 180f,
                new Vector2(x + w * 0.5f, y + h * 0.5f)) * savedTransform;

        switch (s.Kind)
        {
            case ShapeKind.Rectangle:
                if (s.Filled) ds.FillRectangle(x, y, w, h, s.Color);
                if (w > 0 && h > 0)
                    ds.DrawRectangle(x, y, w, h, s.Color, s.StrokeWidth, style);
                break;

            case ShapeKind.Ellipse:
                float rx = w * 0.5f, ry = h * 0.5f;
                float cx = x + rx, cy = y + ry;
                if (s.Filled) ds.FillEllipse(cx, cy, rx, ry, s.Color);
                if (rx > 0 && ry > 0)
                    ds.DrawEllipse(cx, cy, rx, ry, s.Color, s.StrokeWidth, style);
                break;

            case ShapeKind.Line:
                ds.DrawLine(s.X1, s.Y1, s.X2, s.Y2, s.Color, s.StrokeWidth, style);
                break;

            case ShapeKind.Triangle:
            {
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(s.X1, s.Y1);
                pb.AddLine(s.X2, s.Y2);
                pb.AddLine(s.X3, s.Y3);
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var geo = CanvasGeometry.CreatePath(pb);
                if (s.Filled) ds.FillGeometry(geo, s.Color);
                ds.DrawGeometry(geo, s.Color, s.StrokeWidth, style);
                break;
            }
        }

        if (rotated) ds.Transform = savedTransform;
    }

    public void DrawStroke(CanvasDrawingSession ds, Stroke s)
    {
        if (s.Points.Count == 0) return;

        // Compute target opacity (highlighter is always semi-transparent). When the
        // stroke isn't fully opaque we draw it inside a CanvasActiveLayer with that
        // opacity, using opaque colors inside. This gives a flattened single-shape
        // composite — overlapping segments don't compound alpha, matching how the
        // live overlay also renders the in-progress stroke.
        byte rawAlpha = s.Color.A;
        float opacity = rawAlpha / 255f;
        if (s.Kind == StrokeKind.Highlighter)
            opacity = Math.Min(140f, rawAlpha == 255 ? 110f : rawAlpha) / 255f;

        bool needsLayer = opacity < 0.99f;
        Color drawColor = needsLayer
            ? Color.FromArgb(255, s.Color.R, s.Color.G, s.Color.B)
            : s.Color;

        CanvasActiveLayer? layer = needsLayer ? ds.CreateLayer(opacity) : null;
        try
        {
            if (s.Points.Count == 1)
            {
                var p = s.Points[0];
                ds.FillCircle(p.X, p.Y, s.Width * 0.5f * Math.Max(0.4f, p.Pressure), drawColor);
                return;
            }

            ds.DrawCachedGeometry(GetOrBakeStrokeGeometry(ds, s), drawColor);
        }
        finally
        {
            layer?.Dispose();
        }
    }

    // Catmull-Rom smoothing: the curve passes through every sample (so even
    // short strokes look curved, not segment-y) and is C1-continuous. Each
    // segment is a cubic Bezier whose control points come from neighbours:
    //   C1 = P[i]   + (P[i+1] - P[i-1]) / 6
    //   C2 = P[i+1] - (P[i+2] - P[i]  ) / 6
    // At the endpoints we clamp the missing neighbour to the endpoint itself.
    private static CanvasGeometry BuildCatmullRomGeometry(CanvasDrawingSession ds, Stroke s)
    {
        using var path = new CanvasPathBuilder(ds);
        var pts = s.Points;
        path.BeginFigure(pts[0].X, pts[0].Y);
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[i == 0 ? 0 : i - 1];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[i + 2 < pts.Count ? i + 2 : pts.Count - 1];
            var c1 = new Vector2(p1.X + (p2.X - p0.X) / 6f, p1.Y + (p2.Y - p0.Y) / 6f);
            var c2 = new Vector2(p2.X - (p3.X - p1.X) / 6f, p2.Y - (p3.Y - p1.Y) / 6f);
            path.AddCubicBezier(c1, c2, new Vector2(p2.X, p2.Y));
        }
        path.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(path);
    }

    // Builds a tapered pressure ribbon — one quad per segment plus a circle per
    // joint to hide the seams — as a SINGLE winding-fill geometry. Winding fill
    // makes the overlapping figures union instead of even-odd cancelling.
    private static CanvasGeometry BuildVariableWidthGeometry(CanvasDrawingSession ds, Stroke s)
    {
        var halfBase = s.Width * 0.5f;
        var pts = s.Points;

        using var path = new CanvasPathBuilder(ds);
        path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);

        // Start cap
        var first = pts[0];
        AddCircleFigure(path, first.X, first.Y, halfBase * Math.Max(0.2f, first.Pressure));

        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            var dir = new Vector2(b.X - a.X, b.Y - a.Y);
            var len = dir.Length();
            if (len < 0.0001f) continue;
            dir /= len;
            var n = new Vector2(-dir.Y, dir.X);
            var wA = halfBase * Math.Max(0.2f, a.Pressure);
            var wB = halfBase * Math.Max(0.2f, b.Pressure);

            path.BeginFigure(a.X + n.X * wA, a.Y + n.Y * wA);
            path.AddLine(b.X + n.X * wB, b.Y + n.Y * wB);
            path.AddLine(b.X - n.X * wB, b.Y - n.Y * wB);
            path.AddLine(a.X - n.X * wA, a.Y - n.Y * wA);
            path.EndFigure(CanvasFigureLoop.Closed);

            AddCircleFigure(path, b.X, b.Y, wB);
        }

        return CanvasGeometry.CreatePath(path);
    }

    // Full-circle figure as two half arcs (a single 360° arc is degenerate —
    // start and end coincide). Negative sweep so the circle winds the same
    // direction as the segment quads (left edge forward, right edge back):
    // under Winding fill, figures that wind OPPOSITE ways cancel where they
    // overlap, which punched a hole at every quad∩circle joint.
    private static void AddCircleFigure(CanvasPathBuilder path, float cx, float cy, float r)
    {
        if (r <= 0) return;
        var center = new Vector2(cx, cy);
        path.BeginFigure(cx + r, cy);
        path.AddArc(center, r, r, 0f, -MathF.PI);
        path.AddArc(center, r, r, -MathF.PI, -MathF.PI);
        path.EndFigure(CanvasFigureLoop.Closed);
    }

    private void DrawText(CanvasDrawingSession ds, TextElement t)
    {
        // Guard every value that feeds Win2D: a negative Rect width/height throws
        // in the Rect constructor, NaN/∞ throws inside DrawText, and FontSize <= 0
        // throws when CanvasTextFormat is realized. Any of these inside this native
        // callback would take the whole process down.
        if (!IsFinite(t.X) || !IsFinite(t.Y) || !IsFinite(t.Width) || !IsFinite(t.Height))
            return;
        if (t.Width <= 0 || t.Height <= 0) return;

        float fontSize = IsFinite(t.FontSize) && t.FontSize > 0 ? t.FontSize : 18f;
        using var fmt = new CanvasTextFormat
        {
            FontFamily = string.IsNullOrWhiteSpace(t.FontFamily) ? "Segoe UI" : t.FontFamily,
            FontSize = fontSize,
            FontWeight = t.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = t.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            WordWrapping = CanvasWordWrapping.Wrap
        };
        ds.DrawText(t.Text ?? "", new Rect(t.X, t.Y, t.Width, t.Height), t.Color, fmt);
    }
}

