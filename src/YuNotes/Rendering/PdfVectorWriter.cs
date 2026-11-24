using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Drawing;
using YuNotes.Models;
using Color = Windows.UI.Color;

namespace YuNotes.Rendering;

// A neighbouring page whose ink spills across the seam into this page, and the
// vertical offset that maps its coords into this page's coords.
public sealed record BleedSource(NotePage Page, float OffsetY);

// Everything needed to write one page into a PDF as vectors. Built on the UI
// thread (the text layer needs Win2D); drawn by PdfVectorWriter on any thread.
public sealed class PageVectorExport
{
    public required NotePage Page { get; init; }
    public required TemplateSettings Template { get; init; }
    // True when the page sits on top of an original source-PDF page: draw only
    // the user's elements (no white fill, background or template).
    public bool OverlayOnly { get; init; }
    public List<BleedSource> Bleed { get; } = new();

    // Text elements written as real PDF text (own page and bleed).
    public List<VectorText> Texts { get; } = new();

    // Text that can't be written faithfully as PDF text (emoji, right-to-left,
    // fonts PDFsharp can't embed) is rasterized by PageCanvas into a PNG
    // cropped to its bounds, placed at this rect (page coords).
    public byte[]? TextPng { get; set; }
    public double TextX { get; set; }
    public double TextY { get; set; }
    public double TextWidth { get; set; }
    public double TextHeight { get; set; }
}

// A piece of text in one font, placed at its baseline origin — relative to the
// owning element's top-left — exactly where DirectWrite laid it out.
public sealed record TextSegment(string Text, string FontKey, float FontSize, float X, float Y);

// One text element as PDF text: DirectWrite's line breaks, positions and
// fallback fonts, captured on the UI thread (see PageCanvas).
public sealed class VectorText
{
    public Color Color { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double Rotation { get; init; }
    public float OffsetY { get; init; }   // bleed: neighbour → this page
    public List<TextSegment> Segments { get; } = new();
}

// Writes a page's content into a PDF as vector paths — the same geometry
// PageRenderer draws with Win2D — so ink stays sharp at any zoom in any viewer
// and costs a few hundred bytes per stroke instead of a full-page raster.
// Draw order matches PageRenderer.DrawPage: background, template, images,
// shapes, highlighter strokes, pen strokes, text.
public static class PdfVectorWriter
{
    // Page coords map onto the PDF page exactly like the old full-page PNG
    // did (stretched to targetWidth × targetHeight points), so output lines up
    // with existing files and with cloned source-PDF pages.
    public static void Draw(XGraphics gfx, PageVectorExport e, double targetWidth, double targetHeight)
    {
        var page = e.Page;
        double w = page.Width, h = page.Height;
        if (!(w > 0) || !(h > 0) || !IsFinite(w) || !IsFinite(h)) return;

        var state = gfx.Save();
        try
        {
            gfx.ScaleTransform(targetWidth / w, targetHeight / h);
            gfx.IntersectClip(new XRect(0, 0, w, h));

            if (!e.OverlayOnly) DrawBackground(gfx, e);

            DrawElements(gfx, page, cull: null);

            // Cross-page bleed: draw only the neighbour's elements that can
            // reach this page (region in the neighbour's coords), so a page
            // doesn't carry its neighbours' whole ink, clipped away.
            foreach (var b in e.Bleed)
            {
                var s = gfx.Save();
                gfx.TranslateTransform(0, b.OffsetY);
                DrawElements(gfx, b.Page, cull: new XRect(0, -b.OffsetY, w, h));
                gfx.Restore(s);
            }

            foreach (var vt in e.Texts) DrawText(gfx, vt);

            if (e.TextPng is { Length: > 0 } textPng && e.TextWidth > 0 && e.TextHeight > 0)
                DrawPng(gfx, textPng, e.TextX, e.TextY, e.TextWidth, e.TextHeight);
        }
        finally { gfx.Restore(state); }
    }

    // ── Background + template ───────────────────────────────────────────────

