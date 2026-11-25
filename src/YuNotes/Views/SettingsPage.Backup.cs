using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using YuNotes.Services;

namespace YuNotes.Views;

// Settings → Storage → Back up / Restore. The work runs off the UI thread;
// the button shows progress and a dialog reports what happened.
public sealed partial class SettingsPage
{
    private void InitBackup()
    {
        BackupBtn.Click += async (_, _) => await BackupAsync();
        RestoreBackupBtn.Click += async (_, _) => await RestoreBackupAsync();
    }

    private void InitBackupPicker(object picker) =>
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Of(this)));

    private async Task BackupAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = $"YuNotes backup {DateTime.Now:yyyy-MM-dd}" };
        picker.FileTypeChoices.Add("YuNotes backup", new[] { BackupService.FileExtension });
        InitBackupPicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var settings = App.Services.Settings.Current;
        SetBackupBusy(true, "Backing up…");
        try
        {
            var r = await Task.Run(() => BackupService.Create(
                file.Path, settings, StickerLibrary.Folder, TemplateLibrary.Folder));
            string extra = (r.Stickers, r.Templates) switch
            {
                (0, 0) => "",
                (_, 0) => $", {Plural(r.Stickers, "sticker")}",
                (0, _) => $", {Plural(r.Templates, "template")}",
                _ => $", {Plural(r.Stickers, "sticker")} and {Plural(r.Templates, "template")}",
            };
            await ShowBackupDialog("Backup saved",
                $"{Plural(r.Notes, "note")}{extra} are in {file.Name} ({FormatSize(r.Bytes)}).\n\n" +
                "Keep it somewhere other than this PC, such as a USB drive or cloud storage.");
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Backup");
            await ShowBackupDialog("Backup failed", ex.Message);
        }
        finally { SetBackupBusy(false, null); }
    }

    private async Task RestoreBackupAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(BackupService.FileExtension);
        InitBackupPicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        BackupService.Manifest manifest;
        try { manifest = await Task.Run(() => BackupService.ReadManifest(file.Path)); }
        catch (Exception ex)
        {
            await ShowBackupDialog("Can't restore this file", ex.Message);
            return;
        }

        var prefs = new CheckBox { Content = "Also restore settings (pens, toolbar, theme and the rest)" };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Backup from {manifest.CreatedUtc.ToLocalTime():d MMMM yyyy, HH:mm} with {Plural(manifest.NoteCount, "note")}.\n\n" +
                   "Notes are added to your notes folder. Nothing is overwritten: notes you already have are skipped, " +
                   "and a note that has changed since comes back next to yours as a copy marked (restored).",
        });
        body.Children.Add(prefs);
        var confirm = new ContentDialog
        {
            Title = "Restore from backup",
            Content = body,
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        bool restorePrefs = prefs.IsChecked == true;
        var settings = App.Services.Settings.Current;
        SetBackupBusy(true, "Restoring…");
        try
        {
            var r = await Task.Run(() => BackupService.Restore(
                file.Path, settings, StickerLibrary.Folder, TemplateLibrary.Folder, restorePrefs));
            App.Services.Settings.Save();
            LoadIntoUi();

            var lines = new System.Collections.Generic.List<string>
            {
                $"{Plural(r.NotesAdded, "note")} added.",
            };
            if (r.NotesSkipped > 0) lines.Add($"{Plural(r.NotesSkipped, "note")} you already had were skipped.");
            if (r.NotesRenamed > 0) lines.Add($"{Plural(r.NotesRenamed, "note")} came back as a copy marked (restored), because yours has changed since.");
            if (r.Stickers > 0) lines.Add($"{Plural(r.Stickers, "sticker")} added.");
            if (r.Templates > 0) lines.Add($"{Plural(r.Templates, "template")} added.");
            if (r.PreferencesRestored) lines.Add("Settings restored.");
            await ShowBackupDialog("Restore finished", string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Restore");
            await ShowBackupDialog("Restore failed", ex.Message);
        }
        finally { SetBackupBusy(false, null); }
    }

    private object? _backupBtnContent, _restoreBtnContent;

    private void SetBackupBusy(bool busy, string? label)
    {
        if (busy)
        {
            _backupBtnContent = BackupBtn.Content;
            _restoreBtnContent = RestoreBackupBtn.Content;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
            row.Children.Add(new TextBlock { Text = label });
            if (label == "Restoring…") RestoreBackupBtn.Content = row; else BackupBtn.Content = row;
        }
        else
        {
            if (_backupBtnContent is not null) BackupBtn.Content = _backupBtnContent;
            if (_restoreBtnContent is not null) RestoreBackupBtn.Content = _restoreBtnContent;
        }
        BackupBtn.IsEnabled = RestoreBackupBtn.IsEnabled = !busy;
    }

    private async Task ShowBackupDialog(string title, string message)
    {
        var d = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await d.ShowAsync();
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
