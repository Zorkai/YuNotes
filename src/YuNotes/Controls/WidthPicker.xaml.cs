using System;
using System.Collections.Generic;
using Microsoft.UI;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace YuNotes.Controls;

public sealed partial class WidthPickerControl : UserControl
{
    public event EventHandler<double>? WidthChanged;
    private double[] _presets = new double[] { 1.0, 2.5, 4.0, 7.0, 12.0 };
    private double _active;
    private double _chipSize = 36;
    private double _maxDot = 22;

    public WidthPickerControl()
    {
        InitializeComponent();
        // Re-resolve the palette when the effective theme flips (e.g. the user
        // forces Light while Windows is Dark) — the brushes are picked in code
        // below, so they don't auto-update the way {ThemeResource} bindings do.
        ActualThemeChanged += (_, __) => Rebuild();
        Rebuild();
    }

    // Set the 5 preset values for the active tool (e.g. pen uses smaller, eraser uses larger).
    public void SetPresets(double[] presets, double active)
    {
        if (presets is null || presets.Length == 0) return;
        _presets = presets;
        _active = active;
        Rebuild();
    }

    public void SetActive(double v)
    {
        _active = v;
        Rebuild();
    }

    public void SetChipSize(double chipSize, double maxDotSize)
    {
        _chipSize = chipSize;
        _maxDot = maxDotSize;
        Rebuild();
    }

    public void SetOrientation(Orientation orientation)
    {
        Root.Orientation = orientation;
    }

    private void Rebuild()
    {
        Root.Children.Clear();
        // pick the dot size based on the largest preset so scale stays consistent
        double maxPreset = _presets[0];
        foreach (var p in _presets) if (p > maxPreset) maxPreset = p;
        double maxDot = _maxDot;

        // Resolve against THIS control's actual theme, not the app/OS theme, so a
        // forced Light/Dark override colors the dots for what's actually on screen.
        var accent     = ThemeBrushes.Color(ActualTheme, "AppAccentBrush");
        var primary    = ThemeBrushes.Color(ActualTheme, "AppTextPrimaryBrush");
        var activeChip = ThemeBrushes.Brush(ActualTheme, "AppAccentSoftBrush");

        foreach (var w in _presets)
        {
            double dotSize = Math.Max(3, (w / maxPreset) * maxDot);
            bool isActive = Math.Abs(w - _active) < 0.01;
            var dot = new Ellipse
            {
                Width = dotSize, Height = dotSize,
                Fill = new SolidColorBrush(isActive ? accent : primary)
            };
            var chip = new Button
            {
                Width = _chipSize, Height = _chipSize,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = isActive
                    ? activeChip
                    : new SolidColorBrush(Colors.Transparent),
                Content = dot,
                Margin = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var captured = w;
            chip.Click += (_, __) =>
            {
                _active = captured;
                Rebuild();
                WidthChanged?.Invoke(this, captured);
            };
            Root.Children.Add(chip);
        }
    }
}