    private static void DrawBackground(XGraphics gfx, PageVectorExport e)
    {
        var page = e.Page;
        double w = page.Width, h = page.Height;
        gfx.DrawRectangle(XBrushes.White, 0, 0, w, h);

        double bgLeft = page.BackgroundLeft;
        double bgW = page.BackgroundContentWidth > 0 ? page.BackgroundContentWidth : w - bgLeft;
        if (page.BackgroundPng is { Length: > 0 } bg && bgW > 0)
            DrawPng(gfx, bg, bgLeft, 0, bgW, h);

        var t = e.Template;
        if (t.SkipImportedPdf && PageRenderer.HasImportedPdf(page))
        {
            // Template only on the extension strips beside the PDF content,
            // each clipped so the pattern stays aligned across them.
            double pdfRight = bgLeft + bgW;
            if (bgLeft > 0.5) DrawTemplateClipped(gfx, w, h, t, new XRect(0, 0, bgLeft, h));
            if (w - pdfRight > 0.5) DrawTemplateClipped(gfx, w, h, t, new XRect(pdfRight, 0, w - pdfRight, h));
        }
        else
        {
            DrawTemplate(gfx, w, h, t);
        }
    }

    private static void DrawTemplateClipped(XGraphics gfx, double w, double h, TemplateSettings t, XRect clip)
    {
        var s = gfx.Save();
        gfx.IntersectClip(clip);
        DrawTemplate(gfx, w, h, t);
        gfx.Restore(s);
    }

    // Mirrors TemplateService.DrawTemplate.
    private static void DrawTemplate(XGraphics gfx, double width, double height, TemplateSettings t)
    {
        if (t.Kind == TemplateKind.Blank) return;
        double spacing = t.Spacing;
        double thickness = t.LineThickness;
        if (!(spacing > 0.5) || !IsFinite(spacing) || !IsFinite(thickness) || thickness < 0) return;
        var color = ToXColor(Services.TemplateService.ParseHex(t.LineColorHex));
        var pen = new XPen(color, thickness);

        switch (t.Kind)
        {
            case TemplateKind.Grid:
                for (double x = spacing; x < width; x += spacing) gfx.DrawLine(pen, x, 0, x, height);
                for (double y = spacing; y < height; y += spacing) gfx.DrawLine(pen, 0, y, width, y);
                break;
            case TemplateKind.Dots:
            {
                // One path for every dot keeps the content stream compact.
                var path = new XGraphicsPath { FillMode = XFillMode.Winding };
                double r = thickness * 1.2;
                for (double x = spacing; x < width; x += spacing)
                    for (double y = spacing; y < height; y += spacing)
                        AddCircle(path, x, y, r);
                gfx.DrawPath(new XSolidBrush(color), path);
                break;
            }
            case TemplateKind.Lined:
                for (double y = spacing; y < height; y += spacing) gfx.DrawLine(pen, 0, y, width, y);
                break;
            case TemplateKind.Cornell:
                gfx.DrawLine(pen, width * 0.25, 0, width * 0.25, height);
                gfx.DrawLine(pen, 0, height * 0.85, width, height * 0.85);
                var thin = new XPen(color, thickness * 0.6);
                for (double y = spacing; y < height; y += spacing) gfx.DrawLine(thin, width * 0.25, y, width, y);
                break;
        }
    }

    // ── Elements ────────────────────────────────────────────────────────────

    // cull: when set, skip elements whose bounds can't reach this rect.
    private static void DrawElements(XGraphics gfx, NotePage page, XRect? cull)
    {
        foreach (var img in page.Images)
        {
            if (!IsFinite(img.X) || !IsFinite(img.Y) || !IsFinite(img.Width) || !IsFinite(img.Height) ||
                img.Width <= 0 || img.Height <= 0 || img.PngData is not { Length: > 0 })
                continue;
            if (cull is { } c && !RotatedBoxIntersects(c, img.X, img.Y, img.Width, img.Height, img.Rotation)) continue;
            var s = gfx.Save();
            if (img.Rotation != 0 && IsFinite(img.Rotation))
                gfx.RotateAtTransform(img.Rotation, new XPoint(img.X + img.Width * 0.5, img.Y + img.Height * 0.5));
            DrawPng(gfx, img.PngData, img.X, img.Y, img.Width, img.Height);
            gfx.Restore(s);
        }

        foreach (var sh in page.Shapes)
        {
            if (cull is { } c && !ShapeIntersects(c, sh)) continue;
            DrawShape(gfx, sh);
        }

        foreach (var st in page.Strokes)
            if (st.Kind == StrokeKind.Highlighter && (cull is not { } c || StrokeIntersects(c, st)))
                DrawStroke(gfx, st);
        foreach (var st in page.Strokes)
            if (st.Kind == StrokeKind.Pen && (cull is not { } c || StrokeIntersects(c, st)))
                DrawStroke(gfx, st);
    }

