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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(boldBtn, "Bold");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(italicBtn, "Italic");
        ToolTipService.SetToolTip(colorBtn, "Text color");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(colorBtn, "Text color");
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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(shrinkBtn, "Decrease font size");
        ToolTipService.SetToolTip(growBtn, "Increase font size");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(growBtn, "Increase font size");

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
            t.Text = (_editor.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            t.Width = Math.Max(_editor.ActualWidth, t.Width);
            // Fit the height to the text: grows for new lines, shrinks when
            // text was deleted or the font made smaller.
            t.Height = FitTextHeight(t);
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
}
