using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using YuNotes.Models;

namespace YuNotes.Controls;

// ─── Insert space ───────────────────────────────────────────────────────────
// With the Page size tool, dragging down inside a page opens blank space at
// that line: everything below (the PDF too) moves down and the page grows, so
// there's room to write between two questions. Dragging up over space opened
// earlier closes it again. The page itself changes on release
// (InkCanvasControl.OnSpaceDragCompleted); while dragging, a band shows what
// will be added (blue) or removed (red).
public sealed partial class PageCanvas
{
    // Fired on release: (page row the drag started at, how far it went —
    // positive opens space below that row, negative closes space above it).
    public event EventHandler<(double Y, double Delta)>? SpaceDragCompleted;

    private bool _spacing;
    private double _spaceStartY;
    private double _spaceDelta;
    private Rectangle? _spaceBand;
    private Line? _spaceLine;
    private Border? _spaceLabelBorder;
    private TextBlock? _spaceLabel;

    // Drags shorter than this are taps, not space.
    private const double MinSpace = 6;

    private static readonly Color SpaceAddColor = Color.FromArgb(255, 91, 107, 255);
    private static readonly Color SpaceRemoveColor = Color.FromArgb(255, 220, 60, 60);

    private void BeginSpace(System.Numerics.Vector2 p)
    {
        _spacing = true;
        _spaceStartY = Math.Clamp(p.Y, 0, Page.Height);
        _spaceDelta = 0;
        EnsureSpaceOverlay();
        UpdateSpacePreview();
    }

    private void ContinueSpace(System.Numerics.Vector2 p)
    {
        // Whole units, so the band doesn't shimmer under a pen.
        _spaceDelta = Math.Round(p.Y - _spaceStartY);
        UpdateSpacePreview();
    }

    // How much inserted space the band between `top` and `bottom` covers —
    // what dragging up over it would close.
    private double RemovableSpace(double top, double bottom)
    {
        double total = 0;
        foreach (var (gy, gh) in PageGaps.GapRows(Page))
            total += Math.Max(0, Math.Min(bottom, gy + gh) - Math.Max(top, gy));
        return total;
    }

    private void EndSpace(bool commit)
    {
        if (!_spacing) return;
        _spacing = false;
        double delta = _spaceDelta;
        TearDownSpaceOverlay();
        if (!commit || Math.Abs(delta) < MinSpace) return;
        if (delta < 0 && RemovableSpace(_spaceStartY + delta, _spaceStartY) < 0.5) return;
        SpaceDragCompleted?.Invoke(this, (_spaceStartY, delta));
    }

    private void EnsureSpaceOverlay()
    {
        if (_spaceBand is not null) return;
        _spaceBand = new Rectangle { IsHitTestVisible = false, StrokeThickness = 0 };
        _spaceLine = new Line
        {
            IsHitTestVisible = false,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        _spaceLabel = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            IsHitTestVisible = false,
        };
        _spaceLabelBorder = new Border
        {
            Child = _spaceLabel,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
            IsHitTestVisible = false,
        };
        foreach (var el in new UIElement[] { _spaceBand, _spaceLine, _spaceLabelBorder })
        {
            Canvas.SetZIndex(el, 60);
            _overlay.Children.Add(el);
        }
    }

    private void UpdateSpacePreview()
    {
        if (_spaceBand is null || _spaceLine is null || _spaceLabel is null || _spaceLabelBorder is null) return;
        double w = Page.Width, d = _spaceDelta, y = _spaceStartY;
        bool adding = d >= 0;
        double removable = adding ? 0 : RemovableSpace(y + d, y);
        var color = adding ? SpaceAddColor : SpaceRemoveColor;

        _spaceBand.Width = w;
        _spaceBand.Height = Math.Abs(d);
        _spaceBand.Fill = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B));
        Canvas.SetLeft(_spaceBand, 0);
        Canvas.SetTop(_spaceBand, adding ? y : y + d);

        _spaceLine.X1 = 0; _spaceLine.X2 = w;
        _spaceLine.Y1 = _spaceLine.Y2 = y;
        _spaceLine.Stroke = new SolidColorBrush(color);

        _spaceLabel.Text = adding
            ? (d < MinSpace ? "Drag down to add space, up to remove it" : $"+ {d:0} space")
            : removable < 0.5 ? "No added space here to remove"
            : $"− {removable:0} space";
        _spaceLabelBorder.Background = new SolidColorBrush(color);
        Canvas.SetLeft(_spaceLabelBorder, 16);
        Canvas.SetTop(_spaceLabelBorder, adding ? y + Math.Max(d, 0) + 6 : y + d - 34);
    }

    private void TearDownSpaceOverlay()
    {
        foreach (var el in new UIElement?[] { _spaceBand, _spaceLine, _spaceLabelBorder })
            if (el is not null) _overlay.Children.Remove(el);
        _spaceBand = null;
        _spaceLine = null;
        _spaceLabelBorder = null;
        _spaceLabel = null;
    }
}
