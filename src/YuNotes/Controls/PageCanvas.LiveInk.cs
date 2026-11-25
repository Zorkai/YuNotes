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

    // Live shape preview — a lightweight XAML element in the overlay that acts
    // as the rubber-band while the user is dragging out a new shape.
    private UIElement? _liveShapePreview;
    private string? _liveShapeId;

    // Live-stroke backend: dirty-rect draws into a virtualized DComp surface
    // (one XAML Image at the bottom of the overlay). Created lazily on the
    // first stroke. Replaced the retained-mode XAML shape overlays (chunked
    // Polyline / filled-ribbon Path): swapping a shape's geometry every sample
    // re-rasterized it at screen scale and forced DWM to recomposite the whole
    // window per frame (~half the drawing-time GPU cost; see
    // prototypes/InkPerfLab).
    private LiveInkSurface? _liveInk;
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
}
