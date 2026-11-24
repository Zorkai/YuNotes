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
    // Export dialog: PDF or PNG, for all pages, the current page, a range such
    // as "1-3, 5", or (PNG only) just the current selection.
    private async Task ExportAsync()
    {
        if (_doc is null) return;
        int pageCount = _doc.Pages.Count;
        int current = Canvas.ActivePage is { } ap ? Math.Max(0, _doc.Pages.IndexOf(ap)) : 0;
        bool hasSelection = Canvas.HasSelection;

        var format = new RadioButtons { Header = "Format", MaxColumns = 2 };
        format.Items.Add("PDF");
        format.Items.Add("PNG images");
        format.SelectedIndex = 0;

        var which = new RadioButtons { Header = "Pages" };
        which.Items.Add("All pages");
        which.Items.Add($"Current page ({current + 1})");
        which.Items.Add("Page range");
        if (hasSelection) which.Items.Add("Selection only (PNG)");
        which.SelectedIndex = hasSelection ? 3 : 0;

        var range = new TextBox { PlaceholderText = $"1-3, 5  (1 to {pageCount})", IsEnabled = false };
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 58, 58)),
            FontSize = 12, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap,
        };
        void Sync()
        {
            range.IsEnabled = which.SelectedIndex == 2;
            bool selection = hasSelection && which.SelectedIndex == 3;
            if (selection) format.SelectedIndex = 1;
            format.IsEnabled = !selection;
        }
        which.SelectionChanged += (_, __) => { Sync(); error.Visibility = Visibility.Collapsed; };
        range.TextChanged += (_, __) => error.Visibility = Visibility.Collapsed;
        Sync();

        var content = new StackPanel { Spacing = 14, MinWidth = 320 };
        content.Children.Add(format);
        content.Children.Add(which);
        content.Children.Add(range);
        content.Children.Add(error);

        var dlg = new ContentDialog
        {
            Title = "Export",
            Content = content,
            XamlRoot = XamlRoot,
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        List<int>? indices = null;
        dlg.PrimaryButtonClick += (_, args) =>
        {
            indices = which.SelectedIndex switch
            {
                0 => Enumerable.Range(0, pageCount).ToList(),
                1 => new List<int> { current },
                2 => ParsePageRange(range.Text, pageCount),
                _ => new List<int>(),
            };
            if (which.SelectedIndex == 2 && (indices is null || indices.Count == 0))
            {
                error.Text = $"Enter pages between 1 and {pageCount}, for example 1-3, 5.";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || indices is null) return;

        try
        {
            if (hasSelection && which.SelectedIndex == 3)
            {
                var png = Canvas.RenderSelectionRegionToPng();
                if (png is null) { await Notify("Export failed", "The selection is empty."); return; }
                var save = new FileSavePicker();
                save.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
                save.SuggestedFileName = $"{_doc.Info.Title} selection";
                InitPicker(save);
                var file = await save.PickSaveFileAsync();
                if (file is null) return;
                await File.WriteAllBytesAsync(file.Path, png);
                ShowToast("Selection exported");
            }
            else if (format.SelectedIndex == 0)
            {
                var save = new FileSavePicker();
                save.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
                save.SuggestedFileName = indices.Count == pageCount ? _doc.Info.Title
                    : $"{_doc.Info.Title} (pages {DescribePages(indices)})";
                InitPicker(save);
                var file = await save.PickSaveFileAsync();
                if (file is null) return;
                // Same vector writer as the .pdf save. Imported PDFs keep their
                // original pages (selectable text) with the notes drawn on top.
                bool useOverlay = _doc.SourcePdfBytes is { Length: > 0 };
                var pages = indices.Select(i => Canvas.BuildVectorExport(i, useOverlay)).ToArray();
                // A document with only the chosen pages, sharing their data.
                var subset = new Document { Info = _doc.Info, SourcePdfBytes = _doc.SourcePdfBytes, Template = _doc.Template };
                foreach (var i in indices) subset.Pages.Add(_doc.Pages[i]);
                var outPath = file.Path;
                await Task.Run(() => App.Services.Documents.ExportPdf(subset, pages, outPath, App.Services.PdfContainer));
                ShowToast(indices.Count == 1 ? "Page exported as PDF" : $"{indices.Count} pages exported as PDF");
            }
            else
            {
                var folder = new FolderPicker();
                folder.FileTypeFilter.Add("*");
                InitPicker(folder);
                var f = await folder.PickSingleFolderAsync();
                if (f is null) return;
                foreach (var i in indices)
                {
                    var png = Canvas.RenderPageForExport(i, overlayOnly: false);
                    if (png is not null)
                        await File.WriteAllBytesAsync(System.IO.Path.Combine(f.Path, $"{_doc.Info.Title}-{i + 1:D3}.png"), png);
                }
                ShowToast(indices.Count == 1 ? "Page exported as PNG" : $"{indices.Count} pages exported as PNG");
            }
        }
        catch (Exception ex) { await Notify("Export failed", ex.Message); }
    }

    // "1-3, 5, 8-6" → [0,1,2,4,5,6,7] (sorted, unique, 0-based). Null if any
    // part is malformed or out of 1..count.
    private static List<int>? ParsePageRange(string text, int count)
    {
        var result = new SortedSet<int>();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*[-–]\s*", "-");   // "1 - 3" → "1-3"
        foreach (var raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            var bits = part.Split(new[] { '-', '–' }, StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length == 1 && int.TryParse(bits[0], out int one))
            {
                if (one < 1 || one > count) return null;
                result.Add(one - 1);
            }
            else if (bits.Length == 2 && int.TryParse(bits[0], out int a) && int.TryParse(bits[1], out int b))
            {
                if (a < 1 || b < 1 || a > count || b > count) return null;
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) result.Add(i - 1);
            }
            else return null;
        }
        return result.ToList();
    }

    // [0,1,2,4] → "1-3, 5"
    private static string DescribePages(List<int> indices)
    {
        var parts = new List<string>();
        for (int i = 0; i < indices.Count; i++)
        {
            int start = indices[i];
            while (i + 1 < indices.Count && indices[i + 1] == indices[i] + 1) i++;
            parts.Add(start == indices[i] ? $"{start + 1}" : $"{start + 1}-{indices[i] + 1}");
        }
        return string.Join(", ", parts);
    }

    // A page whose background is imported PDF content (as opposed to a YuNotes
    // blank page). Shared with PageRenderer's clip so they always agree.
    private static bool IsPdfPage(NotePage p) => Rendering.PageRenderer.HasImportedPdf(p);
}
