using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using YuNotes.Controls;
using YuNotes.Input;
using YuNotes.Models;

namespace YuNotes.Views;

public sealed partial class SettingsPage
{

    private IEnumerable<(string, Ellipse)> NavDots() => new[]
    {
        ("pen", NavDotPen), ("touch", NavDotTouch), ("ink", NavDotInk), ("appearance", NavDotAppearance),
        ("storage", NavDotStorage), ("updates", NavDotUpdates), ("diagnostics", NavDotDiagnostics),
    };

    // ─── Categories, rows and search ────────────────────────────────────────
    private readonly List<(SettingRow Row, string Category)> _rows = new();

    private IEnumerable<StackPanel> Panels() => new[]
    {
        PenPanel, TouchPanel, InkPanel, AppearancePanel, StoragePanel, UpdatesPanel, ShortcutsPanel, DiagnosticsPanel,
    };

    private void CollectRows()
    {
        foreach (var panel in Panels())
            foreach (var row in LogicalDescendants(panel).OfType<SettingRow>())
            {
                _rows.Add((row, (string)panel.Tag));
                row.ResetRequested += (s, _) => ResetRow((SettingRow)s!);
            }
    }

    // Walks panels, borders and content controls without needing the visual
    // tree, so rows on pages that were never shown are found too.
    private static IEnumerable<UIElement> LogicalDescendants(UIElement root)
    {
        var stack = new Stack<UIElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            switch (e)
            {
                case Panel p: foreach (var c in p.Children) stack.Push(c); break;
                case Border b when b.Child is not null: stack.Push(b.Child); break;
                case SettingRow r: break;   // a row's own control holds no rows
                case ContentControl cc when cc.Content is UIElement u: stack.Push(u); break;
            }
        }
    }

    private void SelectCategory(string key)
    {
        var item = NavList.Items.OfType<ListViewItem>().FirstOrDefault(i => (string)i.Tag == key)
                   ?? NavList.Items.OfType<ListViewItem>().First();
        if (ReferenceEquals(NavList.SelectedItem, item)) ShowCategory((string)item.Tag);
        else NavList.SelectedItem = item;
    }

    private void ShowCategory(string key)
    {
        s_category = key;
        if (SettingsSearch.Text.Length > 0) { SettingsSearch.Text = ""; return; }   // ApplySearch shows the category
        ApplySearch();
        RootScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private bool _searching;

    // While searching, every category shows its matching rows; otherwise only
    // the selected category is shown, in full.
    private void ApplySearch()
    {
        var terms = SettingsSearch.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _searching = terms.Length > 0;
        int shown = 0;

        foreach (var panel in Panels())
        {
            string category = (string)panel.Tag;
            string categoryTitle = ((panel.Children[0] as StackPanel)?.Children[0] as TextBlock)?.Text ?? "";
            int panelShown = 0;
            foreach (var (row, cat) in _rows.Where(r => r.Category == category))
            {
                var hay = $"{row.Title} {row.Description} {row.Keywords} {categoryTitle}".ToLowerInvariant();
                bool ok = !_searching || terms.All(hay.Contains);
                row.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
                if (ok) panelShown++;
            }
            // Hide cards whose rows are all filtered out, and fix row hairlines.
            foreach (var card in panel.Children.OfType<Border>())
            {
                var rows = (card.Child as Panel)?.Children.OfType<SettingRow>().ToList() ?? new();
                bool any = rows.Any(r => r.Visibility == Visibility.Visible);
                card.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                bool first = true;
                foreach (var r in rows.Where(r => r.Visibility == Visibility.Visible))
                {
                    r.BorderThickness = first ? new Thickness(0) : new Thickness(0, 1, 0, 0);
                    first = false;
                }
            }
            bool visible = _searching ? panelShown > 0 : category == s_category;
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) shown++;
        }
        NoResultsText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        NavList.Opacity = _searching ? 0.5 : 1;
    }

    // ─── Layout ─────────────────────────────────────────────────────────────
    private const double ContentMaxWidth = 760;

    private void FitContentWidth(double viewport)
    {
        double w = Math.Max(0, viewport - RootStack.Margin.Left - RootStack.Margin.Right);
        RootStack.Width = Math.Min(ContentMaxWidth, w);
    }

    // Narrow windows get an icon-only sidebar.
    private void FitSidebar(double width)
    {
        bool narrow = width < 760;
        FitHeader(width);
        NavColumn.Width = new GridLength(narrow ? 64 : 220);
        NavLegend.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in NavList.Items.OfType<ListViewItem>())
            if (item.Content is Grid g && g.Children.Count > 1)
            {
                g.Children[1].Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                ToolTipService.SetToolTip(item, narrow ? (g.Children[1] as TextBlock)?.Text : null);
            }
    }

    // Header in a narrow window: the update chip shows its icon only, the
    // search box gives up width (down to 120), and the "Settings" title goes
    // last, so nothing slides under the caption buttons.
    private void FitHeader(double width)
    {
        if (width <= 0) return;
        bool chipCompact = width < 760;
        UpdateChipText.Visibility = chipCompact ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(UpdateChip, chipCompact ? UpdateChipText.Text : null);
        SettingsTitle.Visibility = width < 560 ? Visibility.Collapsed : Visibility.Visible;

        // Room left for the search box once the fixed parts are placed.
        double fixedParts = HeaderGrid.Padding.Left + HeaderGrid.Padding.Right
                            + BackBtn.Width + 8
                            + (SettingsTitle.Visibility == Visibility.Visible ? 90 : 0)
                            + (chipCompact ? 44 : 150)
                            + HeaderGrid.ColumnSpacing * 3 + 24;   // + a sliver of drag region
        SettingsSearch.Width = Math.Clamp(width - fixedParts, 120, 280);
    }
}
