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
        // Several pickers share the recent-colors list (toolbar + selection bar),
        // so refresh on open to pick up colors added through another one.
        ColorFlyout.Opening += (_, __) => Rebuild();
    }

    private const int MaxRecentCustomColors = 3;

    private static bool SameRgb(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    private bool MatchesPreset(Color c)
    {
        foreach (var hex in _presets)
            if (TryParseHex(hex, out var p) && SameRgb(p, c)) return true;
        return false;
    }

    // Newest-first recent custom colors, skipping any that match a current preset.
    private List<Color> RecentColors()
    {
        var list = new List<Color>();
        foreach (var hex in App.Services.Settings.Current.RecentCustomColors)
            if (TryParseHex(hex, out var c) && !MatchesPreset(c) && !list.Exists(x => SameRgb(x, c)))
                list.Add(c);
        return list;
    }

    private void RememberCustomColor(Color c)
    {
        if (MatchesPreset(c)) return;
        var settings = App.Services.Settings.Current;
        var list = settings.RecentCustomColors;
        list.RemoveAll(h => TryParseHex(h, out var x) && SameRgb(x, c));
        list.Insert(0, ToHex(c));
        if (list.Count > MaxRecentCustomColors) list.RemoveRange(MaxRecentCustomColors, list.Count - MaxRecentCustomColors);
        App.Services.Settings.Save();
    }

    private static bool TryParseHex(string hex, out Color c)
    {
        try { c = ParseHex(hex); return true; }
        catch { c = default; return false; }
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

        // Flyout content: preset swatches in a row, then up to 3 recent custom
        // colors (behind a thin divider), then a custom-color button.
        FlyoutPanel.Children.Clear();
        foreach (var hex in _presets)
            if (TryParseHex(hex, out var c)) AddSwatch(c, null);

        var recent = RecentColors();
        if (recent.Count > 0)
        {
            bool vertical = FlyoutPanel.Orientation == Orientation.Vertical;
            FlyoutPanel.Children.Add(new Border
            {
                Background = borderBrush,
                Width = vertical ? 24 : 1,
                Height = vertical ? 1 : 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            foreach (var c in recent) AddSwatch(c, "Recent custom color");
        }

        void AddSwatch(Color c, string? tooltip)
        {
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
            if (tooltip is not null) ToolTipService.SetToolTip(swatch, tooltip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch,
                (tooltip ?? $"Color #{c.R:X2}{c.G:X2}{c.B:X2}") + (active ? ", selected" : ""));
            var captured = c;
            swatch.Click += (_, __) =>
            {
                ColorFlyout.Hide();
                SetCurrent(captured, fire: true);
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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(custom, "Custom color");
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
            {
                RememberCustomColor(picker.Color);
                SetCurrent(picker.Color, fire: true);
            }
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
