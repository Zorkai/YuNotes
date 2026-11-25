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
    // ─── Find in note ────────────────────────────────────────────────────────────
    // Searches handwriting (recognized on demand, cached per page until its
    // strokes change), the PDF's own text and typed text boxes. Every match is
    // highlighted; Enter / Shift+Enter step through them.

    private List<(int Page, Services.TextBox Box)> _searchMatches = new();
    private int _searchMatchIndex = -1;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _searchDebounce = null!;
    private int _searchVersion;
    private bool _searchReading;
    private readonly Dictionary<string, (string Sig, List<Services.TextBox> Words)> _inkWords = new();
    // Set when Home opens this note from a search result: query + page to land on.
    private (string Query, int Page)? _pendingSearch;

    private static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OpenSearch()
    {
        // Keep the find bar clear of the floating toolbar when it's docked
        // along the top or right edge.
        var pos = App.Services.Settings.Current.ToolbarPosition;
        SearchPanel.Margin = new Thickness(0, pos == ToolbarPosition.Top ? 84 : 14,
                                           pos == ToolbarPosition.Right ? 84 : 14, 0);
        SearchPanel.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        _searchVersion++;
        _searchMatches.Clear();
        _searchMatchIndex = -1;
        Canvas.SetSearchHighlights(null);
        UpdateSearchUi();
        // Give keys back to the note (Ctrl+Z etc.), not the hidden search box.
        Canvas.Focus(FocusState.Programmatic);
    }

    private static string InkSignature(NotePage p) => Services.SearchIndex.InkSignature(p);

    // Handwriting the Home search index already read for this note: pages whose
    // ink still matches are found at once instead of being read again.
    private void SeedInkWordsFromIndex()
    {
        if (_doc?.Info.FilePath is not { Length: > 0 } path) return;
        foreach (var (pageId, known) in App.Services.Search.HandwritingFor(path))
            if (!_inkWords.ContainsKey(pageId)) _inkWords[pageId] = known;
    }

    private async void RunSearch()
    {
        int version = ++_searchVersion;
        var q = SearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(q) || _doc is null)
        {
            _searchMatches.Clear();
            _searchMatchIndex = -1;
            Canvas.SetSearchHighlights(null);
            UpdateSearchUi();
            return;
        }

        // Instant pass with what is already known, then read any handwriting
        // that hasn't been recognized yet (or changed) and update.
        SeedInkWordsFromIndex();
        ComputeMatches(q, keepPosition: false);
        var doc = _doc;
        // Read the pages nearest the one in view first: on a long note the
        // matches you're about to step to show up soonest.
        int here = Canvas.ActivePage is { } ap ? Math.Max(0, doc.Pages.IndexOf(ap)) : 0;
        var stale = !Services.HandwritingRecognizer.IsAvailable ? new List<NotePage>()
            : doc.Pages.Select((p, i) => (p, i))
                       .Where(x => x.p.Strokes.Any(s => s.Kind == StrokeKind.Pen) &&
                                   !(_inkWords.TryGetValue(x.p.Id, out var c) && c.Sig == InkSignature(x.p)))
                       .OrderBy(x => Math.Abs(x.i - here))
                       .Select(x => x.p)
                       .ToList();
        if (stale.Count == 0) { _pendingSearch = null; return; }
        _searchReading = true;
        UpdateSearchUi();
        try
        {
            // Matches are recounted at most every 200 ms (and once at the end),
            // not after every page: recounting walks the whole note.
            var sinceUpdate = System.Diagnostics.Stopwatch.StartNew();
            bool unshown = false;
            foreach (var p in stale)
            {
                var sig = InkSignature(p);
                var words = await Services.HandwritingRecognizer.RecognizeAsync(p.Strokes.ToList());
                if (version != _searchVersion || !ReferenceEquals(doc, _doc)) return;
                _inkWords[p.Id] = (sig, words);
                unshown = true;
                if (sinceUpdate.ElapsedMilliseconds >= 200)
                {
                    ComputeMatches(q, keepPosition: true);
                    sinceUpdate.Restart();
                    unshown = false;
                }
            }
            if (unshown) ComputeMatches(q, keepPosition: true);
        }
        finally
        {
            if (version == _searchVersion) { _searchReading = false; _pendingSearch = null; UpdateSearchUi(); }
        }
    }

    private void ComputeMatches(string q, bool keepPosition)
    {
        if (_doc is null) return;
        var terms = Services.TextMatcher.Terms(q);
        var previous = _searchMatchIndex >= 0 && _searchMatchIndex < _searchMatches.Count
            ? _searchMatches[_searchMatchIndex] : default;

        var matches = new List<(int, Services.TextBox)>();
        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            var p = _doc.Pages[i];
            var page = new Services.PageText { Index = i };
            if (_inkWords.TryGetValue(p.Id, out var ink)) page.Boxes.AddRange(ink.Words);
            page.Boxes.AddRange(Services.SearchIndex.PdfBoxes(p.TextRuns, 0));
            page.Boxes.AddRange(Services.SearchIndex.TypedBoxes(p.Texts));
            if (Services.SearchIndex.BookmarkBox(p) is { } bm) page.Boxes.Add(bm);
            if (Services.TextMatcher.Match(page, terms) is { } m)
                foreach (var b in m.Boxes) matches.Add((i, b));
        }
        _searchMatches = matches;

        int index = -1;
        if (_pendingSearch is { } want)
            index = _searchMatches.FindIndex(m => m.Page >= want.Page);
        else if (keepPosition && previous.Box is not null)
            index = _searchMatches.FindIndex(m => m.Page == previous.Page && m.Box.Rect == previous.Box.Rect);
        if (index < 0 && !(keepPosition && previous.Box is not null) && _pendingSearch is null)
        {
            // Start from the page in view rather than jumping back to page 1.
            int here = Canvas.ActivePage is { } ap ? _doc.Pages.IndexOf(ap) : 0;
            index = _searchMatches.FindIndex(m => m.Page >= here);
        }
        if (index < 0 && _searchMatches.Count > 0) index = 0;

        bool moved = index != _searchMatchIndex || !keepPosition;
        _searchMatchIndex = index;
        UpdateSearchUi();
        if (moved) NavigateToCurrentMatch(); else ShowSearchHighlights();
    }

    private void StepSearch(int delta)
    {
        if (_searchMatches.Count == 0) return;
        _pendingSearch = null;
        _searchMatchIndex = (_searchMatchIndex + delta + _searchMatches.Count) % _searchMatches.Count;
        UpdateSearchUi();
        NavigateToCurrentMatch();
    }

    private void UpdateSearchUi()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        if (_searchMatches.Count == 0)
            SearchCountLabel.Text = string.IsNullOrEmpty(q) ? "" : _searchReading ? "Reading ink…" : "No matches";
        else
            SearchCountLabel.Text = $"{_searchMatchIndex + 1} / {_searchMatches.Count}";
        ToolTipService.SetToolTip(SearchCountLabel, _searchReading ? "Reading your handwriting so it can be searched" : null);
        SearchPrevBtn.IsEnabled = _searchMatches.Count > 1;
        SearchNextBtn.IsEnabled = _searchMatches.Count > 1;
    }

    private void ShowSearchHighlights()
    {
        var byPage = _searchMatches.GroupBy(m => m.Page)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Box.Rect).ToList());
        if (_searchMatchIndex >= 0 && _searchMatchIndex < _searchMatches.Count)
        {
            var (page, box) = _searchMatches[_searchMatchIndex];
            Canvas.SetSearchHighlights(byPage, page, box.Rect);
        }
        else Canvas.SetSearchHighlights(byPage);
    }

    private void NavigateToCurrentMatch()
    {
        ShowSearchHighlights();
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Count) return;
        var (pageIdx, box) = _searchMatches[_searchMatchIndex];
        Canvas.ScrollToRect(pageIdx, box.Rect);
    }
}
