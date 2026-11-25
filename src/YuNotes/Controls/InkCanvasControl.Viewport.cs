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

    // Step DpiScale up in integer increments as the user zooms in, so Win2D
    // re-renders strokes and PDF backgrounds at the resolution they'll be shown at
    // instead of letting the ScrollViewer stretch a low-res bitmap.
    private float _appliedDpiScale = 1f;
    // Starts sharpening (DpiScale tier + hi-res PDF crop) as soon as the view
    // holds still for the interval — even mid-gesture with fingers down —
    // instead of waiting for DirectManipulation to declare the gesture over.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _viewSettleTimer;
    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        OnPullViewChanged(e.IsIntermediate);
        UpdatePageResidency();
        if (e.IsIntermediate)
        {
            if (_viewSettleTimer is null)
            {
                _viewSettleTimer = DispatcherQueue.CreateTimer();
                _viewSettleTimer.Interval = TimeSpan.FromMilliseconds(120);
                _viewSettleTimer.IsRepeating = false;
                _viewSettleTimer.Tick += (_, __) => { ApplyZoomDpi(); UpdateHiResBackgrounds(); };
            }
            // Restart on every intermediate event — fires only once the view
            // has actually stopped moving.
            _viewSettleTimer.Stop();
            _viewSettleTimer.Start();
            return;
        }
        _viewSettleTimer?.Stop();
        if (_relockPending)
        {
            // A programmatic zoom under the lock has landed: lock again.
            _relockPending = false;
            ApplyZoomMode();
        }
        ApplyZoomDpi();
        UpdateHiResBackgrounds();
        ZoomChanged?.Invoke(this, Scroller.ZoomFactor);
        ActivePageScrolled?.Invoke(this, EventArgs.Empty);
    }

    // ── Which pages keep their images ───────────────────────────────────────
    // Pages within LoadMargin screens of the view decode their images (PDF
    // background, pictures); pages beyond KeepMargin screens free them. The gap
    // between the two keeps a page that sits near the edge from loading and
    // freeing over and over while you scroll back and forth. Runs on every
    // scroll step, so it only reads each page's layout offset (no tree walks).
    private const double LoadMargin = 1.5;
    private const double KeepMargin = 4.0;

    private void UpdatePageResidency()
    {
        if (_pageCanvases.Count == 0) return;
        double zoom = Scroller.ZoomFactor;
        double viewH = Scroller.ViewportHeight / zoom;   // in page units
        if (!(viewH > 0) || !(zoom > 0)) return;
        // Top of the visible area in PagesPanel's coordinates.
        double panelTop;
        try { panelTop = PagesPanel.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y; }
        catch { return; }
        double viewTop = -panelTop / zoom, viewBottom = viewTop + viewH;
        double loadTop = viewTop - viewH * LoadMargin, loadBottom = viewBottom + viewH * LoadMargin;
        double keepTop = viewTop - viewH * KeepMargin, keepBottom = viewBottom + viewH * KeepMargin;

        foreach (var pc in _pageCanvases)
        {
            double top = pc.ActualOffset.Y, bottom = top + pc.Page.Height;
            if (bottom >= loadTop && top <= loadBottom) pc.SetResident(true);
            else if (bottom < keepTop || top > keepBottom) pc.SetResident(false);
        }
    }

    // Beyond 2× zoom the imported 300-DPI background PNGs run out of pixels.
    // For documents that still carry their source PDF, ask each visible page
    // to re-rasterize its visible crop from the vectors at the current
    // backing-store scale (see PageCanvas.UpdateHiResBackground).
    private void UpdateHiResBackgrounds()
    {
        if (Document?.SourcePdfBytes is not { Length: > 0 } src)
            return;

        double zoom = Scroller.ZoomFactor;
        var viewportW = Scroller.ViewportWidth;
        var viewportH = Scroller.ViewportHeight;

        foreach (var pc in _pageCanvases)
        {
            if (pc.Page.SourcePageIndex is not int srcIdx || srcIdx < 0)
                continue;

            // Page bounds in viewport coordinates (TransformToVisual includes
            // the ScrollViewer's zoom and scroll offsets).
            Windows.Foundation.Point topLeft;
            try { topLeft = pc.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(0, 0)); }
            catch { continue; }

            double visL = Math.Max(0, topLeft.X);
            double visT = Math.Max(0, topLeft.Y);
            double visR = Math.Min(viewportW, topLeft.X + pc.Page.Width * zoom);
            double visB = Math.Min(viewportH, topLeft.Y + pc.Page.Height * zoom);
            if (visR <= visL || visB <= visT)
            {
                pc.ClearHiResBackground();   // off-screen — free the crop
                continue;
            }

            var viewRect = new Windows.Foundation.Rect(
                (visL - topLeft.X) / zoom,
                (visT - topLeft.Y) / zoom,
                (visR - visL) / zoom,
                (visB - visT) / zoom);
            pc.UpdateHiResBackground(src, srcIdx, viewRect, _appliedDpiScale);
        }
    }

    private void ApplyZoomDpi()
    {
        var zoom = Scroller.ZoomFactor;
        var target = (float)Math.Min(4.0, Math.Max(1.0, Math.Ceiling(zoom)));
        // A DpiScale change recreates each canvas's backing store, which blanks
        // to white until its tiles re-render. Cover the visible pages with a
        // freeze-frame of their current content first so the swap is seamless
        // (each PageCanvas removes its overlay after the post-change redraw).
        if (Math.Abs(_appliedDpiScale - target) > 0.01f)
            FreezeVisiblePages(_appliedDpiScale);
        // Even when the main DpiScale hasn't changed, the live-stroke cap setting
        // may have — let PageCanvas decide whether to skip.
        _appliedDpiScale = target;
        foreach (var pc in _pageCanvases) pc.SetDpiScale(target);
    }

    private void FreezeVisiblePages(float oldScale)
    {
        double zoom = Scroller.ZoomFactor;
        var viewportW = Scroller.ViewportWidth;
        var viewportH = Scroller.ViewportHeight;
        foreach (var pc in _pageCanvases)
        {
            Windows.Foundation.Point topLeft;
            try { topLeft = pc.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(0, 0)); }
            catch { continue; }

            double visL = Math.Max(0, topLeft.X);
            double visT = Math.Max(0, topLeft.Y);
            double visR = Math.Min(viewportW, topLeft.X + pc.Page.Width * zoom);
            double visB = Math.Min(viewportH, topLeft.Y + pc.Page.Height * zoom);
            if (visR <= visL || visB <= visT) continue;   // off-screen — blanks invisibly

            var viewRect = new Windows.Foundation.Rect(
                (visL - topLeft.X) / zoom,
                (visT - topLeft.Y) / zoom,
                (visR - visL) / zoom,
                (visB - visT) / zoom);
            pc.FreezeViewportForDpiChange(viewRect, oldScale);
        }
    }

    // True while any page has a pointer gesture in flight (stroke being drawn,
    // selection drag, pan). Used to defer autosave out of the inking path.
    public bool IsUserInteracting
    {
        get
        {
            foreach (var pc in _pageCanvases)
                if (pc.HasActivePointer) return true;
            return false;
        }
    }

    /// <summary>Renders one page's flattened/overlay PNG — lets the save loop
    /// render page-by-page and yield between pages so the UI stays responsive.</summary>
    /// <summary>Densest view the user can get on screen, in device pixels per
    /// page unit: the backing store tops out at the max zoom tier (DpiScale ≤ 4,
    /// see ApplyZoomDpi) times the display's scaling. Exports render at this so
    /// someone zooming into the PDF sees the same detail as in the app.</summary>
    public float MaxOnScreenDensity
    {
        get
        {
            double raster = XamlRoot?.RasterizationScale ?? 1.0;
            double zoomTier = Math.Min(4.0, Math.Max(1.0, Math.Ceiling(Scroller.MaxZoomFactor)));
            return (float)(raster * zoomTier);
        }
    }

    // Presentation: zooms so the whole page fits the viewport and centres it.
    public void FitPageToViewport(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _pageCanvases.Count) return;
        var pc = _pageCanvases[pageIndex];
        double vpW = Scroller.ViewportWidth, vpH = Scroller.ViewportHeight;
        if (vpW < 1 || vpH < 1 || pc.ActualHeight < 1) return;
        try
        {
            float oldZoom = Scroller.ZoomFactor <= 0 ? 1f : Scroller.ZoomFactor;
            const double margin = 24;
            float zoom = (float)Math.Clamp(
                Math.Min(vpW / (pc.ActualWidth + margin), vpH / (pc.ActualHeight + margin)),
                Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
            // Page top-left in unzoomed content coordinates.
            var pt = pc.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(0, 0));
            double cx = (Scroller.HorizontalOffset + pt.X) / oldZoom;
            double cy = (Scroller.VerticalOffset + pt.Y) / oldZoom;
            double h = (cx + pc.ActualWidth / 2) * zoom - vpW / 2;
            double v = (cy + pc.ActualHeight / 2) * zoom - vpH / 2;
            ChangeViewProgrammatic(h, v, zoom);
        }
        catch (Exception ex) { App.LogError(ex, "FitPageToViewport"); }
    }

    public void SetZoom(double zoom)
    {
        var newZoom = (float)Math.Clamp(zoom, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        var oldZoom = Scroller.ZoomFactor <= 0 ? 1f : Scroller.ZoomFactor;
        var vpW = Scroller.ViewportWidth;
        var vpH = Scroller.ViewportHeight;
        var centerH = (Scroller.HorizontalOffset + vpW / 2) / oldZoom;
        var centerV = (Scroller.VerticalOffset + vpH / 2) / oldZoom;
        var newH = centerH * newZoom - vpW / 2;
        var newV = centerV * newZoom - vpH / 2;
        ChangeViewProgrammatic(newH, newV, newZoom);
    }

    // ── Zoom lock ────────────────────────────────────────────────────────────
    // Locked = pinch and Ctrl+wheel don't zoom (scrolling still works). The
    // app's own zoom commands (Fit width, 100%, reopening a note at its saved
    // zoom, presenting) still work: they unlock for the one change, and
    // OnScrollerViewChanged locks again once it lands.
    private bool _zoomLocked;
    private bool _relockPending;

    public bool ZoomLocked
    {
        get => _zoomLocked;
        set { _zoomLocked = value; _relockPending = false; ApplyZoomMode(); }
    }

    private void ApplyZoomMode()
    {
        var mode = _zoomLocked && !_relockPending ? ZoomMode.Disabled : ZoomMode.Enabled;
        // While a pen stroke has pan/zoom suspended, change what gets restored.
        if (_suppressing) _savedZoom = mode;
        else Scroller.ZoomMode = mode;
    }

    private void ChangeViewProgrammatic(double? h, double? v, float? zoom)
    {
        if (_zoomLocked && zoom is float z && Math.Abs(z - Scroller.ZoomFactor) > 0.001f)
        {
            _relockPending = true;
            ApplyZoomMode();
        }
        Scroller.ChangeView(h is double hh ? Math.Max(0, hh) : null,
                            v is double vv ? Math.Max(0, vv) : null, zoom, disableAnimation: true);
    }

    // ── Zoom presets ─────────────────────────────────────────────────────────

    // Steps for Zoom in / Zoom out (Ctrl+= / Ctrl+-), in percent.
    private static readonly int[] ZoomSteps = { 25, 33, 50, 67, 75, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400 };

    public void ZoomStep(int direction)
    {
        double pct = Scroller.ZoomFactor * 100;
        int next = direction > 0
            ? ZoomSteps.FirstOrDefault(s => s > pct + 0.5, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(s => s < pct - 0.5, ZoomSteps[0]);
        SetZoom(next / 100.0);
    }

    /// <summary>Zooms so the page in view fills the window's width, keeping
    /// the same spot of that page at the top.</summary>
    public void FitWidth()
    {
        var pc = ViewPageCanvas();
        double vpW = Scroller.ViewportWidth;
        if (pc is null || vpW < 1 || pc.ActualWidth < 1) return;
        try
        {
            float oldZoom = Scroller.ZoomFactor <= 0 ? 1f : Scroller.ZoomFactor;
            const double margin = 32;
            float zoom = (float)Math.Clamp((vpW - margin) / pc.ActualWidth, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
            var pt = pc.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(0, 0));
            double pageLeft = (Scroller.HorizontalOffset + pt.X) / oldZoom;
            double pageTop = (Scroller.VerticalOffset + pt.Y) / oldZoom;
            // Keep the content at the top edge of the view where it is on the page.
            double topInPage = Math.Max(0, -pt.Y / oldZoom);
            double h = pageLeft * zoom - (vpW - pc.ActualWidth * zoom) / 2;
            double v = (pageTop + topInPage) * zoom - (topInPage > 0 ? 0 : 16);
            ChangeViewProgrammatic(h, v, zoom);
        }
        catch (Exception ex) { App.LogError(ex, "FitWidth"); }
    }

    /// <summary>Whole page in view, centred.</summary>
    public void FitPage()
    {
        var pc = ViewPageCanvas();
        if (pc is not null) FitPageToViewport(_pageCanvases.IndexOf(pc));
    }

    private PageCanvas? ViewPageCanvas()
    {
        var page = ActivePage;
        return page is null ? _pageCanvases.FirstOrDefault() : _pageCanvases.FirstOrDefault(p => p.Page == page);
    }

    // ── Saved view (zoom + position) ────────────────────────────────────────

    /// <summary>Where the view is now, relative to the page at its top.</summary>
    public NoteView? CaptureView()
    {
        var pc = ViewPageCanvas();
        if (pc is null || Scroller.ViewportHeight < 1) return null;
        try
        {
            float zoom = Scroller.ZoomFactor <= 0 ? 1f : Scroller.ZoomFactor;
            var pt = pc.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(0, 0));
            return new NoteView
            {
                Zoom = zoom,
                Page = _pageCanvases.IndexOf(pc),
                OffsetInPage = -pt.Y / zoom,
                HorizontalOffset = Scroller.HorizontalOffset / zoom,
            };
        }
        catch { return null; }
    }

    /// <summary>Puts a saved view back once the pages are laid out (used on open).</summary>
    public void RestoreViewDeferred(NoteView view)
    {
        if (view.Page < 0 || view.Page >= _pageCanvases.Count) return;
        int attempts = 0;
        void Attempt()
        {
            var pc = view.Page < _pageCanvases.Count ? _pageCanvases[view.Page] : null;
            if (pc is null) return;
            if (pc.ActualHeight <= 1 || Scroller.ViewportHeight <= 1)
            {
                if (attempts++ < 40) DispatcherQueue.TryEnqueue(Attempt);
                return;
            }
            try
            {
                float oldZoom = Scroller.ZoomFactor <= 0 ? 1f : Scroller.ZoomFactor;
                float zoom = (float)Math.Clamp(view.Zoom, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
                var pt = pc.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(0, 0));
                double pageTop = (Scroller.VerticalOffset + pt.Y) / oldZoom;
                double v = (pageTop + Math.Clamp(view.OffsetInPage, -pc.ActualHeight, pc.ActualHeight)) * zoom;
                ChangeViewProgrammatic(view.HorizontalOffset * zoom, v, zoom);
            }
            catch (Exception ex) { App.LogError(ex, "RestoreView"); }
        }
        DispatcherQueue.TryEnqueue(Attempt);
    }

    /// <summary>
    /// Scrolls to a page once layout is ready — used on document open, where the
    /// pages haven't been measured yet so TransformToVisual would return garbage.
    /// Retries on the dispatcher until the target page has a real height.
    /// </summary>
    public void ScrollToPageDeferred(int pageIndex)
    {
        if (pageIndex <= 0 || pageIndex >= _pageCanvases.Count) return;
        int attempts = 0;
        void Attempt()
        {
            var pc = pageIndex < _pageCanvases.Count ? _pageCanvases[pageIndex] : null;
            if (pc is not null && pc.ActualHeight > 1 && Scroller.ViewportHeight > 1)
            {
                ScrollToPage(pageIndex);
                return;
            }
            if (attempts++ < 40) DispatcherQueue.TryEnqueue(Attempt);
        }
        DispatcherQueue.TryEnqueue(Attempt);
    }

    /// <summary>Scrolls the viewport so that the given page (0-based) is visible at the top.</summary>
    public void ScrollToPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _pageCanvases.Count) return;
        var pc = _pageCanvases[pageIndex];
        try
        {
            var transform = pc.TransformToVisual(Scroller);
            var pt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            // pt.Y is already in Scroller coordinate space (0 = top of visible viewport).
            // Adding the current vertical offset converts it to absolute scroll position.
            Scroller.ChangeView(null, Math.Max(0, Scroller.VerticalOffset + pt.Y - 16), null);
        }
        catch { }
    }

    // Scrolls so a rect on a page (page coordinates) sits in the upper third
    // of the viewport, horizontally centered when the page is wider than it.
    public void ScrollToRect(int pageIndex, Windows.Foundation.Rect rect, int attempt = 0)
    {
        if (pageIndex < 0 || pageIndex >= _pageCanvases.Count) return;
        var pc = _pageCanvases[pageIndex];
        // Right after a note opens the pages aren't laid out yet; try again shortly.
        if ((pc.ActualHeight <= 1 || Scroller.ViewportHeight <= 1) && attempt < 40)
        {
            DispatcherQueue.TryEnqueue(() => ScrollToRect(pageIndex, rect, attempt + 1));
            return;
        }
        try
        {
            var pt = pc.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(rect.X + rect.Width / 2, rect.Y));
            double y = Scroller.VerticalOffset + pt.Y - Scroller.ViewportHeight / 3;
            double x = Scroller.HorizontalOffset + pt.X - Scroller.ViewportWidth / 2;
            Scroller.ChangeView(Math.Max(0, x), Math.Max(0, y), null);
        }
        catch { ScrollToPage(pageIndex); }
    }
}
