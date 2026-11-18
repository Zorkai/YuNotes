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
    // Starts sharpening (DpiScale tier + hi-res PDF crop) as soon as the view
    // holds still for the interval — even mid-gesture with fingers down —
    // instead of waiting for DirectManipulation to declare the gesture over.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _viewSettleTimer;
    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        OnPullViewChanged(e.IsIntermediate);
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
        ApplyZoomDpi();
        UpdateHiResBackgrounds();
        ZoomChanged?.Invoke(this, Scroller.ZoomFactor);
        ActivePageScrolled?.Invoke(this, EventArgs.Empty);
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
        _pullZone = null;
        _pullRingHost = null;
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
    }

    // ── Pull to add page ────────────────────────────────────────────────────────
    //
    // Scrolling on past the end of the document fills a ring around the "+"
    // button; letting go once it's full adds a page. Implemented as real scroll
    // space (the "pull zone") below the button, so it works the same for mouse
    // wheel, precision touchpad, touch and the scrollbar: how far the view is
    // scrolled into the zone IS the progress.
    //
    //  • Touchpad / touch: only finger-driven movement fills the ring. Pushing
    //    past the end with fingers down always counts — wherever the swipe
    //    started, even if it began mid-fling — so it never "refuses" to fill.
    //    Momentum alone never fills: a fling that reaches the end stops there.
    //  • Mouse wheel: notches that arrive once the view is at the end fill the
    //    ring; a fast spin that reaches the end from above stops at the end.
    //  • Letting go: lifting fingers (the moment momentum takes over, or the
    //    manipulation ends), or pausing the wheel briefly. At 100% a page is
    //    added; below, the ring drains and the view springs back right away.

    // On-screen distance (DIPs) to push past the end for 100%.
    private const double PullDistance = 240;
    private const double PullFullThreshold = 0.995;

    private Grid? _pullZone;
    private Grid? _pullRingHost;
    private Microsoft.UI.Xaml.Shapes.Ellipse? _pullTrack;
    private Microsoft.UI.Xaml.Shapes.Ellipse? _pullFull;
    private Microsoft.UI.Xaml.Shapes.Path? _pullArc;
    private Button? _addBtn;
    private FontIcon? _addIcon;
    private TextBlock? _pullLabel;

    private bool _pullArmed;          // the current push may add a page
    private bool _pullInManipulation; // DirectManipulation (touch/touchpad) active
    private bool _pullViewMoving;     // last ViewChanged was intermediate
    private bool _pullSnappingBack;   // our own spring-back animation is running
    private DateTime _pullSnapAt;
    // Spring-back in flight. Time-boxed so a ChangeView that never reports back
    // (e.g. swallowed mid-manipulation) can't wedge the gesture.
    private bool PullSnapping =>
        _pullSnappingBack && (DateTime.UtcNow - _pullSnapAt).TotalMilliseconds < 1000;
    private bool _pullInertiaSeen;    // already handled "let go" for this manipulation
    private bool _pullWheelCarry;     // a wheel scroll began above the end: stop there
    private bool _pullCommitting;
    private DateTime _pullLastWheel = DateTime.MinValue;
    private double _pullProgress;
    private double _pullZoneZoom = -1;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pullReleaseTimer;
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush PullWhite = new(Microsoft.UI.Colors.White);

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private bool PullWheelRecent => (DateTime.UtcNow - _pullLastWheel).TotalMilliseconds < 600;

    private double PullProgressAt(double offset) =>
        Math.Clamp((offset - PullRestOffset()) / PullDistance, 0, 1);

    private void BuildAddPageControls(double stdW)
    {
        const double ring = 64, track = 58;

        _addIcon = new FontIcon
        {
            Glyph = "", FontSize = 20,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform(),
        };
        _addBtn = new Button
        {
            Width = 52, Height = 52,
            CornerRadius = new CornerRadius(26),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Res("AppSurfaceVariantBrush"),
            BorderBrush = Res("AppBorderBrush"),
            BorderThickness = new Thickness(1.5),
            Content = _addIcon,
            // Little "pop" when the ring completes.
            CenterPoint = new System.Numerics.Vector3(26, 26, 0),
            ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) },
        };
        ToolTipService.SetToolTip(_addBtn, "Add page (or keep scrolling down past the end)");
        _addBtn.Click += (_, __) => AddPageRequested?.Invoke(this, EventArgs.Empty);

        _pullTrack = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = track, Height = track,
            Stroke = Res("AppBorderBrush"), StrokeThickness = 3,
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
        };
        _pullArc = new Microsoft.UI.Xaml.Shapes.Path
        {
            Stroke = Res("AppAccentBrush"), StrokeThickness = 3,
            StrokeStartLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
            StrokeEndLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
            Visibility = Visibility.Collapsed,
        };
        _pullFull = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = track, Height = track,
            Stroke = Res("AppAccentBrush"), StrokeThickness = 3,
            Visibility = Visibility.Collapsed,
        };
        _pullRingHost = new Grid
        {
            Width = ring, Height = ring,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = SeamlessPages ? new Thickness(0, 2, 0, 0) : new Thickness(0, 2, 0, 18),
        };
        _pullRingHost.Children.Add(_pullTrack);
        _pullRingHost.Children.Add(_pullArc);
        _pullRingHost.Children.Add(_pullFull);
        _pullRingHost.Children.Add(_addBtn);

        var addBtnWrapper = new Grid { Width = stdW, HorizontalAlignment = HorizontalAlignment.Left };
        addBtnWrapper.Children.Add(_pullRingHost);
        PagesPanel.Children.Add(addBtnWrapper);

        // The pull zone: empty scroll space revealed as the user pushes past
        // the end. Its hint label sits at the top, right under the button.
        _pullLabel = new TextBlock
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Res("AppTextSecondaryBrush"),
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
            IsHitTestVisible = false,
        };
        _pullZone = new Grid
        {
            Width = stdW,
            Height = PullDistance,   // corrected by UpdatePullZoneHeight once laid out
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _pullZone.Children.Add(_pullLabel);
        PagesPanel.Children.Add(_pullZone);

        _pullArmed = false;
        _pullWheelCarry = false;
        _pullSnappingBack = false;
        _pullZoneZoom = -1;
        _pullProgress = 0;
    }

    private void HookPullToAddPage()
    {
        Scroller.ViewChanging += OnPullViewChanging;
        Scroller.DirectManipulationStarted += (_, __) =>
        {
            // Fingers down: the user takes over from any spring-back. Arming
            // happens in OnPullViewChanging, from finger-driven movement only.
            _pullInManipulation = true;
            _pullInertiaSeen = false;
            _pullSnappingBack = false;
            _pullReleaseTimer?.Stop();
        };
        Scroller.DirectManipulationCompleted += (_, __) =>
        {
            _pullInManipulation = false;
            SchedulePullRelease();
        };
        Scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPullWheel), handledEventsToo: true);
        Scroller.SizeChanged += (_, __) => UpdatePullZoneHeight();
        PagesPanel.SizeChanged += (_, __) => UpdatePullZoneHeight();
    }

    // Scroll offset at which the end of the document (just above the pull zone)
    // meets the bottom of the viewport — where the view rests.
    private double PullRestOffset()
    {
        if (_pullZone is null) return double.MaxValue;
        try
        {
            double top = _pullZone.TransformToVisual(ContentRoot).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            return Math.Max(0, top * Scroller.ZoomFactor - Scroller.ViewportHeight);
        }
        catch { return double.MaxValue; }
    }

    // Size the zone so the farthest the view can scroll is exactly 100%,
    // whatever the zoom and even when the note is shorter than the window.
    private void UpdatePullZoneHeight()
    {
        if (_pullZone is null) return;
        double zoom = Scroller.ZoomFactor <= 0 ? 1 : Scroller.ZoomFactor;
        double top;
        try { top = _pullZone.TransformToVisual(ContentRoot).TransformPoint(new Windows.Foundation.Point(0, 0)).Y; }
        catch { return; }
        double v = Scroller.ViewportHeight;
        double h = Math.Max(0, (PullDistance + Math.Max(v, top * zoom)) / zoom - top - ContentRoot.Padding.Bottom);
        if (double.IsNaN(_pullZone.Height) || Math.Abs(_pullZone.Height - h) > 0.5)
            _pullZone.Height = h;
        _pullZoneZoom = zoom;
    }

    private void OnPullWheel(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(Scroller).Properties;
        // Ctrl = zoom, Shift / tilt = horizontal scroll.
        if (props.IsHorizontalMouseWheel ||
            (e.KeyModifiers & (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift)) != 0)
            return;
        _pullLastWheel = DateTime.UtcNow;
        _pullSnappingBack = false;
        if (props.MouseWheelDelta < 0)   // scrolling down
        {
            if (Scroller.VerticalOffset >= PullRestOffset() - 1) { _pullArmed = true; _pullWheelCarry = false; }
            else if (!_pullArmed) _pullWheelCarry = true;
        }
        // Even if the view can't move (already at 100%), the pause after the
        // last notch is the "let go".
        SchedulePullRelease();
    }

    private void OnPullViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        if (_pullZone is null || _pullCommitting) return;
        double rest = PullRestOffset();
        double next = e.NextView.VerticalOffset;
        bool inZone = next > rest + 0.5;

        if (inZone && !PullSnapping && _pullWheelCarry && !_pullArmed)
        {
            // A wheel scroll that began above the end stops AT the end.
            _pullWheelCarry = false;
            DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, PullRestOffset(), null, disableAnimation: true));
            return;
        }

        if (_pullInManipulation && !PullWheelRecent)
        {
            if (!e.IsInertial)
            {
                // Fingers on the touchpad / screen. Pushing past the end with
                // them IS insisting — arm, wherever this swipe started.
                _pullInertiaSeen = false;
                // (Not while our spring-back animation drives the view — a real
                // new touch clears that flag in DirectManipulationStarted.)
                if (inZone && !PullSnapping) _pullArmed = true;
            }
            else if (!_pullInertiaSeen)
            {
                // Fingers just lifted and momentum took over: "letting go".
                _pullInertiaSeen = true;
                if (_pullArmed && _pullProgress >= PullFullThreshold) { CommitPull(); return; }
                _pullArmed = false;
                if (inZone || _pullProgress > 0) { SpringBack(); return; }
            }
            else if (inZone && !_pullArmed && !PullSnapping)
            {
                // A fling's momentum reached the end: stop there instead of
                // sailing on into the empty pull zone.
                SpringBack();
                return;
            }
        }

        SetPullProgress(Math.Clamp((next - rest) / PullDistance, 0, 1));
    }

    // Drain the ring and glide back to the end of the document, interrupting
    // any momentum still carrying the view into the pull zone.
    private void SpringBack()
    {
        _pullArmed = false;
        _pullWheelCarry = false;
        _pullSnappingBack = true;
        _pullSnapAt = DateTime.UtcNow;
        SetPullProgress(PullProgressAt(Scroller.VerticalOffset));
        DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, PullRestOffset(), null, disableAnimation: false));
    }

    // Called from OnScrollerViewChanged.
    private void OnPullViewChanged(bool isIntermediate)
    {
        _pullViewMoving = isIntermediate;
        if (isIntermediate) return;
        if (Math.Abs(Scroller.ZoomFactor - _pullZoneZoom) > 0.0005) UpdatePullZoneHeight();
        if (_pullSnappingBack)
        {
            // Spring-back finished (or was cut short). If the view still isn't
            // at the end, the release check below sends it back again.
            _pullSnappingBack = false;
            SetPullProgress(PullProgressAt(Scroller.VerticalOffset));
        }
        if (!_pullInManipulation) SchedulePullRelease();
    }

    private void SchedulePullRelease()
    {
        if (_pullZone is null) return;
        if (_pullReleaseTimer is null)
        {
            _pullReleaseTimer = DispatcherQueue.CreateTimer();
            _pullReleaseTimer.IsRepeating = false;
            _pullReleaseTimer.Tick += (_, __) => EvaluatePullRelease();
        }
        _pullReleaseTimer.Stop();
        // A wheel has no "release": a short pause after the last notch is it.
        _pullReleaseTimer.Interval = TimeSpan.FromMilliseconds(PullWheelRecent ? 420 : 40);
        _pullReleaseTimer.Start();
    }

    private void EvaluatePullRelease()
    {
        if (_pullZone is null || _pullCommitting || _pullInManipulation || PullSnapping) return;
        if (_pullViewMoving) return;   // the final ViewChanged reschedules us
        double rest = PullRestOffset();
        double p = PullProgressAt(Scroller.VerticalOffset);
        if (_pullArmed && p >= PullFullThreshold) { CommitPull(); return; }

        _pullArmed = false;
        _pullWheelCarry = false;
        // Not far enough: drain the ring and spring back to the end.
        if (Scroller.VerticalOffset > rest + 0.5) SpringBack();
        else SetPullProgress(0);
    }

    private void CommitPull()
    {
        if (_pullCommitting) return;
        _pullCommitting = true;
        _pullArmed = false;
        _pullWheelCarry = false;
        _pullReleaseTimer?.Stop();
        // Rebuild replaces these controls; defer out of the scroll event.
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                AddPageRequested?.Invoke(this, EventArgs.Empty);
                // Glide to the new page so it's clearly there.
                ScrollToPageDeferred(_pageCanvases.Count - 1);
            }
            finally { _pullCommitting = false; }
        });
    }

    private void SetPullProgress(double p)
    {
        _pullProgress = p;
        if (_pullRingHost is null || _pullTrack is null || _pullArc is null || _pullFull is null
            || _addBtn is null || _addIcon is null || _pullLabel is null) return;

        // Only a real (finger/wheel) push shows the ring; momentum bouncing off
        // the end stays quiet. While springing back the ring just drains.
        bool active = p > 0.001 && (_pullArmed || PullSnapping);
        bool full = _pullArmed && p >= PullFullThreshold;

        _pullTrack.Opacity = active ? 1 : 0;
        if (active && !full)
        {
            _pullArc.Data = PullArcGeometry(p, 32, 29);
            _pullArc.Visibility = Visibility.Visible;
        }
        else _pullArc.Visibility = Visibility.Collapsed;
        _pullFull.Visibility = full ? Visibility.Visible : Visibility.Collapsed;

        // The "+" turns with the pull and lights up when it's ready.
        if (_addIcon.RenderTransform is Microsoft.UI.Xaml.Media.RotateTransform rt)
            rt.Angle = active ? p * 90 : 0;
        _addBtn.Scale = full ? new System.Numerics.Vector3(1.12f, 1.12f, 1) : System.Numerics.Vector3.One;
        _addBtn.Background = Res(full ? "AppAccentBrush" : "AppSurfaceVariantBrush");
        if (full) _addIcon.Foreground = PullWhite;
        else _addIcon.ClearValue(IconElement.ForegroundProperty);

        if (!active || !_pullArmed || PullSnapping)
        {
            _pullLabel.Opacity = 0;
            return;
        }
        _pullLabel.Text = full ? "Let go to add a page" : "Keep scrolling to add a page";
        _pullLabel.Foreground = Res(full ? "AppAccentBrush" : "AppTextSecondaryBrush");
        _pullLabel.Opacity = 1;
    }

    // Clockwise arc from 12 o'clock covering fraction p of the circle.
    private static Microsoft.UI.Xaml.Media.Geometry PullArcGeometry(double p, double c, double r)
    {
        double a = p * 2 * Math.PI;
        var fig = new Microsoft.UI.Xaml.Media.PathFigure
        {
            StartPoint = new Windows.Foundation.Point(c, c - r),
            IsClosed = false,
        };
        fig.Segments.Add(new Microsoft.UI.Xaml.Media.ArcSegment
        {
            Point = new Windows.Foundation.Point(c + r * Math.Sin(a), c - r * Math.Cos(a)),
            Size = new Windows.Foundation.Size(r, r),
            IsLargeArc = a > Math.PI,
            SweepDirection = Microsoft.UI.Xaml.Media.SweepDirection.Clockwise,
        });
        var g = new Microsoft.UI.Xaml.Media.PathGeometry();
        g.Figures.Add(fig);
        return g;
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

    public int PageCount => _pageCanvases.Count;

    /// <summary>Renders one page's flattened/overlay PNG — lets the save loop
    /// render page-by-page and yield between pages so the UI stays responsive.</summary>
    public byte[]? RenderPageForExport(int index, float scale, bool overlayOnly)
    {
        if (index < 0 || index >= _pageCanvases.Count) return null;
        return _pageCanvases[index].RenderToPng(scale, overlayOnly);
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
    public void RecolorSelection(Windows.UI.Color c) => ActivePageCanvas?.RecolorSelection(c);
    public bool SelectionHasRecolorable => ActivePageCanvas?.SelectionHasRecolorable == true;
    public bool SelectionIsAllHighlighter => ActivePageCanvas?.SelectionIsAllHighlighter == true;
    public Windows.UI.Color? SelectionColor() => ActivePageCanvas?.SelectionColor();
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

    /// <summary>Renders the bounding box of the current selection as a PNG.</summary>
    public byte[]? RenderSelectionRegionToPng()
    {
        var pc = ActivePageCanvas;
        if (pc is null || !pc.SelectionBbox(out var bbox)) return null;
        return pc.RenderRegionToPng(bbox.X, bbox.Y, Math.Max(1, bbox.W), Math.Max(1, bbox.H));
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
        DocumentMutated?.Invoke(this, page);
    }

    private void CommitPageExtension(NotePage page, ExtendSide side, double amount, PageCanvas? pc)
    {
        // amount = total desired extension; SetExtensionAbsolute handles both
        // increase and reduction (amount may be 0 to fully unextend a side).
        SetExtensionAbsolute(page, side, amount);
        pc?.ResizeCanvas();
        DocumentMutated?.Invoke(this, page);
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
