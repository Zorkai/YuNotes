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
    // Lists the copies VersionHistory kept and puts one back. The current note
    // is saved and kept as a version first, so a restore can be undone the
    // same way.
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
            var restore = new Button { Content = "Restore", CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(restore, $"Restore version from {VersionDate(v.SavedAt)}");
            restore.Click += (_, __) => { chosen = v; dlg.Hide(); };
            Grid.SetColumn(restore, 1);
            row.Children.Add(text);
            row.Children.Add(restore);
            list.Children.Add(row);
        }

        await dlg.ShowAsync();
        if (chosen is null) return;

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
            if (_doc.IsDirty) await SaveAsync(toast: false);
            _doc.IsDirty = false;   // reopening below must not prompt or save over the restore
            var version = chosen;
            await Task.Run(() => App.Services.Versions.Restore(path, version));
            // Reopen the note fresh from the restored file (in this same tab).
            DropCachedTab(path);
            if (ActiveTab is not null) ActivateTab(s_activeTab);
            else { MainWindow.GoBack(); MainWindow.Navigate<EditorPage>(path); }
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
