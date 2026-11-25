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

    /// <summary>Renders one page's flattened/overlay PNG at its auto-detected
    /// export resolution — lets the save loop render page-by-page and yield
    /// between pages so the UI stays responsive.</summary>
    public byte[]? RenderPageForExport(int index, bool overlayOnly)
    {
        if (index < 0 || index >= _pageCanvases.Count) return null;
        var pc = _pageCanvases[index];
        return pc.RenderToPng(pc.AutoExportScale(MaxOnScreenDensity, overlayOnly), overlayOnly);
    }

    /// <summary>One page prepared for the vector PDF writer (see
    /// PageCanvas.BuildVectorExport). Must run on the UI thread.</summary>
    public Rendering.PageVectorExport? BuildVectorExport(int index, bool overlayOnly)
    {
        if (index < 0 || index >= _pageCanvases.Count) return null;
        return _pageCanvases[index].BuildVectorExport(MaxOnScreenDensity, overlayOnly);
    }

    public byte[][] RenderAllForExport(bool overlayOnly = false)
    {
        var result = new byte[_pageCanvases.Count][];
        for (int i = 0; i < _pageCanvases.Count; i++) result[i] = RenderPageForExport(i, overlayOnly)!;
        return result;
    }

    public byte[]? RenderCurrentPagePng(int index, float scale = 1f)
    {
        if (index < 0 || index >= _pageCanvases.Count) return null;
        return _pageCanvases[index].RenderToPng(scale);
    }

    // ── In-app screenshot capture ───────────────────────────────────────────────

    /// <summary>
    /// Renders the given rectangle (in this control's own coordinate space, i.e. the
    /// same space returned by <c>element.TransformToVisual(Canvas)</c>) into a crisp
    /// PNG by re-rendering the underlying document content — not by grabbing screen
    /// pixels. Every page overlapping the region contributes its slice, so a capture
    /// that spans the seam between two pages composites both. Areas that aren't over a
    /// page (margins, gaps) come out white. Returns null if the region is degenerate.
    /// </summary>
    public byte[]? CaptureRegionPng(Windows.Foundation.Rect regionInControl)
    {
        if (regionInControl.Width < 2 || regionInControl.Height < 2) return null;

        float zoom = (float)Scroller.ZoomFactor;
        // Aim for ~2.5× page-space resolution regardless of the current zoom so the
        // capture is crisp whether the user is zoomed in or out. Clamped for sanity.
        float captureScale = Math.Clamp(2.5f / Math.Max(zoom, 0.05f), 1f, 4f);

        // Bound total output to ~16 MP so a huge selection can't blow up memory.
        double outWd = regionInControl.Width * captureScale;
        double outHd = regionInControl.Height * captureScale;
        double pxCount = outWd * outHd;
        const double maxPx = 16_000_000;
        if (pxCount > maxPx)
        {
            double k = Math.Sqrt(maxPx / pxCount);
            captureScale *= (float)k; outWd *= k; outHd *= k;
        }
        int outW = Math.Max(1, (int)Math.Round(outWd));
        int outH = Math.Max(1, (int)Math.Round(outHd));

        var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, outW, outH, 96f);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.White);
            foreach (var pc in _pageCanvases)
            {
                if (pc.ActualWidth < 1 || pc.ActualHeight < 1) continue;

                // Page rectangle expressed in this control's coordinate space (folds
                // in the ScrollViewer zoom/offset), intersected with the capture region.
                Windows.Foundation.Rect pageRect;
                Windows.Foundation.Point tl, br;
                try
                {
                    pageRect = pc.TransformToVisual(this)
                        .TransformBounds(new Windows.Foundation.Rect(0, 0, pc.ActualWidth, pc.ActualHeight));
                    double ix = Math.Max(pageRect.X, regionInControl.X);
                    double iy = Math.Max(pageRect.Y, regionInControl.Y);
                    double ir = Math.Min(pageRect.Right, regionInControl.Right);
                    double ib = Math.Min(pageRect.Bottom, regionInControl.Bottom);
                    if (ir - ix < 0.5 || ib - iy < 0.5) continue;

                    // Overlap corners mapped into the page's local coordinates.
                    var toPage = this.TransformToVisual(pc);
                    tl = toPage.TransformPoint(new Windows.Foundation.Point(ix, iy));
                    br = toPage.TransformPoint(new Windows.Foundation.Point(ir, ib));

                    // Destination sub-rect within the output bitmap (in pixels).
                    float dx = (float)((ix - regionInControl.X) * captureScale);
                    float dy = (float)((iy - regionInControl.Y) * captureScale);
                    float dw = (float)((ir - ix) * captureScale);
                    float dh = (float)((ib - iy) * captureScale);

                    float sx = (float)tl.X, sy = (float)tl.Y;
                    float srcW = (float)(br.X - tl.X), srcH = (float)(br.Y - tl.Y);
                    if (srcW < 0.01f || srcH < 0.01f) continue;

                    // page-local → output-pixel: shift the source origin to 0, scale to
                    // the destination size, then offset into the destination sub-rect.
                    var mtx = System.Numerics.Matrix3x2.CreateTranslation(-sx, -sy)
                            * System.Numerics.Matrix3x2.CreateScale(dw / srcW, dh / srcH)
                            * System.Numerics.Matrix3x2.CreateTranslation(dx, dy);

                    // Clip to the destination sub-rect (created while the transform is
                    // identity so the rect is in pixel space) so this page can't paint
                    // over a neighbour's slice.
                    ds.Transform = System.Numerics.Matrix3x2.Identity;
                    using (ds.CreateLayer(1f, new Windows.Foundation.Rect(dx, dy, dw, dh)))
                    {
                        ds.Transform = mtx;
                        pc.DrawContentInto(ds, device);
                    }
                    ds.Transform = System.Numerics.Matrix3x2.Identity;
                }
                catch { /* transform can throw if a page isn't laid out yet — skip it */ }
            }
        }

        using var ms = new System.IO.MemoryStream();
        target.SaveAsync(ms.AsRandomAccessStream(),
            Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
        return ms.ToArray();
    }
}
