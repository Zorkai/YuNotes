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
    // ─── Side by side ────────────────────────────────────────────────────────────
    // A second note shown read-only next to this one (e.g. the exercise sheet
    // beside your solution). Scroll it with the wheel, touch or the scrollbars;
    // "Open to edit" switches to it.

    private string? _refPath;
    private double _refFraction = 0.45;
    private bool _splitDragging;

    private void RebuildSideBySideFlyout()
    {
        SideBySideFlyout.Items.Clear();
        var current = _doc?.Info.FilePath ?? "";
        var ordered = CandidateNotePaths(p => SamePath(p, current));

        SideBySideFlyout.Items.Add(new MenuFlyoutItem { Text = "Show next to this note:", IsEnabled = false });
        foreach (var p in ordered)
        {
            var item = new MenuFlyoutItem { Text = NoteMenuLabel(p) };
            if (string.Equals(p, _refPath, StringComparison.OrdinalIgnoreCase))
                item.Icon = new FontIcon { Glyph = "" };
            string path = p;
            item.Click += async (_, __) => await OpenReferenceAsync(path);
            SideBySideFlyout.Items.Add(item);
        }
        if (ordered.Count == 0)
            SideBySideFlyout.Items.Add(new MenuFlyoutItem { Text = "No other notes yet", IsEnabled = false });
        if (_refPath is not null)
        {
            SideBySideFlyout.Items.Add(new MenuFlyoutSeparator());
            var close = new MenuFlyoutItem { Text = "Close side-by-side view" };
            close.Click += (_, __) => CloseReference();
            SideBySideFlyout.Items.Add(close);
        }
    }

    private async Task OpenReferenceAsync(string path)
    {
        try
        {
            var doc = await Task.Run(() =>
                string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
                    ? App.Services.Documents.OpenPdfContainer(path, App.Services.PdfContainer, App.Services.PdfImport)
                    : App.Services.Documents.Open(path));
            _refPath = path;
            RefTitle.Text = doc.Info.Title;
            RefCanvas.Bind(doc, App.Services.Settings.Current, new PageRenderer(App.Services.Templates));
            RefCanvas.Context.EditingSuspended = true;   // read-only: no pen or mouse edits
            RefPane.Visibility = Visibility.Visible;
            RefSplitter.Visibility = Visibility.Visible;
            ApplyReferenceWidths();
            // The scroll view ignores zoom changes until the new pages are laid
            // out, so fit a moment later (and again whenever the pane resizes).
            var fit = DispatcherQueue.CreateTimer();
            fit.Interval = TimeSpan.FromMilliseconds(350);
            fit.IsRepeating = false;
            fit.Tick += (_, __) => FitReferenceZoom();
            fit.Start();
        }
        catch (Exception ex) { await Notify("Couldn't open that note", ex.Message); }
    }

    private void ApplyReferenceWidths()
    {
        bool open = _refPath is not null;
        MainCol.Width = new GridLength(open ? 1 - _refFraction : 1, GridUnitType.Star);
        RefCol.Width = open ? new GridLength(_refFraction, GridUnitType.Star) : new GridLength(0);
        RefSplitCol.Width = new GridLength(open ? 8 : 0);
    }

    // Fit the reference note's page width to its pane once laid out.
    private void FitReferenceZoom(int attempt = 0)
    {
        if (_refPath is null) return;
        var page = RefCanvas.Document?.Pages.FirstOrDefault();
        if (page is null) return;
        if (RefCanvas.ActualWidth < 50)
        {
            if (attempt < 20) DispatcherQueue.TryEnqueue(() => FitReferenceZoom(attempt + 1));
            return;
        }
        RefCanvas.SetZoom(Math.Clamp((RefCanvas.ActualWidth - 100) / page.Width, 0.25, 2.0));
    }

    private void CloseReference()
    {
        _refPath = null;
        RefPane.Visibility = Visibility.Collapsed;
        RefSplitter.Visibility = Visibility.Collapsed;
        ApplyReferenceWidths();
    }

    // Open the reference note for editing in its own tab (this one is saved
    // first); the reference pane closes since that note now has a tab.
    private async Task OpenReferenceAsMainAsync()
    {
        if (_refPath is null) return;
        var target = _refPath;
        CloseReference();
        await OpenInNewTabAsync(target);
    }
}
