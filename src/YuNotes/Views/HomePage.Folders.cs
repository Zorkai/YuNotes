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
    // ── Folder tree ───────────────────────────────────────────────────────────

    private static bool IsSystemFolder(string path)
        => Path.GetFileName(path)?.StartsWith("_") == true;

    private void LoadFolderTree()
    {
        FolderTree.RootNodes.Clear();

        var rootFolder = App.Services.Settings.Current.DocumentsFolder;
        if (!Directory.Exists(rootFolder)) return;

        // Keep the registry in sync with the actual filesystem before building the tree.
        SyncFolderRegistry(rootFolder, parentId: null);
        App.Services.Settings.Save();

        foreach (var dir in Directory.GetDirectories(rootFolder)
                                     .Where(d => !IsSystemFolder(d))
                                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            FolderTree.RootNodes.Add(BuildFolderNode(dir));
        }

        // Re-apply selection highlight if a folder is already selected.
        if (_selectedFolderPath is not null)
            SelectTreeNode(FolderTree.RootNodes, _selectedFolderPath);
    }

    private TreeViewNode BuildFolderNode(string dirPath)
    {
        var name = Path.GetFileName(dirPath)!;
        var info = App.Services.Settings.Current.Folders
            .FirstOrDefault(f => string.Equals(f.Path, dirPath, StringComparison.OrdinalIgnoreCase));

        var item = new FolderTreeItem
        {
            Id         = info?.Id ?? Guid.NewGuid().ToString("N"),
            Name       = name,
            Path       = dirPath,
            IsSelected = string.Equals(dirPath, _selectedFolderPath, StringComparison.OrdinalIgnoreCase),
            ColorBrush = new SolidColorBrush(FolderColors.Parse(info?.Color)),
            NoteCount  = CountNotes(dirPath)
        };
        var node = new TreeViewNode { Content = item, IsExpanded = true };

        foreach (var sub in Directory.GetDirectories(dirPath)
                                     .Where(d => !IsSystemFolder(d))
                                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            node.Children.Add(BuildFolderNode(sub));
        }
        return node;
    }

    // Adds any filesystem folders missing from the registry; removes stale entries.
    private static void SyncFolderRegistry(string parentDir, string? parentId)
    {
        var settings = App.Services.Settings.Current;
        foreach (var dir in Directory.GetDirectories(parentDir).Where(d => !IsSystemFolder(d)))
        {
            var existing = settings.Folders.FirstOrDefault(f =>
                string.Equals(f.Path, dir, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new FolderInfo
                {
                    Name     = Path.GetFileName(dir)!,
                    Path     = dir,
                    ParentId = parentId,
                    Color    = FolderColors.NextFor(settings.Folders)
                };
                settings.Folders.Add(existing);
            }
            // Folders saved before colors existed get one now.
            existing.Color ??= FolderColors.NextFor(settings.Folders);
            SyncFolderRegistry(dir, existing.Id);
        }
        // Prune entries whose directories no longer exist under this parent.
        settings.Folders.RemoveAll(f =>
            !Directory.Exists(f.Path) &&
            (parentId is null
                ? Path.GetDirectoryName(f.Path)?.Equals(parentDir, StringComparison.OrdinalIgnoreCase) == true
                : f.ParentId == parentId));
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs e)
    {
        if (e.InvokedItem is TreeViewNode node && node.Content is FolderTreeItem item)
            SelectFolder(item.Path);
    }

    private void FolderTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null && d is not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        if (d is not TreeViewItem tvi) return;

        // The container's DataContext may be the TreeViewNode or its FolderTreeItem
        // content depending on TreeView mode — handle both.
        FolderTreeItem? item = tvi.DataContext switch
        {
            TreeViewNode { Content: FolderTreeItem fi } => fi,
            FolderTreeItem fi => fi,
            _ => null
        };
        if (item is null) return;

        BuildFolderMenu(item).ShowAt(tvi, new FlyoutShowOptions { Position = e.GetPosition(tvi) });
        e.Handled = true;
    }

    // Per-row "⋯" button — a discoverable alternative to right-clicking.
    private void FolderMoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var item = fe.DataContext switch
        {
            TreeViewNode { Content: FolderTreeItem fi } => fi,
            FolderTreeItem fi => fi,
            _ => null
        };
        if (item is null) return;
        BuildFolderMenu(item).ShowAt(fe);
    }

    private MenuFlyout BuildFolderMenu(FolderTreeItem item)
    {
        var flyout   = new MenuFlyout { ShouldConstrainToRootBounds = true };
        var newSub   = new MenuFlyoutItem { Text = "New subfolder" };
        newSub.Click += async (_, __) => await CreateFolderAsync(item.Path);
        var rename   = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += async (_, __) => await RenameFolderAsync(item.Path);
        var archive  = new MenuFlyoutItem { Text = "Archive folder" };
        archive.Click += async (_, __) => await ArchiveFolderAsync(item.Path);
        var deleteFi = new MenuFlyoutItem { Text = "Delete folder" };
        deleteFi.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 58, 58));
        deleteFi.Click += async (_, __) => await DeleteFolderAsync(item.Path);
        var colorMenu = new MenuFlyoutSubItem { Text = "Color" };
        var current = App.Services.Settings.Current.Folders.FirstOrDefault(f =>
            string.Equals(f.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        foreach (var (cname, hex) in FolderColors.Palette)
        {
            var ci = new MenuFlyoutItem
            {
                Text = string.Equals(current?.Color, hex, StringComparison.OrdinalIgnoreCase) ? $"{cname}  ✓" : cname,
                Icon = new FontIcon
                {
                    Glyph = "●",
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = new SolidColorBrush(FolderColors.Parse(hex))
                }
            };
            ci.Click += (_, __) => SetFolderColor(item.Path, hex);
            colorMenu.Items.Add(ci);
        }
        flyout.Items.Add(newSub);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(rename);
        flyout.Items.Add(colorMenu);
        flyout.Items.Add(archive);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteFi);
        return flyout;
    }

    private void SetFolderColor(string folderPath, string hex)
    {
        var settings = App.Services.Settings.Current;
        var info = settings.Folders.FirstOrDefault(f =>
            string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        if (info is null) return;
        info.Color = hex;
        App.Services.Settings.Save();
        LoadFolderTree();
        Refresh();
    }

    private static int CountNotes(string dirPath)
    {
        try { return GetAllNotePaths(dirPath).Count(); }
        catch { return 0; }
    }

    // Recomputes the sidebar note counts after notes move around.
    private void UpdateFolderCounts()
    {
        void Walk(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Content is FolderTreeItem fi) fi.NoteCount = CountNotes(fi.Path);
                Walk(n.Children);
            }
        }
        Walk(FolderTree.RootNodes);
    }

    // Folder rows show their note count; on hover it gives way to the ⋯ button.
    private void FolderRow_PointerEntered(object sender, PointerRoutedEventArgs e) => SetFolderRowHover(sender, true);
    private void FolderRow_PointerExited(object sender, PointerRoutedEventArgs e)  => SetFolderRowHover(sender, false);

    private static void SetFolderRowHover(object sender, bool hover)
    {
        if (sender is not Grid g) return;
        foreach (var child in g.Children)
        {
            if (child is Button b) b.Opacity = hover ? 1 : 0;
            else if (child is TextBlock t && Grid.GetColumn(t) == 1) t.Opacity = hover ? 0 : 1;
        }
    }

    // ── Drag-drop from notes grid onto folder tree ────────────────────────────

    private void RecentList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<RecentItem>().Select(r => r.Path);
        e.Data.SetText(string.Join("\n", paths));
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void FolderTree_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        var item = HitTestFolderTree(e.GetPosition(FolderTree));
        if (item is not null)
        {
            _dragTargetPath = item.Path;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption          = $"Move to \"{item.Name}\"";
            e.DragUIOverride.IsCaptionVisible  = true;
            e.DragUIOverride.IsContentVisible  = false;
        }
        else
        {
            _dragTargetPath     = null;
            e.AcceptedOperation = DataPackageOperation.None;
        }
        e.Handled = true;
    }

    private async void FolderTree_Drop(object sender, DragEventArgs e)
    {
        var targetPath = _dragTargetPath;
        _dragTargetPath = null;
        if (targetPath is null) return;
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;

        var text  = await e.DataView.GetTextAsync();
        var paths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var recent = App.Services.Settings.Current.RecentDocuments;
        var errors = new List<string>();
        foreach (var src in paths)
        {
            if (string.Equals(Path.GetDirectoryName(src), targetPath, StringComparison.OrdinalIgnoreCase))
                continue;   // already in target
            try
            {
                var dest = Path.Combine(targetPath, Path.GetFileName(src));
                if (File.Exists(dest) && !string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
                    dest = Path.Combine(targetPath,
                        Path.GetFileNameWithoutExtension(src) +
                        $"_{DateTime.Now:yyyyMMddHHmmss}" +
                        Path.GetExtension(src));
                File.Move(src, dest);
                Services.NoteTags.MovePath(src, dest);
                recent.Remove(src);
                if (!recent.Contains(dest)) recent.Insert(0, dest);
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(src)}: {ex.Message}"); }
        }
        App.Services.Settings.Save();
        Refresh();
        if (errors.Count > 0)
            await ShowDialogAsync("Some files couldn't be moved", string.Join("\n", errors));
    }

    // Returns the FolderTreeItem under the given TreeView-local point, or null.
    private FolderTreeItem? HitTestFolderTree(Point localPoint)
    {
        try
        {
            var windowPoint = FolderTree.TransformToVisual(null).TransformPoint(localPoint);
            var elements    = VisualTreeHelper.FindElementsInHostCoordinates(windowPoint, FolderTree);
            foreach (var el in elements)
            {
                DependencyObject? d = el as DependencyObject;
                while (d is not null && d != FolderTree)
                {
                    if (d is TreeViewItem tvi &&
                        tvi.DataContext is TreeViewNode node &&
                        node.Content is FolderTreeItem item)
                        return item;
                    d = VisualTreeHelper.GetParent(d);
                }
            }
        }
        catch { }
        return null;
    }

    // ── Folder CRUD ───────────────────────────────────────────────────────────

    private async Task CreateFolderAsync(string? parentPath = null)
    {
        var box = new TextBox { PlaceholderText = "Folder name", Width = 260 };
        var dlg = new ContentDialog
        {
            Title = "New folder", Content = box,
            PrimaryButtonText = "Create", CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        // Create inside selected subfolder if invoked from its context menu,
        // otherwise inside the currently selected folder (or root).
        var parent = parentPath
            ?? (_activeSection == NavSection.Folders && _selectedFolderPath is not null
                ? _selectedFolderPath
                : App.Services.Settings.Current.DocumentsFolder);

        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var dirPath  = Path.Combine(parent, safeName);
        try
        {
            Directory.CreateDirectory(dirPath);

            var parentInfo = App.Services.Settings.Current.Folders
                .FirstOrDefault(f => string.Equals(f.Path, parent, StringComparison.OrdinalIgnoreCase));
            App.Services.Settings.Current.Folders.Add(new FolderInfo
            {
                Name     = name,
                Path     = dirPath,
                ParentId = parentInfo?.Id,
                Color    = FolderColors.NextFor(App.Services.Settings.Current.Folders)
            });
            App.Services.Settings.Save();

            LoadFolderTree();
        }
        catch (Exception ex) { await ShowDialogAsync("Could not create folder", ex.Message); }
    }

    private async Task RenameFolderAsync(string folderPath)
    {
        var box = new TextBox { Text = Path.GetFileName(folderPath), Width = 260 };
        var dlg = new ContentDialog
        {
            Title = "Rename folder", Content = box,
            PrimaryButtonText = "Rename", CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = box.Text?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == Path.GetFileName(folderPath)) return;

        var newPath = Path.Combine(Path.GetDirectoryName(folderPath)!,
                                   string.Join("_", newName.Split(Path.GetInvalidFileNameChars())));
        try
        {
            Directory.Move(folderPath, newPath);
            Services.NoteTags.MoveFolder(folderPath, newPath);

            // Update FolderInfo registry
            var info = App.Services.Settings.Current.Folders
                .FirstOrDefault(f => string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
            if (info is not null) { info.Name = newName; info.Path = newPath; }

            // Also patch RecentDocuments paths (they store full file paths)
            var recent = App.Services.Settings.Current.RecentDocuments;
            var prefix = folderPath + Path.DirectorySeparatorChar;
            for (int i = 0; i < recent.Count; i++)
                if (recent[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    recent[i] = Path.Combine(newPath, recent[i][prefix.Length..]);

            App.Services.Settings.Save();

            if (string.Equals(_selectedFolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
            {
                _selectedFolderPath = newPath;
                SectionTitle.Text   = newName;
            }
            LoadFolderTree();
            Refresh();
        }
        catch (Exception ex) { await ShowDialogAsync("Could not rename folder", ex.Message); }
    }

    private async Task DeleteFolderAsync(string folderPath)
    {
        var name     = Path.GetFileName(folderPath);
        bool hasNotes = Directory.Exists(folderPath) &&
                        GetAllNotePaths(folderPath).Any();

        var msg = hasNotes
            ? $"\"{name}\" contains notes. Move the folder and all its notes to Trash?"
            : $"Move the folder \"{name}\" to Trash?";

        var dlg = new ContentDialog
        {
            Title = "Delete folder", Content = msg,
            PrimaryButtonText = "Move to Trash", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            // Move each note in the folder to trash first, then delete the empty directory.
            Directory.CreateDirectory(TrashFolder);
            var recent = App.Services.Settings.Current.RecentDocuments;
            foreach (var notePath in GetAllNotePaths(folderPath).ToList())
            {
                var dest = UniqueTrashPath(notePath);
                File.Move(notePath, dest);
                Services.NoteTags.MovePath(notePath, dest);
                recent.Remove(notePath);
            }

            // Remove the folder from disk. The tree is rebuilt by scanning the filesystem, so
            // the directory MUST actually leave the documents root or it reappears on refresh.
            RemoveFolderTree(folderPath);

            // Clean up registry
            App.Services.Settings.Current.Folders.RemoveAll(f =>
                f.Path.StartsWith(folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));

            App.Services.Settings.Save();

            if (string.Equals(_selectedFolderPath, folderPath, StringComparison.OrdinalIgnoreCase) ||
                _selectedFolderPath?.StartsWith(folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true)
            {
                _selectedFolderPath = null;
                SectionTitle.Text   = "Folders";
            }
            LoadFolderTree();
            Refresh();
        }
        catch (Exception ex) { await ShowDialogAsync("Could not delete folder", ex.Message); }
    }

    /// <summary>
    /// Removes a folder directory from the documents root. Because the folder tree is rebuilt
    /// by scanning the filesystem, the directory must genuinely leave the root. Tries a normal
    /// recursive delete (first clearing the read-only attributes that OneDrive-synced files
    /// often carry, which otherwise make Directory.Delete throw); if that still fails, moves the
    /// directory into the hidden "_trash" folder (excluded from the tree) as a guaranteed fallback.
    /// </summary>
    private void RemoveFolderTree(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(folderPath, recursive: true);
        }
        catch
        {
            try
            {
                Directory.CreateDirectory(TrashFolder);
                var dest = Path.Combine(TrashFolder, "_folder_" + Path.GetFileName(folderPath));
                if (Directory.Exists(dest)) dest += "_" + DateTime.Now.ToString("yyyyMMddHHmmss");
                Directory.Move(folderPath, dest);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Folder cleanup failed: {ex.Message}"); }
        }
    }

    // ── Move to folder ────────────────────────────────────────────────────────

    private async Task MoveToFolderAsync()
    {
        var picked = RecentList.SelectedItems.OfType<RecentItem>().Where(r => !r.IsNewCard).ToList();
        if (picked.Count == 0) return;

        var rootFolder = App.Services.Settings.Current.DocumentsFolder;
        var allFolders = new List<(string Path, int Depth)>();
        CollectFoldersRecursive(rootFolder, 0, allFolders);

        if (allFolders.Count == 0)
        {
            await ShowDialogAsync("No folders",
                "Create a folder first using the Folders section in the sidebar.");
            return;
        }

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300 };
        foreach (var (dir, depth) in allFolders)
        {
            var indent = depth > 0 ? new string(' ', depth * 3) : "";
            var sp     = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                                          Padding = new Thickness(0, 4, 0, 4) };
            if (depth > 0)
                sp.Children.Add(new TextBlock { Text = indent,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11 });
            sp.Children.Add(new FontIcon { Glyph = "", FontSize = 15, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["MaterialIconsFamily"] });
            sp.Children.Add(new TextBlock { Text = Path.GetFileName(dir),
                                            VerticalAlignment = VerticalAlignment.Center });
            list.Items.Add(new ListViewItem { Content = sp, Tag = dir });
        }
        list.SelectedIndex = 0;

        var noun = picked.Count == 1 ? "note" : "notes";
        var dlg  = new ContentDialog
        {
            Title             = $"Move {picked.Count} {noun} to folder",
            Content           = list,
            PrimaryButtonText = "Move",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (list.SelectedItem is not ListViewItem selected || selected.Tag is not string targetFolder) return;

        var errors = new List<string>();
        var recent = App.Services.Settings.Current.RecentDocuments;
        foreach (var item in picked)
        {
            try
            {
                var dest = Path.Combine(targetFolder, Path.GetFileName(item.Path));
                if (File.Exists(dest) && !string.Equals(item.Path, dest, StringComparison.OrdinalIgnoreCase))
                    dest = Path.Combine(targetFolder,
                               Path.GetFileNameWithoutExtension(item.Path) +
                               $"_{DateTime.Now:yyyyMMddHHmmss}" +
                               Path.GetExtension(item.Path));
                File.Move(item.Path, dest);
                Services.NoteTags.MovePath(item.Path, dest);
                recent.Remove(item.Path);
                if (!recent.Contains(dest)) recent.Insert(0, dest);
            }
            catch (Exception ex) { errors.Add($"{item.Title}: {ex.Message}"); }
        }
        App.Services.Settings.Save();
        SetSelectMode(false);
        Refresh();

        if (errors.Count > 0)
            await ShowDialogAsync("Some files couldn't be moved", string.Join("\n", errors));
    }

    private static void CollectFoldersRecursive(string parent, int depth, List<(string, int)> result)
    {
        foreach (var dir in Directory.GetDirectories(parent)
                                     .Where(d => !IsSystemFolder(d))
                                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            result.Add((dir, depth));
            CollectFoldersRecursive(dir, depth + 1, result);
        }
    }
}
