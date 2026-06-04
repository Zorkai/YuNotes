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

        foreach (var w in _presets)
        {
            double dotSize = Math.Max(3, (w / maxPreset) * maxDot);
            bool isActive = Math.Abs(w - _active) < 0.01;
            var dot = new Ellipse
            {
                Width = dotSize, Height = dotSize,
                Fill = new SolidColorBrush(isActive
                    ? (Color)((SolidColorBrush)Application.Current.Resources["AppAccentBrush"]).Color
                    : (Color)((SolidColorBrush)Application.Current.Resources["AppTextPrimaryBrush"]).Color)
            };
            var chip = new Button
            {
                Width = _chipSize, Height = _chipSize,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = isActive
                    ? (Brush)Application.Current.Resources["AppAccentSoftBrush"]
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
