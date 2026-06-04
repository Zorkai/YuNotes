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

    public void DrawPage(CanvasDrawingSession ds, ICanvasResourceCreator dev, NotePage page, TemplateSettings template,
                         CanvasBitmap? backgroundBitmap, System.Collections.Generic.IDictionary<string, CanvasBitmap>? imageCache = null,
                         string? skipTextId = null, bool overlayOnly = false,
                         float previewExtLeft = 0, float previewExtRight = 0)
    {
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
                ds.DrawImage(backgroundBitmap, new Rect(bgLeft, 0, bgW, page.Height));
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
            DrawShape(ds, sh);

        // Strokes (highlighters first so pen sits on top)
        foreach (var s in page.Strokes)
            if (s.Kind == StrokeKind.Highlighter) DrawStroke(ds, s);
        foreach (var s in page.Strokes)
            if (s.Kind == StrokeKind.Pen) DrawStroke(ds, s);

        // Text — skip the element currently being inline-edited
        foreach (var t in page.Texts)
        {
            if (t.Id == skipTextId) continue;
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

    public void DrawShape(CanvasDrawingSession ds, ShapeElement s)
    {
        if (s is null) return;
        float x = Math.Min(s.X1, s.X2);
        float y = Math.Min(s.Y1, s.Y2);
        float w = Math.Abs(s.X2 - s.X1);
        float h = Math.Abs(s.Y2 - s.Y1);

        // Use round caps/joins so thin strokes look clean
        var style = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round
        };

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

            if (s.PressureMode)
            {
                DrawVariableWidthStroke(ds, s, drawColor);
                return;
            }

            // Catmull-Rom smoothing: the curve passes through every sample (so even
            // short strokes look curved, not segment-y) and is C1-continuous. Each
            // segment is a cubic Bezier whose control points come from neighbours:
            //   C1 = P[i]   + (P[i+1] - P[i-1]) / 6
            //   C2 = P[i+1] - (P[i+2] - P[i]  ) / 6
            // At the endpoints we clamp the missing neighbour to the endpoint itself.
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
            using var geo = CanvasGeometry.CreatePath(path);
            ds.DrawGeometry(geo, drawColor, s.Width, s_strokeStyle);
        }
        finally
        {
            layer?.Dispose();
        }
    }

    // Render a contiguous range of stroke segments as a POLYLINE — straight lines
    // between consecutive points, no Catmull-Rom smoothing. Used by the live
    // overlay; per-segment tessellation is trivial (essentially zero CPU), so the
    // GPU has near-nothing to do per frame even at very high DPI.
    //
    // This is the Xournal++ trick: distance-thinned samples are dense enough that
    // straight lines between them look indistinguishable from a smoothed curve at
    // the pixel level, and we save the entire cost of cubic Bezier tessellation.
    // Final committed strokes still render with full Catmull-Rom smoothing in
    // DrawStroke, so the snap on lift is invisible.
    //
    // When `opaque` is true, the segment is rendered with full alpha (255). The
    // caller is expected to push a CanvasActiveLayer at the stroke's real opacity
    // so the chunks composite as one flattened shape instead of compounding alpha
    // at their joins.
    //
    // Caller invariant: 0 ≤ firstSeg ≤ lastSeg ≤ points.Count - 2.
    public void DrawStrokeSegments(CanvasDrawingSession ds, Stroke s, int firstSeg, int lastSeg, bool opaque = false)
    {
        var pts = s.Points;
        if (pts.Count < 2 || firstSeg < 0 || lastSeg < firstSeg || lastSeg > pts.Count - 2) return;

        var color = s.Color;
        if (opaque)
        {
            color = Color.FromArgb(255, color.R, color.G, color.B);
        }
        else if (s.Kind == StrokeKind.Highlighter)
        {
            color = Color.FromArgb((byte)Math.Min(140, color.A == 255 ? 110 : color.A), color.R, color.G, color.B);
        }

        if (s.PressureMode)
        {
            DrawVariableWidthSegments(ds, s, color, firstSeg, lastSeg);
            return;
        }

        using var path = new CanvasPathBuilder(ds);
        path.BeginFigure(pts[firstSeg].X, pts[firstSeg].Y);
        for (int i = firstSeg; i <= lastSeg; i++)
        {
            path.AddLine(new Vector2(pts[i + 1].X, pts[i + 1].Y));
        }
        path.EndFigure(CanvasFigureLoop.Open);
        using var geo = CanvasGeometry.CreatePath(path);
        ds.DrawGeometry(geo, color, s.Width, s_strokeStyle);
    }

    private void DrawVariableWidthSegments(CanvasDrawingSession ds, Stroke s, Color color, int firstSeg, int lastSeg)
    {
        var halfBase = s.Width * 0.5f;
        var pts = s.Points;

        if (firstSeg == 0)
        {
            var first = pts[0];
            ds.FillCircle(first.X, first.Y, halfBase * Math.Max(0.2f, first.Pressure), color);
        }

        for (int i = firstSeg; i <= lastSeg; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            var dir = new Vector2(b.X - a.X, b.Y - a.Y);
            var len = dir.Length();
            if (len < 0.0001f) continue;
            dir /= len;
            var n = new Vector2(-dir.Y, dir.X);
            var wA = halfBase * Math.Max(0.2f, a.Pressure);
            var wB = halfBase * Math.Max(0.2f, b.Pressure);

            using var path = new CanvasPathBuilder(ds);
            path.BeginFigure(a.X + n.X * wA, a.Y + n.Y * wA);
            path.AddLine(b.X + n.X * wB, b.Y + n.Y * wB);
            path.AddLine(b.X - n.X * wB, b.Y - n.Y * wB);
            path.AddLine(a.X - n.X * wA, a.Y - n.Y * wA);
            path.EndFigure(CanvasFigureLoop.Closed);
            using var quad = CanvasGeometry.CreatePath(path);
            ds.FillGeometry(quad, color);

            ds.FillCircle(b.X, b.Y, wB, color);
        }
    }

    // Builds a tapered ribbon by emitting one filled quad per segment plus a
    // filled circle at each point to hide the seams between segments.
    private void DrawVariableWidthStroke(CanvasDrawingSession ds, Stroke s, Color color)
    {
        var halfBase = s.Width * 0.5f;
        var pts = s.Points;

        // Start cap
        var first = pts[0];
        ds.FillCircle(first.X, first.Y, halfBase * Math.Max(0.2f, first.Pressure), color);

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

            using var path = new CanvasPathBuilder(ds);
            path.BeginFigure(a.X + n.X * wA, a.Y + n.Y * wA);
            path.AddLine(b.X + n.X * wB, b.Y + n.Y * wB);
            path.AddLine(b.X - n.X * wB, b.Y - n.Y * wB);
            path.AddLine(a.X - n.X * wA, a.Y - n.Y * wA);
            path.EndFigure(CanvasFigureLoop.Closed);
            using var quad = CanvasGeometry.CreatePath(path);
            ds.FillGeometry(quad, color);

            ds.FillCircle(b.X, b.Y, wB, color);
        }
    }

    private void DrawText(CanvasDrawingSession ds, TextElement t)
    {
        var fmt = new CanvasTextFormat
        {
            FontFamily = t.FontFamily,
            FontSize = t.FontSize,
            FontWeight = t.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = t.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            WordWrapping = CanvasWordWrapping.Wrap
        };
        ds.DrawText(t.Text ?? "", new Rect(t.X, t.Y, t.Width, t.Height), t.Color, fmt);
    }
}

