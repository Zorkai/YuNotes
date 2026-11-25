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

    // ── Hi-res PDF background (zoomed in) ───────────────────────────────────
    // The imported background PNG is rasterized at 300 DPI (2× the coord
    // space), so beyond a 2× backing scale it runs out of pixels and zoom
    // looks soft next to vector PDF viewers. When the document still has its
    // source PDF, the VISIBLE crop of this page is re-rasterized from the
    // vectors at the backing-store resolution and drawn over the base bitmap.
    // Output size is viewport-bounded, so memory stays flat at any zoom.
    private CanvasBitmap? _bgHiRes;
    private Windows.Foundation.Rect _bgHiResRect;   // page-coord rect the crop covers
    private float _bgHiResScale;                    // backing scale it was rendered for
    private int _bgHiResGen;                        // stale-async guard

    public void ClearHiResBackground()
    {
        _bgHiResGen++;
        if (_bgHiRes is null) return;
        var old = _bgHiResRect;
        _bgHiRes.Dispose();
        _bgHiRes = null;
        _bgHiResScale = 0;
        _canvas.Invalidate(old);
    }

    // Cached source-PDF page size in PDF points — Conversion.GetPageSize
    // re-parses the document on every call, which is measurable on large PDFs.
    private System.Drawing.SizeF? _srcPdfPageSize;
    // Serializes this page's hi-res renders with latest-wins semantics: a
    // fresh request checks the gen counter before rendering, so it never pays
    // for a backlog of stale crops queued up by rapid zooming.
    private Task _hiResRenderChain = Task.CompletedTask;

    // Called by InkCanvasControl after a (non-intermediate) scroll/zoom change.
    // `viewRect` is the visible part of this page in page coordinates;
    // `backingScale` is the CanvasVirtualControl DpiScale tier currently in use
    // (rendering the crop any sharper than that is wasted — the backing store
    // is the resolution ceiling).
    public void UpdateHiResBackground(byte[] sourcePdf, int sourcePageIndex,
                                      Windows.Foundation.Rect viewRect, float backingScale)
    {
        // Base PNG is native up to 2× — only go to the vectors beyond that.
        if (backingScale <= 2.01f || Page.BackgroundPng is not { Length: > 0 })
        {
            ClearHiResBackground();
            return;
        }

        double bgLeft = Page.BackgroundLeft;
        double bgW = Page.BackgroundContentWidth > 0
            ? Page.BackgroundContentWidth
            : Page.Width - bgLeft;
        if (bgW <= 0) { ClearHiResBackground(); return; }
        // The crop is worked out in background rows (as imported, before any
        // inserted space); the renderer places each piece. Without gaps these
        // are the same as page rows.
        var bgRect = new Windows.Foundation.Rect(bgLeft, 0, bgW, PageGaps.BackgroundHeight(Page));
        if (PageGaps.Any(Page))
        {
            double vt = PageGaps.ToBackground(Page, viewRect.Y);
            double vb = PageGaps.ToBackground(Page, viewRect.Bottom);
            viewRect = new Windows.Foundation.Rect(viewRect.X, vt, viewRect.Width, Math.Max(0, vb - vt));
        }

        // Keep the current crop while it still covers the view at this scale.
        if (_bgHiRes is not null && Math.Abs(_bgHiResScale - backingScale) < 0.01f)
        {
            double vr = Math.Min(viewRect.Right, bgRect.Right);
            double vb = Math.Min(viewRect.Bottom, bgRect.Bottom);
            double vx = Math.Max(viewRect.X, bgRect.X);
            double vy = Math.Max(viewRect.Y, bgRect.Y);
            if (vr <= vx || vb <= vy ||
                (_bgHiResRect.X <= vx + 0.5 && _bgHiResRect.Y <= vy + 0.5 &&
                 _bgHiResRect.Right >= vr - 0.5 && _bgHiResRect.Bottom >= vb - 0.5))
                return;
        }

        // Two-pass render: the tight crop (view ∩ background) has ~3× fewer
        // pixels than the inflated one, so it reaches the screen ~3× sooner —
        // that's the pass the user is waiting on after a zoom. The inflated
        // crop (35% margin so small scrolls stay covered) follows quietly and
        // replaces it.
        var tight = viewRect;
        tight.Intersect(bgRect);
        if (tight.IsEmpty || tight.Width < 1 || tight.Height < 1)
        {
            ClearHiResBackground();
            return;
        }

        double mx = viewRect.Width * 0.35, my = viewRect.Height * 0.35;
        var inflated = new Windows.Foundation.Rect(
            viewRect.X - mx, viewRect.Y - my,
            viewRect.Width + mx * 2, viewRect.Height + my * 2);
        inflated.Intersect(bgRect);

        int gen = ++_bgHiResGen;
        QueueHiResRender(sourcePdf, sourcePageIndex, tight, backingScale, gen,
                         followUp: inflated.Equals(tight) ? null : inflated);
    }

    private void QueueHiResRender(byte[] sourcePdf, int sourcePageIndex,
                                  Windows.Foundation.Rect want, float backingScale, int gen,
                                  Windows.Foundation.Rect? followUp)
    {
        double pageH = PageGaps.BackgroundHeight(Page);
        double bgLeft = Page.BackgroundLeft;
        double bgW = Page.BackgroundContentWidth > 0
            ? Page.BackgroundContentWidth
            : Page.Width - bgLeft;
        var dispatcher = DispatcherQueue;
        _hiResRenderChain = _hiResRenderChain.ContinueWith(_ =>
        {
            // Superseded while queued — skip before paying for the render.
            if (gen != _bgHiResGen) return;
            try
            {
                // PDF points (72 DPI) per page-coordinate unit.
                if (_srcPdfPageSize is not { } pdfSize)
                    _srcPdfPageSize = pdfSize =
                        PDFtoImage.Conversion.GetPageSize(sourcePdf, (Index)sourcePageIndex);
                double ptsPerUnitX = pdfSize.Width / bgW;
                double ptsPerUnitY = pdfSize.Height / pageH;
                var bounds = new System.Drawing.RectangleF(
                    (float)((want.X - bgLeft) * ptsPerUnitX),
                    (float)(want.Y * ptsPerUnitY),
                    (float)(want.Width * ptsPerUnitX),
                    (float)(want.Height * ptsPerUnitY));

                // Output pixels = page units × backingScale, capped so a huge
                // window can't balloon the crop (cap ≈ 64 MB BGRA).
                double scale = backingScale;
                double outPixels = want.Width * want.Height * scale * scale;
                const double maxPixels = 16_000_000;
                if (outPixels > maxPixels) scale *= Math.Sqrt(maxPixels / outPixels);
                int dpi = (int)Math.Round(72.0 * scale / ptsPerUnitX);
                if (dpi < 72) return;

                using var bmp = PDFtoImage.Conversion.ToImage(sourcePdf, page: (Index)sourcePageIndex,
                    options: new PDFtoImage.RenderOptions
                    {
                        Dpi = dpi,
                        Bounds = bounds,
                        UseTiling = true,
                        WithAnnotations = true,
                        WithFormFill = true,
                        // CRITICAL: without this, Dpi is relative to the PAGE and
                        // the output is full-page-sized (~35 MP at scale 4, ~500 ms)
                        // no matter how small Bounds is. With it, output = Bounds ×
                        // Dpi — the intended crop pixels (~75-100 ms), and the
                        // maxPixels cap above actually matches reality.
                        DpiRelativeToBounds = true
                    });
                var pixels = bmp.Bytes;   // BGRA8888
                int w = bmp.Width, h = bmp.Height;

                dispatcher.TryEnqueue(() =>
                {
                    if (gen != _bgHiResGen) return;   // superseded or cleared
                    var device = TryGetCanvasDevice();
                    if (device is null) return;
                    CanvasBitmap bitmap;
                    try
                    {
                        bitmap = CanvasBitmap.CreateFromBytes(device, pixels, w, h,
                            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    }
                    catch { return; }
                    var old = _bgHiRes;
                    var oldRect = _bgHiResRect;
                    _bgHiRes = bitmap;
                    _bgHiResRect = want;
                    _bgHiResScale = backingScale;
                    var dirty = want;
                    if (old is not null) { old.Dispose(); dirty.Union(oldRect); }
                    if (PageGaps.Any(Page))
                    {
                        double top = PageGaps.ToPage(Page, dirty.Y), bottom = PageGaps.ToPage(Page, dirty.Bottom);
                        dirty = new Windows.Foundation.Rect(dirty.X, top, dirty.Width, Math.Max(0, bottom - top));
                    }
                    _canvas.Invalidate(dirty);
                    // Tight pass is on screen — widen to the scroll-headroom crop.
                    if (followUp is { } inflated)
                        QueueHiResRender(sourcePdf, sourcePageIndex, inflated, backingScale, gen,
                                         followUp: null);
                });
            }
            catch { /* hi-res is purely cosmetic — base bitmap remains */ }
        }, TaskScheduler.Default);
    }
}
