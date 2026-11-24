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
    // ── Delete / Trash ────────────────────────────────────────────────────────

    private async Task DeleteOrTrashSelectedAsync()
    {
        var picked = RecentList.SelectedItems.OfType<RecentItem>().Where(r => !r.IsNewCard).ToList();
        if (picked.Count == 0) return;

        if (_activeSection is NavSection.Trash or NavSection.Archive)
            await DeletePermanentlyAsync(picked);
        else
            await MoveToTrashAsync(picked);
    }

    private async Task MoveToTrashAsync(IList<RecentItem> items)
    {
        var noun = items.Count == 1 ? "note" : "notes";
        var dlg  = new ContentDialog
        {
            Title             = $"Move {items.Count} {noun} to Trash?",
            Content           = "You can restore them from the Trash section.",
            PrimaryButtonText = "Move to Trash",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        Directory.CreateDirectory(TrashFolder);
        var recent = App.Services.Settings.Current.RecentDocuments;
        var errors = new List<string>();
        foreach (var item in items)
        {
            try
            {
                var dest = UniqueTrashPath(item.Path);
                File.Move(item.Path, dest);
                Services.NoteTags.MovePath(item.Path, dest);
                recent.Remove(item.Path);
            }
            catch (Exception ex) { errors.Add($"{item.Title}: {ex.Message}"); }
        }
        App.Services.Settings.Save();
        SetSelectMode(false);
        Refresh();
        if (errors.Count > 0)
            await ShowDialogAsync("Some files couldn't be moved to Trash", string.Join("\n", errors));
    }

    private async Task RestoreSelectedAsync()
    {
        var picked = RecentList.SelectedItems.OfType<RecentItem>().Where(r => !r.IsNewCard).ToList();
        if (picked.Count > 0) await RestoreFromTrashAsync(picked);
    }

    private async Task RestoreFromTrashAsync(IEnumerable<RecentItem> items)
    {
        var rootFolder = App.Services.Settings.Current.DocumentsFolder;
        var recent     = App.Services.Settings.Current.RecentDocuments;
        var errors     = new List<string>();
        foreach (var item in items)
        {
            try
            {
                var dest = Path.Combine(rootFolder, Path.GetFileName(item.Path));
                if (File.Exists(dest))
                    dest = Path.Combine(rootFolder,
                               Path.GetFileNameWithoutExtension(item.Path) +
                               $"_{DateTime.Now:yyyyMMddHHmmss}" +
                               Path.GetExtension(item.Path));
                File.Move(item.Path, dest);
                Services.NoteTags.MovePath(item.Path, dest);
                if (!recent.Contains(dest)) recent.Insert(0, dest);
            }
            catch (Exception ex) { errors.Add($"{item.Title}: {ex.Message}"); }
        }
        App.Services.Settings.Save();
        SetSelectMode(false);
        Refresh();
        if (errors.Count > 0)
            await ShowDialogAsync("Some files couldn't be restored", string.Join("\n", errors));
    }

    private async Task DeletePermanentlyAsync(IEnumerable<RecentItem> items)
    {
        var list = items.ToList();
        var noun = list.Count == 1 ? "note" : "notes";
        var dlg  = new ContentDialog
        {
            Title             = $"Permanently delete {list.Count} {noun}?",
            Content           = "This cannot be undone.",
            PrimaryButtonText = "Delete permanently",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var recent = App.Services.Settings.Current.RecentDocuments;
        var errors = new List<string>();
        foreach (var item in list)
        {
            try
            {
                App.Services.Documents.Delete(item.Path);
                Services.NoteTags.Remove(item.Path);
                recent.Remove(item.Path);
            }
            catch (Exception ex) { errors.Add($"{item.Title}: {ex.Message}"); }
        }
        App.Services.Settings.Save();
        SetSelectMode(false);
        Refresh();
        if (errors.Count > 0)
            await ShowDialogAsync("Some files couldn't be deleted", string.Join("\n", errors));
    }

    private string UniqueTrashPath(string sourcePath)
    {
        Directory.CreateDirectory(TrashFolder);
        var dest = Path.Combine(TrashFolder, Path.GetFileName(sourcePath));
        if (!File.Exists(dest)) return dest;
        return Path.Combine(TrashFolder,
            Path.GetFileNameWithoutExtension(sourcePath) +
            $"_{DateTime.Now:yyyyMMddHHmmss}" +
            Path.GetExtension(sourcePath));
    }

    // ── Archive / Unarchive ─────────────────────────────────────────────────────

    private async Task ArchiveSelectedAsync()
    {
        var picked = RecentList.SelectedItems.OfType<RecentItem>().Where(r => !r.IsNewCard).ToList();
        if (picked.Count == 0) return;

        var recent = App.Services.Settings.Current.RecentDocuments;
        var errors = new List<string>();
        foreach (var item in picked)
        {
            try
            {
                var dest = ArchiveDestFor(item.Path);
                File.Move(item.Path, dest);
                Services.NoteTags.MovePath(item.Path, dest);
                recent.Remove(item.Path);
            }
            catch (Exception ex) { errors.Add($"{item.Title}: {ex.Message}"); }
        }
        App.Services.Settings.Save();
        SetSelectMode(false);
        Refresh();
        if (errors.Count > 0)
            await ShowDialogAsync("Some notes couldn't be archived", string.Join("\n", errors));
    }

    private async Task UnarchiveSelectedAsync()
    {
        var picked = RecentList.SelectedItems.OfType<RecentItem>().Where(r => !r.IsNewCard).ToList();
        if (picked.Count > 0) await UnarchiveAsync(picked);
    }

    private async Task UnarchiveAsync(IEnumerable<RecentItem> items)
    {
        var recent = App.Services.Settings.Current.RecentDocuments;
        var errors = new List<string>();
        foreach (var item in items)
        {
            try
            {
                var dest = UnarchiveDestFor(item.Path);
                File.Move(item.Path, dest);
                Services.NoteTags.MovePath(item.Path, dest);
                if (!recent.Contains(dest)) recent.Insert(0, dest);
            }
            catch (Exception ex) { errors.Add($"{item.Title}: {ex.Message}"); }
        }
        App.Services.Settings.Save();
        SetSelectMode(false);
        LoadFolderTree();   // folders recreated by restoring their notes reappear
        Refresh();
        if (errors.Count > 0)
            await ShowDialogAsync("Some notes couldn't be unarchived", string.Join("\n", errors));
    }

    private async Task ArchiveFolderAsync(string folderPath)
    {
        var name      = Path.GetFileName(folderPath);
        bool hasNotes = Directory.Exists(folderPath) && GetAllNotePaths(folderPath).Any();
        var msg = hasNotes
            ? $"Archive \"{name}\" and all its notes? You can unarchive them later from the Archive section."
            : $"Archive the folder \"{name}\"?";

        var dlg = new ContentDialog
        {
            Title = "Archive folder", Content = msg,
            PrimaryButtonText = "Archive", CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            // Move the whole folder in one go (a same-volume rename), preserving its
            // structure under _archive. This avoids a recursive delete, which can fail
            // with "access denied" on OneDrive-synced folders.
            var rel  = RelativePathUnder(App.Services.Settings.Current.DocumentsFolder, folderPath);
            var dest = Path.Combine(ArchiveFolder, rel);
            if (Directory.Exists(dest) || File.Exists(dest))
                dest += $"_{DateTime.Now:yyyyMMddHHmmss}";
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Directory.Move(folderPath, dest);
            Services.NoteTags.MoveFolder(folderPath, dest);

            var prefix = folderPath + Path.DirectorySeparatorChar;

            // Drop recent entries and registry rows for the folder and its descendants.
            App.Services.Settings.Current.RecentDocuments.RemoveAll(p =>
                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p, folderPath, StringComparison.OrdinalIgnoreCase));
            App.Services.Settings.Current.Folders.RemoveAll(f =>
                f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
            App.Services.Settings.Save();

            if (string.Equals(_selectedFolderPath, folderPath, StringComparison.OrdinalIgnoreCase) ||
                _selectedFolderPath?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                _selectedFolderPath = null;
                SectionTitle.Text   = "Folders";
            }
            LoadFolderTree();
            Refresh();
        }
        catch (Exception ex) { await ShowDialogAsync("Could not archive folder", ex.Message); }
    }

    // Destination inside _archive that mirrors the note's path relative to the
    // documents root, so unarchiving can restore the original folder structure.
    private string ArchiveDestFor(string sourcePath)
    {
        var rel  = RelativePathUnder(App.Services.Settings.Current.DocumentsFolder, sourcePath);
        var dest = Path.Combine(ArchiveFolder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        return File.Exists(dest) ? UniqueSibling(dest) : dest;
    }

    // Destination back under the documents root, mirroring the archived path.
    private string UnarchiveDestFor(string archivedPath)
    {
        var rel  = RelativePathUnder(ArchiveFolder, archivedPath);
        var dest = Path.Combine(App.Services.Settings.Current.DocumentsFolder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        return File.Exists(dest) ? UniqueSibling(dest) : dest;
    }

    private static string RelativePathUnder(string root, string fullPath)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullPath[prefix.Length..]
            : Path.GetFileName(fullPath);
    }

    private static string UniqueSibling(string path)
        => Path.Combine(Path.GetDirectoryName(path)!,
               Path.GetFileNameWithoutExtension(path) +
               $"_{DateTime.Now:yyyyMMddHHmmss}" +
               Path.GetExtension(path));
}
