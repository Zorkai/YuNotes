using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Views;

public sealed partial class EditorPage
{
    // Template dialog with visual previews and apply-current / apply-all choice
    private async Task ChooseTemplateAsync()
    {
        if (_doc is null) return;

        var kinds = (TemplateKind[])Enum.GetValues(typeof(TemplateKind));
        var selected = _doc.Template.Kind;
        var sizePicker = new TemplateSizePicker(selected,
            (Canvas.ActivePage?.TemplateOverride ?? _doc.Template).Spacing);

        var previewsPanel = new ItemsControl();
        previewsPanel.ItemsPanel = MakeWrap();
        var items = new List<UIElement>();
        ContentDialog? dlg = null;
        // The page's own custom template (if it has one) starts selected.
        var pageCustom = Services.TemplateLibrary.Find(Canvas.ActivePage);
        var customRow = new CustomTemplateRow(this, () => dlg, pageCustom?.Id);
        bool customSelected = pageCustom is not null;
        RadioButtons? scopeChoice = null;
        void HighlightBuiltIn()
        {
            foreach (Button candidate in previewsPanel.Items.OfType<Button>())
            {
                var b = (Border)candidate.Content;
                var match = !customSelected && (TemplateKind)candidate.Tag == selected;
                b.BorderThickness = new Thickness(match ? 2.5 : 1);
                b.BorderBrush = match
                    ? (Brush)Application.Current.Resources["AppAccentBrush"]
                    : (Brush)Application.Current.Resources["AppBorderBrush"];
            }
            sizePicker.View.Visibility = customSelected ? Visibility.Collapsed : Visibility.Visible;
            if (scopeChoice is not null) scopeChoice.Visibility = sizePicker.View.Visibility;
            if (dlg is not null)
                dlg.SecondaryButtonText = customSelected ? "Apply to all blank pages" : "Apply to all pages";
        }
        customRow.Selected += t => { customSelected = t is not null; HighlightBuiltIn(); };
        foreach (var k in kinds)
        {
            var preview = BuildTemplatePreview(k, 110, 140);
            var border = new Border
            {
                BorderThickness = new Thickness(!customSelected && selected == k ? 2.5 : 1),
                BorderBrush = !customSelected && selected == k
                    ? (Brush)Application.Current.Resources["AppAccentBrush"]
                    : (Brush)Application.Current.Resources["AppBorderBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6),
                Background = (Brush)Application.Current.Resources["AppSurfaceBrush"],
                Margin = new Thickness(0, 0, 8, 8),
            };
            var stack = new StackPanel();
            stack.Children.Add(preview);
            stack.Children.Add(new TextBlock { Text = k.ToString(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            border.Child = stack;

            var btn = new Button
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Content = border,
                Tag = k
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"{k} template");
            btn.Click += (s, _) =>
            {
                selected = (TemplateKind)((Button)s).Tag;
                customSelected = false;
                customRow.ClearSelection();
                sizePicker.SetKind(selected);
                HighlightBuiltIn();
            };
            items.Add(btn);
        }
        previewsPanel.ItemsSource = items;

        var root = new StackPanel { Spacing = 12, MinWidth = 540 };
        root.Children.Add(new TextBlock { Text = "Choose a template, then apply to the current page or to every page.", Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"] });
        root.Children.Add(previewsPanel);
        root.Children.Add(customRow.View);
        root.Children.Add(sizePicker.View);

        // Where to draw — only asked when the note has imported PDF pages, since
        // otherwise every page is YuNotes' own and the answer makes no difference.
        // "Safe areas" = YuNotes blank pages + page-extension strips, never over
        // the imported PDF content itself.
        bool hasPdfPages = _doc.Pages.Any(IsPdfPage);
        if (hasPdfPages)
        {
            var effective = Canvas.ActivePage?.TemplateOverride ?? _doc.Template;
            // A note that never had a template defaults to the safe choice.
            bool skipPdf = effective.Kind == TemplateKind.Blank || effective.SkipImportedPdf;
            scopeChoice = new RadioButtons
            {
                Header = "Where to draw the template",
                Items =
                {
                    "Only on YuNotes pages and page extensions (not over imported PDF content)",
                    "Everywhere, including over imported PDF content"
                },
                SelectedIndex = skipPdf ? 0 : 1
            };
            root.Children.Add(scopeChoice);
        }

        dlg = new ContentDialog
        {
            Title = "Page template",
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Apply to current page",
            SecondaryButtonText = "Apply to all pages",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        HighlightBuiltIn();
        var res = await customRow.ShowHostAsync(dlg);
        if (res == ContentDialogResult.None) return;
        if (customSelected && customRow.Current is { } custom)
        {
            await ApplyCustomTemplateAsync(custom, allPages: res == ContentDialogResult.Secondary);
            return;
        }
        bool skipImportedPdf = scopeChoice?.SelectedIndex == 0;
        // A built-in template replaces custom-template paper on the pages it goes on.
        bool removedPaper = false;
        foreach (var p in res == ContentDialogResult.Primary
                     ? new[] { Canvas.ActivePage }.OfType<NotePage>()
                     : _doc.Pages)
        {
            if (Services.TemplateLibrary.Find(p) is null) continue;
            p.BackgroundPng = null;
            removedPaper = true;
        }
        if (removedPaper) Canvas.Rebuild();

        if (res == ContentDialogResult.Primary)
        {
            // Apply to current page only — set or update this page's override.
            var page = Canvas.ActivePage;
            if (page is null) return;
            page.TemplateOverride = new TemplateSettings
            {
                Kind = selected,
                Spacing = sizePicker.Spacing,
                LineColorHex = _doc.Template.LineColorHex,
                LineThickness = _doc.Template.LineThickness,
                SkipImportedPdf = skipImportedPdf
            };
        }
        else
        {
            // Apply to all pages — set document template and drop every per-page override.
            _doc.Template.Kind = selected;
            _doc.Template.Spacing = sizePicker.Spacing;
            _doc.Template.SkipImportedPdf = skipImportedPdf;
            foreach (var p in _doc.Pages) p.TemplateOverride = null;
        }
        // Template changes alter every affected page's flattened PDF image.
        _doc.IsDirty = true;
        _doc.MarkFlattenDirty(res == ContentDialogResult.Primary ? Canvas.ActivePage : null);

        Canvas.RefreshTemplates();
        Canvas.InvalidateAll();
        App.Services.History.RecordMutation();
    }

    // A custom template is paper (a background image), so it only goes on
    // pages that have none of their own: never over an imported PDF page.
    private async Task ApplyCustomTemplateAsync(Services.CustomTemplate t, bool allPages)
    {
        if (_doc is null) return;
        var targets = allPages
            ? _doc.Pages.Where(Services.TemplateLibrary.CanTakeTemplate).ToList()
            : new[] { Canvas.ActivePage }.OfType<NotePage>().Where(Services.TemplateLibrary.CanTakeTemplate).ToList();
        if (targets.Count == 0)
        {
            await Notify("Template not applied",
                allPages
                    ? "Every page in this note is an imported PDF page. Custom templates go on blank pages; add one with Add → Blank page."
                    : "This page is an imported PDF page. Custom templates go on blank pages.");
            return;
        }
        foreach (var p in targets)
        {
            Services.TemplateLibrary.ApplyTo(p, t);
            p.TemplateOverride = new TemplateSettings { Kind = TemplateKind.Blank };   // no lines over the paper
            _doc.MarkFlattenDirty(p);
        }
        Canvas.Rebuild();
        App.Services.History.RecordPageListChange();
        UpdateHistoryUi();
        MarkPageSetChanged();
        if (_pagePanelOpen) _ = RefreshPagePanelAsync();
        int skipped = allPages ? _doc.Pages.Count - targets.Count : 0;
        ShowToast(skipped > 0
            ? $"Applied to {targets.Count} pages (PDF pages kept as they are)"
            : targets.Count == 1 ? "Template applied" : $"Applied to {targets.Count} pages");
    }

    // New page after a page with custom-template paper: same paper.
    private static void ContinueTemplate(NotePage newPage, NotePage? previous)
    {
        if (Services.TemplateLibrary.Find(previous) is not { } t) return;
        Services.TemplateLibrary.ApplyTo(newPage, t);
        newPage.TemplateOverride = new TemplateSettings { Kind = TemplateKind.Blank };
    }

    private static ItemsPanelTemplate MakeWrap() =>
        (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Orientation='Horizontal' /></ItemsPanelTemplate>");

    // ── Extend Page Width dialog ───────────────────────────────────────────────

    private async Task ShowExtendPageDialogAsync(NotePage page)
    {
        double pdfW = page.BackgroundContentWidth > 0 ? page.BackgroundContentWidth : page.Width;

        // Side selector
        var leftBtn  = new RadioButton { Content = "Left",  GroupName = "ExtendSide", Margin = new Thickness(0, 0, 16, 0) };
        var rightBtn = new RadioButton { Content = "Right", GroupName = "ExtendSide" };
        rightBtn.IsChecked = true;
        var sideRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        sideRow.Children.Add(leftBtn);
        sideRow.Children.Add(rightBtn);

        // Amount slider with preset buttons
        double initialAmount = 0;

        // Figure out existing extension to show as initial value
        if (page.BackgroundLeft > 0 || (page.BackgroundContentWidth > 0 && page.Width > page.BackgroundContentWidth + page.BackgroundLeft))
        {
            double existingLeft  = page.BackgroundLeft;
            double existingRight = page.BackgroundContentWidth > 0
                                   ? Math.Max(0, page.Width - page.BackgroundLeft - page.BackgroundContentWidth)
                                   : 0;
            if (existingLeft > 0)  { leftBtn.IsChecked  = true;  rightBtn.IsChecked = false; initialAmount = existingLeft;  }
            else if (existingRight > 0) initialAmount = existingRight;
        }

        var amountLabel = new TextBlock { Margin = new Thickness(0, 0, 0, 6) };
        void UpdateLabel(double v)
        {
            int pct = pdfW > 0 ? (int)Math.Round(v / pdfW * 100) : 0;
            amountLabel.Text = $"Amount: {(int)v} px  ({pct}% of page width)";
        }

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = pdfW * 2.5,
            StepFrequency = 1,
            Value = initialAmount,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TickFrequency = pdfW * 0.5,
            TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside
        };
        UpdateLabel(slider.Value);
        slider.ValueChanged += (_, e) => UpdateLabel(e.NewValue);

        // Preset buttons
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        (string Label, double Value)[] presets = { ("None (0)", 0), ("¼ page", pdfW * 0.25), ("½ page", pdfW * 0.5), ("1×", pdfW), ("1.5×", pdfW * 1.5), ("2×", pdfW * 2.0) };
        foreach (var (label, value) in presets)
        {
            var btn = new Button { Content = label };
            var v = value;
            btn.Click += (_, __) => slider.Value = v;
            presetRow.Children.Add(btn);
        }

        // Reset hyperlink (only shown when page already has an extension)
        bool hasExtension = page.BackgroundLeft > 0 || (page.BackgroundContentWidth > 0 && page.Width > page.BackgroundContentWidth + page.BackgroundLeft);
        var resetLink = new HyperlinkButton { Content = "Reset extension", Margin = new Thickness(0, 8, 0, 0), Visibility = hasExtension ? Visibility.Visible : Visibility.Collapsed };

        var root = new StackPanel { Spacing = 4, MinWidth = 400 };
        root.Children.Add(new TextBlock
        {
            Text = "Extend this page to the left or right. The extension area fills with the page's current template.",
            Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(new TextBlock { Text = "Direction", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        root.Children.Add(sideRow);
        root.Children.Add(amountLabel);
        root.Children.Add(slider);
        root.Children.Add(presetRow);
        root.Children.Add(resetLink);

        var dlg = new ContentDialog
        {
            Title = "Extend page width",
            Content = root,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        bool resetRequested = false;
        resetLink.Click += (_, __) => { resetRequested = true; dlg.Hide(); };

        var result = await dlg.ShowAsync();

        if (resetRequested)
        {
            // ResetPageExtension raises DocumentMutated, which already records
            // history — an explicit RecordMutation here double-recorded.
            Canvas.ResetPageExtension(page);
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        double amount = slider.Value;

        bool isLeft = leftBtn.IsChecked == true;
        // CommitPageExtension raises DocumentMutated → history records there.
        Canvas.CommitPageExtension(page, isLeft ? YuNotes.Controls.ExtendSide.Left : YuNotes.Controls.ExtendSide.Right, amount);
    }

    internal static UIElement BuildTemplatePreview(TemplateKind kind, double w, double h, double spacing = 14)
    {
        // Render the template lines as XAML primitives (cheap and crisp).
        var canvas = new XamlCanvas { Width = w, Height = h, Background = new SolidColorBrush(Colors.White) };
        var lineBrush = new SolidColorBrush(Color.FromArgb(255, 200, 205, 215));
        spacing = Math.Max(2, spacing);
        switch (kind)
        {
            case TemplateKind.Blank:
                break;
            case TemplateKind.Grid:
                for (double x = spacing; x < w; x += spacing)
                    canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = h, Stroke = lineBrush, StrokeThickness = 1 });
                for (double y = spacing; y < h; y += spacing)
                    canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = lineBrush, StrokeThickness = 1 });
                break;
            case TemplateKind.Dots:
                for (double x = spacing; x < w; x += spacing)
                    for (double y = spacing; y < h; y += spacing)
                    {
                        var dot = new Ellipse { Width = 2, Height = 2, Fill = lineBrush };
                        XamlCanvas.SetLeft(dot, x - 1); XamlCanvas.SetTop(dot, y - 1);
                        canvas.Children.Add(dot);
                    }
                break;
            case TemplateKind.Lined:
                for (double y = spacing; y < h; y += spacing)
                    canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = lineBrush, StrokeThickness = 1 });
                break;
            case TemplateKind.Cornell:
                canvas.Children.Add(new Line { X1 = w * 0.25, X2 = w * 0.25, Y1 = 0, Y2 = h, Stroke = lineBrush, StrokeThickness = 1 });
                canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h * 0.85, Y2 = h * 0.85, Stroke = lineBrush, StrokeThickness = 1 });
                for (double y = spacing; y < h; y += spacing)
                    canvas.Children.Add(new Line { X1 = w * 0.25, X2 = w, Y1 = y, Y2 = y, Stroke = lineBrush, StrokeThickness = 0.6 });
                break;
        }
        return new Border
        {
            Width = w, Height = h,
            BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = canvas
        };
    }
}
