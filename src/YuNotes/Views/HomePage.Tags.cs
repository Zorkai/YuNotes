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
    // ── Tags ──────────────────────────────────────────────────────────────────
    // Notes can carry any number of tags. The sidebar lists every tag in use;
    // picking one shows the notes with it from all folders. "#tag" in the
    // search box filters the same way.

    private void SelectTag(string tag)
    {
        if (string.Equals(_activeTag, tag, StringComparison.OrdinalIgnoreCase))
        {
            SelectNav(NavSection.AllNotes);   // clicking the active tag clears it
            return;
        }
        _activeSection = NavSection.AllNotes;
        _selectedFolderPath = null;
        _activeTag = tag;
        _activeSmartId = null;
        ApplyNavSelection();
        SectionTitle.Text = "#" + tag;
        LoadFolderTree();   // drops the folder highlight
        Refresh();
    }

    private bool IsLiveNotePath(string path)
    {
        if (!File.Exists(path)) return false;
        string trash = TrashFolder + Path.DirectorySeparatorChar;
        string archive = ArchiveFolder + Path.DirectorySeparatorChar;
        return !path.StartsWith(trash, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(archive, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildTagList()
    {
        var counts = Services.NoteTags.Counts(IsLiveNotePath);
        // A tag whose last note was untagged or deleted ends the filter.
        if (_activeTag is not null && !counts.ContainsKey(_activeTag))
        {
            _activeTag = null;
            if (_activeSection == NavSection.AllNotes) SectionTitle.Text = "All notes";
            ApplyNavSelection();
        }
        TagList.Children.Clear();
        TagSection.Visibility = counts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var res = Application.Current.Resources;
        foreach (var (tag, count) in counts)
        {
            bool active = string.Equals(tag, _activeTag, StringComparison.OrdinalIgnoreCase);
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon
            {
                Glyph = "", FontSize = 16,
                FontFamily = (FontFamily)res["MaterialIconsFamily"],
                Foreground = (Brush)res[active ? "AppAccentBrush" : "AppTextSecondaryBrush"],
            });
            var name = new TextBlock
            {
                Text = tag, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var num = new TextBlock
            {
                Text = count.ToString(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)res["AppTextSecondaryBrush"],
            };
            Grid.SetColumn(num, 2);
            row.Children.Add(num);

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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"Tag {tag}, {count} {(count == 1 ? "note" : "notes")}");
            ToolTipService.SetToolTip(btn, active ? "Show all notes" : $"Show notes tagged #{tag}");
            string t = tag;
            btn.Click += (_, __) => SelectTag(t);
            TagList.Children.Add(btn);
        }
    }

    // Add or remove tags on the selected notes. Each known tag is a checkbox:
    // checked = on every selected note, unchecked = on none, and a third
    // "mixed" state (left alone) when only some of them have it.
    private async Task EditTagsForSelectedAsync()
    {
        var picked = RecentList.SelectedItems.OfType<RecentItem>().Where(r => !r.IsNewCard).ToList();
        if (picked.Count == 0) return;

        var known = Services.NoteTags.Counts(IsLiveNotePath).Keys
            .Concat(picked.SelectMany(p => p.Tags))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCulture)
            .ToList();

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        var boxes = new List<(string Tag, CheckBox Box)>();
        if (known.Count > 0)
        {
            var list = new StackPanel { Spacing = 0 };
            foreach (var tag in known)
            {
                int have = picked.Count(p => p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
                bool mixed = have > 0 && have < picked.Count;
                var cb = new CheckBox
                {
                    Content = "#" + tag,
                    IsThreeState = mixed,
                    IsChecked = mixed ? null : have == picked.Count,
                };
                boxes.Add((tag, cb));
                list.Children.Add(cb);
            }
            panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        var addBox = new TextBox
        {
            Header = "Add new tags",
            PlaceholderText = "exam, todo, week-3",
        };
        panel.Children.Add(addBox);

        var noun = picked.Count == 1 ? "note" : $"{picked.Count} notes";
        var dlg = new ContentDialog
        {
            Title = $"Tags for {(picked.Count == 1 ? $"\"{picked[0].Title}\"" : noun)}",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        addBox.Loaded += (_, __) => addBox.Focus(FocusState.Programmatic);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var added = Services.NoteTags.Parse(addBox.Text ?? "").ToList();
        foreach (var item in picked)
        {
            var tags = new HashSet<string>(item.Tags, StringComparer.OrdinalIgnoreCase);
            foreach (var (tag, box) in boxes)
            {
                if (box.IsChecked == true) tags.Add(tag);
                else if (box.IsChecked == false) tags.Remove(tag);
            }
            foreach (var t in added) tags.Add(t);
            Services.NoteTags.Set(item.Path, tags);
        }
        App.Services.Settings.Save();
        SetSelectMode(false);
        Refresh();
    }
}
