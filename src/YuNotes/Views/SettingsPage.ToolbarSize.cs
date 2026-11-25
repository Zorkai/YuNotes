using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using YuNotes.Models;

namespace YuNotes.Views;

// Appearance → Toolbar size: a slider (60–160 %, 100 % = the default) with a
// live preview of the floating toolbar, drawn at the exact sizes the editor
// will use in this window (ToolbarMetrics), with the user's own tools.
public sealed partial class SettingsPage
{
    private void InitToolbarSize()
    {
        ToolbarSizeSlider.ValueChanged += (s, e) =>
        {
            int pct = SnappedToolbarPercent();
            ToolbarSizeLabel.Text = $"{pct}%";
            RebuildToolbarSizePreview();
            if (_loading) return;
            App.Services.Settings.Current.ToolbarScale = ToolbarMetrics.FromPercent(pct);
            OnChanged(s);
        };
        // The size tier follows the window width, like in the editor.
        SizeChanged += (_, __) => RebuildToolbarSizePreview();
    }

    private int SnappedToolbarPercent() =>
        (int)(Math.Round(ToolbarSizeSlider.Value / ToolbarMetrics.StepPercent) * ToolbarMetrics.StepPercent);

    private void LoadToolbarSize(AppSettings s)
    {
        int pct = Math.Clamp(ToolbarMetrics.PercentOf(ToolbarMetrics.ScaleOf(s)), ToolbarMetrics.MinPercent, ToolbarMetrics.MaxPercent);
        ToolbarSizeSlider.Value = pct;
        ToolbarSizeLabel.Text = $"{pct}%";
        RebuildToolbarSizePreview();
    }

    private void RebuildToolbarSizePreview()
    {
        if (ToolbarSizePreviewRow is null || _drawingTools is null) return;
        var res = Application.Current.Resources;
        var font = (FontFamily)res["MaterialIconsFamily"];
        var text = (Brush)res["AppTextPrimaryBrush"];
        double windowWidth = XamlRoot?.Size.Width is double w and > 0 ? w : 1200;
        var m = ToolbarMetrics.For(windowWidth, ToolbarMetrics.FromPercent(SnappedToolbarPercent()));

        var row = ToolbarSizePreviewRow;
        row.Children.Clear();
        Grid Icon(string glyph, bool selected = false)
        {
            var g = new Grid
            {
                Width = m.Button, Height = m.Button,
                CornerRadius = new CornerRadius(m.Button / 2),
                Background = selected ? (Brush)res["AppAccentSoftBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            g.Children.Add(new FontIcon
            {
                Glyph = glyph, FontFamily = font, FontSize = m.Icon,
                Foreground = selected ? (Brush)res["AppAccentBrush"] : text,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            return g;
        }
        UIElement Sep() => new Border
        {
            Width = 1, Height = Math.Max(12, m.Button * 0.62), Margin = new Thickness(3, 0, 3, 0),
            Background = (Brush)res["AppBorderBrush"], VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(Icon(""));   // drag handle
        row.Children.Add(Sep());
        var groups = App.Services.Settings.Current.ToolGroups;
        var placed = new System.Collections.Generic.HashSet<string>();
        foreach (var t in _drawingTools.Where(t => t.IsVisible))
        {
            var g = groups.FirstOrDefault(x => x.Tools.Contains(t.Key));
            if (g is not null && !placed.Add(g.Id)) continue;
            // The pen shows as the selected tool, as it usually is.
            row.Children.Add(Icon(g?.Glyph ?? t.Glyph, selected: g is null && t.Key == "Pen"));
        }
        row.Children.Add(Sep());
        // Current color and stroke width, like the pickers at the end of the bar.
        var colorGrid = new Grid { Width = m.Button, Height = m.Button };
        colorGrid.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = m.ColorButton, Height = m.ColorButton,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1E, 0x88, 0xE5)),
            Stroke = (Brush)res["AppBorderBrush"], StrokeThickness = 1,
        });
        row.Children.Add(colorGrid);
        var chip = new Border
        {
            Width = m.Chip, Height = m.Chip, CornerRadius = new CornerRadius(m.Chip / 2),
            Background = (Brush)res["AppSurfaceVariantBrush"], VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
            Child = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = m.ChipDot * 0.45, Height = m.ChipDot * 0.45, Fill = text,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        row.Children.Add(chip);

        ToolbarSizePreview.CornerRadius = new CornerRadius(m.Button / 2 + 5);
    }
}
