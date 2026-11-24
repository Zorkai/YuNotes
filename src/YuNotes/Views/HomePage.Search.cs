using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using YuNotes.Models;

namespace YuNotes.Views;

public sealed partial class HomePage
{
    // ── Search inside notes ───────────────────────────────────────────────────
    // Every note's handwriting, PDF text and text boxes are indexed in the
    // background (only notes that changed since last time). Matches show in a
    // panel above the grid while the search box has text.

    private int _contentHitCount;

    private void StartIndexing()
    {
        var s = App.Services.Settings.Current;
        var paths = GetAllNotePaths(s.DocumentsFolder)
            .Concat(s.RecentDocuments.Where(File.Exists))
            .ToList();
        _ = App.Services.Search.EnsureIndexedAsync(paths);
        UpdateContentResults();
    }

    private void OnIndexUpdated() => DispatcherQueue.TryEnqueue(UpdateContentResults);

    private void UpdateContentResults()
    {
        var q = SearchBox.Text.Trim();
        var index = App.Services.Search;
        if (q.Length < 2)
        {
            ContentResultsPanel.Visibility = Visibility.Collapsed;
            ContentResultsList.Children.Clear();
            _contentHitCount = 0;
            return;
        }

        var hits = index.Search(q, 40);
        _contentHitCount = hits.Count;
        var terms = q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        ContentResultsList.Children.Clear();
        foreach (var h in hits) ContentResultsList.Children.Add(BuildResultRow(h, terms, q));

        bool busy = index.Pending > 0;
        IndexingRing.IsActive = busy;
        IndexingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ContentResultsStatus.Text =
            busy ? $"Reading handwriting… {index.Pending} note{(index.Pending == 1 ? "" : "s")} left"
            : !index.HandwritingAvailable ? "Handwriting search needs a handwriting language in Windows settings"
            : hits.Count == 0 ? "No matches inside notes"
            : $"{hits.Count} match{(hits.Count == 1 ? "" : "es")}";
        ContentResultsList.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentResultsPanel.Visibility = Visibility.Visible;
        UpdateEmptyHintForSearch();
    }

    private FrameworkElement BuildResultRow(Services.NoteSearchHit hit, string[] terms, string query)
    {
        var res = Application.Current.Resources;
        var muted = (Brush)res["AppTextSecondaryBrush"];
        var (glyph, sourceName) = hit.Match.Source switch
        {
            Services.TextSource.Handwriting => ("", "handwriting"),
            Services.TextSource.Pdf => ("", "the PDF's text"),
            _ => ("", "a text box"),
        };

        var head = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        head.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = hit.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var folder = GetFolderLabel(hit.Path);
        head.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = $"   Page {hit.Match.PageIndex + 1}" + (folder.Length > 0 ? $" · {folder}" : ""),
            Foreground = muted, FontSize = 12,
        });

        var snippet = new TextBlock { FontSize = 12, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis };
        AddHighlighted(snippet, hit.Match.Snippet, terms);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon
        {
            Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)res["MaterialIconsFamily"],
            Foreground = (Brush)res["AppAccentBrush"],
        };
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(head);
        text.Children.Add(snippet);
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);

        var btn = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(8),
        };
        ToolTipService.SetToolTip(btn, $"Found in {sourceName}. Opens the note at this page.");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn,
            $"{hit.Title}, page {hit.Match.PageIndex + 1}: {hit.Match.Snippet}");
        btn.Click += (_, __) =>
        {
            TouchRecent(hit.Path);
            MainWindow.Navigate<EditorPage>(this, new EditorOpenRequest(hit.Path, hit.Match.PageIndex, query),
                                            new DrillInNavigationTransitionInfo());
        };
        return btn;
    }

    // Bold, accent-colored runs wherever a search term appears in the snippet.
    private static void AddHighlighted(TextBlock tb, string text, string[] terms)
    {
        var accent = (Brush)Application.Current.Resources["AppTextPrimaryBrush"];
        int i = 0;
        while (i < text.Length)
        {
            int best = -1, len = 0;
            foreach (var t in terms)
            {
                int at = text.IndexOf(t, i, StringComparison.CurrentCultureIgnoreCase);
                if (at >= 0 && (best < 0 || at < best)) { best = at; len = t.Length; }
            }
            if (best < 0) { tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[i..] }); break; }
            if (best > i) tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text[i..best] });
            tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = text.Substring(best, len),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = accent,
            });
            i = best + len;
        }
    }

    private void UpdateEmptyHintForSearch()
    {
        if (SearchBox.Text.Trim().Length == 0) return;
        var shown = RecentList.ItemsSource as System.Collections.ICollection;
        EmptyHint.Visibility = (shown?.Count ?? 0) == 0 && _contentHitCount == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowUpdateNotice()
    {
        if (Services.UpdateService.Current != Services.UpdateService.State.Ready) return;
        UpdateInfoBar.Message = Services.UpdateService.StatusText;
        UpdateInfoBar.IsOpen  = true;
    }
}
