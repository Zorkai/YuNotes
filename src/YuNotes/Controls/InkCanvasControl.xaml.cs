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
    // Carries the page the mutation touched (null when unknown) so history can
    // snapshot just that page instead of deep-copying the whole document.
    public event EventHandler<YuNotes.Models.NotePage?>? DocumentMutated;
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
        Context.Mutated = () => DocumentMutated?.Invoke(this, Context.CurrentPage);
        Context.SelectionChanged = () => SelectionChanged?.Invoke(this, EventArgs.Empty);
        Context.EditTextRequested = id => ActivePageCanvas?.BeginInlineTextEdit(id);
        Context.ToolRequested = kind => ToolRequested?.Invoke(this, kind);
        Context.RequestPan = (dx, dy) =>
        {
            // Hand-tool / middle-drag panning stops at the end of the document;
            // the pull-to-add-page zone below it is for scroll gestures only.
            double v = Scroller.VerticalOffset + dy;
            v = Math.Min(v, Math.Max(PullRestOffset(), Scroller.VerticalOffset));
            Scroller.ChangeView(Scroller.HorizontalOffset + dx, v, null, disableAnimation: true);
        };
        Context.RulerChanged = () =>
        {
            foreach (var pc in _pageCanvases) pc.SyncRulerOverlay();
        };
        Scroller.ViewChanged += OnScrollerViewChanged;
        // Pages moving (zoom, window size, pages growing or reordering) changes
        // which of them are near the screen.
        Scroller.SizeChanged += (_, __) => UpdatePageResidency();
        PagesPanel.SizeChanged += (_, __) => UpdatePageResidency();
        HookPullToAddPage();

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

    /// <summary>
    /// Ends any gesture left half-finished (e.g. by Alt+Tab or a touchpad swipe
    /// mid-stroke, where no release ever arrives): restores the ScrollViewer's
    /// pan/zoom suppression and resets every page's pointer state. Called when
    /// the window is activated or deactivated; a no-op when idle.
    /// </summary>
    public void ResetInputState()
    {
        _suppressPointers.Clear();
        if (_suppressing)
        {
            Scroller.HorizontalScrollMode = _savedHScroll;
            Scroller.VerticalScrollMode = _savedVScroll;
            Scroller.ZoomMode = _savedZoom;
            _suppressing = false;
        }
        foreach (var pc in _pageCanvases) pc.ResetInputState();
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

    public void Bind(Document doc, AppSettings settings, PageRenderer renderer)
    {
        Document = doc;
        _renderer = renderer;
        // Every tape strip starts covered when a note is opened.
        Context.RevealedTapeIds.Clear();
        renderer.IsTapeRevealed = id => Context.RevealedTapeIds.Contains(id);
        _studyFocusId = null;
        renderer.IsTapeFocused = id => id == _studyFocusId;
        renderer.IsTapeMissed = id => Services.StudyDeck.IsMissed(Document?.Info.FilePath ?? "", id);
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
        _pullZone = null;
        _pullRingHost = null;
        _addBtnWrapper = null;
        _separators.Clear();
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
                // Width is set by UpdateSeparators: only as wide as both pages
                // it separates (pages are left-aligned, so a widened page must
                // not drag every separator out past narrower pages).
                var sep = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Height = 4,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["PageSeparatorBrush"]
                };
                PagesPanel.Children.Add(sep);
                _separators.Add(sep);
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
            // Paper look: each page floats with a soft drop shadow. Skipped in
            // seamless mode, where pages butt together into one long sheet.
            if (!SeamlessPages)
            {
                canvas.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
                canvas.Translation = new System.Numerics.Vector3(0, 0, 14);
            }
            canvas.SetDpiScale(_appliedDpiScale);
            canvas.MomentaryToolStart += (_, kind) => MomentaryToolStart?.Invoke(this, kind);
            canvas.MomentaryToolEnd += (_, __) => MomentaryToolEnd?.Invoke(this, EventArgs.Empty);
            canvas.RectSelectionCompleted += (_, __) => RectSelectionCompleted?.Invoke(this, EventArgs.Empty);
            canvas.SelectionDragDropped += OnSelectionDragDropped;
            canvas.ExtensionDragCompleted += OnExtensionDragCompleted;
            canvas.SpaceDragCompleted += OnSpaceDragCompleted;
            canvas.TapeToggled += t => TapeToggled?.Invoke(this, t);
            canvas.ExtendModeActive = ExtendModeActive;
            PagesPanel.Children.Add(canvas);
            _pageCanvases.Add(canvas);
        }

        // Link each page to its vertical neighbours so they can render the
        // slice of a straddling element that bleeds across the shared seam.
        for (int i = 0; i < _pageCanvases.Count; i++)
        {
            _pageCanvases[i].PrevPageCanvas = i > 0 ? _pageCanvases[i - 1] : null;
            _pageCanvases[i].NextPageCanvas = i < _pageCanvases.Count - 1 ? _pageCanvases[i + 1] : null;
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
        BuildAddPageControls(stdW);
        UpdateSeparators();
        // Decode images only for the pages that end up near the screen, once
        // layout has placed them.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdatePageResidency);
    }

    // Separator i sits between page i and page i+1 (seamless mode only).
    private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _separators = new();

    // Each separator spans only where both of its pages are, i.e. the narrower
    // of the two. Called after anything that changes a page's width.
    public void UpdateSeparators()
    {
        for (int i = 0; i < _separators.Count && i + 1 < _pageCanvases.Count; i++)
            _separators[i].Width = Math.Max(1, Math.Min(_pageCanvases[i].Page.Width, _pageCanvases[i + 1].Page.Width));
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
        UpdateSeparators();
    }

    public void InvalidateAllLive()
    {
        // Only the page that owns the active stroke can show anything on its live
        // overlay; invalidating other pages is just framework chatter. Big PDFs
        // would otherwise pay 100x Invalidate() calls per pen sample.
        ActivePageCanvas?.RequestLiveRedraw();
    }

    public int PageCount => _pageCanvases.Count;

    // Index of the page over the viewport centre (-1 with no document).
    public int ActivePageIndex => ActivePage is { } p ? _pageCanvases.FindIndex(pc => ReferenceEquals(pc.Page, p)) : -1;

    public int TapeCount => Document?.Pages.Sum(p => p.Shapes.Count(s => s.Kind == ShapeKind.Tape)) ?? 0;

    // Covers (false) or reveals (true) every tape strip in the note.
    public void SetAllTapeRevealed(bool revealed)
    {
        Context.RevealedTapeIds.Clear();
        if (revealed && Document is not null)
            foreach (var p in Document.Pages)
                foreach (var s in p.Shapes)
                    if (s.Kind == ShapeKind.Tape) Context.RevealedTapeIds.Add(s.Id);
        InvalidateAll();
    }

    // ── Study mode ──────────────────────────────────────────────────────────

    public event EventHandler<ShapeElement>? TapeToggled;
    private string? _studyFocusId;

    public List<Services.StudyItem> StudyItems() =>
        Document is null ? new() : Services.StudyDeck.Order(Document.Pages);

    private PageCanvas? CanvasFor(Services.StudyItem item) =>
        item.PageIndex >= 0 && item.PageIndex < _pageCanvases.Count ? _pageCanvases[item.PageIndex] : null;

    /// <summary>Outlines the strip being asked (null = none).</summary>
    public void SetStudyFocus(Services.StudyItem? item)
    {
        var old = _studyFocusId;
        _studyFocusId = item?.Id;
        if (old is not null)
            foreach (var pc in _pageCanvases)
                if (pc.Page.Shapes.FirstOrDefault(s => s.Id == old) is { } prev) { pc.RedrawTape(prev); break; }
        if (item is not null) CanvasFor(item)?.RedrawTape(item.Tape);
    }

    public void SetTapeRevealed(Services.StudyItem item, bool revealed)
    {
        if (revealed) Context.RevealedTapeIds.Add(item.Id); else Context.RevealedTapeIds.Remove(item.Id);
        CanvasFor(item)?.RedrawTape(item.Tape);
    }

    public byte[]? RenderStudyCard(Services.StudyItem item, bool revealed)
    {
        try { return CanvasFor(item)?.RenderStudyCardPng(item.Tape, revealed); }
        catch (Exception ex) { App.LogError(ex, "RenderStudyCard"); return null; }
    }

    // Removes every laser trail on every page at once.
    public void ClearLaser()
    {
        foreach (var pc in _pageCanvases) pc.ClearLaser();
    }

    public PageCanvas? ActivePageCanvas =>
        Document is null ? null
        : _pageCanvases.FirstOrDefault(pc => ReferenceEquals(pc.Page, Context.CurrentPage))
          ?? _pageCanvases.FirstOrDefault();

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
}
