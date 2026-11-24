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

public enum ExtendSide { Left, Right }

// Payload for PageCanvas.SelectionDragDropped — a selection Move drag released.
public sealed class SelectionDropEventArgs : EventArgs
{
    // Pointer release position in the SOURCE page's local coordinates.
    public Vector2 ReleaseLocal { get; init; }
    // Move delta (release − grab) in source-local coordinates.
    public Vector2 Delta { get; init; }
    // Set by the handler when it moved the selection to a different page.
    public bool Transferred { get; set; }
}

public sealed class PageCanvas : ContentControl
{
    public NotePage Page { get; }
    public TemplateSettings PageTemplate { get; set; }
    public EditorContext Context { get; }
    public Func<ITool?> ToolProvider { get; }
    public PalmRejection PalmRejection { get; }
    public PenButtonRouter ButtonRouter { get; }
    public Dictionary<PenButtonAction, ITool> ButtonTools { get; }
    public PageRenderer Renderer { get; }

    // Main canvas — also tile-based so stroke commit can invalidate just the
    // bounding box of the new stroke rather than the whole page (which at high
    // zoom means re-blitting the multi-MB PDF background bitmap).
    private readonly CanvasVirtualControl _canvas;
    // XAML layer hosting the live-stroke shapes, shape previews, selection
    // handles and extension handles. All in-progress ink is drawn here as
    // retained-mode XAML shapes (Polyline et al.) — DComp redraws them
    // natively, no Win2D surface is touched until the stroke commits.
    private readonly Canvas _overlay;

    // Live shape preview — a lightweight XAML element in the overlay that acts
    // as the rubber-band while the user is dragging out a new shape.
    private UIElement? _liveShapePreview;
    private string? _liveShapeId;

    private CanvasBitmap? _bgBitmap;

    // Live-stroke backend: dirty-rect draws into a virtualized DComp surface
    // (one XAML Image at the bottom of the overlay). Created lazily on the
    // first stroke. Replaced the retained-mode XAML shape overlays (chunked
    // Polyline / filled-ribbon Path): swapping a shape's geometry every sample
    // re-rasterized it at screen scale and forced DWM to recomposite the whole
    // window per frame (~half the drawing-time GPU cost; see
    // prototypes/InkPerfLab).
    private LiveInkSurface? _liveInk;
    private bool _liveInkActive;   // last value raised through LiveInkActiveChanged

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

    private readonly Dictionary<string, CanvasBitmap> _imageCache = new();
    private uint? _capturedPointerId;
    private ITool? _activeTool;
    // True while a pointer gesture (stroke, drag, pan…) is in flight on this
    // page. EditorPage's autosave defers while any page is being interacted
    // with so a save can't stall the UI thread mid-stroke.
    public bool HasActivePointer => _capturedPointerId is not null;
    // One live-overlay sync per input event: tools invoke ctx.InvalidateLive
    // once per sample, but a single PointerMoved can carry several coalesced
    // samples — rebuilding the ribbon/polyline tail for each is wasted work.
    private bool _suppressLiveSync;
    // One live-ink surface update per composed frame: pens often report
    // faster than the display refreshes (240 Hz pen, 60/120 Hz panel), and
    // every surface update costs GPU + DWM work even though only the last one
    // before the frame is ever seen. Samples still land in the model on each
    // event; the surface catches up once, right before the frame renders.
    private bool _liveInkSyncPending;

    // ── Cross-page bleed ────────────────────────────────────────────────────
    // Adjacent page canvases (set by InkCanvasControl after Rebuild). When an
    // element sits partly past this page's top/bottom edge — e.g. a selection
    // dragged so it straddles the seam — the overflowing part is invisible on
    // its owning page (clipped to the page surface). Each page therefore also
    // renders the portion of its neighbours' content that bleeds into it, so a
    // straddling element shows on BOTH pages.
    public PageCanvas? PrevPageCanvas { get; set; }
    public PageCanvas? NextPageCanvas { get; set; }
    internal System.Collections.Generic.IDictionary<string, CanvasBitmap> ImageCacheView => _imageCache;
    internal System.Collections.Generic.ISet<string>? HiddenElementIdsView => _hiddenElementIds;

    // Cached vertical content extent, so a neighbour can cheaply tell how far
    // this page's content overflows its edges without rescanning every frame.
    private bool _contentBoundsDirty = true;
    private float _contentMinY, _contentMaxY;
    // How far content pokes above y=0 / below Height (0 when nothing overflows).
    public float TopOverflow    { get { EnsureContentBounds(); return Math.Max(0f, -_contentMinY); } }
    public float BottomOverflow { get { EnsureContentBounds(); return Math.Max(0f, _contentMaxY - (float)Page.Height); } }

