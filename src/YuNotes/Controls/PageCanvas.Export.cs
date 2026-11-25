using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.Text;
using Colors = Microsoft.UI.Colors;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Controls;

public sealed partial class PageCanvas
{

    // Pixel budget for one exported page. A4 (1240×1754) at 4× is ~35 MP; this
    // leaves room for that on 100% displays and caps hi-DPI / oversized pages
    // before the render target and PNG encode get memory-hungry.
    private const double ExportMaxPixels = 50_000_000;

    /// <summary>
    /// Picks the export render scale for this page: <paramref name="targetDensity"/>
    /// (the densest view the user can get on screen, px per page unit) so the
    /// exported PDF holds the same detail they see at max zoom — reduced only
    /// where that buys nothing (an overlay page with nothing on it) or would
    /// exceed the pixel budget / the GPU's max bitmap size.
    /// </summary>
    public float AutoExportScale(float targetDensity, bool overlayOnly)
    {
        bool hasBleed = (PrevPageCanvas?.BottomOverflow ?? 0f) > 0.5f
                     || (NextPageCanvas?.TopOverflow ?? 0f) > 0.5f;
        bool hasElements = Page.Strokes.Count > 0 || Page.Shapes.Count > 0
                        || Page.Texts.Count > 0 || Page.Images.Count > 0 || hasBleed;
        // Fully transparent overlay: the source PDF page shows through untouched.
        if (overlayOnly && !hasElements) return 1f;

        return ScaleForArea(Page.Width, Page.Height, targetDensity);
    }

    // targetDensity, reduced to fit a w×h area in the pixel budget and the
    // GPU's max bitmap size.
    private static float ScaleForArea(double w, double h, float targetDensity)
    {
        float scale = Math.Max(1f, targetDensity);
        double area = Math.Max(1.0, w * h);
        if (area * scale * scale > ExportMaxPixels)
            scale = (float)Math.Sqrt(ExportMaxPixels / area);
        float maxDim = CanvasDevice.GetSharedDevice().MaximumBitmapSizeInPixels;
        float longest = (float)Math.Max(1.0, Math.Max(w, h));
        scale = Math.Min(scale, maxDim / longest);
        return Math.Max(0.25f, scale);
    }

    /// <summary>
    /// Snapshot of this page for the vector PDF writer: strokes, shapes, images,
    /// background and template are written as PDF vectors from the model; only
    /// text is rasterized here (cropped to its bounds, at
    /// <paramref name="targetDensity"/>) until it gets real PDF text.
    /// </summary>
    public PageVectorExport BuildVectorExport(float targetDensity, bool overlayOnly)
    {
        var e = new PageVectorExport { Page = Page, Template = PageTemplate, OverlayOnly = overlayOnly };
        // Same offsets as DrawNeighborBleedForExport.
        if (PrevPageCanvas is { } prev && prev.BottomOverflow > 0.5f)
            e.Bleed.Add(new BleedSource(prev.Page, -(float)prev.Page.Height));
        if (NextPageCanvas is { } next && next.TopOverflow > 0.5f)
            e.Bleed.Add(new BleedSource(next.Page, (float)Page.Height));
        RenderTextLayerForExport(e, targetDensity);
        return e;
    }

    private void RenderTextLayerForExport(PageVectorExport e, float targetDensity)
    {
        var device = CanvasDevice.GetSharedDevice();
        var pageRect = new Windows.Foundation.Rect(0, 0, Page.Width, Page.Height);

        var items = new System.Collections.Generic.List<(TextElement Text, float OffsetY)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Collect(NotePage page, float oy)
        {
            foreach (var t in page.Texts)
            {
                if (PageRenderer.TextInkBounds(device, t) is not { } b) continue;
                b.Y += oy;
                if (b.Right < pageRect.X || b.X > pageRect.Right || b.Bottom < pageRect.Y || b.Y > pageRect.Bottom)
                    continue;   // e.g. a neighbour's text that doesn't cross the seam
                if (PageRenderer.TryBuildVectorText(device, t, oy) is { } vt)
                {
                    e.Texts.Add(vt);   // real PDF text
                    continue;
                }
                items.Add((t, oy));   // raster fallback
                minX = Math.Min(minX, b.X); minY = Math.Min(minY, b.Y);
                maxX = Math.Max(maxX, b.Right); maxY = Math.Max(maxY, b.Bottom);
            }
        }
        Collect(Page, 0f);
        foreach (var b in e.Bleed) Collect(b.Page, b.OffsetY);
        if (items.Count == 0) return;

        // Crop to the text, clamped to the page (the PDF clips there anyway).
        double x0 = Math.Max(0, Math.Floor(minX)), y0 = Math.Max(0, Math.Floor(minY));
        double x1 = Math.Min(Page.Width, Math.Ceiling(maxX)), y1 = Math.Min(Page.Height, Math.Ceiling(maxY));
        if (x1 - x0 < 1 || y1 - y0 < 1) return;

        float scale = ScaleForArea(x1 - x0, y1 - y0, targetDensity);
        int pxW = (int)Math.Ceiling((x1 - x0) * scale), pxH = (int)Math.Ceiling((y1 - y0) * scale);

        using var target = new CanvasRenderTarget(device, pxW, pxH, 96f);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            var toTarget = Matrix3x2.CreateTranslation((float)-x0, (float)-y0) * Matrix3x2.CreateScale(scale);
            foreach (var (t, oy) in items)
            {
                ds.Transform = Matrix3x2.CreateTranslation(0, oy) * toTarget;
                Renderer.DrawTextElement(ds, t);
            }
        }
        using var ms = new MemoryStream();
        target.SaveAsync(ms.AsRandomAccessStream(), CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
        e.TextPng = ms.ToArray();
        e.TextX = x0;
        e.TextY = y0;
        e.TextWidth = pxW / (double)scale;
        e.TextHeight = pxH / (double)scale;
    }

