using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using YuNotes.Models;

namespace YuNotes.Views;

public sealed partial class SettingsPage
{
    // ─── Tool groups ────────────────────────────────────────────────────────────
    // Lists AppSettings.ToolGroups with Edit / Delete, and a dialog to create or
    // edit one: name, an icon from ToolGroup.Icons, and which drawing tools go
    // in it (a tool can only be in one group). Changes save immediately and the
    // editor rebuilds its toolbar from them when you go back.

    private bool _toolGroupsWired;

    private void RebuildToolGroupsUi()
    {
        RefreshToolGroupChips();
        RebuildToolbarPreview();
        if (!_toolGroupsWired)
        {
            _toolGroupsWired = true;
            NewToolGroupBtn.Click += async (_, __) => await EditToolGroupAsync(null);
        }
        ToolGroupsPanel.Children.Clear();
        var res = Application.Current.Resources;
        var font = (FontFamily)res["MaterialIconsFamily"];
        var groups = App.Services.Settings.Current.ToolGroups;
        if (groups.Count == 0)
        {
            ToolGroupsPanel.Children.Add(new TextBlock
            {
                Text = "No groups yet.", FontSize = 13,
                Foreground = (Brush)res["AppTextSecondaryBrush"],
            });
            return;
        }
        foreach (var g in groups)
        {
            var row = new Grid
            {
                ColumnSpacing = 12, Padding = new Thickness(10, 6, 6, 6), CornerRadius = new CornerRadius(8),
                Background = (Brush)res["AppSurfaceVariantBrush"],
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = g.Glyph, FontFamily = font, FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(g.Name) ? "Group" : g.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            // Its tools, as icons and names, in toolbar order.
            var members = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 3, 0, 0) };
            foreach (var key in OrderedKeys().Where(g.Tools.Contains))
            {
                var def = DrawingToolDefs.FirstOrDefault(d => d.Key == key);
                var m = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                m.Children.Add(new FontIcon { Glyph = def.Glyph ?? "", FontFamily = font, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                m.Children.Add(new TextBlock { Text = def.Label ?? key, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                                               Foreground = (Brush)res["AppTextSecondaryBrush"] });
                members.Children.Add(m);
            }
            if (members.Children.Count == 0)
                members.Children.Add(new TextBlock { Text = "No tools", FontSize = 12, Foreground = (Brush)res["AppTextSecondaryBrush"] });
            text.Children.Add(new ScrollViewer
            {
                Content = members, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var edit = new Button { Content = "Edit", CornerRadius = new CornerRadius(8) };
            var del = new Button { Content = "Delete", CornerRadius = new CornerRadius(8) };
            var group = g;
            edit.Click += async (_, __) => await EditToolGroupAsync(group);
            del.Click += (_, __) =>
            {
                App.Services.Settings.Current.ToolGroups.RemoveAll(x => x.Id == group.Id);
                App.Services.Settings.Save();
                RebuildToolGroupsUi();
            };
            buttons.Children.Add(edit);
            buttons.Children.Add(del);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(buttons);
            ToolGroupsPanel.Children.Add(row);
        }
    }

    private static string ToolLabel(string key) =>
        DrawingToolDefs.FirstOrDefault(d => d.Key == key).Label ?? key;

    // Drawing tools in the order of the list on screen (falls back to defaults).
    private List<string> OrderedKeys() =>
        _drawingTools.Count > 0 ? _drawingTools.Select(t => t.Key).ToList()
                                : DrawingToolDefs.Select(d => d.Key).ToList();

    // Puts each tool's group (if any) on its row's chip.
    private void RefreshToolGroupChips()
    {
        var groups = App.Services.Settings.Current.ToolGroups;
        foreach (var t in _drawingTools)
        {
            var g = groups.FirstOrDefault(x => x.Tools.Contains(t.Key));
            t.SetGroup(g is null ? "" : (string.IsNullOrWhiteSpace(g.Name) ? "Group" : g.Name), g?.Glyph ?? "");
        }
    }

    // The floating toolbar as it will look: visible tools in list order, each
    // group as one button (with its corner arrow) where its first tool is.
    private void RebuildToolbarPreview()
    {
        if (ToolbarPreviewPanel is null) return;
        ToolbarPreviewPanel.Children.Clear();
        var res = Application.Current.Resources;
        var font = (FontFamily)res["MaterialIconsFamily"];
        var groups = App.Services.Settings.Current.ToolGroups;
        var placed = new HashSet<string>();
        Border Chip(string glyph, string tip, bool isGroup)
        {
            var grid = new Grid { Width = 34, Height = 34 };
            grid.Children.Add(new FontIcon { Glyph = glyph, FontFamily = font, FontSize = 18,
                                             HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            if (isGroup)
                grid.Children.Add(new FontIcon { Glyph = "\ue5cf", FontFamily = font, FontSize = 9,
                                                 HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                                                 Margin = new Thickness(0, 0, 3, 2), Foreground = (Brush)res["AppAccentBrush"] });
            var b = new Border
            {
                Child = grid, CornerRadius = new CornerRadius(17),
                Background = isGroup ? (Brush)res["AppSurfaceBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = isGroup ? (Brush)res["AppAccentBrush"] : null,
                BorderThickness = new Thickness(isGroup ? 1 : 0),
            };
            ToolTipService.SetToolTip(b, tip);
            return b;
        }
        foreach (var t in _drawingTools.Where(t => t.IsVisible))
        {
            var g = groups.FirstOrDefault(x => x.Tools.Contains(t.Key));
            if (g is null) { ToolbarPreviewPanel.Children.Add(Chip(t.Glyph, t.Label, false)); continue; }
            if (!placed.Add(g.Id)) continue;
            var names = string.Join(", ", _drawingTools.Where(x => x.IsVisible && g.Tools.Contains(x.Key)).Select(x => x.Label));
            ToolbarPreviewPanel.Children.Add(Chip(g.Glyph, $"{g.Name}: {names}", true));
        }
        if (ToolbarPreviewPanel.Children.Count == 0)
            ToolbarPreviewPanel.Children.Add(new TextBlock { Text = "All tools are hidden.", FontSize = 12,
                                                              Foreground = (Brush)res["AppTextSecondaryBrush"] });
    }

    private async Task EditToolGroupAsync(ToolGroup? existing)
    {
        var res = Application.Current.Resources;
        var font = (FontFamily)res["MaterialIconsFamily"];
        var all = App.Services.Settings.Current.ToolGroups;

        var name = new TextBox { Header = "Name", PlaceholderText = "More tools", Text = existing?.Name ?? "", MaxLength = 40 };

        // Icon picker: one row of toggle tiles; exactly one stays chosen.
        string glyph = existing?.Glyph ?? ToolGroup.Icons[0].Glyph;
        var iconTiles = new List<ToggleButton>();
        var iconGrid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 46, ItemHeight = 46, MaximumRowsOrColumns = 8 };
        foreach (var (g, label) in ToolGroup.Icons)
        {
            var tile = new ToggleButton
            {
                Content = new FontIcon { Glyph = g, FontFamily = font, FontSize = 20 },
                Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(8),
                IsChecked = g == glyph, Tag = g,
            };
            ToolTipService.SetToolTip(tile, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, $"{label} icon");
            tile.Click += (_, __) =>
            {
                glyph = (string)tile.Tag;
                foreach (var t in iconTiles) t.IsChecked = ReferenceEquals(t, tile);
            };
            iconTiles.Add(tile);
            iconGrid.Children.Add(tile);
        }

        // Tools: those already in another group can't be added here.
        var checks = new List<(string Key, CheckBox Box)>();
        var toolPanel = new StackPanel { Spacing = 0 };
        foreach (var (key, label, _) in DrawingToolDefs)
        {
            var other = all.FirstOrDefault(g => g.Id != existing?.Id && g.Tools.Contains(key));
            var cb = new CheckBox
            {
                Content = other is null ? label : $"{label}  (in “{other.Name}”)",
                IsChecked = existing?.Tools.Contains(key) == true,
                IsEnabled = other is null,
            };
            checks.Add((key, cb));
            toolPanel.Children.Add(cb);
        }
        var error = new TextBlock
        {
            Text = "Choose at least one tool.", FontSize = 12, Visibility = Visibility.Collapsed,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 58, 58)),
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Icon" });
        panel.Children.Add(iconGrid);
        panel.Children.Add(new TextBlock { Text = "Tools in this group" });
        panel.Children.Add(toolPanel);
        panel.Children.Add(error);

        var dlg = new ContentDialog
        {
            Title = existing is null ? "New tool group" : "Edit tool group",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = existing is null ? "Create" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            if (checks.Any(c => c.Box.IsChecked == true)) return;
            error.Visibility = Visibility.Visible;
            args.Cancel = true;
        };
        foreach (var (_, cb) in checks) cb.Checked += (_, __) => error.Visibility = Visibility.Collapsed;
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var group = existing ?? new ToolGroup();
        group.Name = string.IsNullOrWhiteSpace(name.Text) ? "Tools" : name.Text.Trim();
        group.Glyph = glyph;
        group.Tools = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Key).ToList();
        if (existing is null) all.Add(group);
        App.Services.Settings.Save();
        RebuildToolGroupsUi();
    }
}
