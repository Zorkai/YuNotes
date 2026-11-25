using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Controls;

public sealed partial class InkCanvasControl
{

    /// <summary>Renders the bounding box of the current selection as a PNG.</summary>
    // ── Stickers ──────────────────────────────────────────────────────────────

    // Whether the selected elements sit on an imported PDF page, so the save
    // dialog can offer to keep the PDF behind them.
    public bool SelectionIsOnPdfPage =>
        HasSelection && ActivePageCanvas is { } pc && Rendering.PageRenderer.HasImportedPdf(pc.Page);

    private PageCanvas? AreaPageCanvas =>
        Context.LastSelectionArea is { Count: >= 3 } && Context.LastSelectionAreaPage is { } p
            ? _pageCanvases.FirstOrDefault(c => ReferenceEquals(c.Page, p)) : null;

    // A rectangle/lasso over an imported PDF that selected no ink: that piece
    // of the PDF can be saved as a sticker on its own.
    public bool HasStickerArea =>
        !HasSelection && AreaPageCanvas is { } pc && Rendering.PageRenderer.HasImportedPdf(pc.Page);

    public void ClearSelectionArea()
    {
        Context.LastSelectionArea = null;
        Context.LastSelectionAreaPage = null;
        UpdateStickerAreaOutline();
    }

    // The region a sticker is cut from. For a selection with the PDF included
    // it's the whole rectangle/lasso the user drew (so a big piece of PDF with
    // a little ink on it keeps its size), widened if needed so no selected ink
    // is cut off; otherwise just the selection's own box. Kept on the page.
    private bool ResolveStickerRegion(bool fromSelection, bool includeBackground, out PageCanvas pc,
        out (float X, float Y, float W, float H) region, out IReadOnlyList<System.Numerics.Vector2>? clip)
    {
        pc = null!; region = default; clip = null;
        if (fromSelection)
        {
            if (ActivePageCanvas is not { } apc || !HasSelection || !apc.SelectionBbox(out var b)) return false;
            pc = apc;
            float x0 = b.X, y0 = b.Y, x1 = b.Right, y1 = b.Bottom;
            if (includeBackground && AreaPageCanvas is { } areaPc && ReferenceEquals(areaPc, apc)
                && Context.LastSelectionArea is { } poly)
            {
                var a = PolygonBounds(poly);
                bool overlaps = !(a.X > b.Right || a.X + a.W < b.X || a.Y > b.Bottom || a.Y + a.H < b.Y);
                if (overlaps)
                {
                    x0 = MathF.Min(x0, a.X); y0 = MathF.Min(y0, a.Y);
                    x1 = MathF.Max(x1, a.X + a.W); y1 = MathF.Max(y1, a.Y + a.H);
                    clip = poly;
                }
            }
            region = (x0, y0, MathF.Max(1, x1 - x0), MathF.Max(1, y1 - y0));
            return true;
        }
        if (AreaPageCanvas is not { } p2 || Context.LastSelectionArea is not { } area) return false;
        pc = p2;
        var r = PolygonBounds(area);
        float ax0 = Math.Max(0, r.X), ay0 = Math.Max(0, r.Y);
        float ax1 = Math.Min((float)p2.Page.Width, r.X + r.W), ay1 = Math.Min((float)p2.Page.Height, r.Y + r.H);
        if (ax1 - ax0 < 4 || ay1 - ay0 < 4) return false;
        region = (ax0, ay0, ax1 - ax0, ay1 - ay0);
        clip = area;
        return true;
    }

    private HashSet<string> SelectedIds() =>
        new(Context.SelectedStrokeIds.Concat(Context.SelectedShapeIds)
            .Concat(Context.SelectedTextIds).Concat(Context.SelectedImageIds));

    // Picture of exactly what will be saved (also used as the sticker's thumbnail).
    public byte[]? RenderStickerPreview(bool fromSelection, bool includeBackground)
    {
        if (!ResolveStickerRegion(fromSelection, includeBackground || !fromSelection, out var pc, out var r, out var clip)) return null;
        var keep = fromSelection ? SelectedIds() : new HashSet<string>();
        float longest = MathF.Max(r.W, r.H);
        float scale = longest > 0 ? Math.Clamp(480f / longest, 0.25f, 2f) : 1f;
        return pc.RenderStickerPreviewPng(r.X, r.Y, r.W, r.H, clip, includeBackground || !fromSelection, keep, scale);
    }