    public byte[] RenderToPng(float scale = 1.0f, bool overlayOnly = false)
    {
        var device = CanvasDevice.GetSharedDevice();

        // The on-screen control loads its background/image bitmaps lazily via Win2D's
        // OnCreateResources, which hasn't fired yet for a page that was just (re)created —
        // e.g. immediately after a reorder. In that window _bgBitmap/_imageCache are empty and
        // the thumbnail would render blank. Decode whatever's missing locally for this render
        // (kept in temps and disposed after — we don't touch the live control's own fields).
        var temps = new System.Collections.Generic.List<CanvasBitmap>();
        var bg = _bgBitmap;
        if (bg is null && Page.BackgroundPng is { Length: > 0 })
        {
            try
            {
                using var bgMs = new MemoryStream(Page.BackgroundPng);
                bg = CanvasBitmap.LoadAsync(device, bgMs.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
                temps.Add(bg);
            }
            catch { bg = null; }
        }

        var images = _imageCache;
        if (Page.Images.Any(i => !_imageCache.ContainsKey(i.Id)))
        {
            images = new System.Collections.Generic.Dictionary<string, CanvasBitmap>(_imageCache);
            foreach (var img in Page.Images.Where(i => !images.ContainsKey(i.Id)))
            {
                try
                {
                    using var imgMs = new MemoryStream(img.PngData);
                    var bmp = CanvasBitmap.LoadAsync(device, imgMs.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
                    images[img.Id] = bmp;
                    temps.Add(bmp);
                }
                catch { }
            }
        }

        try
        {
            using var target = new CanvasRenderTarget(device, (float)(Page.Width * scale), (float)(Page.Height * scale), 96f);
            using (var ds = target.CreateDrawingSession())
            {
                // overlayOnly: leave the canvas transparent so the underlying source PDF
                // page shows through; otherwise paint white as the flattened background.
                if (!overlayOnly) ds.Clear(Colors.White);
                if (scale != 1f) ds.Transform = Matrix3x2.CreateScale(scale);
                // Tape is a study aid on screen: exported images show what's under it
                // (the vector PDF writer leaves it out the same way).
                Renderer.HideTape = true;
                try
                {
                    Renderer.DrawPage(ds, device, Page, PageTemplate, bg, images, overlayOnly: overlayOnly);
                    // Bake cross-page bleed into the export so a stroke straddling the
                    // seam shows on both pages in external PDF viewers, matching the
                    // on-screen rendering.
                    DrawNeighborBleedForExport(ds, device);
                }
                finally { Renderer.HideTape = false; }
            }
            using var ms = new MemoryStream();
            target.SaveAsync(ms.AsRandomAccessStream(), CanvasBitmapFileFormat.Png)
                  .AsTask().GetAwaiter().GetResult();
            return ms.ToArray();
        }
        finally
        {
            foreach (var t in temps) t.Dispose();
        }
    }

    // Draws this page's full content (template + PDF background + elements) into an
    // arbitrary drawing session using whatever Transform/clip the caller has set.
    // Used by the in-app screenshot tool to composite a cross-page capture region.
    public void DrawContentInto(CanvasDrawingSession ds, ICanvasResourceCreator dev)
        => Renderer.DrawPage(ds, dev, Page, PageTemplate, _bgBitmap, _imageCache);

    // Exactly what a sticker from this region will look like: the page behind
    // it (cut to `clip`) when includeBackground, then only the `keep` elements
    // on top (never cut). Transparent where there's nothing.
    public byte[] RenderStickerPreviewPng(float x, float y, float w, float h,
        IReadOnlyList<Vector2>? clip, bool includeBackground, ISet<string> keep, float scale = 1f)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, w * scale, h * scale, 96f);
        var all = new HashSet<string>();
        foreach (var s in Page.Strokes) all.Add(s.Id);
        foreach (var s in Page.Shapes) all.Add(s.Id);
        foreach (var t in Page.Texts) all.Add(t.Id);
        foreach (var i in Page.Images) all.Add(i.Id);
        var others = new HashSet<string>(all);
        others.ExceptWith(keep);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(-x, -y) * Matrix3x2.CreateScale(scale);
            if (includeBackground)
            {
                Microsoft.Graphics.Canvas.Geometry.CanvasGeometry? geo = null;
                if (clip is { Count: >= 3 })
                    geo = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePolygon(device, clip.ToArray());
                using (geo)
                using (geo is null ? null : ds.CreateLayer(1f, geo))
                    Renderer.DrawPage(ds, device, Page, PageTemplate, _bgBitmap, _imageCache, skipElementIds: all);
            }
            if (keep.Count > 0)
                Renderer.DrawPage(ds, device, Page, PageTemplate, _bgBitmap, _imageCache, overlayOnly: true, skipElementIds: others);
        }
        using var ms = new MemoryStream();
        target.SaveAsync(ms.AsRandomAccessStream(), CanvasBitmapFileFormat.Png)
              .AsTask().GetAwaiter().GetResult();
        return ms.ToArray();
    }

    // Dashed outline of the area a sticker would be cut from (null hides it).
    private Polygon? _stickerAreaOutline;
    public void ShowStickerArea(IReadOnlyList<Vector2>? poly)
    {
        if (poly is not { Count: >= 3 })
        {
            if (_stickerAreaOutline is not null) _stickerAreaOutline.Visibility = Visibility.Collapsed;
            return;
        }
        if (_stickerAreaOutline is null)
        {
            _stickerAreaOutline = new Polygon
            {
                Stroke = new SolidColorBrush(Color.FromArgb(230, 91, 107, 255)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                StrokeLineJoin = PenLineJoin.Round,
                Fill = new SolidColorBrush(Color.FromArgb(22, 91, 107, 255)),
                IsHitTestVisible = false,
            };
            Canvas.SetZIndex(_stickerAreaOutline, 8500);
            _overlay.Children.Add(_stickerAreaOutline);
        }
        var pts = new PointCollection();
        foreach (var v in poly) pts.Add(new Windows.Foundation.Point(v.X, v.Y));
        _stickerAreaOutline.Points = pts;
        _stickerAreaOutline.Visibility = Visibility.Visible;
    }

    // Renders only the page itself (PDF / template background, no ink, text,
    // images or tape) for the given region, at `scale`× for a crisp sticker.
    // With a polygon, everything outside it is transparent (a lasso cut-out).
    public byte[] RenderBackgroundRegionToPng(float x, float y, float w, float h,
        IReadOnlyList<Vector2>? clipPolygon = null, float scale = 2f)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, w * scale, h * scale, 96f);
        var skip = new HashSet<string>();
        foreach (var s in Page.Strokes) skip.Add(s.Id);
        foreach (var s in Page.Shapes) skip.Add(s.Id);
        foreach (var t in Page.Texts) skip.Add(t.Id);
        foreach (var i in Page.Images) skip.Add(i.Id);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(-x, -y) * Matrix3x2.CreateScale(scale);
            Microsoft.Graphics.Canvas.Geometry.CanvasGeometry? clip = null;
            if (clipPolygon is { Count: >= 3 })
                clip = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePolygon(device, clipPolygon.ToArray());
            using (clip)
            using (clip is null ? null : ds.CreateLayer(1f, clip))
                Renderer.DrawPage(ds, device, Page, PageTemplate, _bgBitmap, _imageCache, skipElementIds: skip);
        }
        using var ms = new MemoryStream();
        target.SaveAsync(ms.AsRandomAccessStream(), CanvasBitmapFileFormat.Png)
              .AsTask().GetAwaiter().GetResult();
        return ms.ToArray();
    }

    public byte[] RenderRegionToPng(float x, float y, float w, float h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, w, h, 96f);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.White);
            ds.Transform = Matrix3x2.CreateTranslation(-x, -y);
            Renderer.DrawPage(ds, device, Page, PageTemplate, _bgBitmap, _imageCache);
        }
        using var ms = new MemoryStream();
        target.SaveAsync(ms.AsRandomAccessStream(), CanvasBitmapFileFormat.Png)
              .AsTask().GetAwaiter().GetResult();
        return ms.ToArray();
    }
}
