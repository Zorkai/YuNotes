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
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.Text;
using Colors = Microsoft.UI.Colors;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Controls;

public enum ExtendSide { Left, Right }

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
    // Transparent tile-based overlay for the active (in-progress) stroke.
    // CanvasVirtualControl (not CanvasControl) is the key: it tiles the surface
    // and supports Invalidate(Rect) — when we mark a small region dirty, only
    // the tiles intersecting that region are redrawn AND re-composited by
    // DComp. This is what makes per-sample GPU cost stay flat at high zoom,
    // mirroring Xournal++'s cairo expose-region behaviour.
    private readonly CanvasVirtualControl _liveCanvas;
    private readonly Canvas _overlay;

    // Xournal++-style persistent mask: an off-screen render target that the
    // active stroke is painted into INCREMENTALLY. Each pen sample paints only
    // the new segment(s) onto the mask (additive, no clear), and only the
    // bounding rect of those segments is invalidated on the live canvas. GPU
    // work per pen sample stays roughly constant regardless of stroke length
    // or zoom — the live canvas redraw is a single bitmap blit, not a re-
    // tessellation of the whole stroke.
    private CanvasRenderTarget? _liveMask;
    private string? _liveMaskStrokeId;
    // How many of ActiveStroke.Points have been baked onto the mask so far.
    // 0 = nothing, 1 = single-dot painted, N ≥ 2 = N-1 segments painted.
    private int _liveMaskPaintedPoints;
    private float _liveMaskDpi;

    // Vsync coalescing. Pen reports at 120–240 Hz; the display only refreshes
    // at 60–120 Hz. We hook CompositionTarget.Rendering on the first pen sample
    // of a stroke and unhook on stroke end — the actual mask paint + invalidate
    // happens once per vsync regardless of how many pen samples arrived.
    private bool _liveRenderHooked;

    // Linear-extrapolated predicted tip — drawn ON TOP of the mask (never into
    // it), so a stale prediction is wiped by simply invalidating its old rect.
    private Vector2? _predictedTip;
    private Windows.Foundation.Rect _predictedRect;
    private bool _hasPredictedRect;

    // Live shape preview — a lightweight XAML element in the overlay that acts
    // as the rubber-band while the user is dragging out a new shape.
    private UIElement? _liveShapePreview;
    private string? _liveShapeId;

    // Fast-path for uniform-width pen + highlighter: render the in-progress
    // stroke as a single XAML Polyline. DComp handles the redraw natively.
    private Polyline? _livePolyline;
    private string? _polylineStrokeId;
    private bool _polylineHasPrediction;

    // Fast-path for pressure-variable pen: a XAML Canvas holding one short
    // Polyline per segment, with that segment's thickness set to the average
    // of its endpoint pressures. Round line caps make adjacent segments blend
    // into a continuous tapered ribbon. Same idea as the uniform Polyline
    // path — no Win2D surfaces touched during the stroke.
    private Canvas? _pressureContainer;
    private string? _pressureStrokeId;
    private int _pressureSegmentsRendered;
    private bool _pressureDotRendered;
    private Polyline? _pressurePredicted;
    private Brush? _pressureBrush;
    private float _pressureBaseHalfWidth;
    private CanvasBitmap? _bgBitmap;
    private readonly Dictionary<string, CanvasBitmap> _imageCache = new();
    private uint? _capturedPointerId;
    private ITool? _activeTool;

    // Drag modes when interacting with selection
    private enum DragMode { None, Move, ResizeNW, ResizeNE, ResizeSW, ResizeSE, Rotate }
    private DragMode _drag = DragMode.None;
    private Vector2 _lastMovePoint;
    private Bbox _dragStartBbox;
    private Vector2 _dragStartCenter;
    private double _dragStartRotation;

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

        _liveCanvas = new CanvasVirtualControl
        {
            Width = page.Width,
            Height = page.Height,
            ClearColor = Color.FromArgb(0, 0, 0, 0),
            IsHitTestVisible = false
        };
        _liveCanvas.RegionsInvalidated += OnLiveRegionsInvalidated;

        _overlay = new Canvas
        {
            Width = page.Width,
            Height = page.Height,
            IsHitTestVisible = true,
            Background = null
        };

        var root = new Grid { Width = page.Width, Height = page.Height };
        root.Children.Add(_canvas);
        root.Children.Add(_liveCanvas);
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
        PointerCaptureLost += (_, __) => _capturedPointerId = null;
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

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
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
        _canvas.Invalidate();
        _liveCanvas.Invalidate();
    }

    // Called by tools on every pen sample. Branches between two live-stroke
    // backends depending on the stroke type:
    //   - Polyline (XAML retained-mode shape) for uniform-width pen + highlighter.
    //     DComp draws and recomposites natively — no Win2D surfaces touched.
    //   - Win2D mask + CanvasVirtualControl for pressure-variable strokes,
    //     which Polyline can't represent. Same code path as before.
    public void RequestLiveRedraw()
    {
        var s = Context.ActiveStroke;

        // Shape tool: show a rubber-band XAML preview while dragging
        if (Context.ActiveShape is { } shape && ReferenceEquals(Context.CurrentPage, Page))
        {
            TearDownPolyline();
            TearDownPressureContainer();
            TearDownLiveMaskPath();
            EnsureLiveShapePreview(shape);
            SyncLiveShapePreview(shape);
            return;
        }
        TearDownLiveShapePreview();

        if (s is null || !ReferenceEquals(Context.CurrentPage, Page))
        {
            TearDownPolyline();
            TearDownPressureContainer();
            TearDownLiveMaskPath();
            return;
        }

        if (s.PressureMode)
        {
            // Pressure stroke — per-segment polylines in a XAML Canvas.
            TearDownPolyline();
            TearDownLiveMaskPath();
            EnsurePressureContainer(s);
            SyncPressureSegments(s);
            return;
        }

        // Uniform-width stroke — single XAML Polyline.
        TearDownPressureContainer();
        TearDownLiveMaskPath();
        EnsureLivePolyline(s);
        if (_livePolyline is null) return;
        SyncLivePolyline(s);
    }

    private void EnsurePressureContainer(Stroke s)
    {
        if (_pressureContainer is not null && _pressureStrokeId == s.Id) return;
        TearDownPressureContainer();

        _pressureContainer = new Canvas
        {
            Width = Page.Width,
            Height = Page.Height,
            IsHitTestVisible = false
        };
        _overlay.Children.Add(_pressureContainer);

        byte alpha = s.Color.A;
        if (s.Kind == StrokeKind.Highlighter)
            alpha = (byte)Math.Min(140, alpha == 255 ? 110 : alpha);
        _pressureBrush = new SolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B));
        _pressureBaseHalfWidth = s.Width * 0.5f;
        _pressureStrokeId = s.Id;
        _pressureSegmentsRendered = 0;
        _pressureDotRendered = false;
        _pressurePredicted = null;
    }

    private void SyncPressureSegments(Stroke s)
    {
        if (_pressureContainer is null || _pressureBrush is null) return;
        var pts = s.Points;
        int pointCount = pts.Count;

        // Drop the previous predicted segment before appending real ones.
        if (_pressurePredicted is not null)
        {
            _pressureContainer.Children.Remove(_pressurePredicted);
            _pressurePredicted = null;
        }

        // First sample of a stroke: drop a circular dot at the touchdown
        // point so even a tap leaves visible ink.
        if (pointCount >= 1 && !_pressureDotRendered)
        {
            var p = pts[0];
            float r = _pressureBaseHalfWidth * Math.Max(0.2f, p.Pressure);
            var dot = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = _pressureBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, p.X - r);
            Canvas.SetTop(dot, p.Y - r);
            _pressureContainer.Children.Add(dot);
            _pressureDotRendered = true;
        }

        // Append a fresh Polyline for each new segment we haven't drawn yet.
        for (int i = _pressureSegmentsRendered; i < pointCount - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            float wA = _pressureBaseHalfWidth * Math.Max(0.2f, a.Pressure) * 2f;
            float wB = _pressureBaseHalfWidth * Math.Max(0.2f, b.Pressure) * 2f;
            _pressureContainer.Children.Add(MakePressureSeg(a.X, a.Y, b.X, b.Y, (wA + wB) * 0.5f));
        }
        _pressureSegmentsRendered = Math.Max(0, pointCount - 1);

        // One-sample linear extrapolation as the predicted tail.
        if (pointCount >= 2)
        {
            var pn = pts[pointCount - 1];
            var pn1 = pts[pointCount - 2];
            float dx = pn.X - pn1.X;
            float dy = pn.Y - pn1.Y;
            float predWidth = _pressureBaseHalfWidth * Math.Max(0.2f, pn.Pressure) * 2f;
            _pressurePredicted = MakePressureSeg(pn.X, pn.Y, pn.X + dx, pn.Y + dy, predWidth);
            _pressureContainer.Children.Add(_pressurePredicted);
        }
    }

    private Polyline MakePressureSeg(float x0, float y0, float x1, float y1, float thickness)
    {
        var seg = new Polyline
        {
            Stroke = _pressureBrush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        seg.Points.Add(new Windows.Foundation.Point(x0, y0));
        seg.Points.Add(new Windows.Foundation.Point(x1, y1));
        return seg;
    }

    private void TearDownPressureContainer()
    {
        if (_pressureContainer is null) return;
        _overlay.Children.Remove(_pressureContainer);
        _pressureContainer = null;
        _pressureBrush = null;
        _pressureStrokeId = null;
        _pressureSegmentsRendered = 0;
        _pressureDotRendered = false;
        _pressurePredicted = null;
    }

    private void EnsureLivePolyline(Stroke s)
    {
        if (_livePolyline is not null && _polylineStrokeId == s.Id) return;
        if (_livePolyline is not null) _overlay.Children.Remove(_livePolyline);

        byte alpha = s.Color.A;
        if (s.Kind == StrokeKind.Highlighter)
            alpha = (byte)Math.Min(140, alpha == 255 ? 110 : alpha);

        _livePolyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B)),
            StrokeThickness = s.Width,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        _overlay.Children.Add(_livePolyline);
        _polylineStrokeId = s.Id;
        _polylineHasPrediction = false;
    }

    private void SyncLivePolyline(Stroke s)
    {
        if (_livePolyline is null) return;
        var pts = _livePolyline.Points;
        int realCount = s.Points.Count;

        // Strip the old prediction (always the last entry) before appending
        // real samples, then re-add a fresh prediction at the end.
        if (_polylineHasPrediction && pts.Count > 0)
        {
            pts.RemoveAt(pts.Count - 1);
            _polylineHasPrediction = false;
        }

        for (int i = pts.Count; i < realCount; i++)
        {
            var p = s.Points[i];
            pts.Add(new Windows.Foundation.Point(p.X, p.Y));
        }

        if (realCount >= 2)
        {
            var pn = s.Points[realCount - 1];
            var pn1 = s.Points[realCount - 2];
            float dx = pn.X - pn1.X;
            float dy = pn.Y - pn1.Y;
            pts.Add(new Windows.Foundation.Point(pn.X + dx, pn.Y + dy));
            _polylineHasPrediction = true;
        }
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
                break;
            case Ellipse ell:
                Canvas.SetLeft(ell, x); Canvas.SetTop(ell, y);
                ell.Width = w; ell.Height = h;
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

    private void TearDownLiveShapePreview()
    {
        if (_liveShapePreview is null) return;
        _overlay.Children.Remove(_liveShapePreview);
        _liveShapePreview = null;
        _liveShapeId = null;
    }

    private void TearDownPolyline()
    {
        if (_livePolyline is null) return;
        _overlay.Children.Remove(_livePolyline);
        _livePolyline = null;
        _polylineStrokeId = null;
        _polylineHasPrediction = false;
    }

    private void TearDownLiveMaskPath()
    {
        if (_hasPredictedRect)
        {
            _liveCanvas.Invalidate(_predictedRect);
            _hasPredictedRect = false;
            _predictedTip = null;
        }
        if (_liveMask is not null || _liveMaskStrokeId is not null)
        {
            DisposeLiveMask();
            _liveCanvas.Invalidate();
        }
        UnhookLiveRender();
    }

    private void HookLiveRender()
    {
        if (_liveRenderHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnLiveRenderTick;
        _liveRenderHooked = true;
    }

    private void UnhookLiveRender()
    {
        if (!_liveRenderHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnLiveRenderTick;
        _liveRenderHooked = false;
    }

    private void OnLiveRenderTick(object? sender, object e)
    {
        var s = Context.ActiveStroke;
        if (s is null || !ReferenceEquals(Context.CurrentPage, Page))
        {
            UnhookLiveRender();
            return;
        }

        EnsureLiveMaskFor(s);
        if (_liveMask is null) return;

        int pointCount = s.Points.Count;
        if (pointCount == 0) return;

        // 1) Paint any NEW real segments (or first-frame dot) into the mask.
        if (pointCount == 1)
        {
            if (_liveMaskPaintedPoints == 0)
            {
                var p = s.Points[0];
                float dotR = s.Width * 0.5f * Math.Max(0.4f, p.Pressure);
                using (var mds = _liveMask.CreateDrawingSession())
                {
                    mds.Blend = CanvasBlend.SourceOver;
                    mds.FillCircle(p.X, p.Y, dotR, OpaqueColor(s.Color));
                }
                float dotPad = dotR + 2f;
                _liveCanvas.Invalidate(new Windows.Foundation.Rect(
                    p.X - dotPad, p.Y - dotPad, dotPad * 2, dotPad * 2));
                _liveMaskPaintedPoints = 1;
            }
        }
        else if (pointCount > _liveMaskPaintedPoints)
        {
            // First segment back is index 0 if we only had a dot (or nothing);
            // otherwise resume from where we left off. _liveMaskPaintedPoints
            // already accounts for the dot via the value 1.
            int firstSeg = _liveMaskPaintedPoints <= 1 ? 0 : _liveMaskPaintedPoints - 1;
            int lastSeg = pointCount - 2;
            if (firstSeg <= lastSeg)
            {
                var dirty = ComputeSegmentDirtyRect(s, firstSeg, lastSeg);
                using (var mds = _liveMask.CreateDrawingSession())
                {
                    mds.Blend = CanvasBlend.SourceOver;
                    Renderer.DrawStrokeSegments(mds, s, firstSeg, lastSeg, opaque: true);
                }
                _liveCanvas.Invalidate(dirty);
            }
            _liveMaskPaintedPoints = pointCount;
        }

        // 2) Linear-extrapolated predicted tip. Drawn on top of the mask in
        // OnLiveRegionsInvalidated — never baked into the mask — so stale
        // predictions are erased simply by invalidating their old rect.
        if (pointCount >= 2)
        {
            var pn = s.Points[pointCount - 1];
            var pn1 = s.Points[pointCount - 2];
            float dx = pn.X - pn1.X;
            float dy = pn.Y - pn1.Y;
            // One sample-step ahead — roughly a half-vsync of perceived latency
            // erased on a 120Hz pen.
            var newTip = new Vector2(pn.X + dx, pn.Y + dy);

            float halfW = s.Width * 0.5f + 2f;
            float minX = Math.Min(pn.X, newTip.X) - halfW;
            float minY = Math.Min(pn.Y, newTip.Y) - halfW;
            float maxX = Math.Max(pn.X, newTip.X) + halfW;
            float maxY = Math.Max(pn.Y, newTip.Y) + halfW;
            var newRect = new Windows.Foundation.Rect(minX, minY, maxX - minX, maxY - minY);

            // Invalidate the OLD predicted rect (so mask re-shows underneath)
            // and the NEW one (so the fresh prediction gets drawn).
            if (_hasPredictedRect) _liveCanvas.Invalidate(_predictedRect);
            _predictedTip = newTip;
            _predictedRect = newRect;
            _hasPredictedRect = true;
            _liveCanvas.Invalidate(_predictedRect);
        }
    }

    private static Color OpaqueColor(Color c) => Color.FromArgb(255, c.R, c.G, c.B);

    private static Windows.Foundation.Rect ComputeSegmentDirtyRect(Stroke s, int firstSeg, int lastSeg)
    {
        var pts = s.Points;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        float maxHalfW = s.Width * 0.5f;
        for (int i = firstSeg; i <= lastSeg + 1; i++)
        {
            var p = pts[i];
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
            if (s.PressureMode)
            {
                float r = s.Width * 0.5f * Math.Max(0.2f, p.Pressure);
                if (r > maxHalfW) maxHalfW = r;
            }
        }
        float pad = maxHalfW + 2f;
        return new Windows.Foundation.Rect(
            minX - pad, minY - pad,
            (maxX - minX) + pad * 2, (maxY - minY) + pad * 2);
    }

    private void EnsureLiveMaskFor(Stroke s)
    {
        var dpi = _liveCanvas.DpiScale;
        bool dpiChanged = _liveMask is not null && Math.Abs(_liveMaskDpi - dpi) > 0.001f;
        bool strokeChanged = _liveMaskStrokeId != s.Id;

        if (_liveMask is not null && !dpiChanged && !strokeChanged) return;

        var device = _liveCanvas.Device;
        if (device is null)
        {
            // Device isn't ready (CreateResources hasn't fired). Defer.
            return;
        }

        if (_liveMask is null || dpiChanged)
        {
            _liveMask?.Dispose();
            _liveMask = new CanvasRenderTarget(
                device, (float)Page.Width, (float)Page.Height, 96f * dpi);
            _liveMaskDpi = dpi;
        }
        else
        {
            // Same DPI, just a new stroke — wipe the existing target.
            using var clr = _liveMask.CreateDrawingSession();
            clr.Clear(Color.FromArgb(0, 0, 0, 0));
        }
        _liveMaskStrokeId = s.Id;
        _liveMaskPaintedPoints = 0;
    }

    private void DisposeLiveMask()
    {
        _liveMask?.Dispose();
        _liveMask = null;
        _liveMaskStrokeId = null;
        _liveMaskPaintedPoints = 0;
    }

    public void SetTemplate(TemplateSettings template)
    {
        PageTemplate = template;
        _canvas.Invalidate();
    }

    // Render the main Win2D backing store at a higher pixel density so it stays
    // crisp when the parent ScrollViewer zooms in. The LIVE overlay deliberately
    // stays at 1× regardless of zoom — keeps per-vsync work tiny, and the
    // committed stroke re-renders at full DPI as soon as the pen lifts, so the
    // soft-while-drawing effect is brief and only visible mid-stroke.
    public void SetDpiScale(float scale)
    {
        if (scale < 1f) scale = 1f;
        const float liveTarget = 1f;
        bool mainChanged = Math.Abs(_canvas.DpiScale - scale) > 0.01f;
        bool liveChanged = Math.Abs(_liveCanvas.DpiScale - liveTarget) > 0.01f;
        if (!mainChanged && !liveChanged) return;
        if (mainChanged) _canvas.DpiScale = scale;
        if (liveChanged) _liveCanvas.DpiScale = liveTarget;
    }

    private void OnCreateResources(CanvasVirtualControl sender, CanvasCreateResourcesEventArgs args)
        => args.TrackAsyncAction(LoadResourcesAsync(sender).AsAsyncAction());

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
        var missing = Page.Images.Where(i => !_imageCache.ContainsKey(i.Id)).ToList();
        if (missing.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var img in missing) await EnsureImageLoadedAsync(sender, img);
                DispatcherQueue.TryEnqueue(() => sender.Invalidate());
            });
        }

        // One drawing session per dirty region — Win2D clips drawing to that
        // region's tiles. Re-rendering the full page per region wastes a bit of
        // CPU on path tessellation for off-region strokes, but the GPU side is
        // bounded by the dirty rect.
        foreach (var region in args.InvalidatedRegions)
        {
            using var ds = sender.CreateDrawingSession(region);
            Renderer.DrawPage(ds, sender, Page, PageTemplate, _bgBitmap, _imageCache, Context.EditingTextId,
                              previewExtLeft: _previewExtLeft, previewExtRight: _previewExtRight);

            if (_selectedTextRuns.Count > 0)
            {
                var hi = Color.FromArgb(110, 91, 107, 255);
                foreach (var r in _selectedTextRuns)
                    ds.FillRectangle((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height, hi);
            }

            if (ReferenceEquals(Context.CurrentPage, Page))
            {
                if (Context.SelectionRect is { } r)
                {
                    ds.DrawRectangle(r.X, r.Y, r.W, r.H, Color.FromArgb(180, 91, 107, 255), 1.4f);
                    ds.FillRectangle(r.X, r.Y, r.W, r.H, Color.FromArgb(40, 91, 107, 255));
                }
                if (Context.SelectionLasso is { Count: > 1 } poly)
                {
                    for (int i = 1; i < poly.Count; i++)
                        ds.DrawLine(poly[i - 1], poly[i], Color.FromArgb(220, 91, 107, 255), 1.4f);
                }
                if (HasCommittedSelection())
                {
                    DrawSelectionVisuals(ds);
                }
            }
        }
    }

    // Called from PenTool/HighlighterTool via ctx.CommitStrokeAt on stroke
    // commit. Repaints only the just-committed stroke's bbox on the main
    // canvas instead of the whole page — that's the difference between a
    // sub-ms commit and one that blocks the UI thread for ~10–20 ms.
    public void CommitStrokeRedraw(Bbox bbox)
    {
        double x = Math.Max(0, bbox.X);
        double y = Math.Max(0, bbox.Y);
        double w = Math.Min(Page.Width,  bbox.X + bbox.W) - x;
        double h = Math.Min(Page.Height, bbox.Y + bbox.H) - y;
        if (w <= 0 || h <= 0) return;
        _canvas.Invalidate(new Windows.Foundation.Rect(x, y, w, h));
    }

    private void OnLiveRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        var s = Context.ActiveStroke;
        if (_liveMask is null || s is null || !ReferenceEquals(Context.CurrentPage, Page))
        {
            // Stroke is gone — make sure invalidated tiles are cleared (transparent).
            foreach (var region in args.InvalidatedRegions)
            {
                using var clear = sender.CreateDrawingSession(region);
                clear.Clear(Color.FromArgb(0, 0, 0, 0));
            }
            return;
        }

        byte rawAlpha = s.Color.A;
        float opacity = rawAlpha / 255f;
        if (s.Kind == StrokeKind.Highlighter)
            opacity = Math.Min(140f, rawAlpha == 255 ? 110f : rawAlpha) / 255f;
        bool needsLayer = opacity < 0.99f;

        Vector2? lastReal = s.Points.Count >= 1
            ? new Vector2(s.Points[^1].X, s.Points[^1].Y)
            : (Vector2?)null;
        var predTip = _predictedTip;
        var lineColor = OpaqueColor(s.Color);

        // One drawing session per invalidated region — Win2D clips drawing to
        // that region's tiles, so DrawImage(_liveMask) only touches pixels inside.
        foreach (var region in args.InvalidatedRegions)
        {
            using var ds = sender.CreateDrawingSession(region);
            CanvasActiveLayer? layer = needsLayer ? ds.CreateLayer(opacity) : null;
            try
            {
                ds.DrawImage(_liveMask);
                // Predicted tail on top of the mask. The session is clipped to
                // the region, so this is a no-op when prediction is elsewhere.
                if (predTip is { } tip && lastReal is { } lr)
                {
                    ds.DrawLine(lr, tip, lineColor, s.Width);
                }
            }
            finally
            {
                layer?.Dispose();
            }
        }
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
        if (!PalmRejection.Accept(e)) { e.Handled = true; return; }
        // Fingers never trigger our tools — the outer ScrollViewer's DirectManipulation
        // handles touch pan + pinch-zoom; we just refuse to draw.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;
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
                    _canvas.Invalidate();
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
                StartDrag(DragMode.Move, p);
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
                StartDrag(DragMode.Move, p);
                CapturePointer(e.Pointer);
                _capturedPointerId = e.Pointer.PointerId;
                e.Handled = true;
                _canvas.Invalidate();
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
            _canvas.Invalidate();
            return;
        }
        if (_drag != DragMode.None)
        {
            ContinueDrag(p);
            _canvas.Invalidate();
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
        // Tools self-invalidate (ctx.InvalidateLive while drawing, ctx.Invalidate on
        // commit), so we don't need to re-invalidate the full main canvas here.
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
        if (_capturedPointerId is null || e.Pointer.PointerId != _capturedPointerId) return;

        // While panning via middle mouse, ignore button-release events that fire because
        // the user clicked-and-released a different button (e.g., a left click during a
        // middle-mouse pan). Only end the pan when the middle button itself is released.
        if (_panning && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsMiddleButtonPressed) return;
        }

        if (_panning)
        {
            _panning = false;
            if (_inMomentaryPan)
            {
                _inMomentaryPan = false;
                MomentaryToolEnd?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (_selectingText)
        {
            _selectingText = false;
        }
        else if (_drag != DragMode.None)
        {
            _drag = DragMode.None;
            Context.Mutated?.Invoke();
        }
        else
        {
            var wasSelectTool = _activeTool is LassoTool or RectSelectTool;
            var wasRectSelect = _activeTool is RectSelectTool;
            _activeTool?.OnPointerUp(Context, ToPageSpace(e), PressureOf(e));
            TearDownLiveShapePreview();   // no-op for non-shape tools
            if (wasSelectTool) Context.SelectionChanged?.Invoke();
            if (wasRectSelect && Context.LastDrawnRectSelection.HasValue)
                RectSelectionCompleted?.Invoke(this, EventArgs.Empty);
        }
        ReleasePointerCapture(e.Pointer);
        _capturedPointerId = null;
        _activeTool = null;
        _canvas.Invalidate();
    }

    private void StartDrag(DragMode mode, Vector2 p)
    {
        _drag = mode;
        _lastMovePoint = p;
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
    }

    private void ContinueDrag(Vector2 p)
    {
        switch (_drag)
        {
            case DragMode.Move:
                var dx = p.X - _lastMovePoint.X;
                var dy = p.Y - _lastMovePoint.Y;
                TranslateSelection(dx, dy);
                _lastMovePoint = p;
                break;

            case DragMode.ResizeNW:
            case DragMode.ResizeNE:
            case DragMode.ResizeSW:
            case DragMode.ResizeSE:
                ApplyResize(p);
                break;

            case DragMode.Rotate:
                ApplyRotate(p);
                break;
        }
    }

    private void ApplyResize(Vector2 p)
    {
        // Compute new bbox from the opposing fixed corner.
        var orig = _dragStartBbox;
        float left = orig.X, top = orig.Y, right = orig.Right, bottom = orig.Bottom;
        switch (_drag)
        {
            case DragMode.ResizeNW: left = p.X; top = p.Y; break;
            case DragMode.ResizeNE: right = p.X; top = p.Y; break;
            case DragMode.ResizeSW: left = p.X; bottom = p.Y; break;
            case DragMode.ResizeSE: right = p.X; bottom = p.Y; break;
        }
        if (right - left < 8) right = left + 8;
        if (bottom - top < 8) bottom = top + 8;
        var newBox = new Bbox(left, top, right - left, bottom - top);

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
            var clone = new Stroke { Kind = s.Kind, Color = s.Color, Width = s.Width };
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
                X2 = sh.X2 + offsetX, Y2 = sh.Y2 + offsetY
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
        _canvas.Invalidate();
    }

    public void ClearSelection()
    {
        bool hadSelection = HasCommittedSelection();
        Context.SelectedStrokeIds.Clear();
        Context.SelectedShapeIds.Clear();
        Context.SelectedTextIds.Clear();
        Context.SelectedImageIds.Clear();
        Context.SelectionRect = null;
        Context.SelectionLasso = null;
        _canvas.Invalidate();
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
        _canvas.Invalidate();
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
        _liveCanvas.Width = newW;
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
        _liveCanvas.Width  = newW;
        _overlay.Width     = newW;
        var root = (Grid)Content;
        root.Width = newW;
        Width = newW;

        // Reposition right handle for new width.
        if (_rightExtHandle is not null)
            Canvas.SetLeft(_rightExtHandle, newW - ExtHandleWidth);

        // Drop the live mask — it was sized to the old width.
        DisposeLiveMask();

        _canvas.Invalidate();
    }
}
