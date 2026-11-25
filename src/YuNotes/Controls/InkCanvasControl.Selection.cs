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

    public bool HasSelection =>
        (Context.SelectedStrokeIds.Count + Context.SelectedTextIds.Count +
         Context.SelectedImageIds.Count + Context.SelectedShapeIds.Count) > 0;

    /// <summary>
    /// Bounding box of the current selection expressed in the coordinate space of
    /// <paramref name="relativeTo"/> (e.g. the editor's root grid), with page zoom
    /// and scroll folded in so it maps to where the selection sits on screen.
    /// Returns false when nothing is selected.
    /// </summary>
    public bool TryGetSelectionScreenBounds(UIElement relativeTo, out Windows.Foundation.Rect bounds)
    {
        bounds = default;
        var pc = ActivePageCanvas;
        if (pc is null || !pc.SelectionBbox(out var b)) return false;
        try
        {
            var t = pc.TransformToVisual(relativeTo);
            var tl = t.TransformPoint(new Windows.Foundation.Point(b.X, b.Y));
            var br = t.TransformPoint(new Windows.Foundation.Point(b.Right, b.Bottom));
            bounds = new Windows.Foundation.Rect(tl, br);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void DeleteSelection() => ActivePageCanvas?.DeleteSelection();
    public void DuplicateSelection() => ActivePageCanvas?.DuplicateSelection();
    public void ClearSelection() => ActivePageCanvas?.ClearSelection();
    public void RecolorSelection(Windows.UI.Color c) => ActivePageCanvas?.RecolorSelection(c);
    public bool SelectionHasRecolorable => ActivePageCanvas?.SelectionHasRecolorable == true;
    public bool SelectionIsAllHighlighter => ActivePageCanvas?.SelectionIsAllHighlighter == true;
    public Windows.UI.Color? SelectionColor() => ActivePageCanvas?.SelectionColor();
    public bool SelectionHasResizableWidth => ActivePageCanvas?.SelectionHasResizableWidth == true;
    public float? SelectionWidth() => ActivePageCanvas?.SelectionWidth();
    public void SetSelectionWidth(float width) => ActivePageCanvas?.SetSelectionWidth(width);
    public bool SelectionHasPenInk => ActivePageCanvas?.SelectionHasPenInk == true;
    public List<Stroke> SelectedPenStrokes() => ActivePageCanvas?.SelectedPenStrokes() ?? new();
    public bool ReplaceSelectedPenInkWithText(string text, Windows.UI.Color color, float fontSize) =>
        ActivePageCanvas?.ReplaceSelectedPenInkWithText(text, color, fontSize) == true;
    public byte[]? RenderRegionOfCurrentPage(float x, float y, float w, float h)
        => ActivePageCanvas?.RenderRegionToPng(x, y, w, h);

    /// <summary>
    /// Drops a pasted image onto the active page, centred in the current viewport,
    /// sized to its real aspect ratio, selected and ready to drag. Used by Ctrl+V /
    /// the clipboard paste button. Returns true if it placed an image.
    /// </summary>
    public bool PasteImageAtViewportCenter(byte[] png)
    {
        var pc = ActivePageCanvas;
        if (pc is null || png.Length == 0) return false;
        var page = pc.Page;

        // Map the viewport centre into the page's local coordinates (folds in the
        // ScrollViewer zoom/offset), then clamp inside the page so a paste while the
        // centre sits over a margin/gap still lands on the sheet.
        Windows.Foundation.Point c;
        try { c = this.TransformToVisual(pc).TransformPoint(new Windows.Foundation.Point(ActualWidth / 2, ActualHeight / 2)); }
        catch { c = new Windows.Foundation.Point(page.Width / 2, page.Height / 2); }
        double cx = Math.Clamp(c.X, 0, page.Width);
        double cy = Math.Clamp(c.Y, 0, page.Height);

        // Size to the image's real aspect (longest side ~360 page units).
        double w = 320, h = 240;
        if (ImageTool.TryGetPngSize(png, out int pw, out int ph) && pw > 0 && ph > 0)
        {
            double longest = 360.0, s = longest / Math.Max(pw, ph);
            w = pw * s; h = ph * s;
        }

        var img = new ImageElement
        {
            X = cx - w / 2, Y = cy - h / 2, Width = w, Height = h, PngData = png
        };
        page.Images.Add(img);
        Context.CurrentPage = page;
        // Select the pasted image so its transform handles appear immediately.
        Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
        Context.SelectedTextIds.Clear();   Context.SelectedImageIds.Clear();
        Context.SelectedImageIds.Add(img.Id);
        Context.Mutated?.Invoke();
        Context.SelectionChanged?.Invoke();
        Context.ToolRequested?.Invoke(ToolKind.RectSelect);
        Context.Invalidate?.Invoke();
        return true;
    }

    // ── Cross-page selection move ────────────────────────────────────────────
    //
    // A selection Move drag is captured by its origin PageCanvas, so dragging it
    // over another page keeps translating in the origin's local space — the
    // elements slide off the page edge and vanish. When the drag ends, the
    // origin raises SelectionDragDropped; here we find the page the pointer
    // ended over and, if it's a different one, reparent the elements into it
    // (translating their coordinates into that page's local space).
    private void OnSelectionDragDropped(object? sender, SelectionDropEventArgs e)
    {
        if (sender is not PageCanvas source) return;
        var target = PageCanvasAtLocalPoint(source, e.ReleaseLocal);
        if (target is null || ReferenceEquals(target, source)) return;   // same page → in-page move

        // Pages share the same zoom and left edge, so source→target is a pure
        // translation; its origin offset converts a source-local point to
        // target-local. Final target position = original + dragDelta + offset.
        float ox, oy;
        try
        {
            var origin = source.TransformToVisual(target)
                               .TransformPoint(new Windows.Foundation.Point(0, 0));
            ox = (float)origin.X;
            oy = (float)origin.Y;
        }
        catch { return; }

        float shiftX = e.Delta.X + ox;
        float shiftY = e.Delta.Y + oy;
        if (!MoveSelectedElements(source.Page, target.Page, shiftX, shiftY)) return;

        // Keep the selection live on its new page so the chrome + floating bar
        // follow it and the user can keep editing.
        Context.CurrentPage = target.Page;
        target.RequestRedraw();   // source repaints itself after this returns
        // A drop can leave the moved elements straddling a seam; refresh the
        // pages above/below both source and target so the bleed shows at once.
        target.PrevPageCanvas?.RequestRedraw();
        target.NextPageCanvas?.RequestRedraw();
        source.PrevPageCanvas?.RequestRedraw();
        source.NextPageCanvas?.RequestRedraw();

        // Two pages changed → snapshot the whole document (null hint) so a single
        // undo restores both; see the page-scoped history invariant.
        DocumentMutated?.Invoke(this, null);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Transferred = true;
    }

    // The page canvas whose bounds contain `pLocal` (given in `source`'s local
    // coordinates); if the drop landed in a gap/margin, the vertically nearest.
    private PageCanvas? PageCanvasAtLocalPoint(PageCanvas source, System.Numerics.Vector2 pLocal)
    {
        var srcPt = new Windows.Foundation.Point(pLocal.X, pLocal.Y);
        PageCanvas? nearest = null;
        double nearestDy = double.MaxValue;
        foreach (var pc in _pageCanvases)
        {
            Windows.Foundation.Point local;
            try { local = source.TransformToVisual(pc).TransformPoint(srcPt); }
            catch { continue; }
            if (local.X >= 0 && local.X <= pc.Page.Width &&
                local.Y >= 0 && local.Y <= pc.Page.Height)
                return pc;   // direct hit
            double dy = local.Y < 0 ? -local.Y
                      : local.Y > pc.Page.Height ? local.Y - pc.Page.Height : 0;
            if (dy < nearestDy) { nearestDy = dy; nearest = pc; }
        }
        return nearest;
    }

    // Reparents the currently-selected elements from `from` to `to`, offsetting
    // their coordinates by (shiftX, shiftY). Returns false if nothing moved.
    private bool MoveSelectedElements(NotePage from, NotePage to, float shiftX, float shiftY)
    {
        bool moved = false;
        foreach (var id in Context.SelectedStrokeIds)
        {
            var s = from.Strokes.FirstOrDefault(z => z.Id == id);
            if (s is null) continue;
            for (int i = 0; i < s.Points.Count; i++)
                s.Points[i] = s.Points[i] with { X = s.Points[i].X + shiftX, Y = s.Points[i].Y + shiftY };
            from.Strokes.Remove(s); to.Strokes.Add(s); moved = true;
        }
        foreach (var id in Context.SelectedShapeIds)
        {
            var sh = from.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            sh.X1 += shiftX; sh.Y1 += shiftY; sh.X2 += shiftX; sh.Y2 += shiftY;
            if (sh.Kind == ShapeKind.Triangle) { sh.X3 += shiftX; sh.Y3 += shiftY; }
            from.Shapes.Remove(sh); to.Shapes.Add(sh); moved = true;
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = from.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            t.X += shiftX; t.Y += shiftY;
            from.Texts.Remove(t); to.Texts.Add(t); moved = true;
        }
        foreach (var id in Context.SelectedImageIds)
        {
            var im = from.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            im.X += shiftX; im.Y += shiftY;
            from.Images.Remove(im); to.Images.Add(im); moved = true;
        }
        return moved;
    }

    public byte[]? RenderSelectionRegionToPng()
    {
        var pc = ActivePageCanvas;
        if (pc is null || !pc.SelectionBbox(out var bbox)) return null;
        return pc.RenderRegionToPng(bbox.X, bbox.Y, Math.Max(1, bbox.W), Math.Max(1, bbox.H));
    }
}
