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
        PointerCaptureLost += (_, __) => { _capturedPointerId = null; Canvas.SetZIndex(this, 0); };
        DoubleTapped += OnDoubleTapped;
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

        SyncLiveInkSurface(s);
    }

    private void TearDownLiveInk() => _liveInk?.Clear();

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
                    var device = _canvas.Device;
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
                                  skipElementIds: _hiddenElementIds);

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

    public void FreezeViewportForDpiChange(Windows.Foundation.Rect viewRect, float oldScale)
    {
        UnfreezeViewport();
        var device = _canvas.Device;
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
                                  skipElementIds: _hiddenElementIds);
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
            return v;
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
        Focus(FocusState.Pointer);
        var p = ToPageSpace(e);
        Context.CurrentPage = Page;
        _penEventsSinceDown = 0;

        var tool = PickTool(e);

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
        try { OnPointerMovedCore(sender, e); }
        catch (Exception ex) { App.LogError(ex, "OnPointerMoved"); }
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
        ReleasePointerCapture(e.Pointer);
        _capturedPointerId = null;
        _activeTool = null;
        if (!skipFullRedraw) _canvas.Invalidate();
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
            var device = _canvas.Device ?? CanvasDevice.GetSharedDevice();
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
                Renderer.DrawPage(ds, device, Page, PageTemplate, bg, images, overlayOnly: overlayOnly);
                // Bake cross-page bleed into the export so a stroke straddling the
                // seam shows on both pages in external PDF viewers, matching the
                // on-screen rendering.
                DrawNeighborBleedForExport(ds, device);
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
        _extendExisting = _extendSide == ExtendSide.Right
            ? (Page.BackgroundContentWidth > 0
                ? Math.Max(0, Page.Width - Page.BackgroundLeft - Page.BackgroundContentWidth)
                : 0)
            : Page.BackgroundLeft;

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
        double totalDesired = Math.Max(0, _extendExisting + screenDelta);

        // Snap the total to the preset snap points.
        double pdfW = Page.BackgroundContentWidth > 0 ? Page.BackgroundContentWidth : Page.Width;
        double snappedTotal = SnapExtension(totalDesired, pdfW);
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
        if (amount > 0)
            ExtensionDragCompleted?.Invoke(this, (side, amount));
        e.Handled = true;
    }

    private void CancelExtensionDrag()
    {
        ClearPreview();
        _extendDragging = false;
        _extendDragPtrId = null;
        _extendCurrentAmount = 0;
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
        _extTooltip.Text = amount <= 0 ? $"{dir} Original size" : $"{dir} {(int)amount}px ({pct}%)";
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
