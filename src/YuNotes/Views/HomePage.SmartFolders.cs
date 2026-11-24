using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using YuNotes.Models;

namespace YuNotes.Views;

public sealed partial class HomePage
{
    // ── Smart folders ─────────────────────────────────────────────────────────
    // Saved searches in the sidebar ("tagged #exam, edited in the last 7
    // days"). A smart folder never holds files: it shows, from every folder,
    // the notes that match all of its conditions right now. Right-click one to
    // edit or delete it.

    private string? _activeSmartId;
    // Last full list of live notes (built when All notes refreshes). Used for
    // the sidebar counts and the editor's live preview without rereading files.
    private List<RecentItem>? _allLiveItems;

    private SmartFolder? ActiveSmartFolder =>
        _activeSmartId is null ? null
        : App.Services.Settings.Current.SmartFolders.FirstOrDefault(f => f.Id == _activeSmartId);

    private static bool MatchesSmartFolder(SmartFolder f, RecentItem i) =>
        f.Matches(i.Title, i.ModifiedAt, i.IsPdf, i.IsPinned, i.Tags.ToList());

    private void InitSmartFolders()
    {
        NewSmartFolderBtn.Click += async (_, __) => await EditSmartFolderAsync(null);
    }

    private void SelectSmartFolder(SmartFolder f)
    {
        if (_activeSmartId == f.Id) { SelectNav(NavSection.AllNotes); return; }   // click again = leave
        _activeSection = NavSection.AllNotes;
        _selectedFolderPath = null;
        _activeTag = null;
        _activeSmartId = f.Id;
        ApplyNavSelection();
        SectionTitle.Text = f.Name;
        LoadFolderTree();   // drops the folder highlight
        Refresh();
    }

    private void RebuildSmartFolderList()
    {
        var folders = App.Services.Settings.Current.SmartFolders;
        if (_activeSmartId is not null && ActiveSmartFolder is null) _activeSmartId = null;   // deleted
        SmartList.Children.Clear();
        var res = Application.Current.Resources;
        foreach (var f in folders)
        {
            bool active = f.Id == _activeSmartId;
            int? count = _allLiveItems?.Count(i => MatchesSmartFolder(f, i));

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon
            {
                Glyph = "", FontSize = 16,
                FontFamily = (FontFamily)res["MaterialIconsFamily"],
                Foreground = (Brush)res[active ? "AppAccentBrush" : "AppTextSecondaryBrush"],
            });
            var name = new TextBlock
            {
                Text = f.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (count is int c)
            {
                var num = new TextBlock
                {
                    Text = c.ToString(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)res["AppTextSecondaryBrush"],
                };
                Grid.SetColumn(num, 2);
                row.Children.Add(num);
            }

            var btn = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Background = active ? (Brush)res["AppSurfaceVariantBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn,
                count is int n ? $"Smart folder {f.Name}, {n} {(n == 1 ? "note" : "notes")}" : $"Smart folder {f.Name}");
            ToolTipService.SetToolTip(btn, $"{f.Describe()}\nRight-click to edit or delete");
            var folder = f;
            btn.Click += (_, __) => SelectSmartFolder(folder);

            var menu = new MenuFlyout();
            var edit = new MenuFlyoutItem { Text = "Edit", Icon = new FontIcon { Glyph = "", FontFamily = (FontFamily)res["MaterialIconsFamily"] } };
            edit.Click += async (_, __) => await EditSmartFolderAsync(folder);
            var del = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "", FontFamily = (FontFamily)res["MaterialIconsFamily"] } };
            del.Click += async (_, __) => await DeleteSmartFolderAsync(folder);
            menu.Items.Add(edit);
            menu.Items.Add(del);
            btn.ContextFlyout = menu;