    private static void DrawStroke(XGraphics gfx, Stroke s)
    {
        var pts = s.Points;
        if (pts.Count == 0 || !IsFinite(s.Width) || s.Width <= 0) return;
        foreach (var p in pts)
            if (!IsFinite(p.X) || !IsFinite(p.Y) || !IsFinite(p.Pressure)) return;

        // Same opacity rule as PageRenderer.DrawStroke. A single PDF fill or
        // stroke paints each pixel once, so overlapping parts of one stroke
        // don't compound alpha — the same as Win2D's opacity layer.
        byte rawAlpha = s.Color.A;
        double opacity = rawAlpha / 255.0;
        if (s.Kind == StrokeKind.Highlighter)
            opacity = Math.Min(140.0, rawAlpha == 255 ? 110.0 : rawAlpha) / 255.0;
        var color = XColor.FromArgb((int)Math.Round(Math.Clamp(opacity, 0, 1) * 255), s.Color.R, s.Color.G, s.Color.B);

        if (pts.Count == 1)
        {
            var p = pts[0];
            double r = s.Width * 0.5 * Math.Max(0.4f, p.Pressure);
            gfx.DrawEllipse(new XSolidBrush(color), p.X - r, p.Y - r, r * 2, r * 2);
            return;
        }

        if (s.PressureMode)
        {
            gfx.DrawPath(new XSolidBrush(color), BuildVariableWidthPath(s));
        }
        else
        {
            var pen = new XPen(color, s.Width)
            {
                LineCap = XLineCap.Round,
                LineJoin = XLineJoin.Round
            };
            gfx.DrawPath(pen, BuildCatmullRomPath(s));
        }
    }

    // Same Catmull-Rom → cubic Bezier conversion as PageRenderer.
    private static XGraphicsPath BuildCatmullRomPath(Stroke s)
    {
        var pts = s.Points;
        var path = new XGraphicsPath();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[i == 0 ? 0 : i - 1];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[i + 2 < pts.Count ? i + 2 : pts.Count - 1];
            path.AddBezier(
                new XPoint(p1.X, p1.Y),
                new XPoint(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0),
                new XPoint(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0),
                new XPoint(p2.X, p2.Y));
        }
        return path;
    }

    // Same tapered ribbon as PageRenderer.BuildVariableWidthGeometry: one
    // closed outline (left edge forward, right edge back) with round caps, and
    // a round joint only at turns sharper than 60°, unioned by a nonzero-winding
    // fill. Every figure winds the same way so overlaps add instead of
    // cancelling. (Was a quad + 4-curve circle per sample: ~4x the save time
    // and ~10 MB for a single handwritten page.)
    private static XGraphicsPath BuildVariableWidthPath(Stroke s)
    {
        double halfBase = s.Width * 0.5;
        var src = s.Points;
        var path = new XGraphicsPath { FillMode = XFillMode.Winding };

        // Drop zero-length segments (no direction to offset along).
        var px = new double[src.Count];
        var py = new double[src.Count];
        var w = new double[src.Count];
        int n = 0;
        for (int i = 0; i < src.Count; i++)
        {
            double x = src[i].X, y = src[i].Y;
            if (n > 0)
            {
                double ddx = x - px[n - 1], ddy = y - py[n - 1];
                if (Math.Sqrt(ddx * ddx + ddy * ddy) < 0.0001) continue;
            }
            px[n] = x; py[n] = y;
            w[n] = halfBase * Math.Max(0.2f, src[i].Pressure);
            n++;
        }
        if (n == 1)
        {
            AddCircle(path, px[0], py[0], w[0]);
            return path;
        }

        const double SharpTurnCos = 0.5;   // keep in sync with PageRenderer
        var outline = new XPoint[n * 2];
        for (int i = 0; i < n; i++)
        {
            double nx, ny, scale = 1;
            if (i == 0 || i == n - 1)
            {
                int a = i == 0 ? 0 : i - 1;
                var (dx, dy) = Dir(px[a], py[a], px[a + 1], py[a + 1]);
                nx = -dy; ny = dx;
            }
            else
            {
                var (pdx, pdy) = Dir(px[i - 1], py[i - 1], px[i], py[i]);
                var (ndx, ndy) = Dir(px[i], py[i], px[i + 1], py[i + 1]);
                double sx = -pdy - ndy, sy = pdx + ndx;   // nA + nB
                double len = Math.Sqrt(sx * sx + sy * sy);
                if (pdx * ndx + pdy * ndy < SharpTurnCos || len < 1e-4)
                {
                    // Sharp turn: no miter (it would spike); a round joint fills the gap.
                    if (len < 1e-4) { nx = -pdy; ny = pdx; }
                    else { nx = sx / len; ny = sy / len; }
                    AddCircle(path, px[i], py[i], w[i]);
                }
                else
                {
                    nx = sx / len; ny = sy / len;
                    scale = 1 / Math.Max(0.5, nx * -pdy + ny * pdx);   // miter length
                }
            }
            double r = w[i] * scale;
            outline[i] = new XPoint(px[i] + nx * r, py[i] + ny * r);
            outline[2 * n - 1 - i] = new XPoint(px[i] - nx * r, py[i] - ny * r);
        }
        path.StartFigure();
        path.AddPolygon(outline);
        path.CloseFigure();

        AddCircle(path, px[0], py[0], w[0]);                 // start cap
        AddCircle(path, px[n - 1], py[n - 1], w[n - 1]);     // end cap
        return path;
    }

