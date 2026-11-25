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

    // Fired during and at the end of a page-extension drag. InkCanvasControl
    // listens to show a live preview overlay and then commit the extension.
    public event EventHandler<(ExtendSide Side, double Amount)>? ExtensionDragCompleted;

    // ── Extension drag handles ─────────────────────────────────────────────────
    private Border? _leftExtHandle;
    private Border? _rightExtHandle;
    private Border? _extTooltipBorder;
    private TextBlock? _extTooltip;
    private bool _extendDragging;
    private ExtendSide _extendSide;
    private float _extendDragStartScreenX;
    private double _extendCurrentAmount;  // total desired extension (from 0), set during drag
    private double _extendExisting;       // extension on the dragged side at drag start
    private uint? _extendDragPtrId;
    private double _extendScreenScale = 1;  // screen px per page px at drag start (zoom)
    private string? _extendSnapLabel;       // "Matches page 3" while snapped to a neighbour
    private const double ExtHandleWidth = 44.0;
    private const double ExtHandleHeight = 100.0;
    // Non-zero while drag preview is active — revert to 0 on commit or cancel.
    private float _previewExtLeft = 0;
    private float _previewExtRight = 0;

    /// <summary>
    /// Set to true by InkCanvasControl when the "Page Width" toolbar tool is active.
    /// Shows the large pill handles; hides them when false.
    /// </summary>
    private bool _extendModeActive;
    public bool ExtendModeActive
    {
        get => _extendModeActive;
        set
        {
            _extendModeActive = value;
            // 0.85 = visible idle; 0 = hidden (another tool active)
            SetHandleOpacity(value ? 0.85 : 0.0);
        }
    }

    // ── Extension drag handles ─────────────────────────────────────────────────

    private void SetupExtensionHandles()
    {
        var accentBrush = new SolidColorBrush(Color.FromArgb(180, 91, 107, 255));
        double centerY = (Page.Height - ExtHandleHeight) / 2;

        _leftExtHandle = new Border
        {
            Width = ExtHandleWidth,
            Height = ExtHandleHeight,
            Background = accentBrush,
            CornerRadius = new CornerRadius(0, 10, 10, 0),
            Opacity = 0,
            IsHitTestVisible = true,
            Child = new FontIcon
            {
                Glyph = "",    // ChevronLeft
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White),
                IsHitTestVisible = false,
            }
        };
        Canvas.SetLeft(_leftExtHandle, 0);
        Canvas.SetTop(_leftExtHandle, centerY);
        Canvas.SetZIndex(_leftExtHandle, 50);
        _leftExtHandle.PointerEntered  += (_, __) => { if (_extendModeActive) SetHandleOpacity(1.0); };
        _leftExtHandle.PointerExited   += (_, __) => { if (_extendModeActive && !_extendDragging) SetHandleOpacity(0.85); };
        _leftExtHandle.PointerPressed  += OnExtHandlePressed;
        _leftExtHandle.PointerMoved    += OnExtHandleMoved;
        _leftExtHandle.PointerReleased += OnExtHandleReleased;
        _leftExtHandle.PointerCaptureLost += (_, __) => CancelExtensionDrag();
        _leftExtHandle.DoubleTapped += OnExtHandleDoubleTapped;

        _rightExtHandle = new Border
        {
            Width = ExtHandleWidth,
            Height = ExtHandleHeight,
            Background = accentBrush,
            CornerRadius = new CornerRadius(10, 0, 0, 10),
            Opacity = 0,
            IsHitTestVisible = true,
            Child = new FontIcon
            {
                Glyph = "",    // ChevronRight
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White),
                IsHitTestVisible = false,
            }
        };
        Canvas.SetLeft(_rightExtHandle, Page.Width - ExtHandleWidth);
        Canvas.SetTop(_rightExtHandle, centerY);
        Canvas.SetZIndex(_rightExtHandle, 50);
        _rightExtHandle.PointerEntered  += (_, __) => { if (_extendModeActive) SetHandleOpacity(1.0); };
        _rightExtHandle.PointerExited   += (_, __) => { if (_extendModeActive && !_extendDragging) SetHandleOpacity(0.85); };
        _rightExtHandle.PointerPressed  += OnExtHandlePressed;
        _rightExtHandle.PointerMoved    += OnExtHandleMoved;
        _rightExtHandle.PointerReleased += OnExtHandleReleased;
        _rightExtHandle.PointerCaptureLost += (_, __) => CancelExtensionDrag();
        _rightExtHandle.DoubleTapped += OnExtHandleDoubleTapped;

        _extTooltip = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Colors.White),
            IsHitTestVisible = false
        };
        _extTooltipBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)),
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
            Opacity = 0,
            Child = _extTooltip
        };
        Canvas.SetZIndex(_extTooltipBorder, 51);

        _overlay.Children.Add(_leftExtHandle);
        _overlay.Children.Add(_rightExtHandle);
        _overlay.Children.Add(_extTooltipBorder);
    }

    private void SetHandleOpacity(double opacity)
    {
        if (_leftExtHandle  is not null) _leftExtHandle.Opacity  = opacity;
        if (_rightExtHandle is not null) _rightExtHandle.Opacity = opacity;
    }

    private void OnExtHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        _extendSide = ReferenceEquals(sender, _leftExtHandle) ? ExtendSide.Left : ExtendSide.Right;
        _extendDragStartScreenX = (float)e.GetCurrentPoint(null).Position.X;

        // Record how much extension already exists for this side so the user can
        // drag inward to reduce it (all the way back to 0 = original page size).
        _extendExisting = SideExtension(Page, _extendSide);

        // Drag deltas arrive in screen pixels; convert them to page pixels so the
        // edge stays under the finger at any zoom.
        _extendScreenScale = 1;
        try
        {
            var t = TransformToVisual(null);
            var a = t.TransformPoint(new Windows.Foundation.Point(0, 0));
            var b = t.TransformPoint(new Windows.Foundation.Point(100, 0));
            double sc = (b.X - a.X) / 100.0;
            if (sc > 0.01 && double.IsFinite(sc)) _extendScreenScale = sc;
        }
        catch { }

        _extendSnapLabel = null;
        _extendCurrentAmount = _extendExisting;
        _extendDragging = true;
        _extendDragPtrId = e.Pointer.PointerId;
        ((Border)sender).CapturePointer(e.Pointer);
        e.Handled = true;
        UpdateExtTooltip(_extendExisting);
    }

    private void OnExtHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_extendDragging || e.Pointer.PointerId != _extendDragPtrId) return;
        float screenX = (float)e.GetCurrentPoint(null).Position.X;

        // Positive delta = dragging outward (extend); negative = inward (reduce).
        double screenDelta = _extendSide == ExtendSide.Right
            ? screenX - _extendDragStartScreenX
            : _extendDragStartScreenX - screenX;

        // Total desired extension = existing + drag delta, clamped to [0, ∞).
        double totalDesired = Math.Max(0, _extendExisting + screenDelta / _extendScreenScale);

        // Neighbouring extended pages win over the preset snap points, so pages
        // extended one after another line up. Their pull is measured in screen
        // pixels, so it feels the same at every zoom level.
        double snappedTotal;
        var match = NearestNeighbourSnap(_extendSide, totalDesired, NeighbourSnapRadiusPx / _extendScreenScale);
        if (match is { } m)
        {
            snappedTotal = m.Amount;
            _extendSnapLabel = m.Label;
        }
        else
        {
            double pdfW = Page.BackgroundContentWidth > 0 ? Page.BackgroundContentWidth : Page.Width;
            snappedTotal = SnapExtension(totalDesired, pdfW);
            _extendSnapLabel = null;
        }
        _extendCurrentAmount = snappedTotal;

        // Preview delta: positive = canvas grows, negative = canvas shows reduction overlay.
        ApplyPreview(_extendSide, (float)(snappedTotal - _extendExisting));
        UpdateExtTooltip(snappedTotal);
        e.Handled = true;
    }

    private void OnExtHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_extendDragging || e.Pointer.PointerId != _extendDragPtrId) return;
        double amount = _extendCurrentAmount;
        var side = _extendSide;
        // Clear preview BEFORE firing the event so InkCanvasControl's ResizeCanvas
        // call (after ApplyExtension) starts from the canonical page.Width.
        ClearPreview();
        _extendDragging = false;
        _extendDragPtrId = null;
        _extendCurrentAmount = 0;
        if (_extTooltipBorder is not null) _extTooltipBorder.Opacity = 0;
        SetHandleOpacity(_extendModeActive ? 0.85 : 0.0);
        // Commit any real change, including dragging all the way back to the
        // original size (0). A tap that moved nothing records no undo step.
        if (Math.Abs(amount - _extendExisting) > 0.5)
            ExtensionDragCompleted?.Invoke(this, (side, amount));
        _extendSnapLabel = null;
        e.Handled = true;
    }

    // Double-tap a handle: match the nearest extended page above (or, failing
    // that, below) on that side in one go.
    private void OnExtHandleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!_extendModeActive || _extendDragging) return;
        var side = ReferenceEquals(sender, _leftExtHandle) ? ExtendSide.Left : ExtendSide.Right;
        var targets = NeighbourSnapTargets(side);
        if (targets.Count == 0) return;
        double amount = targets[0].Amount;
        if (Math.Abs(amount - SideExtension(Page, side)) > 0.5)
            ExtensionDragCompleted?.Invoke(this, (side, amount));
    }

    // ── Snapping to neighbouring extended pages ───────────────────────────────

    private const double NeighbourSnapRadiusPx = 28;   // screen pixels

    private static double SideExtension(NotePage p, ExtendSide side) => side == ExtendSide.Right
        ? (p.BackgroundContentWidth > 0 ? Math.Max(0, p.Width - p.BackgroundLeft - p.BackgroundContentWidth) : 0)
        : p.BackgroundLeft;

    // Width of the page before any extension.
    private static double ContentWidth(NotePage p) =>
        p.BackgroundContentWidth > 0 ? p.BackgroundContentWidth : p.Width - p.BackgroundLeft;

    private static bool IsExtended(NotePage p) =>
        SideExtension(p, ExtendSide.Left) > 0.5 || SideExtension(p, ExtendSide.Right) > 0.5;

    private int PageNumber()
    {
        int n = 1;
        for (var c = PrevPageCanvas; c is not null; c = c.PrevPageCanvas) n++;
        return n;
    }

    // Snap targets from the nearest page above that offers one for this side,
    // then the nearest below (plain pages in between are skipped, so a run of
    // pages can be matched even with a plain page between them). For each one:
    //  - the same extension on this side, which lines up both content and
    //    edges when the pages have the same original width;
    //  - when original widths differ (mixed PDF page sizes), the extension
    //    that makes this page as wide as that one; pages are left-aligned, so
    //    their right edges then line up.
    // Duplicates and zero are skipped (0 is already a preset snap point).
    private List<(double Amount, string Label)> NeighbourSnapTargets(ExtendSide side)
    {
        var result = new List<(double Amount, string Label)>();
        int self = PageNumber();
        double otherSide = Page.Width - SideExtension(Page, side);   // width without this side's extension

        void Add(double amount, string label)
        {
            if (amount < 0.5 || !double.IsFinite(amount)) return;
            if (result.Any(r => Math.Abs(r.Amount - amount) < 1)) return;
            result.Add((amount, label));
        }
        void From(PageCanvas? start, bool up)
        {
            int dist = 1;
            for (var c = start; c is not null; c = up ? c.PrevPageCanvas : c.NextPageCanvas, dist++)
            {
                var n = c.Page;
                if (!IsExtended(n)) continue;
                int num = up ? self - dist : self + dist;
                int before = result.Count;
                Add(SideExtension(n, side), $"Matches page {num}");
                if (Math.Abs(ContentWidth(n) - ContentWidth(Page)) > 1)
                    Add(n.Width - otherSide, $"Lines up with page {num}");
                // A page extended only on the other side gives nothing to match
                // here; keep looking further out.
                if (result.Count > before) return;
            }
        }
        From(PrevPageCanvas, up: true);
        From(NextPageCanvas, up: false);
        return result;
    }

    private (double Amount, string Label)? NearestNeighbourSnap(ExtendSide side, double desired, double radius)
    {
        (double Amount, string Label)? best = null;
        double bestDist = radius;
        foreach (var t in NeighbourSnapTargets(side))
        {
            double d = Math.Abs(t.Amount - desired);
            if (d <= bestDist) { bestDist = d; best = t; }
        }
        return best;
    }

    private void CancelExtensionDrag()
    {
        ClearPreview();
        _extendDragging = false;
        _extendDragPtrId = null;
        _extendCurrentAmount = 0;
        _extendSnapLabel = null;
        if (_extTooltipBorder is not null) _extTooltipBorder.Opacity = 0;
        SetHandleOpacity(_extendModeActive ? 0.85 : 0.0);
    }

    // Update the canvas to show a preview of the drag.
    // amount = desired total extension − existing extension (may be negative = reducing).
    private void ApplyPreview(ExtendSide side, float previewDelta)
    {
        _previewExtLeft  = side == ExtendSide.Left  ? previewDelta : 0;
        _previewExtRight = side == ExtendSide.Right ? previewDelta : 0;
        // Only grow the canvas for extension; for reduction we overlay the
        // "to be removed" area on the existing canvas size.
        double totalW = Page.Width + Math.Max(0f, _previewExtLeft) + Math.Max(0f, _previewExtRight);
        SetCanvasWidth(totalW);
        _canvas.Invalidate();
    }

    // Restore canvas to the current page.Width (preview discarded, no model change).
    private void ClearPreview()
    {
        if (_previewExtLeft == 0 && _previewExtRight == 0) return;
        _previewExtLeft  = 0;
        _previewExtRight = 0;
        SetCanvasWidth(Page.Width);
        _canvas.Invalidate();
    }

    // Resize all internal canvases + the PageCanvas itself to the given width.
    private void SetCanvasWidth(double newW)
    {
        _canvas.Width     = newW;
        _overlay.Width    = newW;
        ((Grid)Content).Width = newW;
        Width = newW;
        // Keep right handle at the page right edge (not at the preview edge).
        if (_rightExtHandle is not null)
            Canvas.SetLeft(_rightExtHandle, Page.Width - ExtHandleWidth);
    }

    private static double SnapExtension(double raw, double pdfW)
    {
        if (pdfW <= 0) return raw;
        double snapRadius = pdfW * 0.05;
        double[] snaps = { 0, pdfW * 0.25, pdfW * 0.5, pdfW, pdfW * 1.5, pdfW * 2.0 };
        foreach (var snap in snaps)
            if (Math.Abs(raw - snap) <= snapRadius) return snap;
        return raw;
    }

    private void UpdateExtTooltip(double amount)
    {
        if (_extTooltip is null || _extTooltipBorder is null) return;
        double pdfW = Page.BackgroundContentWidth > 0 ? Page.BackgroundContentWidth : Page.Width;
        int pct = pdfW > 0 ? (int)Math.Round(amount / pdfW * 100) : 0;
        string dir = _extendSide == ExtendSide.Left ? "←" : "→";
        _extTooltip.Text = amount <= 0 ? $"{dir} Original size"
            : _extendSnapLabel is { } label ? $"{dir} {label} · {(int)amount}px"
            : $"{dir} {(int)amount}px ({pct}%)";
        _extTooltipBorder.Opacity = _extendDragging ? 1 : 0;

        // Position tooltip near the dragged handle edge.
        double tipX = _extendSide == ExtendSide.Left ? 14 : Page.Width - ExtHandleWidth - 90;
        Canvas.SetLeft(_extTooltipBorder, Math.Max(0, tipX));
        Canvas.SetTop(_extTooltipBorder, 8);
    }
}
