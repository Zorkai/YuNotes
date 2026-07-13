using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace YuNotes.Controls;

public sealed partial class ColorPickerControl : UserControl
{
    public event EventHandler<Color>? ColorChanged;
    private Color _current = Microsoft.UI.Colors.Black;
    private IList<string> _presets = new List<string>();

    public ColorPickerControl()
    {
        InitializeComponent();
        // The swatch borders/glyphs are brushed in code (below), so re-resolve
        // them when the effective theme flips under a forced Light/Dark override.
        ActualThemeChanged += (_, __) => Rebuild();
    }

    public void SetPresets(IList<string> hexes, Color current)
    {
        _presets = hexes;
        _current = current;
        Rebuild();
    }

    public void SetButtonSize(double size)
    {
        CurrentColorBtn.Width = size;
        CurrentColorBtn.Height = size;
        CurrentColorBtn.CornerRadius = new CornerRadius(size / 2);
    }

    public void SetOrientation(Orientation orientation)
    {
        FlyoutPanel.Orientation = orientation;
        // Bottom for horizontal toolbars, right for vertical — keeps the flyout
        // out of the user's way.
        ColorFlyout.Placement = orientation == Orientation.Vertical
            ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Right
            : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom;
    }

    public void SetCurrent(Color c, bool fire)
    {
        _current = c;
        Rebuild();
        if (fire) ColorChanged?.Invoke(this, c);
    }

    private void Rebuild()
    {
        // Resolve chrome brushes against this control's actual theme (not the app
        // theme) so a forced Light/Dark override reads correctly on screen.
        var borderBrush = ThemeBrushes.Brush(ActualTheme, "AppBorderBrush");
        var accentBrush = ThemeBrushes.Brush(ActualTheme, "AppAccentBrush");
        var textBrush   = ThemeBrushes.Brush(ActualTheme, "AppTextPrimaryBrush");

        // Trigger button — the current color fills the button so the active
        // color is visible at a glance without opening the flyout.
        CurrentColorBtn.Background = new SolidColorBrush(_current);
        CurrentColorBtn.BorderBrush = borderBrush;

        // Flyout content: preset swatches in a row, then a custom-color button.
        FlyoutPanel.Children.Clear();
        foreach (var hex in _presets)
        {
            var c = ParseHex(hex);
            bool active = ColorEqual(c, _current);
            var swatch = new Button
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Style = (Style)Application.Current.Resources["SwatchButtonStyle"],
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(active ? 2.5 : 1),
                BorderBrush = active ? accentBrush : borderBrush
            };
            if (active)
            {
                // Checkmark in whichever of dark/white reads against the swatch.
                bool darkSwatch = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 140;
                swatch.Content = new FontIcon
                {
                    Glyph = "",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(darkSwatch
                        ? Colors.White
                        : Color.FromArgb(0xFF, 0x1B, 0x1F, 0x2A))
                };
            }
            var captured = c;
            swatch.Click += (_, __) =>
            {
                SetCurrent(captured, fire: true);
                ColorFlyout.Hide();
            };
            FlyoutPanel.Children.Add(swatch);
        }

        // Custom-color picker entry.
        var custom = new Button
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Style = (Style)Application.Current.Resources["SwatchButtonStyle"],
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1),
            BorderBrush = borderBrush,
            Foreground = textBrush,
            Content = new FontIcon { Glyph = "", FontSize = 14 },
        };
        ToolTipService.SetToolTip(custom, "Custom color");
        custom.Click += async (_, __) =>
        {
            ColorFlyout.Hide();
            var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
            {
                ColorSpectrumShape = ColorSpectrumShape.Ring,
                IsAlphaEnabled = false,
                IsMoreButtonVisible = true,
                Color = _current
            };
            var dlg = new ContentDialog
            {
                Title = "Pick color",
                Content = picker,
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                SetCurrent(picker.Color, fire: true);
        };
        FlyoutPanel.Children.Add(custom);
    }

    private static bool ColorEqual(Color a, Color b) =>
        a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;

    public static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 0xFF, r, g, b;
        if (hex.Length == 8)
        {
            a = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
            r = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
            g = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
            b = byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber);
        }
        else
        {
            r = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
            g = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
            b = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
        }
        return Color.FromArgb(a, r, g, b);
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
