using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Controls;

public sealed partial class InkCanvasControl : UserControl
{
    public Document? Document { get; private set; }
    public EditorContext Context { get; } = new();

    private readonly List<PageCanvas> _pageCanvases = new();
    private PalmRejection? _palm;
    private PenButtonRouter? _router;
    private PageRenderer? _renderer;
    private Dictionary<PenButtonAction, ITool> _buttonTools = new();

    public Func<ITool?> ToolProvider { get; set; } = () => null;
    public bool ExtendModeActive { get; private set; }
    public bool SeamlessPages { get; private set; } = true;

    public event EventHandler<double>? ZoomChanged;
    public event EventHandler? DocumentMutated;
    public event EventHandler? SelectionChanged;
    public event EventHandler? RectSelectionCompleted;
    public event EventHandler? AddPageRequested;
    public event EventHandler<YuNotes.Tools.ToolKind>? ToolRequested;
    public event EventHandler<YuNotes.Tools.ToolKind>? MomentaryToolStart;
    public event EventHandler? MomentaryToolEnd;
    public event EventHandler? ActivePageScrolled;

    public InkCanvasControl()
    {
        InitializeComponent();
        Context.DispatcherQueue = DispatcherQueue;
        Context.Invalidate = InvalidateAll;
        Context.InvalidateLive = InvalidateAllLive;
        Context.CommitStrokeAt = bbox => ActivePageCanvas?.CommitStrokeRedraw(bbox);
        Context.InvalidateRect = bbox => ActivePageCanvas?.CommitStrokeRedraw(bbox);
        Context.Mutated = () => DocumentMutated?.Invoke(this, EventArgs.Empty);
        Context.SelectionChanged = () => SelectionChanged?.Invoke(this, EventArgs.Empty);
        Context.EditTextRequested = id => ActivePageCanvas?.BeginInlineTextEdit(id);
        Context.ToolRequested = kind => ToolRequested?.Invoke(this, kind);
        Context.RequestPan = (dx, dy) =>
        {
            Scroller.ChangeView(Scroller.HorizontalOffset + dx, Scroller.VerticalOffset + dy, null, disableAnimation: true);
        };
        Context.RulerChanged = () =>
        {
            foreach (var pc in _pageCanvases) pc.SyncRulerOverlay();
        };
        Scroller.ViewChanged += OnScrollerViewChanged;

        // ScrollViewer's DirectManipulation tracker grabs pen input for panning before
        // the inner PageCanvas can capture it for drawing. Suppress manipulation-based
        // pan/zoom while a pen is in contact and restore on release. Touch is NOT
        // suppressed so pinch-zoom and two-finger pan still work via DM.
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnAnyPointerPressed), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnAnyPointerEnded), handledEventsToo: true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(OnAnyPointerEnded), handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnAnyPointerEnded), handledEventsToo: true);

    }

    private readonly HashSet<uint> _suppressPointers = new();
    private bool _suppressing;
    private ScrollMode _savedHScroll;
    private ScrollMode _savedVScroll;
    private ZoomMode _savedZoom;

    private void OnAnyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var type = e.Pointer.PointerDeviceType;
        // Suppress ScrollViewer pan/zoom for pen always; also suppress for touch
        // when the "Page Width" tool is active so finger drag on the handles works.
        bool isPen = type == Microsoft.UI.Input.PointerDeviceType.Pen;
        bool isExtendTouch = type == Microsoft.UI.Input.PointerDeviceType.Touch && ExtendModeActive;
        if (!isPen && !isExtendTouch) return;
        if (!_suppressPointers.Add(e.Pointer.PointerId)) return;
        if (_suppressing) return;
        _savedHScroll = Scroller.HorizontalScrollMode;
        _savedVScroll = Scroller.VerticalScrollMode;
        _savedZoom = Scroller.ZoomMode;
        Scroller.HorizontalScrollMode = ScrollMode.Disabled;
        Scroller.VerticalScrollMode = ScrollMode.Disabled;
        Scroller.ZoomMode = ZoomMode.Disabled;
        _suppressing = true;
    }

    private void OnAnyPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!_suppressPointers.Remove(e.Pointer.PointerId)) return;
        if (_suppressPointers.Count > 0 || !_suppressing) return;
        Scroller.HorizontalScrollMode = _savedHScroll;
        Scroller.VerticalScrollMode = _savedVScroll;
        Scroller.ZoomMode = _savedZoom;
        _suppressing = false;
    }

    // Step DpiScale up in integer increments as the user zooms in, so Win2D
    // re-renders strokes and PDF backgrounds at the resolution they'll be shown at
    // instead of letting the ScrollViewer stretch a low-res bitmap.
    private float _appliedDpiScale = 1f;
    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        ApplyZoomDpi();
        ZoomChanged?.Invoke(this, Scroller.ZoomFactor);
        ActivePageScrolled?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyZoomDpi()
    {
        var zoom = Scroller.ZoomFactor;
        var target = (float)Math.Min(4.0, Math.Max(1.0, Math.Ceiling(zoom)));
        // Even when the main DpiScale hasn't changed, the live-stroke cap setting
        // may have — let PageCanvas decide whether to skip.
        _appliedDpiScale = target;
        foreach (var pc in _pageCanvases) pc.SetDpiScale(target);
    }

    public void Bind(Document doc, AppSettings settings, PageRenderer renderer)
    {
        Document = doc;
        _renderer = renderer;
        SeamlessPages = settings.SeamlessPages;
        _palm = new PalmRejection(settings);
        _router = new PenButtonRouter(settings);
        _buttonTools = new Dictionary<PenButtonAction, ITool>
        {
            [PenButtonAction.Eraser] = new EraserTool(),
            [PenButtonAction.LassoSelect] = new LassoTool(),
            [PenButtonAction.RectSelect] = new RectSelectTool(),
            [PenButtonAction.Highlighter] = new HighlighterTool(),
            [PenButtonAction.Pen] = new PenTool(),
        };
        Rebuild();
    }

    public void Rebuild()
    {
        PagesPanel.Children.Clear();
        _pageCanvases.Clear();
        if (Document is null || _renderer is null || _palm is null || _router is null) return;

        PagesPanel.Spacing = SeamlessPages ? 0 : 24;
        var pageMargin = SeamlessPages ? new Thickness(0) : new Thickness(0, 0, 0, 24);

        foreach (var page in Document.Pages)
        {
            // In seamless mode insert a visible separator bar between pages
            // (not before the very first one). Uses a dedicated brush with
            // enough contrast to be clearly visible against white page paper.
            if (SeamlessPages && _pageCanvases.Count > 0)
            {
                PagesPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Height = 4,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["PageSeparatorBrush"]
                });
            }

            var canvas = new PageCanvas(
                page, page.TemplateOverride ?? Document.Template, Context,
                () => ToolProvider(),
                _palm, _router, _buttonTools, _renderer)
            {
                Margin = pageMargin,
                HorizontalAlignment = HorizontalAlignment.Left,
                BorderThickness = SeamlessPages ? new Thickness(0) : new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"]
            };
            canvas.SetDpiScale(_appliedDpiScale);
            canvas.MomentaryToolStart += (_, kind) => MomentaryToolStart?.Invoke(this, kind);
            canvas.MomentaryToolEnd += (_, __) => MomentaryToolEnd?.Invoke(this, EventArgs.Empty);
            canvas.RectSelectionCompleted += (_, __) => RectSelectionCompleted?.Invoke(this, EventArgs.Empty);
            canvas.ExtensionDragCompleted += OnExtensionDragCompleted;
            canvas.ExtendModeActive = ExtendModeActive;
            PagesPanel.Children.Add(canvas);
            _pageCanvases.Add(canvas);
        }

        // "Add page" button beneath the last page.
        // Wrap in a fixed-width container so it stays centered relative to the
        // standard (pre-extension) page width, not the wider extended panel.
        double stdW = 1240;
        if (Document?.Pages.Count > 0)
        {
            var first = Document.Pages[0];
            stdW = first.BackgroundContentWidth > 0 ? first.BackgroundContentWidth : first.Width;
        }
        var addBtn = new Microsoft.UI.Xaml.Controls.Button
        {
            Width = 52, Height = 52,
            CornerRadius = new CornerRadius(26),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = SeamlessPages ? new Thickness(0, 8, 0, 0) : new Thickness(0, 8, 0, 24),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppSurfaceVariantBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
            BorderThickness = new Thickness(1.5),
            Content = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "", FontSize = 20 }
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(addBtn, "Add page");
        addBtn.Click += (_, __) => AddPageRequested?.Invoke(this, EventArgs.Empty);
        var addBtnWrapper = new Grid
        {
            Width = stdW,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        addBtnWrapper.Children.Add(addBtn);
        PagesPanel.Children.Add(addBtnWrapper);
    }

    /// <summary>
    /// Called by EditorPage when the Page Width tool is activated or deactivated.
    /// Propagates to all current (and future-rebuilt) PageCanvas instances so the
    /// large pill handles appear/disappear, and toggles touch-scroll suppression so
    /// finger drag on the handles is not stolen by the ScrollViewer.
    /// </summary>
    public void SetExtendMode(bool active)
    {
        ExtendModeActive = active;
        foreach (var pc in _pageCanvases)
            pc.ExtendModeActive = active;
    }

    public void InvalidateAll()
    {
        foreach (var c in _pageCanvases) c.RequestRedraw();
    }

    /// <summary>
    /// Resizes every PageCanvas to match its page's current Width after an undo/redo
    /// that may have changed page dimensions (e.g. extension committed then undone).
    /// </summary>
    public void ResizeAllCanvases()
    {
        foreach (var pc in _pageCanvases) pc.ResizeCanvas();
    }

    public void InvalidateAllLive()
    {
        // Only the page that owns the active stroke can show anything on its live
        // overlay; invalidating other pages is just framework chatter. Big PDFs
        // would otherwise pay 100x Invalidate() calls per pen sample.
        ActivePageCanvas?.RequestLiveRedraw();
    }

    public byte[][] RenderAllToPng(float scale = 1f, bool overlayOnly = false)
    {
        var result = new byte[_pageCanvases.Count][];
        for (int i = 0; i < _pageCanvases.Count; i++) result[i] = _pageCanvases[i].RenderToPng(scale, overlayOnly);
        return result;
    }

    public byte[]? RenderCurrentPagePng(int index, float scale = 1f)
    {
        if (index < 0 || index >= _pageCanvases.Count) return null;
        return _pageCanvases[index].RenderToPng(scale);
    }

    public void SetZoom(double zoom)
    {
        var newZoom = (float)zoom;
        var oldZoom = Scroller.ZoomFactor <= 0 ? 1f : Scroller.ZoomFactor;
        var vpW = Scroller.ViewportWidth;
        var vpH = Scroller.ViewportHeight;
        var centerH = (Scroller.HorizontalOffset + vpW / 2) / oldZoom;
        var centerV = (Scroller.VerticalOffset + vpH / 2) / oldZoom;
        var newH = centerH * newZoom - vpW / 2;
        var newV = centerV * newZoom - vpH / 2;
        Scroller.ChangeView(newH, newV, newZoom, disableAnimation: true);
    }

    public PageCanvas? ActivePageCanvas =>
        Document is null ? null
        : _pageCanvases.FirstOrDefault(pc => ReferenceEquals(pc.Page, Context.CurrentPage))
          ?? _pageCanvases.FirstOrDefault();

    public bool HasSelection =>
        (Context.SelectedStrokeIds.Count + Context.SelectedTextIds.Count +
         Context.SelectedImageIds.Count + Context.SelectedShapeIds.Count) > 0;

    public void RefreshTemplates()
    {
        if (Document is null) return;
        foreach (var pc in _pageCanvases)
            pc.SetTemplate(pc.Page.TemplateOverride ?? Document.Template);
    }

    public NotePage? ActivePage
    {
        get
        {
            if (Document is null || _pageCanvases.Count == 0) return null;

            // Find whichever page currently sits over the viewport vertical center.
            var centerY = Scroller.ViewportHeight / 2;
            PageCanvas? best = null;
            double bestDistance = double.MaxValue;
            foreach (var pc in _pageCanvases)
            {
                try
                {
                    var top = pc.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                    var height = pc.ActualHeight * Scroller.ZoomFactor;
                    if (top <= centerY && top + height >= centerY) return pc.Page;

                    var midY = top + height / 2;
                    var d = System.Math.Abs(midY - centerY);
                    if (d < bestDistance) { bestDistance = d; best = pc; }
                }
                catch { }
            }
            return best?.Page ?? _pageCanvases[0].Page;
        }
    }

    // Shows or hides the ruler overlay on every page canvas.
    // On first show the ruler is centred on the first page (if not yet positioned).
    public void SetRulerVisible(bool visible)
    {
        Context.RulerVisible = visible;
        if (visible && Context.RulerX == 0f && Context.RulerY == 0f && _pageCanvases.Count > 0)
        {
            var page = _pageCanvases[0].Page;
            Context.RulerX = (float)(page.Width  / 2.0);
            Context.RulerY = (float)(page.Height / 3.0); // upper-third feels natural
        }
        foreach (var pc in _pageCanvases) pc.SyncRulerOverlay();
    }

    public void DeleteSelection() => ActivePageCanvas?.DeleteSelection();
    public void DuplicateSelection() => ActivePageCanvas?.DuplicateSelection();
    public void ClearSelection() => ActivePageCanvas?.ClearSelection();
    public byte[]? RenderRegionOfCurrentPage(float x, float y, float w, float h)
        => ActivePageCanvas?.RenderRegionToPng(x, y, w, h);

    /// <summary>Renders the bounding box of the current selection as a PNG.</summary>
    public byte[]? RenderSelectionRegionToPng()
    {
        var pc = ActivePageCanvas;
        if (pc is null || !pc.SelectionBbox(out var bbox)) return null;
        return pc.RenderRegionToPng(bbox.X, bbox.Y, Math.Max(1, bbox.W), Math.Max(1, bbox.H));
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

    // PDF text selection (Hand tool). Lives on the active page; copying delegates there.
    public string? GetSelectedText() => ActivePageCanvas?.GetSelectedText();
    public void ClearTextSelection()
    {
        foreach (var pc in _pageCanvases) pc.ClearTextSelection();
    }

    // ── Page extension ─────────────────────────────────────────────────────────

    private void OnExtensionDragCompleted(object? sender, (ExtendSide Side, double Amount) args)
    {
        // Amount is the total desired extension (may be 0 = fully unextended).
        if (sender is not PageCanvas pc) return;
        CommitPageExtension(pc.Page, args.Side, args.Amount, pc);
    }

    // Called by EditorPage when the dialog is used for precise control.
    public void CommitPageExtension(NotePage page, ExtendSide side, double amount)
    {
        var pc = _pageCanvases.FirstOrDefault(c => ReferenceEquals(c.Page, page));
        CommitPageExtension(page, side, amount, pc);
    }

    public void ResetPageExtension(NotePage page)
    {
        var pc = _pageCanvases.FirstOrDefault(c => ReferenceEquals(c.Page, page));
        ResetExtension(page);
        pc?.ResizeCanvas();
        DocumentMutated?.Invoke(this, EventArgs.Empty);
    }

    private void CommitPageExtension(NotePage page, ExtendSide side, double amount, PageCanvas? pc)
    {
        // amount = total desired extension; SetExtensionAbsolute handles both
        // increase and reduction (amount may be 0 to fully unextend a side).
        SetExtensionAbsolute(page, side, amount);
        pc?.ResizeCanvas();
        DocumentMutated?.Invoke(this, EventArgs.Empty);
    }

    // Migrates all element coordinates and updates the page dimensions.
    /// <summary>
    /// Sets the total extension for one side to an absolute amount.
    /// Handles both increasing and decreasing (including fully removing) extension.
    /// </summary>
    public static void SetExtensionAbsolute(NotePage page, ExtendSide side, double totalExtension)
    {
        totalExtension = Math.Max(0, totalExtension);

        // Ensure original width is captured for any page type (PDF or blank).
        if (page.BackgroundContentWidth == 0 && totalExtension > 0)
            page.BackgroundContentWidth = page.Width - page.BackgroundLeft;

        if (side == ExtendSide.Right)
        {
            double currentRightExt = page.BackgroundContentWidth > 0
                ? Math.Max(0, page.Width - page.BackgroundLeft - page.BackgroundContentWidth)
                : 0;
            page.Width += totalExtension - currentRightExt; // may shrink
        }
        else // Left
        {
            double currentLeftExt = page.BackgroundLeft;
            double delta = totalExtension - currentLeftExt;
            if (delta == 0) return;

            float shift = (float)delta; // positive = more left ext; negative = less
            foreach (var s in page.Strokes)
                for (int i = 0; i < s.Points.Count; i++)
                    s.Points[i] = s.Points[i] with { X = s.Points[i].X + shift };
            foreach (var sh in page.Shapes)
            { sh.X1 += shift; sh.X2 += shift; sh.X3 += shift; }
            foreach (var t  in page.Texts)  t.X  += delta;
            foreach (var im in page.Images) im.X += delta;
            foreach (var tr in page.TextRuns) tr.X += delta;
            page.BackgroundLeft += delta;
            page.Width += delta;
        }
    }

    // Left extension shifts every element X forward so the PDF stays in place.
    // Right extension only grows the page width.
    // Kept for internal use; prefer SetExtensionAbsolute for new callers.
    public static void ApplyExtension(NotePage page, double leftAmt, double rightAmt)
    {
        if (leftAmt <= 0 && rightAmt <= 0) return;

        // Capture original width on first ever extension (any page type).
        if (page.BackgroundContentWidth == 0)
            page.BackgroundContentWidth = page.Width - page.BackgroundLeft;

        if (leftAmt > 0)
        {
            float shift = (float)leftAmt;
            foreach (var s in page.Strokes)
                for (int i = 0; i < s.Points.Count; i++)
                    s.Points[i] = s.Points[i] with { X = s.Points[i].X + shift };
            foreach (var sh in page.Shapes)
            { sh.X1 += shift; sh.X2 += shift; sh.X3 += shift; }
            foreach (var t  in page.Texts)  t.X  += leftAmt;
            foreach (var im in page.Images) im.X += leftAmt;
            foreach (var tr in page.TextRuns) tr.X += leftAmt;
            page.BackgroundLeft += leftAmt;
            page.Width += leftAmt;
        }

        if (rightAmt > 0)
            page.Width += rightAmt;
    }

    // Reverses all extensions: shifts elements back by BackgroundLeft, restores Width.
    public static void ResetExtension(NotePage page)
    {
        if (page.BackgroundLeft == 0 && page.BackgroundContentWidth == 0) return;

        double leftExt = page.BackgroundLeft;
        double originalW = page.BackgroundContentWidth > 0 ? page.BackgroundContentWidth : page.Width;

        if (leftExt > 0)
        {
            float shift = -(float)leftExt;
            foreach (var s in page.Strokes)
                for (int i = 0; i < s.Points.Count; i++)
                    s.Points[i] = s.Points[i] with { X = s.Points[i].X + shift };
            foreach (var sh in page.Shapes)
            { sh.X1 += shift; sh.X2 += shift; sh.X3 += shift; }
            foreach (var t  in page.Texts)  t.X  -= leftExt;
            foreach (var im in page.Images) im.X -= leftExt;
            foreach (var tr in page.TextRuns) tr.X -= leftExt;
        }

        page.BackgroundLeft = 0;
        page.BackgroundContentWidth = 0;
        page.Width = originalW;
    }
}