    private void EnsureContentBounds()
    {
        if (!_contentBoundsDirty) return;
        _contentBoundsDirty = false;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var s in Page.Strokes)  { var b = Bbox.Of(s);  if (b.Y < minY) minY = b.Y; if (b.Bottom > maxY) maxY = b.Bottom; }
        foreach (var sh in Page.Shapes)  { var b = Bbox.Of(sh); if (b.Y < minY) minY = b.Y; if (b.Bottom > maxY) maxY = b.Bottom; }
        foreach (var t in Page.Texts)    { var b = Bbox.Of(t);  if (b.Y < minY) minY = b.Y; if (b.Bottom > maxY) maxY = b.Bottom; }
        foreach (var im in Page.Images)  { var b = Bbox.Of(im); if (b.Y < minY) minY = b.Y; if (b.Bottom > maxY) maxY = b.Bottom; }
        if (minY == float.MaxValue) { minY = 0f; maxY = 0f; }
        _contentMinY = minY; _contentMaxY = maxY;
    }

    // Neighbours read this page's overflow to decide whether to bleed; recompute
    // it after any content change on this page.
    public void InvalidateContentBounds() => _contentBoundsDirty = true;

    // Drag modes when interacting with selection
    private enum DragMode { None, Move, ResizeNW, ResizeNE, ResizeSW, ResizeSE, Rotate }
    private DragMode _drag = DragMode.None;
    private Bbox _dragStartBbox;
    private Vector2 _dragStartCenter;
    private double _dragStartRotation;

    // ── Select-tool overlay visuals ──────────────────────────────────────────
    // The marquee (rect / lasso) and the drag ghost live in the XAML overlay
    // like live ink: per-pointer-move updates cost no Win2D redraw. Before
    // this, every marquee move full-page-invalidated the main canvas, and
    // every drag move rewrote the selected elements' geometry AND full-page-
    // redrew (which also busted the stroke geometry cache each sample).
    private Rectangle? _marqueeRect;
    private Polyline? _marqueeLasso;
    // PDF text-selection highlight — one overlay Path (a rectangle per selected
    // line). The old Win2D fills forced a full-page redraw per pointer move
    // while dragging across text.
    private XamlPath? _textSelHighlight;

    // Drag ghost: the selection rendered ONCE into a CanvasImageSource; each
    // move only repositions/transforms this image (composition-only work).
    // The originals are hidden from the main canvas via _hiddenElementIds and
    // the model mutates ONCE on release.
    private Image? _dragGhost;
    private Rectangle? _dragGhostBorder;
    private RotateTransform? _dragGhostRotate;
    private RotateTransform? _dragGhostBorderRotate;
    private Bbox _dragGhostBbox;          // padded doc-space rect the ghost image covers
    private Vector2 _dragStartPoint;
    private Vector2 _lastDragPoint;
    private bool _dragMoved;
    // True when this move drag began by pressing inside an ALREADY-committed
    // selection (not by freshly selecting an element). A tap here — press and
    // release with negligible travel — dismisses the selection instead of
    // moving it, so the user can click to deselect without the sidebar button.
    private bool _dragTapDismisses;
    // Squared page-space travel below which a move counts as a tap, not a drag.
    private const float TapDismissThresholdSq = 16f;   // 4px
    private HashSet<string>? _hiddenElementIds;

    // Snapshot of original element transforms at drag start, keyed by id
    private readonly Dictionary<string, Bbox> _origImageBoxes = new();
    private readonly Dictionary<string, Bbox> _origTextBoxes = new();
    private readonly Dictionary<string, (float X1, float Y1, float X2, float Y2)> _origShapePoints = new();
    private readonly Dictionary<string, double> _origImageRot = new();
    private readonly Dictionary<string, double> _origTextRot = new();

    private const float HandleSize = 14f;
    private const float RotateHandleOffset = 36f;

    public PageCanvas(
        NotePage page,
        TemplateSettings template,
        EditorContext ctx,
        Func<ITool?> toolProvider,
        PalmRejection palmRejection,
        PenButtonRouter buttonRouter,
        Dictionary<PenButtonAction, ITool> buttonTools,
        PageRenderer renderer)
    {
        Page = page;
        PageTemplate = template;
        Context = ctx;
        ToolProvider = toolProvider;
        PalmRejection = palmRejection;
        ButtonRouter = buttonRouter;
        ButtonTools = buttonTools;
        Renderer = renderer;

        _canvas = new CanvasVirtualControl
        {
            Width = page.Width,
            Height = page.Height,
            ClearColor = Colors.White
        };
        _canvas.RegionsInvalidated += OnMainRegionsInvalidated;
        _canvas.CreateResources += OnCreateResources;

        _overlay = new Canvas
        {
            Width = page.Width,
            Height = page.Height,
            IsHitTestVisible = true,
            Background = null
        };

        var root = new Grid { Width = page.Width, Height = page.Height };
        root.Children.Add(_canvas);
        root.Children.Add(_overlay);

        Width = page.Width;
        Height = page.Height;
        Content = root;
        Background = new SolidColorBrush(Colors.White);
        CornerRadius = new CornerRadius(6);
        IsTabStop = true;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerCaptureLost += (_, __) =>
        {
            // Lost without a release (see ResetInputState) → end the gesture cleanly.
            if (_capturedPointerId is not null) ResetInputState();
            _capturedPointerId = null;
            Canvas.SetZIndex(this, 0);
        };
        // Eraser range preview: leave the page → hide; press/lift → re-evaluate
        // (e.g. the eraser button was released mid-hover).
        PointerExited += (_, e) => { if (_capturedPointerId != e.Pointer.PointerId) { HideEraserCursor(); HideLaserDot(); } };
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) => UpdateEraserCursor(e)), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) => UpdateEraserCursor(e)), handledEventsToo: true);
        DoubleTapped += OnDoubleTapped;
        Tapped += OnTapped;
        SetupExtensionHandles();
    }

    private bool _panning;
    private bool _inMomentaryPan;
    private Windows.Foundation.Point _panLastScreen;
    private int _penEventsSinceDown;

    // Raised when a finger touch or middle-mouse drag momentarily overrides the
    // active tool with the Hand/Pan tool. EditorPage listens to keep the toolbar
    // visually in sync — Pan button lights up while held, previous tool returns
    // on release.
    public event EventHandler<ToolKind>? MomentaryToolStart;
    public event EventHandler? MomentaryToolEnd;
    public event EventHandler? RectSelectionCompleted;

    // Raised when a live stroke starts (true) and when it is committed,
    // cancelled or dropped (false). EditorPage pauses the toolbar's live glass
    // refraction in between so drawing doesn't re-blur it every frame.
    public event EventHandler<bool>? LiveInkActiveChanged;

    // Raised when a Move drag is released. InkCanvasControl uses it to hand the
    // selection to whichever page the pointer ended over (a cross-page move);
    // the handler sets Transferred=true if it did, so this page skips its own
    // in-page translate (which would push the elements off the bottom edge).
    public event EventHandler<SelectionDropEventArgs>? SelectionDragDropped;

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

    // PDF-text selection state (Hand tool + left-mouse drag, browser-style).
    private bool _selectingText;
    private TextRun? _selectAnchor;
    private TextRun? _selectCursor;
    private readonly List<TextRun> _selectedTextRuns = new();

    // ── Input-handler containment ──────────────────────────────────────────────
    // Pointer/gesture handlers run on the Windows App SDK native input dispatch
    // path. If a managed exception escapes one, the input manager
    // (Microsoft.InputStateManager.dll) raises a __fastfail (0xc0000409) that NO
    // managed handler can catch — a hard crash with no dialog and no crash.log
    // (the reported "opens and silently crashes"). Every handler below is wrapped
    // so a tool / hit-test / stale-pointer bug is logged and survived instead of
    // taking the process — and the user's unsaved work — down with it.
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        try { OnDoubleTappedCore(sender, e); }
        catch (Exception ex) { App.LogError(ex, "OnDoubleTapped"); }
    }
    private void OnDoubleTappedCore(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Context.EditingSuspended) return;
        var pos = e.GetPosition(this);
        var p = new Vector2((float)pos.X, (float)pos.Y);
        foreach (var t in Page.Texts.AsEnumerable().Reverse())
        {
            if (Bbox.Of(t).Contains(p.X, p.Y))
            {
                BeginInlineTextEdit(t.Id);
                e.Handled = true;
                return;
            }
        }
    }

    private bool TrySelectElementAt(Vector2 p)
    {
        // Hit-test in reverse render order: text (topmost) → strokes → shapes → images.
        foreach (var t in Page.Texts.AsEnumerable().Reverse())
        {
            if (Bbox.Of(t).Contains(p.X, p.Y))
            {
                Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
                Context.SelectedImageIds.Clear();  Context.SelectedTextIds.Clear();
                Context.SelectedTextIds.Add(t.Id);
                Context.SelectionChanged?.Invoke();
                return true;
            }
        }
        foreach (var s in Page.Strokes.AsEnumerable().Reverse())
        {
            if (Bbox.Of(s).Contains(p.X, p.Y))
            {
                Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
                Context.SelectedImageIds.Clear();  Context.SelectedTextIds.Clear();
                Context.SelectedStrokeIds.Add(s.Id);
                Context.SelectionChanged?.Invoke();
                return true;
            }
        }
        foreach (var sh in Page.Shapes.AsEnumerable().Reverse())
        {
            if (Bbox.Of(sh).Contains(p.X, p.Y))
            {
                Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
                Context.SelectedImageIds.Clear();  Context.SelectedTextIds.Clear();
                Context.SelectedShapeIds.Add(sh.Id);
                Context.SelectionChanged?.Invoke();
                return true;
            }
        }
        foreach (var im in Page.Images.AsEnumerable().Reverse())
        {
            if (Bbox.Of(im).Contains(p.X, p.Y))
            {
                Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
                Context.SelectedImageIds.Clear();  Context.SelectedTextIds.Clear();
                Context.SelectedImageIds.Add(im.Id);
                Context.SelectionChanged?.Invoke();
                return true;
            }
        }
        return false;
    }

    public void RequestRedraw()
    {
        _contentBoundsDirty = true;
        _canvas.Invalidate();
    }

    // Called by tools on every pen sample. Live strokes render through
    // LiveInkSurface: a virtualized DComp surface updated with dirty-rect
    // draws — pixels persist between samples, so only the small changed
    // region is redrawn and recomposited (see LiveInkSurface).
    public void RequestLiveRedraw()
    {
        if (_suppressLiveSync) return;   // OnPointerMovedCore syncs once per event
        SyncMarqueeOverlay();
        var s = Context.ActiveStroke;

        // Shape tool: show a rubber-band XAML preview while dragging
        if (Context.ActiveShape is { } shape && ReferenceEquals(Context.CurrentPage, Page))
        {
            TearDownLiveInk();
            EnsureLiveShapePreview(shape);
            SyncLiveShapePreview(shape);
            return;
        }
        TearDownLiveShapePreview();

        if (s is null || !ReferenceEquals(Context.CurrentPage, Page))
        {
            TearDownLiveInk();
            return;
        }

        ScheduleLiveInkSync();
    }

    private void ScheduleLiveInkSync()
    {
        if (_liveInkSyncPending) return;
        _liveInkSyncPending = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRenderingLiveInk;
    }

    private void CancelLiveInkSync()
    {
        if (!_liveInkSyncPending) return;
        _liveInkSyncPending = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingLiveInk;
    }

    // Raised on the UI thread before each frame is composed, after the input
    // queued for that frame has been handled — so drawing here adds no latency.
    // Unsubscribes immediately: a standing Rendering handler would keep XAML
    // rendering every frame even while the pen is idle.
    private void OnRenderingLiveInk(object? sender, object e)
    {
        CancelLiveInkSync();
        try
        {
            // Re-check: the stroke may have been committed, cancelled or moved
            // to another page since the sync was scheduled.
            var s = Context.ActiveStroke;
            if (s is null || Context.ActiveShape is not null || !ReferenceEquals(Context.CurrentPage, Page))
                return;
            SyncLiveInkSurface(s);
        }
        catch (Exception ex) { App.LogError(ex, "OnRenderingLiveInk"); }
    }

    private void TearDownLiveInk()
    {
        CancelLiveInkSync();
        _liveInk?.Clear();
        ReportLiveInkActive();
    }

    private void ReportLiveInkActive()
    {
        bool active = _liveInk?.IsActive == true;
        if (active == _liveInkActive) return;
        _liveInkActive = active;
        LiveInkActiveChanged?.Invoke(this, active);
    }

    private void SyncLiveInkSurface(Stroke s)
    {
        _liveInk ??= new LiveInkSurface(_overlay);
        // Match the committed canvas's backing density so live ink stays as
        // crisp as everything under it at the current zoom tier.
        float raster = (float)(XamlRoot?.RasterizationScale ?? 1.0);
        _liveInk.SetScale(raster * Math.Clamp(_canvas.DpiScale, 1f, 4f));

        // Same color mapping as the XAML backends: highlighter ink is capped
        // to a translucent alpha regardless of the picked color.
        byte alpha = s.Color.A;
        if (s.Kind == StrokeKind.Highlighter)
            alpha = (byte)Math.Min(140, alpha == 255 ? 110 : alpha);
        _liveInk.Sync(s, Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B),
            (float)Page.Width, (float)Page.Height);
        ReportLiveInkActive();   // also catches a device-lost Clear inside Sync
    }

    // Prediction lead: extrapolate up to TWO samples ahead (never less than
    // one, extra lead capped at ~16 px) to cover more of the input→composition
    // pipeline latency without visible overshoot when the pen turns.
    internal static float PredictionScale(float dx, float dy)
    {
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.01f) return 1f;
        return Math.Clamp(16f / len, 1f, 2f);
    }

    private void EnsureLiveShapePreview(ShapeElement s)
    {
        if (_liveShapePreview is not null && _liveShapeId == s.Id) return;
        TearDownLiveShapePreview();

        var brush = new SolidColorBrush(s.Color);
        var dash = new DoubleCollection { 6, 4 };

        UIElement elem = s.Kind switch
        {
            // Tape: the strip's color, see-through while it's being dragged out.
            ShapeKind.Tape => new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(150, s.Color.R, s.Color.G, s.Color.B)),
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeDashArray = dash,
                RadiusX = 4, RadiusY = 4,
                IsHitTestVisible = false
            },
            ShapeKind.Rectangle => new Rectangle
            {
                Stroke = brush,
                StrokeThickness = s.StrokeWidth,
                StrokeDashArray = dash,
                IsHitTestVisible = false
            },
            ShapeKind.Ellipse => new Ellipse
            {
                Stroke = brush,
                StrokeThickness = s.StrokeWidth,
                StrokeDashArray = dash,
                IsHitTestVisible = false
            },
            // Arrow / axes / grid: their segments as one path, rebuilt on each move.
            ShapeKind.Arrow or ShapeKind.Axes or ShapeKind.Grid => new XamlPath
            {
                Stroke = brush,
                StrokeThickness = s.StrokeWidth,
                StrokeDashArray = dash,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            },
            // Triangle uses a Polyline so the preview can show all three vertices.
            ShapeKind.Triangle => new Polyline
            {
                Stroke = brush,
                StrokeThickness = s.StrokeWidth,
                StrokeDashArray = dash,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            },
            _ => new Line
            {
                Stroke = brush,
                StrokeThickness = s.StrokeWidth,
                StrokeDashArray = dash,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            }
        };

        _overlay.Children.Add(elem);
        _liveShapePreview = elem;
        _liveShapeId = s.Id;
        StartDashMarch((Shape)elem);
    }

    // Marching-ants dash animation on the preview outline: while a shape is
    // still tentative (hold-to-snap preview or shape-tool rubber band), the
    // dashes crawl along the outline, signalling "not committed yet — keep
    // holding or lift to confirm". One dash period (6+4, in thickness units)
    // per cycle keeps the loop seamless. Dependent animation is unavoidable
    // for StrokeDashOffset; it's a single overlay shape, so the per-frame
    // cost is negligible.
    private Storyboard? _liveShapeDashAnim;

    private void StartDashMarch(Shape shape)
    {
        var anim = new DoubleAnimation
        {
            From = 0,
            To = -10,                                   // one full dash period
            Duration = new Duration(TimeSpan.FromMilliseconds(800)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, shape);
        Storyboard.SetTargetProperty(anim, "StrokeDashOffset");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
        _liveShapeDashAnim = sb;
    }

    private void SyncLiveShapePreview(ShapeElement s)
    {
        if (_liveShapePreview is null) return;
        float x = Math.Min(s.X1, s.X2);
        float y = Math.Min(s.Y1, s.Y2);
        float w = Math.Max(1f, Math.Abs(s.X2 - s.X1));
        float h = Math.Max(1f, Math.Abs(s.Y2 - s.Y1));

        switch (_liveShapePreview)
        {
            case Rectangle rect:
                Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
                rect.Width = w; rect.Height = h;
                SyncPreviewRotation(rect, s, w, h);
                break;
            case Ellipse ell:
                Canvas.SetLeft(ell, x); Canvas.SetTop(ell, y);
                ell.Width = w; ell.Height = h;
                SyncPreviewRotation(ell, s, w, h);
                break;
            case Polyline poly when s.Kind == ShapeKind.Triangle:
                poly.Points = new PointCollection
                {
                    new(s.X1, s.Y1), new(s.X2, s.Y2), new(s.X3, s.Y3), new(s.X1, s.Y1)
                };
                break;
            case Line line:
                line.X1 = s.X1; line.Y1 = s.Y1;
                line.X2 = s.X2; line.Y2 = s.Y2;
                break;
            case XamlPath path:
            {
                var g = new GeometryGroup();
                foreach (var (a, b) in ShapeGeometry.Segments(s))
                    g.Children.Add(new LineGeometry { StartPoint = new(a.X, a.Y), EndPoint = new(b.X, b.Y) });
                path.Data = g;
                break;
            }
        }
    }

    // Recognized-snap previews can carry a rotation (rect/ellipse only); the
    // shape tool's rubber-band drags never do, so the common path stays a
    // null-check.
    private static void SyncPreviewRotation(FrameworkElement elem, ShapeElement s, float w, float h)
    {
        if (s.Rotation == 0f)
        {
            if (elem.RenderTransform is RotateTransform) elem.RenderTransform = null;
            return;
        }
        if (elem.RenderTransform is not RotateTransform rt)
            elem.RenderTransform = rt = new RotateTransform();
        rt.Angle = s.Rotation;
        rt.CenterX = w * 0.5;
        rt.CenterY = h * 0.5;
    }

    private void TearDownLiveShapePreview()
    {
        if (_liveShapePreview is null) return;
        _liveShapeDashAnim?.Stop();
        _liveShapeDashAnim = null;
        _overlay.Children.Remove(_liveShapePreview);
        _liveShapePreview = null;
        _liveShapeId = null;
    }

    // Shows/updates/removes the select-tool marquee (rect or lasso) as XAML
    // overlay shapes driven by Context.SelectionRect/SelectionLasso. Runs on
    // every live sync; all no-op paths are cheap null checks.
    private void SyncMarqueeOverlay()
    {
        bool isCurrent = ReferenceEquals(Context.CurrentPage, Page);

        if (isCurrent && Context.SelectionRect is { } r)
        {
            if (_marqueeRect is null)
            {
                _marqueeRect = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(180, 91, 107, 255)),
                    StrokeThickness = 1.4,
                    Fill = new SolidColorBrush(Color.FromArgb(40, 91, 107, 255)),
                    IsHitTestVisible = false
                };
                _overlay.Children.Add(_marqueeRect);
            }
            Canvas.SetLeft(_marqueeRect, r.X);
            Canvas.SetTop(_marqueeRect, r.Y);
            _marqueeRect.Width = r.W;
            _marqueeRect.Height = r.H;
        }
        else if (_marqueeRect is not null)
        {
            _overlay.Children.Remove(_marqueeRect);
            _marqueeRect = null;
        }

        if (isCurrent && Context.SelectionLasso is { Count: > 1 } poly)
        {
            if (_marqueeLasso is null)
            {
                _marqueeLasso = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(220, 91, 107, 255)),
                    StrokeThickness = 1.4,
                    IsHitTestVisible = false
                };
                _overlay.Children.Add(_marqueeLasso);
            }
            var pc = _marqueeLasso.Points;
            if (pc.Count > poly.Count) pc.Clear();   // new gesture reused the element
            for (int i = pc.Count; i < poly.Count; i++)
                pc.Add(new Windows.Foundation.Point(poly[i].X, poly[i].Y));
        }
        else if (_marqueeLasso is not null)
        {
            _overlay.Children.Remove(_marqueeLasso);
            _marqueeLasso = null;
        }
    }

    public void SetTemplate(TemplateSettings template)
    {
        PageTemplate = template;
        _canvas.Invalidate();
    }

    // Render the main Win2D backing store at a higher pixel density so it stays
    // crisp when the parent ScrollViewer zooms in. (The live stroke is XAML
    // shapes — DComp renders those at native resolution at any zoom.)
    public void SetDpiScale(float scale)
    {
        if (scale < 1f) scale = 1f;
        if (Math.Abs(_canvas.DpiScale - scale) > 0.01f) _canvas.DpiScale = scale;
    }

    private void OnCreateResources(CanvasVirtualControl sender, CanvasCreateResourcesEventArgs args)
    {
        // Device (re)created — any hi-res crop belongs to the old device.
        ClearHiResBackground();
        args.TrackAsyncAction(LoadResourcesAsync(sender).AsAsyncAction());
    }

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
        var bgRect = new Windows.Foundation.Rect(bgLeft, 0, bgW, Page.Height);

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
        double pageH = Page.Height;
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

    private async Task LoadResourcesAsync(ICanvasResourceCreator dev)
    {
        if (Page.BackgroundPng is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(Page.BackgroundPng);
                _bgBitmap = await CanvasBitmap.LoadAsync(dev, ms.AsRandomAccessStream());
            }
            catch { _bgBitmap = null; }
        }
        foreach (var img in Page.Images.ToList())
            await EnsureImageLoadedAsync(dev, img);
    }

    private async Task EnsureImageLoadedAsync(ICanvasResourceCreator dev, ImageElement img)
    {
        if (_imageCache.ContainsKey(img.Id)) return;
        try
        {
            using var ms = new MemoryStream(img.PngData);
            var bmp = await CanvasBitmap.LoadAsync(dev, ms.AsRandomAccessStream());
            _imageCache[img.Id] = bmp;
        }
        catch { }
    }

    private void OnMainRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        if (Page.Images.Count > 0)
        {
            var missing = Page.Images.Where(i => !_imageCache.ContainsKey(i.Id)).ToList();
            if (missing.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var img in missing) await EnsureImageLoadedAsync(sender, img);
                    DispatcherQueue.TryEnqueue(() => sender.Invalidate());
                });
            }
        }

        // One drawing session per dirty region — Win2D clips drawing to that
        // region's tiles, and the region is passed as `clip` so DrawPage also
        // skips geometry building for ink that can't touch it.
        // The hi-res crop's rect doesn't account for the extension-preview
        // shift, so it sits out during a preview drag.
        bool extPreview = _previewExtLeft != 0 || _previewExtRight != 0;
        foreach (var region in args.InvalidatedRegions)
        {
            using var ds = sender.CreateDrawingSession(region);
            // This runs inside a native Win2D callback. An unhandled managed
            // exception here (a bad element that slips past the renderer's guards,
            // a lost device, …) propagates through native code and hard-kills the
            // process with no dialog and no catchable stack — the "opens and
            // silently crashes" symptom. Contain it per-region: log it, draw what
            // we can, and keep the app (and the user's unsaved work) alive.
            try
            {
                Renderer.DrawPage(ds, sender, Page, PageTemplate, _bgBitmap, _imageCache, Context.EditingTextId,
                                  previewExtLeft: _previewExtLeft, previewExtRight: _previewExtRight,
                                  clip: region,
                                  hiResBackground: extPreview ? null : _bgHiRes,
                                  hiResRect: !extPreview && _bgHiRes is not null ? _bgHiResRect : null,
                                  skipElementIds: _hiddenElementIds,
                                  fadedElementIds: _eraseHoverIds);

                // Draw the slice of any neighbouring page's content that spills
                // across the seam into this region, so a straddling element
                // shows on both pages. Skipped during an extension preview
                // (element coords are shifted then).
                if (!extPreview) DrawNeighborBleed(ds, sender, region);

                // The select-tool marquee is a XAML overlay element (see
                // SyncMarqueeOverlay) — nothing to draw here. Selection chrome
                // is skipped while a drag is in flight: the overlay ghost shows
                // the selection, and drawing chrome here would force a main-
                // canvas repaint on every drag move.
                if (ReferenceEquals(Context.CurrentPage, Page)
                    && _drag == DragMode.None && HasCommittedSelection())
                {
                    DrawSelectionVisuals(ds);
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex, $"DrawPage failed (page index {Page.Index})");
            }
        }

        // A DpiScale change blanked the canvas behind the freeze-frame overlay;
        // its regions are now redrawn and committed, so the overlay can go.
        // Low priority lets this batch present before the overlay disappears.
        if (_unfreezeAfterDraw)
        {
            _unfreezeAfterDraw = false;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UnfreezeViewport);
        }
    }

    // Draws the portion of each vertical neighbour's content that overflows the
    // shared edge into this page's `region`. Cheap in the common case: a
    // neighbour with no overflow toward this page (cached) is skipped outright,
    // and it only engages when the dirty region actually reaches the seam band.
    private void DrawNeighborBleed(CanvasDrawingSession ds, ICanvasResourceCreator dev, Windows.Foundation.Rect region)
    {
        // Previous page (above) → bleeds down into this page's top band.
        if (PrevPageCanvas is { } prev)
        {
            float overflow = prev.BottomOverflow;
            if (overflow > 0.5f && region.Y < overflow && TryNeighborOffset(prev, out float ox, out float oy))
                DrawOneNeighborBleed(ds, dev, prev, region, ox, oy);
        }
        // Next page (below) → bleeds up into this page's bottom band.
        if (NextPageCanvas is { } next)
        {
            float overflow = next.TopOverflow;
            if (overflow > 0.5f && region.Y + region.Height > (float)Page.Height - overflow
                && TryNeighborOffset(next, out float ox, out float oy))
                DrawOneNeighborBleed(ds, dev, next, region, ox, oy);
        }
    }

    // Bakes the same cross-page bleed into a full-page export raster. Uses the
    // exact page-height offset (NOT the on-screen TransformToVisual, which folds
    // in the ~4px seam separator) so a straddling stroke continues seamlessly
    // across the page boundary in the paginated PDF.
    private void DrawNeighborBleedForExport(CanvasDrawingSession ds, ICanvasResourceCreator dev)
    {
        var region = new Windows.Foundation.Rect(0, 0, Page.Width, Page.Height);
        if (PrevPageCanvas is { } prev && prev.BottomOverflow > 0.5f)
            DrawOneNeighborBleed(ds, dev, prev, region, 0f, -(float)prev.Page.Height);
        if (NextPageCanvas is { } next && next.TopOverflow > 0.5f)
            DrawOneNeighborBleed(ds, dev, next, region, 0f, (float)Page.Height);
    }

    // On-screen offset that maps a neighbour's local coords into this page's
    // coords (pure vertical translation — pages share the left edge and zoom).
    private bool TryNeighborOffset(PageCanvas neighbor, out float ox, out float oy)
    {
        try
        {
            var o = neighbor.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
            ox = (float)o.X; oy = (float)o.Y;
            return true;
        }
        catch { ox = 0; oy = 0; return false; }
    }

    private void DrawOneNeighborBleed(CanvasDrawingSession ds, ICanvasResourceCreator dev, PageCanvas neighbor,
                                      Windows.Foundation.Rect region, float ox, float oy)
    {
        // Cull the neighbour against the same region expressed in ITS coords.
        var neighborClip = new Windows.Foundation.Rect(region.X - ox, region.Y - oy, region.Width, region.Height);
        var prev = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(ox, oy) * prev;
        try
        {
            // overlayOnly: elements only — no white fill / template / background
            // over this page. Win2D clips drawing to `region`, so nothing spills
            // outside the seam band. Honour the neighbour's own hidden set so an
            // element being dragged off it isn't drawn twice.
            Renderer.DrawPage(ds, dev, neighbor.Page, neighbor.PageTemplate, null,
                              neighbor.ImageCacheView, Context.EditingTextId, overlayOnly: true,
                              clip: neighborClip, skipElementIds: neighbor.HiddenElementIdsView);
        }
        finally { ds.Transform = prev; }
    }

    // ── Freeze-frame across DpiScale changes ────────────────────────────────
    // Changing CanvasVirtualControl.DpiScale recreates its virtual surface,
    // which blanks to ClearColor (white) until RegionsInvalidated re-renders —
    // a visible flash on every backing-tier change while zooming. Freeze draws
    // the visible crop into a CanvasImageSource overlay BEFORE the change at
    // the OLD backing scale (pixel-identical to what was on screen); the
    // overlay is removed right after the first post-change redraw commits.
    private Image? _freezeImage;
    private bool _unfreezeAfterDraw;

    // CanvasVirtualControl.Device THROWS (ArgumentException, "does not currently
    // have a CanvasDevice") rather than returning null until the control has
    // created its device — e.g. canvases just recreated by InkCanvasControl.
    // Rebuild (adding a page) that haven't loaded/drawn yet. Callers treat null
    // as "skip" or fall back to the shared device.
    private CanvasDevice? TryGetCanvasDevice()
    {
        try { return _canvas.ReadyToDraw ? _canvas.Device : null; }
        catch { return null; }
    }

    public void FreezeViewportForDpiChange(Windows.Foundation.Rect viewRect, float oldScale)
    {
        UnfreezeViewport();
        var device = TryGetCanvasDevice();
        if (device is null || viewRect.Width < 1 || viewRect.Height < 1) return;
        try
        {
            float dpi = 96f * Math.Max(1f, oldScale);
            var snapshot = new CanvasImageSource(device, (float)viewRect.Width, (float)viewRect.Height, dpi);
            bool extPreview = _previewExtLeft != 0 || _previewExtRight != 0;
            using (var ds = snapshot.CreateDrawingSession(Colors.White))
            {
                ds.Transform = Matrix3x2.CreateTranslation((float)-viewRect.X, (float)-viewRect.Y);
                Renderer.DrawPage(ds, device, Page, PageTemplate, _bgBitmap, _imageCache, Context.EditingTextId,
                                  previewExtLeft: _previewExtLeft, previewExtRight: _previewExtRight,
                                  clip: viewRect,
                                  hiResBackground: extPreview ? null : _bgHiRes,
                                  hiResRect: !extPreview && _bgHiRes is not null ? _bgHiResRect : null,
                                  skipElementIds: _hiddenElementIds,
                                  fadedElementIds: _eraseHoverIds);
            }
            _freezeImage = new Image
            {
                Source = snapshot,
                Width = viewRect.Width,
                Height = viewRect.Height,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_freezeImage, viewRect.X);
            Canvas.SetTop(_freezeImage, viewRect.Y);
            Canvas.SetZIndex(_freezeImage, 15);
            _overlay.Children.Add(_freezeImage);
            _unfreezeAfterDraw = true;

            // Safety net: if no redraw ever lands (page scrolled out mid-change,
            // device lost, …), don't leave a stale snapshot covering the page.
            var current = _freezeImage;
            _ = Task.Delay(1000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_freezeImage, current)) UnfreezeViewport();
            }));
        }
        catch { UnfreezeViewport(); }
    }

    public void UnfreezeViewport()
    {
        _unfreezeAfterDraw = false;
        if (_freezeImage is null) return;
        _overlay.Children.Remove(_freezeImage);
        _freezeImage = null;
    }

    // Called from PenTool/HighlighterTool via ctx.CommitStrokeAt on stroke
    // commit. Repaints only the just-committed stroke's bbox on the main
    // canvas instead of the whole page — that's the difference between a
    // sub-ms commit and one that blocks the UI thread for ~10–20 ms.
    public void CommitStrokeRedraw(Bbox bbox)
    {
        _contentBoundsDirty = true;
        // If the change reaches past an edge, the neighbour the content bleeds
        // into must repaint (it reads this page's now-stale overflow). Do this
        // before clamping the rect to this page's bounds.
        if (bbox.Y < 0) PrevPageCanvas?.RequestRedraw();
        if (bbox.Y + bbox.H > Page.Height) NextPageCanvas?.RequestRedraw();

        double x = Math.Max(0, bbox.X);
        double y = Math.Max(0, bbox.Y);
        double w = Math.Min(Page.Width,  bbox.X + bbox.W) - x;
        double h = Math.Min(Page.Height, bbox.Y + bbox.H) - y;
        if (w <= 0 || h <= 0) return;
        _canvas.Invalidate(new Windows.Foundation.Rect(x, y, w, h));
    }

    private void DrawSelectionVisuals(CanvasDrawingSession ds)
    {
        var fill = Color.FromArgb(38, 91, 107, 255);
        var line = Color.FromArgb(220, 91, 107, 255);

        foreach (var id in Context.SelectedStrokeIds)
        {
            var stroke = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (stroke is null) continue;
            var b = Bbox.Of(stroke);
            ds.FillRectangle(b.X, b.Y, b.W, b.H, fill);
            ds.DrawRectangle(b.X, b.Y, b.W, b.H, line, 1f);
        }
        foreach (var id in Context.SelectedShapeIds)
        {
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            var b = Bbox.Of(sh);
            ds.FillRectangle(b.X, b.Y, b.W, b.H, fill);
            ds.DrawRectangle(b.X, b.Y, b.W, b.H, line, 1f);
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            var b = Bbox.Of(t);
            ds.FillRectangle(b.X, b.Y, b.W, b.H, fill);
            ds.DrawRectangle(b.X, b.Y, b.W, b.H, line, 1f);
        }
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            var b = Bbox.Of(im);
            ds.FillRectangle(b.X, b.Y, b.W, b.H, fill);
            ds.DrawRectangle(b.X, b.Y, b.W, b.H, line, 1f);
        }

        if (!SelectionBbox(out var bbox)) return;

        var pad = 8f;
        var outer = new Bbox(bbox.X - pad, bbox.Y - pad, bbox.W + pad * 2, bbox.H + pad * 2);
        var dash = new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
        {
            DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Dash
        };
        ds.DrawRectangle(outer.X, outer.Y, outer.W, outer.H, Color.FromArgb(255, 91, 107, 255), 1.5f, dash);

        // Corner handles + (single-element only) rotate handle
        var handles = HandlePositions(outer);
        foreach (var (_, pos) in handles)
        {
            ds.FillCircle(pos, HandleSize * 0.5f, Colors.White);
            ds.DrawCircle(pos, HandleSize * 0.5f, Color.FromArgb(255, 91, 107, 255), 2f);
        }
        if (TryRotateHandle(outer, out var rotPos))
        {
            var top = new Vector2(outer.X + outer.W * 0.5f, outer.Y);
            ds.DrawLine(top, rotPos, Color.FromArgb(255, 91, 107, 255), 1.5f);
            ds.FillCircle(rotPos, HandleSize * 0.55f, Colors.White);
            ds.DrawCircle(rotPos, HandleSize * 0.55f, Color.FromArgb(255, 91, 107, 255), 2f);
        }
    }

    private List<(DragMode mode, Vector2 pos)> HandlePositions(Bbox b)
    {
        return new()
        {
            (DragMode.ResizeNW, new Vector2(b.X, b.Y)),
            (DragMode.ResizeNE, new Vector2(b.Right, b.Y)),
            (DragMode.ResizeSW, new Vector2(b.X, b.Bottom)),
            (DragMode.ResizeSE, new Vector2(b.Right, b.Bottom)),
        };
    }

    private bool TryRotateHandle(Bbox outer, out Vector2 pos)
    {
        pos = default;
        // Only enable rotate when exactly one element is selected.
        var count = Context.SelectedStrokeIds.Count + Context.SelectedShapeIds.Count
                  + Context.SelectedTextIds.Count + Context.SelectedImageIds.Count;
        if (count != 1) return false;
        pos = new Vector2(outer.X + outer.W * 0.5f, outer.Y - RotateHandleOffset);
        return true;
    }

    private DragMode HitTestHandles(Vector2 p, out Bbox outer)
    {
        outer = default;
        if (!SelectionBbox(out var bbox)) return DragMode.None;
        outer = new Bbox(bbox.X - 8, bbox.Y - 8, bbox.W + 16, bbox.H + 16);
        var r2 = (HandleSize * 0.7f) * (HandleSize * 0.7f);
        foreach (var (mode, pos) in HandlePositions(outer))
            if ((p - pos).LengthSquared() <= r2) return mode;
        if (TryRotateHandle(outer, out var rotPos))
            if ((p - rotPos).LengthSquared() <= r2) return DragMode.Rotate;
        return DragMode.None;
    }

    private bool HasCommittedSelection() =>
        (Context.SelectionRect is null && Context.SelectionLasso is null) &&
        (Context.SelectedStrokeIds.Count + Context.SelectedShapeIds.Count
         + Context.SelectedTextIds.Count + Context.SelectedImageIds.Count) > 0;

    public bool SelectionBbox(out Bbox b)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;
        foreach (var id in Context.SelectedStrokeIds)
        {
            var s = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (s is null) continue;
            var bb = Bbox.Of(s);
            if (bb.X < minX) minX = bb.X; if (bb.Y < minY) minY = bb.Y;
            if (bb.Right > maxX) maxX = bb.Right; if (bb.Bottom > maxY) maxY = bb.Bottom;
            any = true;
        }
        foreach (var id in Context.SelectedShapeIds)
        {
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            var bb = Bbox.Of(sh);
            if (bb.X < minX) minX = bb.X; if (bb.Y < minY) minY = bb.Y;
            if (bb.Right > maxX) maxX = bb.Right; if (bb.Bottom > maxY) maxY = bb.Bottom;
            any = true;
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            var bb = Bbox.Of(t);
            if (bb.X < minX) minX = bb.X; if (bb.Y < minY) minY = bb.Y;
            if (bb.Right > maxX) maxX = bb.Right; if (bb.Bottom > maxY) maxY = bb.Bottom;
            any = true;
        }
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            var bb = Bbox.Of(im);
            if (bb.X < minX) minX = bb.X; if (bb.Y < minY) minY = bb.Y;
            if (bb.Right > maxX) maxX = bb.Right; if (bb.Bottom > maxY) maxY = bb.Bottom;
            any = true;
        }
        b = new Bbox(minX, minY, maxX - minX, maxY - minY);
        return any;
    }

    private bool PointInSelectionBounds(Vector2 p)
    {
        if (!SelectionBbox(out var b)) return false;
        return p.X >= b.X - 8 && p.X <= b.Right + 8 && p.Y >= b.Y - 8 && p.Y <= b.Bottom + 8;
    }

    private Vector2 ToPageSpace(PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this).Position;
        return new Vector2((float)pt.X, (float)pt.Y);
    }

    // ── Ruler overlay ─────────────────────────────────────────────────────────
    //
    // The ruler body is a Win2D CanvasControl that draws real centimetre tick
    // marks and labels.  The rotate handle is a small Ellipse at the right tip.
    // Both live in _overlay (document coordinates, scales with zoom).
    //
    // Page pixels-per-mm is derived from the A4 assumption: page.Width / 210.
    // (A4 is 1240 px @ ~150 dpi → 1 mm ≈ 5.905 px.)
    //
    // Dragging the body translates; dragging the handle rotates.
    // EnsureRulerOverlay() is lazy — elements are created only on first show.

    private const float RulerLength     = 640f;   // document pixels
    private const float RulerBodyHeight = 36f;    // enough room for labels
    private const float RotHandleR      = 11f;

    private CanvasControl? _rulerCanvas;
    private Ellipse?       _rulerRotHandle;
    private uint?          _rulerDragPtrId;
    private uint?          _rulerRotPtrId;
    private Vector2        _rulerDragOffset;

    private void EnsureRulerOverlay()
    {
        if (_rulerCanvas is not null) return;

        // ── Ruler body ────────────────────────────────────────────────────────
        _rulerCanvas = new CanvasControl
        {
            Width                 = RulerLength,
            Height                = RulerBodyHeight,
            IsHitTestVisible      = true,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform       = new RotateTransform(),
        };
        Canvas.SetZIndex(_rulerCanvas, 20);
        _rulerCanvas.Draw              += OnRulerCanvasDraw;
        _rulerCanvas.PointerPressed    += OnRulerBodyPointerPressed;
        _rulerCanvas.PointerMoved      += OnRulerBodyPointerMoved;
        _rulerCanvas.PointerReleased   += OnRulerBodyPointerReleased;
        _rulerCanvas.PointerCaptureLost += (_, __) => _rulerDragPtrId = null;

        // ── Rotate handle ─────────────────────────────────────────────────────
        _rulerRotHandle = new Ellipse
        {
            Width            = RotHandleR * 2,
            Height           = RotHandleR * 2,
            Fill             = new SolidColorBrush(Color.FromArgb(230, 50, 90, 205)),
            Stroke           = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            StrokeThickness  = 1.5,
            IsHitTestVisible = true,
        };
        Canvas.SetZIndex(_rulerRotHandle, 21);
        _rulerRotHandle.PointerPressed    += OnRotHandlePointerPressed;
        _rulerRotHandle.PointerMoved      += OnRotHandlePointerMoved;
        _rulerRotHandle.PointerReleased   += OnRotHandlePointerReleased;
        _rulerRotHandle.PointerCaptureLost += (_, __) => _rulerRotPtrId = null;

        _overlay.Children.Add(_rulerCanvas);
        _overlay.Children.Add(_rulerRotHandle);

        // Tell PenTool where the drawing edge is.
        Context.RulerHalfHeight = RulerBodyHeight / 2f;
    }

    private void OnRulerCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float w = RulerLength;
        float h = RulerBodyHeight;

        var bgColor     = Color.FromArgb(220, 218, 232, 255);
        var borderColor = Color.FromArgb(255, 45, 85, 200);
        var tickColor   = Color.FromArgb(210, 45, 85, 200);
        var labelColor  = Color.FromArgb(200, 25, 60, 170);

        // Background
        ds.FillRoundedRectangle(0f, 0f, w, h, 4f, 4f, bgColor);

        // Tick marks — 1 mm = (Page.Width / 210) document pixels for A4.
        float pxPerMm = (float)(Page.Width / 210.0);
        int   totalMm = (int)(w / pxPerMm) + 1;

        using var fmt = new CanvasTextFormat
        {
            FontFamily          = "Segoe UI",
            FontSize            = 8f,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment   = CanvasVerticalAlignment.Center,
        };

        for (int mm = 0; mm <= totalMm; mm++)
        {
            float x = mm * pxPerMm;
            if (x > w + 0.5f) break;

            bool isCm  = (mm % 10 == 0);
            bool is5mm = (mm % 5  == 0) && !isCm;

            // Tick heights (from the top and bottom edge inward)
            float tickH = isCm ? h * 0.42f : is5mm ? h * 0.26f : h * 0.15f;
            float thick = isCm ? 1.3f       : is5mm ? 1.0f      : 0.75f;

            ds.DrawLine(x, 1.5f,       x, tickH,       tickColor, thick);  // from top
            ds.DrawLine(x, h - 1.5f,   x, h - tickH,   tickColor, thick);  // from bottom

            // Centimetre labels
            if (isCm && mm > 0)
            {
                int cm = mm / 10;
                ds.DrawText(
                    cm.ToString(),
                    new Windows.Foundation.Rect(x - 9, h / 2f - 7, 18, 14),
                    labelColor,
                    fmt);
            }
        }

        // Border drawn last so it sits on top of tick ends
        ds.DrawRoundedRectangle(0.75f, 0.75f, w - 1.5f, h - 1.5f, 4f, 4f, borderColor, 1.5f);
    }

    // Called by InkCanvasControl whenever RulerVisible/X/Y/Angle changes.
    public void SyncRulerOverlay()
    {
        bool show = Context.RulerVisible;

        if (!show)
        {
            if (_rulerCanvas is not null)
            {
                _rulerCanvas.Visibility     = Visibility.Collapsed;
                _rulerRotHandle!.Visibility = Visibility.Collapsed;
            }
            return;
        }

        EnsureRulerOverlay();

        _rulerCanvas!.Visibility    = Visibility.Visible;
        _rulerRotHandle!.Visibility = Visibility.Visible;

        // Centre the body at (RulerX, RulerY), rotate in place.
        Canvas.SetLeft(_rulerCanvas, Context.RulerX - RulerLength     / 2f);
        Canvas.SetTop (_rulerCanvas, Context.RulerY - RulerBodyHeight  / 2f);
        ((RotateTransform)_rulerCanvas.RenderTransform).Angle = Context.RulerAngle;

        // Rotate handle at the right tip.
        float rad = Context.RulerAngle * MathF.PI / 180f;
        float hx  = Context.RulerX + MathF.Cos(rad) * (RulerLength / 2f);
        float hy  = Context.RulerY + MathF.Sin(rad) * (RulerLength / 2f);
        Canvas.SetLeft(_rulerRotHandle, hx - RotHandleR);
        Canvas.SetTop (_rulerRotHandle, hy - RotHandleR);
    }

    // ── Ruler drag (translate) ────────────────────────────────────────────────

    private void OnRulerBodyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerDragPtrId.HasValue) return;
        var pos          = e.GetCurrentPoint(_overlay).Position;
        _rulerDragOffset = new Vector2(
            (float)pos.X - Context.RulerX,
            (float)pos.Y - Context.RulerY);
        _rulerDragPtrId = e.Pointer.PointerId;
        _rulerCanvas!.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnRulerBodyPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerDragPtrId != e.Pointer.PointerId) return;
        var pos        = e.GetCurrentPoint(_overlay).Position;
        Context.RulerX = (float)pos.X - _rulerDragOffset.X;
        Context.RulerY = (float)pos.Y - _rulerDragOffset.Y;
        Context.RulerChanged?.Invoke();
        e.Handled = true;
    }

    private void OnRulerBodyPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerDragPtrId != e.Pointer.PointerId) return;
        _rulerDragPtrId = null;
        _rulerCanvas!.ReleasePointerCaptures();
        e.Handled = true;
    }

    // ── Ruler rotate ──────────────────────────────────────────────────────────

    private void OnRotHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerRotPtrId.HasValue) return;
        _rulerRotPtrId = e.Pointer.PointerId;
        _rulerRotHandle!.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnRotHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerRotPtrId != e.Pointer.PointerId) return;
        var pos            = e.GetCurrentPoint(_overlay).Position;
        Context.RulerAngle = MathF.Atan2(
            (float)pos.Y - Context.RulerY,
            (float)pos.X - Context.RulerX) * 180f / MathF.PI;
        Context.RulerChanged?.Invoke();
        e.Handled = true;
    }

    private void OnRotHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerRotPtrId != e.Pointer.PointerId) return;
        _rulerRotPtrId = null;
        _rulerRotHandle!.ReleasePointerCaptures();
        e.Handled = true;
    }

    private float PressureOf(PointerRoutedEventArgs e)
        => PressureOf(e.GetCurrentPoint(this).Properties.Pressure, e.Pointer.PointerDeviceType);

    private float PressureOf(float rawPressure, Microsoft.UI.Input.PointerDeviceType device)
    {
        if (rawPressure <= 0) return 0.5f;
        var s = App.Services.Settings.Current;
        if (!s.PressureEnabled) return 0.5f;
        if (device == Microsoft.UI.Input.PointerDeviceType.Pen)
        {
            var v = (rawPressure - (float)s.MinPressure) * (float)s.PressureMultiplier;
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            return (float)YuNotes.Input.PressureCurve.Evaluate(v, s);
        }
        return rawPressure;
    }

    private bool ShouldDropPenSample(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen) return false;
        var s = App.Services.Settings.Current;
        if (s.PressureEnabled)
        {
            var raw = e.GetCurrentPoint(this).Properties.Pressure;
            if (raw > 0 && raw < (float)s.MinPressure) return true;
        }
        if (s.IgnoreFirstEventsEnabled && _penEventsSinceDown < s.IgnoreFirstEventsCount) return true;
        return false;
    }

    private static readonly PanTool s_middleMousePan = new();

    private ITool? PickTool(PointerRoutedEventArgs e)
    {
        // Middle mouse button → momentary pan, overriding whatever tool is active.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsMiddleButtonPressed) return s_middleMousePan;
        }
        var act = ButtonRouter.Resolve(e);
        if (act != PenButtonAction.None && ButtonTools.TryGetValue(act, out var t)) return t;
        return ToolProvider();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try { OnPointerPressedCore(sender, e); }
        catch (Exception ex) { App.LogError(ex, "OnPointerPressed"); }
    }
    private void OnPointerPressedCore(object sender, PointerRoutedEventArgs e)
    {
        if (!PalmRejection.Accept(e)) { e.Handled = true; return; }
        // Fingers never trigger our tools — the outer ScrollViewer's DirectManipulation
        // handles touch pan + pinch-zoom; we just refuse to draw.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            // A finger tap outside a committed selection dismisses it, matching the
            // pen "click outside to clear" behavior below. Touch is owned by the
            // ScrollViewer (we never capture it), so we act on press: only clear
            // when the point is outside the box and off the handles, leaving a
            // finger pan/pinch that starts inside the selection alone.
            if (!Context.EditingSuspended && HasCommittedSelection())
            {
                var tp = ToPageSpace(e);
                if (!PointInSelectionBounds(tp) && HitTestHandles(tp, out _) == DragMode.None)
                    ClearSelection();
            }
            return;
        }
        // Editing suspended (e.g. during a save): reject pen/mouse drawing so the
        // document can't be mutated, but scroll/zoom (handled by the ScrollViewer)
        // still works.
        if (Context.EditingSuspended) return;
        // A new press while a previous gesture is still marked in progress means
        // that gesture was interrupted without a release — finish it first.
        if (_panning || _selectingText || _lasering || _tapeGesture || _drag != DragMode.None || _activeTool is not null)
            ResetInputState();
        Focus(FocusState.Pointer);
        var p = ToPageSpace(e);
        _lastToolPoint = p;
        Context.CurrentPage = Page;
        _penEventsSinceDown = 0;

        var tool = PickTool(e);

        // Tape: a press on a strip is a tap that reveals or covers it (see
        // TapeTapTarget for which tools this applies to). Decided on release,
        // so a drag that starts on tape can still pan with the hand tool.
        if (TapeTapTarget(tool, p, e) is { } tape)
        {
            _tapeGesture = true;
            _tapeTap = tape;
            _tapeTapStart = p;
            _tapeTapPans = tool is PanTool;
            CapturePointer(e.Pointer);
            _capturedPointerId = e.Pointer.PointerId;
            e.Handled = true;
            return;
        }

        // Laser pointer: points, never edits — so it skips selection handling
        // and leaves any committed selection alone.
        if (tool is LaserTool)
        {
            BeginLaser(p);
            CapturePointer(e.Pointer);
            _capturedPointerId = e.Pointer.PointerId;
            e.Handled = true;
            return;
        }

        // Hand tool: left-mouse drag selects PDF text (browser-style); everything else
        // (touch / pen / middle-click) pans the parent ScrollViewer.
        if (tool is PanTool)
        {
            var ttype = e.Pointer.PointerDeviceType;
            var props = e.GetCurrentPoint(this).Properties;
            bool isLeftMouseDrag = ttype == Microsoft.UI.Input.PointerDeviceType.Mouse
                                   && props.IsLeftButtonPressed
                                   && !props.IsMiddleButtonPressed;
            if (isLeftMouseDrag)
            {
                // Fresh left-click clears any prior text selection (like a browser does).
                ClearTextSelection();
                if (Page.TextRuns.Count > 0)
                {
                    _selectAnchor = FindNearestTextRun(p);
                    _selectCursor = _selectAnchor;
                    _selectingText = true;
                    RecomputeTextSelection();
                    CapturePointer(e.Pointer);
                    _capturedPointerId = e.Pointer.PointerId;
                    e.Handled = true;
                    SyncTextSelectionOverlay();
                    return;
                }
                // No selectable text on this page — fall through to pan.
            }

            // Distinguish momentary pan (middle-mouse override) from the user having
            // Pan already chosen — only the former needs a toolbar UI toggle.
            bool isMomentary = ToolProvider() is not PanTool
                               && ttype == Microsoft.UI.Input.PointerDeviceType.Mouse
                               && props.IsMiddleButtonPressed;

            _panning = true;
            _panLastScreen = e.GetCurrentPoint(null).Position;
            CapturePointer(e.Pointer);
            _capturedPointerId = e.Pointer.PointerId;
            if (isMomentary && !_inMomentaryPan)
            {
                _inMomentaryPan = true;
                MomentaryToolStart?.Invoke(this, ToolKind.Pan);
            }
            e.Handled = true;
            return;
        }

        // Selection interactions take priority regardless of the active tool.
        if (HasCommittedSelection())
        {
            var handle = HitTestHandles(p, out _);
            if (handle != DragMode.None)
            {
                StartDrag(handle, p);
                CapturePointer(e.Pointer);
                _capturedPointerId = e.Pointer.PointerId;
                e.Handled = true;
                return;
            }
            if (PointInSelectionBounds(p))
            {
                // Pressed inside the existing selection: drag to move, or — if it
                // turns out to be a tap — dismiss the selection on release.
                StartDrag(DragMode.Move, p, tapDismisses: true);
                CapturePointer(e.Pointer);
                _capturedPointerId = e.Pointer.PointerId;
                e.Handled = true;
                return;
            }
            ClearSelection();
        }

        // With Select tool active, a single tap on an element selects it
        // (so the user can then drag/scale/rotate it via the handles).
        if (tool is LassoTool or RectSelectTool)
        {
            if (TrySelectElementAt(p))
            {
                // StartDrag's ghost setup repaints the selection region itself;
                // chrome appears on release via EndDrag.
                StartDrag(DragMode.Move, p);
                CapturePointer(e.Pointer);
                _capturedPointerId = e.Pointer.PointerId;
                e.Handled = true;
                return;
            }
        }

        _activeTool = tool;
        if (_activeTool is null) return;
        CapturePointer(e.Pointer);
        _capturedPointerId = e.Pointer.PointerId;
        _activeTool.OnPointerDown(Context, p, PressureOf(e));
        // No _canvas.Invalidate() here — drawing tools (PenTool / HighlighterTool)
        // already call ctx.InvalidateLive() to refresh just the active-stroke overlay.
        // Eraser/Text/etc. invalidate the main canvas through ctx.Mutated when they
        // actually mutate committed content. Forcing a full main-canvas redraw on
        // every pen-down would re-rasterise the high-DPI PDF background needlessly
        // and shows up as a visible delay before the first ink appears.
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            UpdateEraserCursor(e);
            UpdateLaserDot(e);
            OnPointerMovedCore(sender, e);
        }
        catch (Exception ex) { App.LogError(ex, "OnPointerMoved"); }
    }

    // ── Eraser range preview ──────────────────────────────────────────────────
    //
    // A circle the size of the eraser follows the pointer whenever erasing is
    // what a press would do — the eraser tool is active, or the pen's eraser
    // button/tip is held — both while hovering and while erasing, so you can
    // see what will be removed before touching the page.

    private Microsoft.UI.Xaml.Shapes.Ellipse? _eraserCursor;

    private void UpdateEraserCursor(PointerRoutedEventArgs e)
    {
        bool show = false;
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch
            && !Context.EditingSuspended)
        {
            // Mid-gesture, the tool picked at press time decides; when hovering,
            // the same rules as a press (eraser button, eraser tip, active tool).
            var tool = _capturedPointerId == e.Pointer.PointerId ? _activeTool : PickTool(e);
            show = tool is EraserTool;
        }
        if (!show) { HideEraserCursor(); return; }

        // Stroke mode, hovering (not erasing): fade what a press here would remove.
        if (!Context.EraserPixelMode && _capturedPointerId != e.Pointer.PointerId
            && PickTool(e) is EraserTool hoverEraser)
        {
            _eraseHoverScratch.Clear();
            hoverEraser.CollectStrokeModeHits(Page, ToPageSpace(e), Context.EraserWidth, _eraseHoverScratch);
            SetEraseHover(_eraseHoverScratch);
        }
        else SetEraseHover(null);

        if (_eraserCursor is null)
        {
            _eraserCursor = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                // Dark ring + faint fill: readable on white paper, ink and PDFs.
                Stroke = new SolidColorBrush(Color.FromArgb(170, 40, 44, 56)),
                StrokeThickness = 1.2,
                Fill = new SolidColorBrush(Color.FromArgb(28, 91, 107, 255)),
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(_eraserCursor, 10000);
            _overlay.Children.Add(_eraserCursor);
        }
        var d = Math.Max(2f, Context.EraserWidth);
        var p = ToPageSpace(e);
        _eraserCursor.Width = d;
        _eraserCursor.Height = d;
        Canvas.SetLeft(_eraserCursor, p.X - d / 2);
        Canvas.SetTop(_eraserCursor, p.Y - d / 2);
        _eraserCursor.Visibility = Visibility.Visible;
    }

    // ── Tape (hide and reveal) ─────────────────────────────────────────────────
    //
    // Tape strips are ShapeElements of kind Tape. Tapping one flips it between
    // covered and revealed; that state lives in Context.RevealedTapeIds only,
    // so it never dirties the note or adds an undo step. Finger taps work with
    // any tool (touch never draws); pen and mouse taps are routed here by
    // TapeTapTarget.

    private bool _tapeGesture;           // a pen/mouse press that started on tape
    private ShapeElement? _tapeTap;      // the strip it will toggle (null = lapsed)
    private Vector2 _tapeTapStart;
    private bool _tapeTapPans;           // hand tool: a drag turns into a pan
    private const float TapeTapSlop = 8f;

    // Topmost tape strip under p (rotation-aware), or null.
    private ShapeElement? TapeAt(Vector2 p)
    {
        for (int i = Page.Shapes.Count - 1; i >= 0; i--)
        {
            var s = Page.Shapes[i];
            if (s.Kind != ShapeKind.Tape) continue;
            float x = MathF.Min(s.X1, s.X2), y = MathF.Min(s.Y1, s.Y2);
            float hw = MathF.Abs(s.X2 - s.X1) * 0.5f, hh = MathF.Abs(s.Y2 - s.Y1) * 0.5f;
            var q = p - new Vector2(x + hw, y + hh);
            if (s.Rotation != 0f && float.IsFinite(s.Rotation))
            {
                float a = -s.Rotation * MathF.PI / 180f, cos = MathF.Cos(a), sin = MathF.Sin(a);
                q = new Vector2(q.X * cos - q.Y * sin, q.X * sin + q.Y * cos);
            }
            if (MathF.Abs(q.X) <= hw && MathF.Abs(q.Y) <= hh) return s;
        }
        return null;
    }

    // Which pen/mouse presses on tape count as a reveal/cover tap:
    //  - select tools and the eraser never (so tape can be selected, moved,
    //    resized, recolored and erased like any shape);
    //  - the tape tool and the hand tool: any strip, covered or revealed;
    //  - every other tool (pen, highlighter, text, shapes, laser…): only a
    //    covered strip, so you can still write on top of revealed content.
    // Presses on a live selection's box or handles keep their usual meaning.
    private ShapeElement? TapeTapTarget(ITool? tool, Vector2 p, PointerRoutedEventArgs e)
    {
        if (tool is LassoTool or RectSelectTool or EraserTool or null) return null;
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            && e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return null;
        if (HasCommittedSelection() && (PointInSelectionBounds(p) || HitTestHandles(p, out _) != DragMode.None)) return null;
        var tape = TapeAt(p);
        if (tape is null) return null;
        bool anyStrip = tool is PanTool || tool is ShapeTool { ShapeKind: ShapeKind.Tape };
        return anyStrip || !Context.RevealedTapeIds.Contains(tape.Id) ? tape : null;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            if (e.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch) return;   // pen/mouse: press path
            var pos = e.GetPosition(this);
            if (TapeAt(new Vector2((float)pos.X, (float)pos.Y)) is { } tape)
            {
                ToggleTape(tape);
                e.Handled = true;
            }
        }
        catch (Exception ex) { App.LogError(ex, "OnTapped"); }
    }

    public void ToggleTape(ShapeElement tape)
    {
        if (!Context.RevealedTapeIds.Remove(tape.Id)) Context.RevealedTapeIds.Add(tape.Id);
        var b = Bbox.Of(tape);
        CommitStrokeRedraw(new Bbox(b.X - 4, b.Y - 4, b.W + 8, b.H + 8));
    }

    // ── Laser pointer ─────────────────────────────────────────────────────────
    //
    // Each press draws a glowing trail into the XAML overlay (never the
    // document). Trails stay while the presenter keeps pointing; a short pause
    // after the last lift fades them all out together. Pressing again during
    // the pause or the fade brings the earlier trails back, so a circle and an
    // arrow drawn one after the other stay on screen as one gesture.
    //
    // Dot mode (Context.LaserMode) instead shows a large glowing dot with a
    // short comet tail of the last fraction of a second, and removes both the
    // moment the pen lifts — pointing only, nothing lingers.

    private bool _lasering;
    private bool _laserDotMode;                    // mode of the gesture in flight
    private readonly List<Canvas> _laserTrails = new();
    private Polyline[]? _laserLines;               // lines of the trail being drawn
    private Canvas? _laserDotTail;                 // dot mode: the comet tail
    private readonly Queue<long> _laserTailTimes = new();
    private const long LaserTailMs = 140;
    private Ellipse? _laserDot;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _laserHoldTimer;
    private Storyboard? _laserFade;
    private static readonly TimeSpan LaserHold = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan LaserFadeTime = TimeSpan.FromMilliseconds(600);
    private const int LaserMaxPoints = 1500;

    private void BeginLaser(Vector2 p)
    {
        _laserDotMode = Context.LaserMode == LaserMode.Dot;
        if (!_laserDotMode)
        {
            _laserHoldTimer?.Stop();
            if (_laserFade is not null) { _laserFade.Stop(); _laserFade = null; }   // revives fading trails
        }

        var c = Context.CurrentColor;
        static Polyline Line(byte a, Color rgb, double thickness) => new()
        {
            Stroke = new SolidColorBrush(Color.FromArgb(a, rgb.R, rgb.G, rgb.B)),
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        // Wide soft glow, solid core, bright centre: reads as light on any paper.
        _laserLines = new[]
        {
            Line(60, c, 16),
            Line(255, c, 5),
            Line(210, Colors.White, 1.6),
        };
        var trail = new Canvas { IsHitTestVisible = false };
        foreach (var l in _laserLines) trail.Children.Add(l);
        Canvas.SetZIndex(trail, 9000);
        _overlay.Children.Add(trail);
        if (_laserDotMode)
        {
            // The tail is thinner than a trail and owned by this gesture only.
            _laserLines[0].StrokeThickness = 12;
            _laserLines[1].StrokeThickness = 4;
            _laserDotTail = trail;
            _laserTailTimes.Clear();
        }
        else _laserTrails.Add(trail);
        _lasering = true;
        // Two points so a tap without movement still shows a round dot.
        AddLaserPoint(p);
        AddLaserPoint(p);
    }

    private void AddLaserPoint(Vector2 p)
    {
        if (_laserLines is null) return;
        var pt = new Windows.Foundation.Point(p.X, p.Y);
        foreach (var l in _laserLines)
        {
            l.Points.Add(pt);
            if (l.Points.Count > LaserMaxPoints) l.Points.RemoveAt(0);
        }
        if (_laserDotMode)
        {
            // Keep only the last LaserTailMs of movement (at least the tip).
            long now = Environment.TickCount64;
            _laserTailTimes.Enqueue(now);
            while (_laserTailTimes.Count > 2 && now - _laserTailTimes.Peek() > LaserTailMs)
            {
                _laserTailTimes.Dequeue();
                foreach (var l in _laserLines) if (l.Points.Count > 2) l.Points.RemoveAt(0);
            }
        }
        UpdateLaserDot(p, true);
    }

    private void EndLaser()
    {
        _lasering = false;
        _laserLines = null;
        if (_laserDotMode)
        {
            // Dot mode: nothing stays behind once the pen lifts.
            if (_laserDotTail is not null) _overlay.Children.Remove(_laserDotTail);
            _laserDotTail = null;
            _laserTailTimes.Clear();
            HideLaserDot();
            return;
        }
        if (_laserHoldTimer is null)
        {
            _laserHoldTimer = DispatcherQueue.CreateTimer();
            _laserHoldTimer.IsRepeating = false;
            _laserHoldTimer.Interval = LaserHold;
            _laserHoldTimer.Tick += (_, __) => FadeLaserTrails();
        }
        _laserHoldTimer.Stop();
        _laserHoldTimer.Start();
    }

    private void FadeLaserTrails()
    {
        if (_lasering || _laserTrails.Count == 0) return;
        var trails = _laserTrails.ToList();
        var sb = new Storyboard();
        foreach (var t in trails)
        {
            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(LaserFadeTime),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(anim, t);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
        }
        sb.Completed += (_, __) =>
        {
            if (_laserFade != sb) return;   // revived by a new press
            _laserFade = null;
            foreach (var t in trails) { _overlay.Children.Remove(t); _laserTrails.Remove(t); }
        };
        _laserFade = sb;
        sb.Begin();
    }

    // Removes every trail at once (leaving the tool or presentation mode).
    public void ClearLaser()
    {
        _laserHoldTimer?.Stop();
        if (_laserFade is not null) { _laserFade.Stop(); _laserFade = null; }
        foreach (var t in _laserTrails) _overlay.Children.Remove(t);
        _laserTrails.Clear();
        if (_laserDotTail is not null) _overlay.Children.Remove(_laserDotTail);
        _laserDotTail = null;
        _laserTailTimes.Clear();
        _laserLines = null;
        _lasering = false;
        HideLaserDot();
    }

    // Hover dot: shows where the laser will land before pressing.
    private void UpdateLaserDot(PointerRoutedEventArgs e)
    {
        if (_lasering) return;   // the trail moves the dot itself
        bool show = e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch
                    && !Context.EditingSuspended
                    && _capturedPointerId is null
                    && PickTool(e) is LaserTool;
        if (!show) { HideLaserDot(); return; }
        UpdateLaserDot(ToPageSpace(e), true);
    }

    private void UpdateLaserDot(Vector2 p, bool show)
    {
        if (!show) { HideLaserDot(); return; }
        var c = Context.CurrentColor;
        if (_laserDot is null)
        {
            _laserDot = new Ellipse { Width = 12, Height = 12, StrokeThickness = 4, IsHitTestVisible = false };
            Canvas.SetZIndex(_laserDot, 9500);
            _overlay.Children.Add(_laserDot);
        }
        if (_laserDot.Fill is not SolidColorBrush fb || fb.Color.R != c.R || fb.Color.G != c.G || fb.Color.B != c.B)
        {
            _laserDot.Fill = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
            _laserDot.Stroke = new SolidColorBrush(Color.FromArgb(80, c.R, c.G, c.B));
        }
        // Pressing in dot mode grows the dot into the pointer itself.
        double d = _lasering && _laserDotMode ? 22 : 12;
        if (_laserDot.Width != d)
        {
            _laserDot.Width = d;
            _laserDot.Height = d;
            _laserDot.StrokeThickness = d > 12 ? 7 : 4;
        }
        Canvas.SetLeft(_laserDot, p.X - d / 2);
        Canvas.SetTop(_laserDot, p.Y - d / 2);
        _laserDot.Visibility = Visibility.Visible;
    }

    private void HideLaserDot()
    {
        if (_laserDot is not null) _laserDot.Visibility = Visibility.Collapsed;
    }

    private void HideEraserCursor()
    {
        if (_eraserCursor is not null) _eraserCursor.Visibility = Visibility.Collapsed;
        SetEraseHover(null);
    }

    // Strokes/shapes currently drawn faded as "would be erased" (null = none).
    private HashSet<string>? _eraseHoverIds;
    private readonly HashSet<string> _eraseHoverScratch = new();

    // Updates the faded set, repainting only the elements whose look changed.
    private void SetEraseHover(ISet<string>? ids)
    {
        bool none = ids is null || ids.Count == 0;
        if (none && _eraseHoverIds is null) return;
        if (!none && _eraseHoverIds is not null && _eraseHoverIds.SetEquals(ids!)) return;

        Bbox? dirty = null;
        void Touch(string id)
        {
            Bbox b;
            float pad;
            if (Page.Strokes.FirstOrDefault(x => x.Id == id) is { } st) { b = Bbox.Of(st); pad = st.Width; }
            else if (Page.Shapes.FirstOrDefault(x => x.Id == id) is { } sh) { b = Bbox.Of(sh); pad = sh.StrokeWidth; }
            else return;
            b = new Bbox(b.X - pad, b.Y - pad, b.W + pad * 2, b.H + pad * 2);
            dirty = dirty is null ? b : Bbox.Union(dirty.Value, b);
        }
        if (_eraseHoverIds is not null) foreach (var id in _eraseHoverIds) if (none || !ids!.Contains(id)) Touch(id);
        if (!none) foreach (var id in ids!) if (_eraseHoverIds?.Contains(id) != true) Touch(id);

        _eraseHoverIds = none ? null : new HashSet<string>(ids!);
        if (dirty is { } d) CommitStrokeRedraw(d);
    }
    private void OnPointerMovedCore(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId is null || e.Pointer.PointerId != _capturedPointerId) return;
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen) _penEventsSinceDown++;
        if (ShouldDropPenSample(e)) return;
        if (_panning)
        {
            var sp = e.GetCurrentPoint(null).Position;
            var dx = _panLastScreen.X - sp.X;
            var dy = _panLastScreen.Y - sp.Y;
            Context.RequestPan?.Invoke((float)dx, (float)dy);
            _panLastScreen = sp;
            return;
        }
        var p = ToPageSpace(e);
        if (_selectingText)
        {
            _selectCursor = FindNearestTextRun(p);
            RecomputeTextSelection();
            SyncTextSelectionOverlay();   // overlay-only; no main-canvas redraw
            return;
        }
        if (_drag != DragMode.None)
        {
            // Ghost-only update — no main-canvas invalidate until release.
            ContinueDrag(p);
            return;
        }
        if (_tapeGesture)
        {
            // Moved too far for a tap: the hand tool turns it into a pan;
            // any other tool just lets the tap lapse.
            if (_tapeTap is not null && Vector2.DistanceSquared(p, _tapeTapStart) > TapeTapSlop * TapeTapSlop)
            {
                _tapeTap = null;
                if (_tapeTapPans)
                {
                    _tapeGesture = false;
                    _panning = true;
                    _panLastScreen = e.GetCurrentPoint(null).Position;
                }
            }
            return;
        }
        if (_lasering)
        {
            var pts = e.GetIntermediatePoints(this);
            if (pts is { Count: > 1 })
                for (int i = pts.Count - 1; i >= 0; i--)
                    AddLaserPoint(new Vector2((float)pts[i].Position.X, (float)pts[i].Position.Y));
            else AddLaserPoint(p);
            return;
        }
        if (_activeTool is null) return;

        // Recover any pointer samples Windows coalesced between this event and the
        // last one we saw, but throw away samples that are visually redundant
        // (closer than a quarter of the stroke width from the previous one). High
        // sample-rate pens can otherwise produce hundreds of points per second,
        // and rendering Catmull-Rom across all of them re-rasterises the entire
        // stroke each frame — costs that compound badly at high zoom.
        var device = e.Pointer.PointerDeviceType;
        var inter = e.GetIntermediatePoints(this);
        _suppressLiveSync = true;
        try
        {
            if (inter is { Count: > 1 })
            {
                for (int i = inter.Count - 1; i >= 0; i--)
                {
                    var pp = inter[i];
                    if (!pp.IsInContact) continue;
                    var raw = pp.Properties.Pressure;
                    if (device == Microsoft.UI.Input.PointerDeviceType.Pen
                        && App.Services.Settings.Current.PressureEnabled
                        && raw > 0 && raw < (float)App.Services.Settings.Current.MinPressure)
                        continue;
                    var pt = new Vector2((float)pp.Position.X, (float)pp.Position.Y);
                    if (TooCloseToLast(pt)) continue;
                    _activeTool.OnPointerMove(Context, pt, PressureOf(raw, device));
                }
            }
            else if (!TooCloseToLast(p))
            {
                _activeTool.OnPointerMove(Context, p, PressureOf(e));
            }
            _lastToolPoint = p;
        }
        finally
        {
            _suppressLiveSync = false;
        }
        // One overlay sync for all samples this event delivered. Tools handle
        // the main canvas themselves (ctx.Invalidate/InvalidateRect on commit),
        // so no full main-canvas invalidate here.
        Context.InvalidateLive?.Invoke();
    }

    private bool TooCloseToLast(Vector2 p)
    {
        var s = Context.ActiveStroke;
        if (s is null || s.Points.Count == 0) return false;
        var last = s.Points[^1];
        var dx = p.X - last.X;
        var dy = p.Y - last.Y;
        var threshold = s.Width * 0.25f;          // quarter the stroke width
        return dx * dx + dy * dy < threshold * threshold;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        try { OnPointerReleasedCore(sender, e); }
        catch (Exception ex) { App.LogError(ex, "OnPointerReleased"); }
    }
    private void OnPointerReleasedCore(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId is null || e.Pointer.PointerId != _capturedPointerId) return;

        // While panning via middle mouse, ignore button-release events that fire because
        // the user clicked-and-released a different button (e.g., a left click during a
        // middle-mouse pan). Only end the pan when the middle button itself is released.
        if (_panning && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsMiddleButtonPressed) return;
        }

        bool skipFullRedraw = false;
        if (_panning)
        {
            _panning = false;
            if (_inMomentaryPan)
            {
                _inMomentaryPan = false;
                MomentaryToolEnd?.Invoke(this, EventArgs.Empty);
            }
            // Panning only moves the viewport; page content is unchanged.
            skipFullRedraw = true;
        }
        else if (_selectingText)
        {
            // Text selection lives entirely on the overlay — the main canvas
            // never changed during the gesture.
            _selectingText = false;
            skipFullRedraw = true;
        }
        else if (_lasering)
        {
            // Overlay-only: the document never changed.
            EndLaser();
            skipFullRedraw = true;
        }
        else if (_tapeGesture)
        {
            if (_tapeTap is { } t) ToggleTape(t);
            _tapeGesture = false;
            _tapeTap = null;
            skipFullRedraw = true;   // ToggleTape repaints just the strip
        }
        else if (_drag != DragMode.None)
        {
            // Applies the transform to the model once and repaints only the
            // affected region (plus Mutated for dirty/history).
            EndDrag();
            skipFullRedraw = true;
        }
        else
        {
            var wasSelectTool = _activeTool is LassoTool or RectSelectTool;
            var wasRectSelect = _activeTool is RectSelectTool;
            // Pen/highlighter repaint themselves on lift (bbox-only CommitStrokeAt)
            // — the full-page invalidate below would re-tessellate every stroke
            // on the page after every stroke. The eraser invalidates each erased
            // region as it goes; the select tools invalidate the selection's own
            // region on commit; the shape tool its committed shape's bbox.
            skipFullRedraw = _activeTool is PenTool or HighlighterTool or EraserTool
                             or RectSelectTool or LassoTool or ShapeTool;
            _activeTool?.OnPointerUp(Context, ToPageSpace(e), PressureOf(e));
            TearDownLiveShapePreview();   // no-op for non-shape tools
            if (wasSelectTool) Context.SelectionChanged?.Invoke();
            if (wasRectSelect && Context.LastDrawnRectSelection.HasValue)
                RectSelectionCompleted?.Invoke(this, EventArgs.Empty);
        }
        // Clear first: ReleasePointerCapture raises PointerCaptureLost, whose
        // handler would otherwise treat this finished gesture as interrupted.
        _capturedPointerId = null;
        _activeTool = null;
        ReleasePointerCapture(e.Pointer);
        if (!skipFullRedraw) _canvas.Invalidate();
    }

    // ── Interrupted-gesture recovery ──────────────────────────────────────────
    //
    // When capture is lost mid-gesture (Alt+Tab, a touchpad swipe, the window
    // being deactivated) no PointerReleased arrives, so the gesture never ended:
    // _panning / _drag / _selectingText / _activeTool stayed set and the next
    // pen stroke was routed into that dead gesture instead of drawing ink —
    // "the pen stops working until I switch tools". This finishes whatever was
    // in flight (committing ink drawn so far) and returns to a clean idle state.
    // Safe to call any time; a no-op when nothing is in progress.
    private Vector2 _lastToolPoint;

    public void ResetInputState()
    {
        try
        {
            if (_panning)
            {
                _panning = false;
                if (_inMomentaryPan)
                {
                    _inMomentaryPan = false;
                    MomentaryToolEnd?.Invoke(this, EventArgs.Empty);
                }
            }
            _selectingText = false;
            _tapeGesture = false;
            _tapeTap = null;
            if (_lasering) EndLaser();
            if (_drag != DragMode.None) EndDrag();
            if (_activeTool is { } tool)
            {
                _activeTool = null;
                var wasSelect = tool is LassoTool or RectSelectTool;
                tool.OnPointerUp(Context, _lastToolPoint, 1f);
                TearDownLiveShapePreview();
                if (wasSelect) Context.SelectionChanged?.Invoke();
            }
            _capturedPointerId = null;
            HideEraserCursor();
            HideLaserDot();
        }
        catch (Exception ex) { App.LogError(ex, "ResetInputState"); }
    }

    private void StartDrag(DragMode mode, Vector2 p, bool tapDismisses = false)
    {
        _drag = mode;
        _dragTapDismisses = tapDismisses;
        SelectionBbox(out _dragStartBbox);
        _dragStartCenter = _dragStartBbox.Center;
        _origImageBoxes.Clear();
        _origTextBoxes.Clear();
        _origShapePoints.Clear();
        _origImageRot.Clear();
        _origTextRot.Clear();
        // For Rotate, snapshot existing rotations and bounds.
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            _origImageBoxes[id] = Bbox.Of(im);
            _origImageRot[id] = im.Rotation;
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            _origTextBoxes[id] = Bbox.Of(t);
            _origTextRot[id] = t.Rotation;
        }
        foreach (var id in Context.SelectedShapeIds)
        {
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            _origShapePoints[id] = (sh.X1, sh.Y1, sh.X2, sh.Y2);
        }
        _dragStartRotation = Math.Atan2(p.Y - _dragStartCenter.Y, p.X - _dragStartCenter.X);
        _dragStartPoint = p;
        _lastDragPoint = p;
        _dragMoved = false;
        // Paint this page (and its drag ghost) above sibling pages so a ghost
        // dragged past the page edge stays visible over the next page instead
        // of being occluded by it. Restored in EndDrag.
        if (mode == DragMode.Move) Canvas.SetZIndex(this, 1);
        BeginDragGhost();
    }

    // During the drag only the overlay ghost is transformed — pure composition
    // work, no Win2D redraw and no model mutation. The model changes once, in
    // EndDrag. (Mutating per move also re-baked every selected stroke's cached
    // geometry each sample, and repeated ApplyResize over already-scaled
    // stroke points compounded the scale.)
    private void ContinueDrag(Vector2 p)
    {
        _lastDragPoint = p;
        if ((p - _dragStartPoint).LengthSquared() > 0.25f) _dragMoved = true;
        if (_dragGhost is null) return;   // ghost failed — release still applies the drag

        switch (_drag)
        {
            case DragMode.Move:
            {
                float tx = p.X - _dragStartPoint.X;
                float ty = p.Y - _dragStartPoint.Y;
                PositionGhost(_dragGhostBbox.X + tx, _dragGhostBbox.Y + ty,
                              _dragGhostBbox.W, _dragGhostBbox.H);
                break;
            }
            case DragMode.ResizeNW:
            case DragMode.ResizeNE:
            case DragMode.ResizeSW:
            case DragMode.ResizeSE:
            {
                var nb = ResizedBox(_drag, p);
                float sx = nb.W / Math.Max(1e-3f, _dragStartBbox.W);
                float sy = nb.H / Math.Max(1e-3f, _dragStartBbox.H);
                PositionGhost(
                    nb.X + (_dragGhostBbox.X - _dragStartBbox.X) * sx,
                    nb.Y + (_dragGhostBbox.Y - _dragStartBbox.Y) * sy,
                    _dragGhostBbox.W * sx,
                    _dragGhostBbox.H * sy);
                break;
            }
            case DragMode.Rotate:
            {
                var ang = Math.Atan2(p.Y - _dragStartCenter.Y, p.X - _dragStartCenter.X);
                var deltaDeg = (ang - _dragStartRotation) * 180.0 / Math.PI;
                _dragGhostRotate!.Angle = deltaDeg;
                _dragGhostBorderRotate!.Angle = deltaDeg;
                break;
            }
        }
    }

    // New selection bbox implied by dragging `mode`'s corner to `p`.
    private Bbox ResizedBox(DragMode mode, Vector2 p)
    {
        var orig = _dragStartBbox;
        float left = orig.X, top = orig.Y, right = orig.Right, bottom = orig.Bottom;
        switch (mode)
        {
            case DragMode.ResizeNW: left = p.X; top = p.Y; break;
            case DragMode.ResizeNE: right = p.X; top = p.Y; break;
            case DragMode.ResizeSW: left = p.X; bottom = p.Y; break;
            case DragMode.ResizeSE: right = p.X; bottom = p.Y; break;
        }
        if (right - left < 8) right = left + 8;
        if (bottom - top < 8) bottom = top + 8;
        return new Bbox(left, top, right - left, bottom - top);
    }

    private void ApplyResize(DragMode mode, Vector2 p)
    {
        var orig = _dragStartBbox;
        var newBox = ResizedBox(mode, p);

        // Scale each selected element from its original bbox to its new position within newBox,
        // preserving relative position/size within the selection.
        float sx = newBox.W / Math.Max(1e-3f, orig.W);
        float sy = newBox.H / Math.Max(1e-3f, orig.H);

        foreach (var id in Context.SelectedImageIds)
        {
            if (!_origImageBoxes.TryGetValue(id, out var ob)) continue;
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            im.X = newBox.X + (ob.X - orig.X) * sx;
            im.Y = newBox.Y + (ob.Y - orig.Y) * sy;
            im.Width = ob.W * sx;
            im.Height = ob.H * sy;
        }
        foreach (var id in Context.SelectedTextIds)
        {
            if (!_origTextBoxes.TryGetValue(id, out var ob)) continue;
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            t.X = newBox.X + (ob.X - orig.X) * sx;
            t.Y = newBox.Y + (ob.Y - orig.Y) * sy;
            t.Width = ob.W * sx;
            t.Height = ob.H * sy;
            t.FontSize = Math.Max(6f, t.FontSize * Math.Min(sx, sy));
        }
        // Shapes: scale each endpoint relative to the original selection bbox.
        foreach (var id in Context.SelectedShapeIds)
        {
            if (!_origShapePoints.TryGetValue(id, out var op)) continue;
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            sh.X1 = newBox.X + (op.X1 - orig.X) * sx;
            sh.Y1 = newBox.Y + (op.Y1 - orig.Y) * sy;
            sh.X2 = newBox.X + (op.X2 - orig.X) * sx;
            sh.Y2 = newBox.Y + (op.Y2 - orig.Y) * sy;
        }
        // Strokes: scale all points relative to original bbox.
        foreach (var id in Context.SelectedStrokeIds)
        {
            var s = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (s is null) continue;
            for (int i = 0; i < s.Points.Count; i++)
            {
                var pt = s.Points[i];
                var nx = newBox.X + (pt.X - orig.X) * sx;
                var ny = newBox.Y + (pt.Y - orig.Y) * sy;
                s.Points[i] = new InkPoint(nx, ny, pt.Pressure);
            }
        }
    }

    private void ApplyRotate(Vector2 p)
    {
        var ang = Math.Atan2(p.Y - _dragStartCenter.Y, p.X - _dragStartCenter.X);
        var deltaDeg = (ang - _dragStartRotation) * 180.0 / Math.PI;
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            if (!_origImageRot.TryGetValue(id, out var orig)) orig = 0;
            im.Rotation = orig + deltaDeg;
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            if (!_origTextRot.TryGetValue(id, out var orig)) orig = 0;
            t.Rotation = orig + deltaDeg;
        }
    }

    // Renders the selected elements once into an overlay image and hides the
    // originals from the main canvas (one partial redraw). The ghost pixel-
    // covers the originals, so nothing visibly changes at drag start.
    private void BeginDragGhost()
    {
        TearDownDragGhost();
        if (!SelectionBbox(out var sel)) return;

        // Pad so stroke half-widths + Catmull-Rom overshoot render inside the
        // image instead of clipping at the selection's point-hull edge.
        float pad = 12f;
        foreach (var id in Context.SelectedStrokeIds)
        {
            var s = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (s is not null) pad = Math.Max(pad, s.Width * 0.5f + 12f);
        }
        var box = new Bbox(sel.X - pad, sel.Y - pad, sel.W + pad * 2, sel.H + pad * 2);
        if (box.W < 1 || box.H < 1) return;

        try
        {
            var device = TryGetCanvasDevice() ?? CanvasDevice.GetSharedDevice();
            // Render at the canvas backing scale so the ghost is crisp at the
            // current zoom; cap total pixels for page-sized selections.
            float scale = Math.Clamp(_canvas.DpiScale, 1f, 4f);
            const float maxPixels = 16_000_000f;
            if (box.W * box.H * scale * scale > maxPixels)
                scale = MathF.Sqrt(maxPixels / (box.W * box.H));
            scale = Math.Max(0.25f, scale);

            var src = new CanvasImageSource(device, box.W, box.H, 96f * scale);
            using (var ds = src.CreateDrawingSession(Colors.Transparent))
            {
                ds.Transform = Matrix3x2.CreateTranslation(-box.X, -box.Y);
                Renderer.DrawElements(ds, Page,
                    Context.SelectedStrokeIds, Context.SelectedShapeIds,
                    Context.SelectedTextIds, Context.SelectedImageIds, _imageCache);
            }

            _dragGhostRotate = new RotateTransform();
            _dragGhost = new Image
            {
                Source = src,
                Width = box.W,
                Height = box.H,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = _dragGhostRotate
            };
            Canvas.SetLeft(_dragGhost, box.X);
            Canvas.SetTop(_dragGhost, box.Y);
            Canvas.SetZIndex(_dragGhost, 10);
            _overlay.Children.Add(_dragGhost);

            _dragGhostBorderRotate = new RotateTransform();
            _dragGhostBorder = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(255, 91, 107, 255)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Width = box.W,
                Height = box.H,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = _dragGhostBorderRotate
            };
            Canvas.SetLeft(_dragGhostBorder, box.X);
            Canvas.SetTop(_dragGhostBorder, box.Y);
            Canvas.SetZIndex(_dragGhostBorder, 11);
            _overlay.Children.Add(_dragGhostBorder);

            _dragGhostBbox = box;

            // Hide the originals (and the Win2D chrome — DrawSelectionVisuals
            // skips while _drag is set) with one partial redraw.
            _hiddenElementIds = new HashSet<string>(
                Context.SelectedStrokeIds
                    .Concat(Context.SelectedShapeIds)
                    .Concat(Context.SelectedTextIds)
                    .Concat(Context.SelectedImageIds));
            InvalidateSelectionRegion(box, 0);
        }
        catch (Exception ex)
        {
            // Fail-soft: no ghost means no live preview, but the drag still
            // applies correctly on release.
            App.LogError(ex, "BeginDragGhost");
            _hiddenElementIds = null;
            TearDownDragGhost();
        }
    }

    private void PositionGhost(float x, float y, float w, float h)
    {
        if (_dragGhost is null || _dragGhostBorder is null) return;
        Canvas.SetLeft(_dragGhost, x);
        Canvas.SetTop(_dragGhost, y);
        _dragGhost.Width = w;
        _dragGhost.Height = h;
        Canvas.SetLeft(_dragGhostBorder, x);
        Canvas.SetTop(_dragGhostBorder, y);
        _dragGhostBorder.Width = w;
        _dragGhostBorder.Height = h;
    }

    private void TearDownDragGhost()
    {
        if (_dragGhost is not null) _overlay.Children.Remove(_dragGhost);
        if (_dragGhostBorder is not null) _overlay.Children.Remove(_dragGhostBorder);
        _dragGhost = null;
        _dragGhostBorder = null;
        _dragGhostRotate = null;
        _dragGhostBorderRotate = null;
    }

    // Partial main-canvas invalidate covering `b` plus the selection chrome
    // (dashed outline pad, handles, rotate stalk) and any extra overhang.
    private void InvalidateSelectionRegion(Bbox b, float extraPad)
    {
        float p = 56f + extraPad;
        CommitStrokeRedraw(new Bbox(b.X - p, b.Y - p, b.W + p * 2, b.H + p * 2));
    }

    // Applies the drag's accumulated transform to the model ONCE, restores the
    // hidden originals and repaints only the affected region.
    private void EndDrag()
    {
        var mode = _drag;
        _drag = DragMode.None;
        Canvas.SetZIndex(this, 0);   // undo the drag-time raise (see StartDrag)
        var oldRegion = _dragGhost is not null ? _dragGhostBbox : _dragStartBbox;

        // A tap (no real travel) inside an existing selection is "click to
        // deselect": tear down the ghost, restore the originals and clear —
        // rather than nudging the selection by a jittery pixel or two.
        bool tapped = (_lastDragPoint - _dragStartPoint).LengthSquared() <= TapDismissThresholdSq;
        if (mode == DragMode.Move && _dragTapDismisses && tapped)
        {
            _hiddenElementIds = null;
            TearDownDragGhost();
            InvalidateSelectionRegion(oldRegion, 0);
            ClearSelection();
            return;
        }

        // Cross-page move: if the pointer ended over a different page, hand the
        // selection to that page (InkCanvasControl does the reparenting + coord
        // translation) instead of translating it off the bottom of this one.
        if (mode == DragMode.Move && _dragMoved && SelectionDragDropped is not null)
        {
            var delta = new Vector2(_lastDragPoint.X - _dragStartPoint.X,
                                    _lastDragPoint.Y - _dragStartPoint.Y);
            var drop = new SelectionDropEventArgs { ReleaseLocal = _lastDragPoint, Delta = delta };
            SelectionDragDropped.Invoke(this, drop);
            if (drop.Transferred)
            {
                _hiddenElementIds = null;
                TearDownDragGhost();
                RequestRedraw();   // repaint this (source) page: the elements are gone
                return;
            }
        }

        if (_dragMoved)
        {
            switch (mode)
            {
                case DragMode.Move:
                    TranslateSelection(_lastDragPoint.X - _dragStartPoint.X,
                                       _lastDragPoint.Y - _dragStartPoint.Y);
                    break;
                case DragMode.ResizeNW:
                case DragMode.ResizeNE:
                case DragMode.ResizeSW:
                case DragMode.ResizeSE:
                    ApplyResize(mode, _lastDragPoint);
                    break;
                case DragMode.Rotate:
                    ApplyRotate(_lastDragPoint);
                    break;
            }
        }

        _hiddenElementIds = null;
        TearDownDragGhost();

        var dirty = oldRegion;
        if (SelectionBbox(out var nb)) dirty = Bbox.Union(dirty, nb);
        // Rotated images/texts overhang their axis-aligned bbox by up to the
        // half-diagonal; pad generously since this repaint happens once.
        float extra = mode == DragMode.Rotate ? 0.5f * Math.Max(dirty.W, dirty.H) : 0f;
        InvalidateSelectionRegion(dirty, extra);

        if (_dragMoved) Context.Mutated?.Invoke();
    }

    public void TranslateSelection(float dx, float dy)
    {
        foreach (var id in Context.SelectedStrokeIds)
        {
            var s = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (s is null) continue;
            for (int i = 0; i < s.Points.Count; i++)
            {
                var pt = s.Points[i];
                s.Points[i] = new InkPoint(pt.X + dx, pt.Y + dy, pt.Pressure);
            }
        }
        foreach (var id in Context.SelectedShapeIds)
        {
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            sh.X1 += dx; sh.Y1 += dy;
            sh.X2 += dx; sh.Y2 += dy;
            // Triangles carry a third vertex; move it too or the shape distorts.
            if (sh.Kind == ShapeKind.Triangle) { sh.X3 += dx; sh.Y3 += dy; }
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            t.X += dx; t.Y += dy;
        }
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            im.X += dx; im.Y += dy;
        }
    }

    public void DeleteSelection()
    {
        if (!HasCommittedSelection()) return;
        Page.Strokes.RemoveAll(s => Context.SelectedStrokeIds.Contains(s.Id));
        Page.Shapes.RemoveAll(s => Context.SelectedShapeIds.Contains(s.Id));
        foreach (var id in Context.SelectedImageIds) _imageCache.Remove(id);
        Page.Texts.RemoveAll(t => Context.SelectedTextIds.Contains(t.Id));
        Page.Images.RemoveAll(i => Context.SelectedImageIds.Contains(i.Id));
        ClearSelection();
        Context.Mutated?.Invoke();
        _canvas.Invalidate();
    }

    // True when the selection holds anything that has a color (ink, shapes, text).
    public bool SelectionHasRecolorable =>
        Context.SelectedStrokeIds.Count + Context.SelectedShapeIds.Count + Context.SelectedTextIds.Count > 0;

    // True when every selected colorable item is a highlighter stroke — the
    // selection bar then offers the highlighter presets instead of the pen ones.
    public bool SelectionIsAllHighlighter =>
        Context.SelectedStrokeIds.Count > 0
        && Context.SelectedShapeIds.Count == 0 && Context.SelectedTextIds.Count == 0
        && Page.Strokes.Where(s => Context.SelectedStrokeIds.Contains(s.Id))
                       .All(s => s.Kind == StrokeKind.Highlighter);

    // Color of the first selected colorable item, used to mark the active swatch.
    public Color? SelectionColor()
    {
        foreach (var id in Context.SelectedStrokeIds)
            if (Page.Strokes.FirstOrDefault(s => s.Id == id) is { } s) return s.Color;
        foreach (var id in Context.SelectedShapeIds)
            if (Page.Shapes.FirstOrDefault(s => s.Id == id) is { } sh) return sh.Color;
        foreach (var id in Context.SelectedTextIds)
            if (Page.Texts.FirstOrDefault(t => t.Id == id) is { } t) return t.Color;
        return null;
    }

    // Recolors the selected strokes, shapes and text boxes in place. Highlighter
    // strokes keep their own alpha so they stay translucent; pen strokes stay
    // opaque even if a (translucent) highlighter swatch was picked. Recorded as
    // one undoable mutation via Context.Mutated, like Delete/Duplicate.
    public void RecolorSelection(Color c)
    {
        bool changed = false;
        foreach (var s in Page.Strokes.Where(s => Context.SelectedStrokeIds.Contains(s.Id)))
        {
            var nc = Color.FromArgb(s.Kind == StrokeKind.Highlighter ? s.Color.A : (byte)255, c.R, c.G, c.B);
            if (!nc.Equals(s.Color)) { s.Color = nc; changed = true; }
        }
        foreach (var sh in Page.Shapes.Where(s => Context.SelectedShapeIds.Contains(s.Id)))
            if (!sh.Color.Equals(c)) { sh.Color = c; changed = true; }
        foreach (var t in Page.Texts.Where(t => Context.SelectedTextIds.Contains(t.Id)))
            if (!t.Color.Equals(c)) { t.Color = c; changed = true; }
        if (!changed) return;

        Context.Mutated?.Invoke();
        if (SelectionBbox(out var b)) InvalidateSelectionRegion(b, 0);
        else _canvas.Invalidate();
    }

    // True when the selection holds anything with a line width (ink, shapes).
    public bool SelectionHasResizableWidth =>
        Context.SelectedStrokeIds.Count + Context.SelectedShapeIds.Count > 0;

    // Width of the first selected pen stroke (or highlighter / shape when there
    // is none), used to mark the active width dot on the selection bar.
    public float? SelectionWidth()
    {
        bool allHl = SelectionIsAllHighlighter;
        foreach (var id in Context.SelectedStrokeIds)
            if (Page.Strokes.FirstOrDefault(s => s.Id == id) is { } s
                && (allHl || s.Kind != StrokeKind.Highlighter)) return s.Width;
        foreach (var id in Context.SelectedShapeIds)
            if (Page.Shapes.FirstOrDefault(s => s.Id == id) is { } sh) return sh.StrokeWidth;
        return null;
    }

    // Sets the line width of the selected ink and shapes in place. The bar
    // offers pen widths unless everything selected is highlighter, so in a
    // mixed selection highlighter strokes keep their (much wider) width rather
    // than shrinking to a pen size. One undoable mutation, like Recolor.
    public void SetSelectionWidth(float width)
    {
        bool allHl = SelectionIsAllHighlighter;
        float oldMax = 0f;
        bool changed = false;
        foreach (var s in Page.Strokes.Where(s => Context.SelectedStrokeIds.Contains(s.Id)))
        {
            if (!allHl && s.Kind == StrokeKind.Highlighter) continue;
            if (s.Width == width) continue;
            oldMax = MathF.Max(oldMax, s.Width);
            s.Width = width;
            changed = true;
        }
        foreach (var sh in Page.Shapes.Where(s => Context.SelectedShapeIds.Contains(s.Id)))
        {
            if (allHl || sh.StrokeWidth == width) continue;
            oldMax = MathF.Max(oldMax, sh.StrokeWidth);
            sh.StrokeWidth = width;
            changed = true;
        }
        if (!changed) return;

        Context.Mutated?.Invoke();
        // Pad by the wider of old/new so a thinning stroke's old edge repaints too.
        if (SelectionBbox(out var b)) InvalidateSelectionRegion(b, MathF.Max(oldMax, width));
        else _canvas.Invalidate();
        // Selection chrome hugs the ink; refresh it for the new extent.
        Context.SelectionChanged?.Invoke();
    }

    public void DuplicateSelection(float offsetX = 24, float offsetY = 24)
    {
        var newStrokeIds = new List<string>();
        var newShapeIds = new List<string>();
        var newTextIds = new List<string>();
        var newImageIds = new List<string>();
        foreach (var s in Page.Strokes.Where(s => Context.SelectedStrokeIds.Contains(s.Id)).ToList())
        {
            var clone = new Stroke { Kind = s.Kind, Color = s.Color, Width = s.Width, PressureMode = s.PressureMode };
            foreach (var pt in s.Points)
                clone.Points.Add(new InkPoint(pt.X + offsetX, pt.Y + offsetY, pt.Pressure));
            Page.Strokes.Add(clone);
            newStrokeIds.Add(clone.Id);
        }
        foreach (var sh in Page.Shapes.Where(s => Context.SelectedShapeIds.Contains(s.Id)).ToList())
        {
            var clone = new ShapeElement
            {
                Kind = sh.Kind, Color = sh.Color, StrokeWidth = sh.StrokeWidth, Filled = sh.Filled,
                X1 = sh.X1 + offsetX, Y1 = sh.Y1 + offsetY,
                X2 = sh.X2 + offsetX, Y2 = sh.Y2 + offsetY,
                // Third vertex only means anything for triangles; leave the
                // unused zeros alone for other kinds.
                X3 = sh.Kind == ShapeKind.Triangle ? sh.X3 + offsetX : sh.X3,
                Y3 = sh.Kind == ShapeKind.Triangle ? sh.Y3 + offsetY : sh.Y3,
                Rotation = sh.Rotation
            };
            Page.Shapes.Add(clone);
            newShapeIds.Add(clone.Id);
        }
        foreach (var t in Page.Texts.Where(t => Context.SelectedTextIds.Contains(t.Id)).ToList())
        {
            var clone = new TextElement
            {
                X = t.X + offsetX, Y = t.Y + offsetY, Width = t.Width, Height = t.Height,
                Rotation = t.Rotation, Text = t.Text, FontSize = t.FontSize, Color = t.Color,
                FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic
            };
            Page.Texts.Add(clone);
            newTextIds.Add(clone.Id);
        }
        foreach (var im in Page.Images.Where(i => Context.SelectedImageIds.Contains(i.Id)).ToList())
        {
            var clone = new ImageElement
            {
                X = im.X + offsetX, Y = im.Y + offsetY, Width = im.Width, Height = im.Height,
                Rotation = im.Rotation, PngData = im.PngData
            };
            Page.Images.Add(clone);
            newImageIds.Add(clone.Id);
        }
        Context.SelectedStrokeIds.Clear(); Context.SelectedStrokeIds.AddRange(newStrokeIds);
        Context.SelectedShapeIds.Clear();  Context.SelectedShapeIds.AddRange(newShapeIds);
        Context.SelectedTextIds.Clear();   Context.SelectedTextIds.AddRange(newTextIds);
        Context.SelectedImageIds.Clear();  Context.SelectedImageIds.AddRange(newImageIds);
        Context.Mutated?.Invoke();
        // Repaint the clones' region and the originals' (chrome moved off it).
        if (SelectionBbox(out var nb))
            InvalidateSelectionRegion(
                Bbox.Union(nb, new Bbox(nb.X - offsetX, nb.Y - offsetY, nb.W, nb.H)), 0);
        else
            _canvas.Invalidate();
    }

    public void ClearSelection()
    {
        bool hadSelection = HasCommittedSelection();
        bool hadBbox = SelectionBbox(out var oldBbox);
        Context.SelectedStrokeIds.Clear();
        Context.SelectedShapeIds.Clear();
        Context.SelectedTextIds.Clear();
        Context.SelectedImageIds.Clear();
        Context.SelectionRect = null;
        Context.SelectionLasso = null;
        SyncMarqueeOverlay();
        // Only the old chrome region needs repainting — a full-page invalidate
        // here re-rendered everything on every outside-click.
        if (hadBbox) InvalidateSelectionRegion(oldBbox, 0);
        if (hadSelection) Context.SelectionChanged?.Invoke();
    }

    // ─── PDF text selection (Hand tool, browser-style) ───────────────────────────

    private TextRun? FindNearestTextRun(Vector2 p)
    {
        TextRun? best = null;
        double bestDist = double.MaxValue;
        foreach (var r in Page.TextRuns)
        {
            var cx = r.X + r.Width * 0.5;
            var cy = r.Y + r.Height * 0.5;
            var dx = p.X - cx;
            var dy = p.Y - cy;
            var d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = r; }
        }
        return best;
    }

    // Encode (line, order) into one comparable integer so we can pick the runs
    // between anchor and cursor in reading order with a single linear scan.
    private static long ReadingKey(TextRun r) => ((long)r.LineIndex << 32) | (uint)r.OrderInLine;

    private void RecomputeTextSelection()
    {
        _selectedTextRuns.Clear();
        if (_selectAnchor is null || _selectCursor is null) return;
        var aKey = ReadingKey(_selectAnchor);
        var cKey = ReadingKey(_selectCursor);
        var lo = Math.Min(aKey, cKey);
        var hi = Math.Max(aKey, cKey);
        foreach (var r in Page.TextRuns)
        {
            var k = ReadingKey(r);
            if (k >= lo && k <= hi) _selectedTextRuns.Add(r);
        }
    }

    public void ClearTextSelection()
    {
        if (_selectedTextRuns.Count == 0 && _selectAnchor is null && _selectCursor is null) return;
        _selectAnchor = null;
        _selectCursor = null;
        _selectedTextRuns.Clear();
        SyncTextSelectionOverlay();
    }

    // Rebuilds the overlay highlight from _selectedTextRuns, coalescing the
    // runs of each line into one rectangle (fewer geometry nodes AND a
    // continuous highlight instead of per-word gaps).
    private void SyncTextSelectionOverlay()
    {
        if (_selectedTextRuns.Count == 0)
        {
            if (_textSelHighlight is not null)
            {
                _overlay.Children.Remove(_textSelHighlight);
                _textSelHighlight = null;
            }
            return;
        }

        // Nonzero so slightly overlapping line boxes union instead of
        // even-odd cancelling at their intersections.
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        int i = 0;
        while (i < _selectedTextRuns.Count)
        {
            var first = _selectedTextRuns[i];
            int line = first.LineIndex;
            double x1 = first.X, y1 = first.Y;
            double x2 = first.X + first.Width, y2 = first.Y + first.Height;
            i++;
            while (i < _selectedTextRuns.Count && _selectedTextRuns[i].LineIndex == line)
            {
                var r = _selectedTextRuns[i];
                x1 = Math.Min(x1, r.X);
                y1 = Math.Min(y1, r.Y);
                x2 = Math.Max(x2, r.X + r.Width);
                y2 = Math.Max(y2, r.Y + r.Height);
                i++;
            }
            group.Children.Add(new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(x1, y1, x2 - x1, y2 - y1)
            });
        }

        if (_textSelHighlight is null)
        {
            _textSelHighlight = new XamlPath
            {
                Fill = new SolidColorBrush(Color.FromArgb(110, 91, 107, 255)),
                IsHitTestVisible = false
            };
            _overlay.Children.Add(_textSelHighlight);
        }
        _textSelHighlight.Data = group;
    }

    // ─── Search highlights ───────────────────────────────────────────────────────
    // Soft yellow marks on every match on this page; the current match gets a
    // stronger fill and an outline. Rects are in page coordinates.
    private XamlPath? _searchMarks, _searchCurrent;

    public void SetSearchHighlights(IReadOnlyList<Windows.Foundation.Rect>? rects, Windows.Foundation.Rect? current)
    {
        static GeometryGroup Group(IEnumerable<Windows.Foundation.Rect> rs)
        {
            var g = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var r in rs)
                g.Children.Add(new RectangleGeometry { Rect = new Windows.Foundation.Rect(r.X - 4, r.Y - 3, r.Width + 8, r.Height + 6) });
            return g;
        }

        if (rects is null || rects.Count == 0)
        {
            if (_searchMarks is not null) { _overlay.Children.Remove(_searchMarks); _searchMarks = null; }
        }
        else
        {
            _searchMarks ??= AddOverlayPath(Color.FromArgb(80, 255, 213, 59), null);
            _searchMarks.Data = Group(rects);
        }

        if (current is not { } c)
        {
            if (_searchCurrent is not null) { _overlay.Children.Remove(_searchCurrent); _searchCurrent = null; }
        }
        else
        {
            _searchCurrent ??= AddOverlayPath(Color.FromArgb(120, 255, 193, 7), Color.FromArgb(255, 230, 150, 0));
            _searchCurrent.Data = Group(new[] { c });
        }
    }

    private XamlPath AddOverlayPath(Color fill, Color? stroke)
    {
        var p = new XamlPath { Fill = new SolidColorBrush(fill), IsHitTestVisible = false };
        if (stroke is { } s) { p.Stroke = new SolidColorBrush(s); p.StrokeThickness = 2; }
        _overlay.Children.Add(p);
        return p;
    }

    public string? GetSelectedText()
    {
        if (_selectedTextRuns.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        int? lastLine = null;
        foreach (var r in _selectedTextRuns)
        {
            if (lastLine.HasValue)
            {
                if (r.LineIndex != lastLine.Value) sb.Append('\n');
                else sb.Append(' ');
            }
            sb.Append(r.Text);
            lastLine = r.LineIndex;
        }
        return sb.ToString();
    }

    // ─── Inline text editing ─────────────────────────────────────────────────────

    private TextBox? _editor;
    private string? _editorTextId;
    private Border? _fmtBar;

    public void BeginInlineTextEdit(string textId)
    {
        var t = Page.Texts.FirstOrDefault(x => x.Id == textId);
        if (t is null) return;
        EndInlineTextEdit(commit: true);

        Context.EditingTextId = textId;
        _editorTextId = textId;

        _editor = new TextBox
        {
            Text = t.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = t.FontSize,
            FontWeight = t.Bold ? new FontWeight { Weight = 700 } : new FontWeight { Weight = 400 },
            FontStyle = t.Italic ? FontStyle.Italic : FontStyle.Normal,
            FontFamily = new FontFamily(t.FontFamily),
            MinWidth = Math.Max(120, t.Width),
            MinHeight = Math.Max(40, t.Height),
            Width = Math.Max(120, t.Width),
            Padding = new Thickness(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["AppAccentBrush"],
            Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
            Foreground = new SolidColorBrush(t.Color)
        };

        // ── Formatting toolbar ──────────────────────────────────────────────
        var boldBtn = new ToggleButton
        {
            IsChecked = t.Bold,
            AllowFocusOnInteraction = false,
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(6),
            Content = new TextBlock { Text = "B", FontWeight = new FontWeight { Weight = 700 }, FontSize = 13 }
        };
        var italicBtn = new ToggleButton
        {
            IsChecked = t.Italic,
            AllowFocusOnInteraction = false,
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(6),
            Content = new TextBlock { Text = "I", FontStyle = FontStyle.Italic, FontSize = 13 }
        };
        var colorDot = new Border
        {
            Width = 14, Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(t.Color),
            VerticalAlignment = VerticalAlignment.Center
        };
        var colorBtn = new Button
        {
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(6),
            Content = colorDot,
            AllowFocusOnInteraction = false
        };
        ToolTipService.SetToolTip(colorBtn, "Text color");
        var sizeLabel = new TextBlock
        {
            Text = $"{t.FontSize:0}",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            MinWidth = 24,
            TextAlignment = TextAlignment.Center
        };
        var shrinkBtn = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 },
            AllowFocusOnInteraction = false,
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(6)
        };
        var growBtn = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 },
            AllowFocusOnInteraction = false,
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(6)
        };
        ToolTipService.SetToolTip(shrinkBtn, "Decrease font size");
        ToolTipService.SetToolTip(growBtn, "Increase font size");

        var sep = new Border
        {
            Width = 1, Height = 16,
            Background = (Brush)Application.Current.Resources["AppBorderBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0)
        };

        var fmtPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            Padding = new Thickness(6, 4, 6, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        fmtPanel.Children.Add(boldBtn);
        fmtPanel.Children.Add(italicBtn);
        fmtPanel.Children.Add(sep);
        fmtPanel.Children.Add(colorBtn);
        fmtPanel.Children.Add(new Border { Width = 1, Height = 16, Background = (Brush)Application.Current.Resources["AppBorderBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
        fmtPanel.Children.Add(shrinkBtn);
        fmtPanel.Children.Add(sizeLabel);
        fmtPanel.Children.Add(growBtn);

        _fmtBar = new Border
        {
            Child = fmtPanel,
            Background = (Brush)Application.Current.Resources["AppToolbarBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Translation = new Vector3(0, 0, 16)
        };
        _fmtBar.Shadow = new ThemeShadow();

        double fmtTop = Math.Max(2, t.Y - 46);
        Canvas.SetLeft(_fmtBar, t.X);
        Canvas.SetTop(_fmtBar, fmtTop);
        _overlay.Children.Add(_fmtBar);

        Canvas.SetLeft(_editor, t.X);
        Canvas.SetTop(_editor, t.Y);
        _overlay.Children.Add(_editor);

        // ── LostFocus: commit when focus leaves the TextBox ───────────────
        RoutedEventHandler lostFocusHandler = null!;
        lostFocusHandler = (_, __) => EndInlineTextEdit(commit: true);
        _editor.LostFocus += lostFocusHandler;

        // ── Bold ─────────────────────────────────────────────────────────
        boldBtn.Click += (_, __) =>
        {
            t.Bold = boldBtn.IsChecked == true;
            _editor.FontWeight = t.Bold ? new FontWeight { Weight = 700 } : new FontWeight { Weight = 400 };
        };

        // ── Italic ────────────────────────────────────────────────────────
        italicBtn.Click += (_, __) =>
        {
            t.Italic = italicBtn.IsChecked == true;
            _editor.FontStyle = t.Italic ? FontStyle.Italic : FontStyle.Normal;
        };

        // ── Color ─────────────────────────────────────────────────────────
        colorBtn.Click += async (_, __) =>
        {
            _editor.LostFocus -= lostFocusHandler;
            var picker = new Microsoft.UI.Xaml.Controls.ColorPicker { Color = t.Color };
            var dlg = new ContentDialog
            {
                Title = "Text color",
                Content = picker,
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                t.Color = picker.Color;
                colorDot.Background = new SolidColorBrush(picker.Color);
                _editor.Foreground = new SolidColorBrush(picker.Color);
            }
            _editor.LostFocus += lostFocusHandler;
            _editor.Focus(FocusState.Programmatic);
        };

        // ── Font size ─────────────────────────────────────────────────────
        shrinkBtn.Click += (_, __) =>
        {
            t.FontSize = Math.Max(8, t.FontSize - 2);
            _editor.FontSize = t.FontSize;
            sizeLabel.Text = $"{t.FontSize:0}";
        };
        growBtn.Click += (_, __) =>
        {
            t.FontSize = Math.Min(120, t.FontSize + 2);
            _editor.FontSize = t.FontSize;
            sizeLabel.Text = $"{t.FontSize:0}";
        };

        _editor.Loaded += (_, __) => { _editor.Focus(FocusState.Programmatic); _editor.SelectAll(); };
        _editor.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape)
            {
                EndInlineTextEdit(commit: false);
                args.Handled = true;
            }
        };
        _canvas.Invalidate();
    }

    public void EndInlineTextEdit(bool commit)
    {
        if (_editor is null || _editorTextId is null) return;
        var t = Page.Texts.FirstOrDefault(x => x.Id == _editorTextId);
        if (commit && t is not null)
        {
            t.Text = _editor.Text ?? "";
            t.Width = Math.Max(_editor.ActualWidth, t.Width);
            t.Height = Math.Max(_editor.ActualHeight, t.Height);
        }
        if (commit && t is not null && string.IsNullOrWhiteSpace(t.Text))
        {
            // Drop empty text elements rather than leaving invisible artifacts.
            Page.Texts.RemoveAll(x => x.Id == t.Id);
            Context.SelectedTextIds.Remove(t.Id);
        }
        _overlay.Children.Remove(_editor);
        if (_fmtBar is not null)
        {
            _overlay.Children.Remove(_fmtBar);
            _fmtBar = null;
        }
        _editor = null;
        _editorTextId = null;
        Context.EditingTextId = null;
        Context.Mutated?.Invoke();
        _canvas.Invalidate();
    }

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
    //    that makes this page as wide as that one, so the centred pages' edges
    //    line up instead.
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

    // Called by InkCanvasControl after an extension is committed to the model,
    // to resize all internal canvases to the new page dimensions.
    public void ResizeCanvas()
    {
        double newW = Page.Width;
        _canvas.Width      = newW;
        _overlay.Width     = newW;
        var root = (Grid)Content;
        root.Width = newW;
        Width = newW;

        // Background placement changed — the hi-res crop rect is stale.
        ClearHiResBackground();

        // Reposition right handle for new width.
        if (_rightExtHandle is not null)
            Canvas.SetLeft(_rightExtHandle, newW - ExtHandleWidth);

        _canvas.Invalidate();
    }
}