    private static (double X, double Y) Dir(double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len = Math.Sqrt(dx * dx + dy * dy);
        return (dx / len, dy / len);
    }

    // Circle as four cubic arcs with DECREASING angle — the same winding sense
    // as the ribbon quads above (negative shoelace area in y-down coords), so
    // a nonzero fill unions them instead of punching holes at the joints.
    private static void AddCircle(XGraphicsPath path, double cx, double cy, double r)
    {
        if (!(r > 0)) return;
        const double step = -Math.PI / 2;
        double k = 4.0 / 3.0 * Math.Tan(step / 4);
        path.StartFigure();
        for (int q = 0; q < 4; q++)
        {
            double a0 = q * step, a1 = (q + 1) * step;
            double c0 = Math.Cos(a0), s0 = Math.Sin(a0);
            double c1 = Math.Cos(a1), s1 = Math.Sin(a1);
            var p0 = new XPoint(cx + r * c0, cy + r * s0);
            var p3 = new XPoint(cx + r * c1, cy + r * s1);
            var p1 = new XPoint(p0.X - k * r * s0, p0.Y + k * r * c0);
            var p2 = new XPoint(p3.X + k * r * s1, p3.Y - k * r * c1);
            path.AddBezier(p0, p1, p2, p3);
        }
        path.CloseFigure();
    }

    private static void DrawShape(XGraphics gfx, ShapeElement s)
    {
        if (!IsFinite(s.X1) || !IsFinite(s.Y1) || !IsFinite(s.X2) || !IsFinite(s.Y2) ||
            (s.Kind == ShapeKind.Triangle && (!IsFinite(s.X3) || !IsFinite(s.Y3))))
            return;
        if (!IsFinite(s.StrokeWidth) || s.StrokeWidth < 0) return;

        double x = Math.Min(s.X1, s.X2), y = Math.Min(s.Y1, s.Y2);
        double w = Math.Abs(s.X2 - s.X1), h = Math.Abs(s.Y2 - s.Y1);
        var color = ToXColor(s.Color);
        var pen = new XPen(color, s.StrokeWidth) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
        var brush = new XSolidBrush(color);

        var state = gfx.Save();
        if (s.Rotation != 0f && IsFinite(s.Rotation) && s.Kind is ShapeKind.Rectangle or ShapeKind.Ellipse)
            gfx.RotateAtTransform(s.Rotation, new XPoint(x + w * 0.5, y + h * 0.5));

        switch (s.Kind)
        {
            case ShapeKind.Rectangle:
                if (s.Filled && w > 0 && h > 0) gfx.DrawRectangle(brush, x, y, w, h);
                if (w > 0 && h > 0 && s.StrokeWidth > 0) gfx.DrawRectangle(pen, x, y, w, h);
                break;
            case ShapeKind.Ellipse:
                if (s.Filled && w > 0 && h > 0) gfx.DrawEllipse(brush, x, y, w, h);
                if (w > 0 && h > 0 && s.StrokeWidth > 0) gfx.DrawEllipse(pen, x, y, w, h);
                break;
            case ShapeKind.Line:
                if (s.StrokeWidth > 0) gfx.DrawLine(pen, s.X1, s.Y1, s.X2, s.Y2);
                break;
            case ShapeKind.Tape:
                break;   // a study aid on screen only: exported pages show what's under it
            case ShapeKind.Arrow:
            case ShapeKind.Axes:
            case ShapeKind.Grid:
                if (s.StrokeWidth > 0)
                    foreach (var (a, b) in ShapeGeometry.Segments(s))
                        gfx.DrawLine(pen, a.X, a.Y, b.X, b.Y);
                break;
            case ShapeKind.Triangle:
            {
                var tri = new[] { new XPoint(s.X1, s.Y1), new XPoint(s.X2, s.Y2), new XPoint(s.X3, s.Y3) };
                if (s.Filled) gfx.DrawPolygon(brush, tri, XFillMode.Alternate);
                if (s.StrokeWidth > 0) gfx.DrawPolygon(pen, tri);
                break;
            }
        }
        gfx.Restore(state);
    }

