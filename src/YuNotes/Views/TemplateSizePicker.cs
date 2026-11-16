using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using YuNotes.Models;

namespace YuNotes.Views;

/// <summary>
/// "Size" slider for a page template's line/grid/dot spacing, with a live
/// preview drawn at a fixed zoom so the chosen size reads true relative to
/// other values. Shared by the editor's Page template dialog and the
/// New note dialog. Hidden for the Blank template (nothing to size).
/// </summary>
internal sealed class TemplateSizePicker
{
    // Spacing in page units (A4 page = 1240 wide ≈ 210 mm, so 1 unit ≈ 0.169 mm).
    public const double MinSpacing = 12;
    public const double MaxSpacing = 96;
    public const double DefaultSpacing = 32;
    private const double MmPerUnit = 210.0 / 1240.0;

    // The preview shows a crop of the page at this zoom: big enough to see the
    // pattern at small sizes, small enough to show several cells at large ones.
    private const double PreviewZoom = 0.6;
    private const double PreviewW = 300, PreviewH = 150;

    private readonly StackPanel _root = new() { Spacing = 6 };
    private readonly Slider _slider;
    private readonly TextBlock _valueText;
    private readonly Border _previewHost = new();
    private TemplateKind _kind;

    public UIElement View => _root;
    public double Spacing => _slider.Value;

    public TemplateSizePicker(TemplateKind kind, double spacing)
    {
        _kind = kind;
        var secondary = (Brush)Application.Current.Resources["AppTextSecondaryBrush"];

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Size", FontSize = 13 });
        _valueText = new TextBlock { FontSize = 13, Foreground = secondary };
        Grid.SetColumn(_valueText, 1);
        header.Children.Add(_valueText);

        _slider = new Slider
        {
            Minimum = MinSpacing,
            Maximum = MaxSpacing,
            StepFrequency = 2,
            Value = Math.Clamp(spacing, MinSpacing, MaxSpacing),
            IsThumbToolTipEnabled = false,
        };
        _slider.ValueChanged += (_, __) => Refresh();

        _root.Children.Add(header);
        _root.Children.Add(_slider);
        _root.Children.Add(_previewHost);
        Refresh();
    }

    public void SetKind(TemplateKind kind)
    {
        _kind = kind;
        Refresh();
    }

    private void Refresh()
    {
        _root.Visibility = _kind == TemplateKind.Blank ? Visibility.Collapsed : Visibility.Visible;
        _valueText.Text = $"{Spacing * MmPerUnit:0.0} mm";
        _previewHost.Child = EditorPage.BuildTemplatePreview(_kind, PreviewW, PreviewH, Spacing * PreviewZoom);
    }
}