            SmartList.Children.Add(btn);
        }
    }

    private async Task DeleteSmartFolderAsync(SmartFolder f)
    {
        var dlg = new ContentDialog
        {
            Title = $"Delete “{f.Name}”?",
            Content = "Only the saved search is removed. Your notes stay where they are.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        App.Services.Settings.Current.SmartFolders.RemoveAll(x => x.Id == f.Id);
        App.Services.Settings.Save();
        if (_activeSmartId == f.Id) SelectNav(NavSection.AllNotes);
        else RebuildSmartFolderList();
    }

    // Create (existing == null) or edit a smart folder. The dialog shows how
    // many notes match while the conditions change.
    private async Task EditSmartFolderAsync(SmartFolder? existing)
    {
        var live = _allLiveItems ??= BuildAllNoteItems();
        var knownTags = Services.NoteTags.Counts(IsLiveNotePath).Keys
            .Concat(existing?.Tags ?? new List<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCulture).ToList();

        var nameBox = new TextBox { Header = "Name", PlaceholderText = "Exam prep", Text = existing?.Name ?? "" };

        var tagChecks = new List<(string Tag, CheckBox Box)>();
        var tagPanel = new StackPanel { Spacing = 0 };
        foreach (var t in knownTags)
        {
            var cb = new CheckBox { Content = "#" + t, IsChecked = existing?.Tags.Contains(t, StringComparer.OrdinalIgnoreCase) == true };
            tagChecks.Add((t, cb));
            tagPanel.Children.Add(cb);
        }
        var tagMode = new ComboBox { Items = { "Any of these tags", "All of these tags" }, SelectedIndex = existing?.MatchAllTags == true ? 1 : 0, MinWidth = 180 };

        int[] dayValues = { 0, 1, 7, 30, 90, 365 };
        var edited = new ComboBox
        {
            Header = "Edited",
            Items = { "Any time", "Today", "In the last 7 days", "In the last 30 days", "In the last 90 days", "In the last year" },
            MinWidth = 200,
        };
        int dayIdx = Array.IndexOf(dayValues, existing?.EditedWithinDays ?? 0);
        edited.SelectedIndex = dayIdx >= 0 ? dayIdx : 0;
        // A custom day count from a hand-edited settings file stays selectable.
        if (dayIdx < 0 && existing is { EditedWithinDays: > 0 } ex)
        {
            edited.Items.Add($"In the last {ex.EditedWithinDays} days");
            dayValues = dayValues.Append(ex.EditedWithinDays).ToArray();
            edited.SelectedIndex = dayValues.Length - 1;
        }

        var kind = new ComboBox
        {
            Header = "Type",
            Items = { "All notes", "Handwritten notes", "Imported PDFs" },
            SelectedIndex = (int)(existing?.Kind ?? SmartNoteKind.Any),
            MinWidth = 200,
        };
        var titleBox = new TextBox { Header = "Title contains", PlaceholderText = "Any title", Text = existing?.TitleContains ?? "" };
        var pinned = new CheckBox { Content = "Pinned notes only", IsChecked = existing?.PinnedOnly == true };
        var preview = new TextBlock { FontSize = 12, Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"], TextWrapping = TextWrapping.Wrap };

        SmartFolder Current() => new()
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = (nameBox.Text ?? "").Trim(),
            Tags = tagChecks.Where(x => x.Box.IsChecked == true).Select(x => x.Tag).ToList(),
            MatchAllTags = tagMode.SelectedIndex == 1,
            EditedWithinDays = dayValues[Math.Max(0, edited.SelectedIndex)],
            Kind = (SmartNoteKind)Math.Max(0, kind.SelectedIndex),
            TitleContains = (titleBox.Text ?? "").Trim(),
            PinnedOnly = pinned.IsChecked == true,
        };
        void UpdatePreview()
        {
            var f = Current();
            int n = live.Count(i => MatchesSmartFolder(f, i));
            preview.Text = $"{f.Describe()}  —  {(n == 1 ? "1 note matches" : $"{n} notes match")} right now";
            tagMode.IsEnabled = tagChecks.Count(x => x.Box.IsChecked == true) > 1;
        }
        foreach (var (_, cb) in tagChecks) { cb.Checked += (_, __) => UpdatePreview(); cb.Unchecked += (_, __) => UpdatePreview(); }
        tagMode.SelectionChanged += (_, __) => UpdatePreview();
        edited.SelectionChanged += (_, __) => UpdatePreview();
        kind.SelectionChanged += (_, __) => UpdatePreview();
        titleBox.TextChanged += (_, __) => UpdatePreview();
        pinned.Checked += (_, __) => UpdatePreview();
        pinned.Unchecked += (_, __) => UpdatePreview();

        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(nameBox);
        var tagsHeader = new TextBlock { Text = "Tags", Margin = new Thickness(0, 0, 0, -6) };
        panel.Children.Add(tagsHeader);
        if (tagChecks.Count > 0)
        {
            panel.Children.Add(new ScrollViewer { Content = tagPanel, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            panel.Children.Add(tagMode);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No tags yet. Tag notes on Home (Select, then Tags) to filter by them here.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
            });
        }
        panel.Children.Add(edited);
        panel.Children.Add(kind);
        panel.Children.Add(titleBox);
        panel.Children.Add(pinned);
        panel.Children.Add(preview);
        UpdatePreview();

        var dlg = new ContentDialog
        {
            Title = existing is null ? "New smart folder" : "Edit smart folder",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = existing is null ? "Create" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        nameBox.Loaded += (_, __) => nameBox.Focus(FocusState.Programmatic);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var result = Current();
        // No name typed: name it after what it finds.
        if (result.Name.Length == 0) result.Name = result.Describe();
        if (result.Name.Length > 60) result.Name = result.Name[..60].TrimEnd() + "…";

        var list = App.Services.Settings.Current.SmartFolders;
        int idx = list.FindIndex(x => x.Id == result.Id);
        if (idx >= 0) list[idx] = result; else list.Add(result);
        App.Services.Settings.Save();

        // Show it straight away (refreshes the title and grid if it was open).
        _activeSmartId = null;
        SelectSmartFolder(result);
    }
}