    // Real PDF text: selectable, searchable, sharp at any zoom. Each segment
    // starts at DirectWrite's own position, so line breaks match the app and
    // any advance-width difference can't drift past one word.
    private static void DrawText(XGraphics gfx, VectorText vt)
    {
        var brush = new XSolidBrush(ToXColor(vt.Color));
        var state = gfx.Save();
        gfx.TranslateTransform(0, vt.OffsetY);
        if (vt.Rotation != 0 && IsFinite(vt.Rotation))
            gfx.RotateAtTransform(vt.Rotation, new XPoint(vt.X + vt.Width * 0.5, vt.Y + vt.Height * 0.5));
        gfx.TranslateTransform(vt.X, vt.Y);
        foreach (var seg in vt.Segments)
        {
            var font = PdfFontResolver.GetFont(seg.FontKey, seg.FontSize);
            gfx.DrawString(seg.Text, font, brush, seg.X, seg.Y, XStringFormats.BaseLineLeft);
        }
        gfx.Restore(state);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void DrawPng(XGraphics gfx, byte[] data, double x, double y, double w, double h)
    {
        try
        {
            using var ms = new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true);
            var img = XImage.FromStream(ms);
            gfx.DrawImage(img, x, y, w, h);
        }
        catch (Exception ex)
        {
            // An undecodable image must not fail the whole save.
            App.LogError(ex, "PdfVectorWriter.DrawPng");
        }
    }

    private static XColor ToXColor(Color c) => XColor.FromArgb(c.A, c.R, c.G, c.B);

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    private static bool Intersects(in XRect r, double minX, double minY, double maxX, double maxY)
        => maxX >= r.X && minX <= r.X + r.Width && maxY >= r.Y && minY <= r.Y + r.Height;

    // Conservative: the circle around the box covers it at any rotation.
    private static bool RotatedBoxIntersects(in XRect r, double x, double y, double w, double h, double rotation)
    {
        if (rotation == 0) return Intersects(r, x, y, x + w, y + h);
        double cx = x + w * 0.5, cy = y + h * 0.5, rad = Math.Sqrt(w * w + h * h) * 0.5;
        return Intersects(r, cx - rad, cy - rad, cx + rad, cy + rad);
    }

    private static bool ShapeIntersects(in XRect r, ShapeElement s)
    {
        double minX = Math.Min(s.X1, s.X2), minY = Math.Min(s.Y1, s.Y2);
        double maxX = Math.Max(s.X1, s.X2), maxY = Math.Max(s.Y1, s.Y2);
        if (s.Kind == ShapeKind.Triangle)
        {
            minX = Math.Min(minX, s.X3); minY = Math.Min(minY, s.Y3);
            maxX = Math.Max(maxX, s.X3); maxY = Math.Max(maxY, s.Y3);
        }
        double pad = s.StrokeWidth * 0.5 + 2 + ShapeGeometry.Overhang(s);
        if (s.Rotation != 0f)
            return RotatedBoxIntersects(r, minX - pad, minY - pad, maxX - minX + pad * 2, maxY - minY + pad * 2, s.Rotation);
        return Intersects(r, minX - pad, minY - pad, maxX + pad, maxY + pad);
    }

    // Point hull padded by the half width plus the Catmull-Rom overshoot bound
    // (same reasoning as PageRenderer.StrokeIntersects).
    private static bool StrokeIntersects(in XRect r, Stroke s)
    {
        var pts = s.Points;
        if (pts.Count == 0) return false;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double maxSpanSq = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            if (i >= 2)
            {
                double dx = pts[i].X - pts[i - 2].X, dy = pts[i].Y - pts[i - 2].Y;
                maxSpanSq = Math.Max(maxSpanSq, dx * dx + dy * dy);
            }
        }
        double pad = s.Width * 0.5 + Math.Sqrt(maxSpanSq) / 6 + 2;
        return Intersects(r, minX - pad, minY - pad, maxX + pad, maxY + pad);
    }
}
