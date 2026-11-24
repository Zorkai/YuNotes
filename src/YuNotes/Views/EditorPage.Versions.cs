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
    // ─── Earlier versions ────────────────────────────────────────────────────────
    // Lists the copies VersionHistory kept. Preview opens one read-only in the
    // side-by-side pane next to the current note, with a Restore button there;
    // Restore puts it back. The current note is saved and kept as a version
    // first, so a restore can be undone the same way.

    // The version shown in the side-by-side pane, if the pane is a preview.
    private Services.VersionHistory.Version? _refVersion;

    private async Task ShowVersionsAsync()
    {
        if (_doc is null || string.IsNullOrEmpty(_doc.Info.FilePath)) return;
        var path = _doc.Info.FilePath;
        var versions = await Task.Run(() => App.Services.Versions.List(path));
        var res = Application.Current.Resources;

        var list = new StackPanel { Spacing = 6, MinWidth = 360 };
        var dlg = new ContentDialog
        {
            Title = "Earlier versions",
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
            Content = new ScrollViewer { Content = list, MaxHeight = 420 },
        };
        list.Children.Add(new TextBlock
        {
            Text = "YuNotes keeps a copy of this note when you save, at most every 10 minutes (the newest 25).",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)res["AppTextSecondaryBrush"],
        });
        if (versions.Count == 0)
            list.Children.Add(new TextBlock { Text = "No earlier versions yet.", Margin = new Thickness(0, 8, 0, 8) });

        Services.VersionHistory.Version? chosen = null;
        Services.VersionHistory.Version? previewed = null;
        foreach (var v in versions)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 8, 8, 8), CornerRadius = new CornerRadius(8),
                                 Background = (Brush)res["AppSurfaceVariantBrush"] };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = VersionDate(v.SavedAt), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = $"{v.SavedAt:dddd d MMM yyyy, HH:mm:ss} · {v.Size / 1024.0 / 1024.0:0.0} MB",
                FontSize = 11, Foreground = (Brush)res["AppTextSecondaryBrush"],
            });
            var preview = new Button { Content = "Preview", CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(preview, $"Preview version from {VersionDate(v.SavedAt)}");
            ToolTipService.SetToolTip(preview, "Show it next to the current note, without changing anything");
            preview.Click += (_, __) => { previewed = v; dlg.Hide(); };
            var restore = new Button { Content = "Restore", CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(restore, $"Restore version from {VersionDate(v.SavedAt)}");
            restore.Click += (_, __) => { chosen = v; dlg.Hide(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(preview);
            buttons.Children.Add(restore);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(text);
            row.Children.Add(buttons);
            list.Children.Add(row);
        }

        await dlg.ShowAsync();
        if (previewed is not null) { await PreviewVersionAsync(previewed); return; }
        if (chosen is null) return;
        await RestoreVersionAsync(chosen);
    }

    // Read-only, side by side with the current note; the pane's button restores it.
    private async Task PreviewVersionAsync(Services.VersionHistory.Version v)
    {
        await OpenReferenceAsync(v.FilePath);
        if (_refPath != v.FilePath) return;   // couldn't open it
        _refVersion = v;
        RefTitle.Text = $"Version from {VersionDate(v.SavedAt).ToLowerInvariant()} (preview)";
        RefOpenBtn.Content = "Restore this version";
        ToolTipService.SetToolTip(RefOpenBtn, "Replace the current note with this version");
    }

    private async Task RestoreVersionAsync(Services.VersionHistory.Version chosen)
    {
        if (_doc is null || string.IsNullOrEmpty(_doc.Info.FilePath)) return;
        var path = _doc.Info.FilePath;
        var confirm = new ContentDialog
        {
            Title = "Restore this version?",
            Content = $"The note goes back to how it was {VersionDate(chosen.SavedAt).ToLowerInvariant()}. " +
                      "Its current state is kept as a version too, so you can switch back.",
            PrimaryButtonText = "Restore", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (_refVersion is not null) CloseReference();   // the preview's file is about to change hands
            if (_doc.IsDirty) await SaveAsync(toast: false);
            _doc.IsDirty = false;   // reopening below must not prompt or save over the restore
            var version = chosen;
            await Task.Run(() => App.Services.Versions.Restore(path, version));
            // Reopen the note fresh from the restored file (in this same tab).
            DropCachedTab(path);
            if (ActiveTab is not null) ActivateTab(_activeTab);
            else { MainWindow.GoBack(this); MainWindow.Navigate<EditorPage>(this, path); }
        }
        catch (Exception ex) { await Notify("Restore failed", ex.Message); }
    }

    private static string VersionDate(DateTime local)
    {
        var days = (DateTime.Today - local.Date).Days;
        if (days <= 0) return $"Today {local:HH:mm}";
        if (days == 1) return $"Yesterday {local:HH:mm}";
        return local.ToString(local.Year == DateTime.Today.Year ? "d MMM, HH:mm" : "d MMM yyyy, HH:mm");
    }
}
