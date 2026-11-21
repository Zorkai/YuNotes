using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using YuNotes.Input;

namespace YuNotes.Controls;

// Pressure curve editor: the curve from light (left) to firm (right) pressure,
// with two handles to drag. ShowLive() puts a dot on the curve for the pressure
// the pen is applying right now.
public sealed partial class PressureCurveEditor : Grid
{
    private const double Pad = 12;
    private const double HandleSize = 14;

    private readonly Canvas _canvas = new();
    private readonly Line _diagonal = new() { StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } };
    private readonly Line _arm1 = new() { StrokeThickness = 1 };
    private readonly Line _arm2 = new() { StrokeThickness = 1 };
    private readonly Polyline _curve = new() { StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
    private readonly Ellipse _h1 = MakeHandle();
    private readonly Ellipse _h2 = MakeHandle();
    private readonly Ellipse _live = new() { Width = 10, Height = 10, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _lightLabel = new() { Text = "Light", FontSize = 11 };
    private readonly TextBlock _firmLabel = new() { Text = "Firm", FontSize = 11 };

    private double _x1 = 1 / 3.0, _y1 = 1 / 3.0, _x2 = 2 / 3.0, _y2 = 2 / 3.0;
    private int _dragging;   // 0 none, 1 or 2 = handle

    public event EventHandler? CurveChanged;

    public double X1 => _x1;
    public double Y1 => _y1;
    public double X2 => _x2;
    public double Y2 => _y2;

    public PressureCurveEditor()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        MinHeight = 150;
        _canvas.Children.Add(_diagonal);
        _canvas.Children.Add(_arm1);
        _canvas.Children.Add(_arm2);
        _canvas.Children.Add(_curve);
        _canvas.Children.Add(_live);
        _canvas.Children.Add(_h1);
        _canvas.Children.Add(_h2);
        _canvas.Children.Add(_lightLabel);
        _canvas.Children.Add(_firmLabel);
        Children.Add(_canvas);

        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => _dragging = 0;
    }

    private static Ellipse MakeHandle() => new() { Width = HandleSize, Height = HandleSize, StrokeThickness = 2 };

    public void SetCurve(double x1, double y1, double x2, double y2)
    {
        _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
        Redraw();
    }

    public void ShowLive(double input)
    {
        double w = PlotW, h = PlotH;
        if (w <= 0) return;
        double y = PressureCurve.Evaluate(input, _x1, _y1, _x2, _y2);
        var pt = ToScreen(input, y);
        Canvas.SetLeft(_live, pt.X - 5);
        Canvas.SetTop(_live, pt.Y - 5);
        _live.Visibility = Visibility.Visible;
    }

    public void HideLive() => _live.Visibility = Visibility.Collapsed;

    private double PlotW => ActualWidth - Pad * 2;
    private double PlotH => ActualHeight - Pad * 2 - 14;   // room for axis labels

    private Point ToScreen(double x, double y) => new(Pad + x * PlotW, Pad + (1 - y) * PlotH);

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void Redraw()
    {
        if (PlotW <= 0 || PlotH <= 0) return;
        var accent = Res("AppAccentBrush");
        var border = Res("AppBorderBrush");
        var muted = Res("AppTextSecondaryBrush");
        var surface = Res("AppSurfaceBrush");

        var a = ToScreen(0, 0);
        var b = ToScreen(1, 1);
        _diagonal.X1 = a.X; _diagonal.Y1 = a.Y; _diagonal.X2 = b.X; _diagonal.Y2 = b.Y;
        _diagonal.Stroke = border;

        var p1 = ToScreen(_x1, _y1);
        var p2 = ToScreen(_x2, _y2);
        _arm1.X1 = a.X; _arm1.Y1 = a.Y; _arm1.X2 = p1.X; _arm1.Y2 = p1.Y;
        _arm2.X1 = b.X; _arm2.Y1 = b.Y; _arm2.X2 = p2.X; _arm2.Y2 = p2.Y;
        _arm1.Stroke = _arm2.Stroke = muted;
        _arm1.Opacity = _arm2.Opacity = 0.5;

        var pts = new PointCollection();
        const int N = 48;
        for (int i = 0; i <= N; i++)
        {
            double x = i / (double)N;
            pts.Add(ToScreen(x, PressureCurve.Evaluate(x, _x1, _y1, _x2, _y2)));
        }
        _curve.Points = pts;
        _curve.Stroke = accent;

        foreach (var (h, p) in new[] { (_h1, p1), (_h2, p2) })
        {
            h.Fill = surface;
            h.Stroke = accent;
            Canvas.SetLeft(h, p.X - HandleSize / 2);
            Canvas.SetTop(h, p.Y - HandleSize / 2);
        }
        _live.Fill = Res("AppTextPrimaryBrush");

        _lightLabel.Foreground = _firmLabel.Foreground = muted;
        Canvas.SetLeft(_lightLabel, Pad);
        Canvas.SetTop(_lightLabel, Pad + PlotH + 2);
        _firmLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_firmLabel, Pad + PlotW - _firmLabel.DesiredSize.Width);
        Canvas.SetTop(_firmLabel, Pad + PlotH + 2);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(this).Position;
        double d1 = Dist(pos, ToScreen(_x1, _y1)), d2 = Dist(pos, ToScreen(_x2, _y2));
        const double grab = 26;
        if (Math.Min(d1, d2) > grab) return;
        _dragging = d1 <= d2 ? 1 : 2;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging == 0) return;
        var pos = e.GetCurrentPoint(this).Position;
        double x = Math.Clamp((pos.X - Pad) / PlotW, 0, 1);
        double y = Math.Clamp(1 - (pos.Y - Pad) / PlotH, 0, 1);
        if (_dragging == 1) { _x1 = x; _y1 = y; } else { _x2 = x; _y2 = y; }
        Redraw();
        CurveChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging == 0) return;
        _dragging = 0;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