    // Shows the dashed outline of the remembered area on its page, while that
    // area is what a sticker would be cut from.
    public void UpdateStickerAreaOutline()
    {
        var target = Context.LastSelectionArea is { Count: >= 3 } && AreaPageCanvas is { } apc
                     && Rendering.PageRenderer.HasImportedPdf(apc.Page)
                     && (!HasSelection || ReferenceEquals(ActivePageCanvas, apc))
            ? apc : null;
        foreach (var pc in _pageCanvases)
            pc.ShowStickerArea(ReferenceEquals(pc, target) ? Context.LastSelectionArea : null);
    }

    private static (float X, float Y, float W, float H) PolygonBounds(IReadOnlyList<System.Numerics.Vector2> poly)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var v in poly)
        {
            minX = MathF.Min(minX, v.X); minY = MathF.Min(minY, v.Y);
            maxX = MathF.Max(maxX, v.X); maxY = MathF.Max(maxY, v.Y);
        }
        return (minX, minY, maxX - minX, maxY - minY);
    }

    // The PDF under the remembered area as a one-picture sticker, cut to the
    // lasso's shape (transparent outside it).
    public Services.StickerContent? CopySelectionAreaAsSticker(out byte[]? png)
    {
        png = null;
        // Kept on the page: a lasso can stray past the paper's edge.
        if (!ResolveStickerRegion(false, true, out var pc, out var r, out var clip)) return null;
        png = pc.RenderBackgroundRegionToPng(r.X, r.Y, r.W, r.H, clip);
        var c = new Services.StickerContent { Width = r.W, Height = r.H };
        c.Images.Add(new ImageElement { X = 0, Y = 0, Width = r.W, Height = r.H, PngData = png });
        return c;
    }

    // Screen bounds of the remembered area, for placing its action bar.
    public bool TryGetAreaScreenBounds(UIElement relativeTo, out Windows.Foundation.Rect bounds)
    {
        bounds = default;
        if (AreaPageCanvas is not { } pc || Context.LastSelectionArea is not { } poly) return false;
        try
        {
            var (x, y, w, h) = PolygonBounds(poly);
            var t = pc.TransformToVisual(relativeTo);
            bounds = t.TransformBounds(new Windows.Foundation.Rect(x, y, w, h));
            return true;
        }
        catch { return false; }
    }

    // Copies the current selection out as sticker content: fresh copies of the
    // selected elements, shifted so the selection's bounding box starts at 0,0.
    // With includeBackground, the PDF behind the selection is added as a
    // picture underneath, so the ink stays editable on top of it.
    public Services.StickerContent? CopySelectionAsSticker(bool includeBackground = false)
    {
        if (!ResolveStickerRegion(true, includeBackground, out var pc, out var r, out var clip)) return null;
        var page = pc.Page;
        float dx = -r.X, dy = -r.Y;
        var c = new Services.StickerContent { Width = r.W, Height = r.H };
        if (includeBackground)
        {
            var bg = pc.RenderBackgroundRegionToPng(r.X, r.Y, r.W, r.H, clip);
            c.Images.Add(new ImageElement { X = 0, Y = 0, Width = c.Width, Height = c.Height, PngData = bg });
        }
        foreach (var s in page.Strokes.Where(s => Context.SelectedStrokeIds.Contains(s.Id)))
        {
            var k = new Stroke { Kind = s.Kind, Color = s.Color, Width = s.Width, PressureMode = s.PressureMode };
            foreach (var p in s.Points) k.Points.Add(new InkPoint(p.X + dx, p.Y + dy, p.Pressure));
            c.Strokes.Add(k);
        }
        foreach (var sh in page.Shapes.Where(s => Context.SelectedShapeIds.Contains(s.Id)))
            c.Shapes.Add(new ShapeElement
            {
                Kind = sh.Kind, Color = sh.Color, StrokeWidth = sh.StrokeWidth, Filled = sh.Filled, Rotation = sh.Rotation,
                X1 = sh.X1 + dx, Y1 = sh.Y1 + dy, X2 = sh.X2 + dx, Y2 = sh.Y2 + dy,
                X3 = sh.Kind == ShapeKind.Triangle ? sh.X3 + dx : sh.X3,
                Y3 = sh.Kind == ShapeKind.Triangle ? sh.Y3 + dy : sh.Y3,
            });
        foreach (var t in page.Texts.Where(t => Context.SelectedTextIds.Contains(t.Id)))
            c.Texts.Add(new TextElement
            {
                X = t.X + dx, Y = t.Y + dy, Width = t.Width, Height = t.Height, Rotation = t.Rotation, Text = t.Text,
                FontSize = t.FontSize, Color = t.Color, FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic,
            });
        foreach (var im in page.Images.Where(i => Context.SelectedImageIds.Contains(i.Id)))
            c.Images.Add(new ImageElement
            {
                X = im.X + dx, Y = im.Y + dy, Width = im.Width, Height = im.Height, Rotation = im.Rotation, PngData = im.PngData,
            });
        return c.IsEmpty ? null : c;
    }

    // Drops a sticker onto the page in view, centred in the viewport and kept
    // on the sheet, then selects it so it can be moved or resized right away.
    // The sticker's elements are copied with new ids, so placing the same
    // sticker twice gives two independent copies.
    public bool InsertSticker(Services.StickerContent sticker)
    {
        var pc = ActivePageCanvas;
        if (pc is null || sticker.IsEmpty) return false;
        var page = pc.Page;

        Windows.Foundation.Point c;
        try { c = this.TransformToVisual(pc).TransformPoint(new Windows.Foundation.Point(ActualWidth / 2, ActualHeight / 2)); }
        catch { c = new Windows.Foundation.Point(page.Width / 2, page.Height / 2); }
        // Top-left so the sticker is centred on the viewport, clamped so it stays
        // on the page (a sticker wider than the page starts at its left edge).
        double ox = Math.Clamp(c.X - sticker.Width / 2, 0, Math.Max(0, page.Width - sticker.Width));
        double oy = Math.Clamp(c.Y - sticker.Height / 2, 0, Math.Max(0, page.Height - sticker.Height));
        float fx = (float)ox, fy = (float)oy;

        Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
        Context.SelectedTextIds.Clear();   Context.SelectedImageIds.Clear();
        foreach (var s in sticker.Strokes)
        {
            var k = new Stroke { Kind = s.Kind, Color = s.Color, Width = s.Width, PressureMode = s.PressureMode };
            foreach (var p in s.Points) k.Points.Add(new InkPoint(p.X + fx, p.Y + fy, p.Pressure));
            page.Strokes.Add(k);
            Context.SelectedStrokeIds.Add(k.Id);
        }
        foreach (var sh in sticker.Shapes)
        {
            var k = new ShapeElement
            {
                Kind = sh.Kind, Color = sh.Color, StrokeWidth = sh.StrokeWidth, Filled = sh.Filled, Rotation = sh.Rotation,
                X1 = sh.X1 + fx, Y1 = sh.Y1 + fy, X2 = sh.X2 + fx, Y2 = sh.Y2 + fy,
                X3 = sh.Kind == ShapeKind.Triangle ? sh.X3 + fx : sh.X3,
                Y3 = sh.Kind == ShapeKind.Triangle ? sh.Y3 + fy : sh.Y3,
            };
            page.Shapes.Add(k);
            Context.SelectedShapeIds.Add(k.Id);
        }
        foreach (var t in sticker.Texts)
        {
            var k = new TextElement
            {
                X = t.X + ox, Y = t.Y + oy, Width = t.Width, Height = t.Height, Rotation = t.Rotation, Text = t.Text,
                FontSize = t.FontSize, Color = t.Color, FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic,
            };
            page.Texts.Add(k);
            Context.SelectedTextIds.Add(k.Id);
        }
        foreach (var im in sticker.Images)
        {
            var k = new ImageElement { X = im.X + ox, Y = im.Y + oy, Width = im.Width, Height = im.Height, Rotation = im.Rotation, PngData = im.PngData };
            page.Images.Add(k);
            Context.SelectedImageIds.Add(k.Id);
        }
        Context.CurrentPage = page;
        pc.InvalidateContentBounds();
        Context.Mutated?.Invoke();
        Context.SelectionChanged?.Invoke();
        Context.ToolRequested?.Invoke(ToolKind.Lasso);
        Context.Invalidate?.Invoke();
        return true;
    }
}
