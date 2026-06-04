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
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using YuNotes.Models;

namespace YuNotes.Views;

// Data object for a folder node in the TreeView.
public sealed class FolderTreeItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsSelected { get; set; }
    public string Glyph => IsSelected ? "" : "";
}

public sealed partial class HomePage : Page
{
    public sealed class RecentItem
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string Path { get; set; } = "";
        public string FolderName { get; set; } = "";
        public Microsoft.UI.Xaml.Media.ImageSource? Thumbnail { get; set; }
        public Microsoft.UI.Xaml.Visibility NoThumbnail =>
            Thumbnail is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        public Microsoft.UI.Xaml.Visibility FolderLabelVisibility =>
            string.IsNullOrEmpty(FolderName) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    }

    private enum NavSection { AllNotes, Trash, Archive, Folders }

    private NavSection _activeSection = NavSection.AllNotes;
    private bool _selectMode;
    private bool _sidebarOpen = true;
    private string? _selectedFolderPath;

    // Folder currently under the drag cursor (set during DragOver, consumed in Drop).
    private string? _dragTargetPath;

    private string TrashFolder =>
        Path.Combine(App.Services.Settings.Current.DocumentsFolder, "_trash");

    private string ArchiveFolder =>
        Path.Combine(App.Services.Settings.Current.DocumentsFolder, "_archive");

    public HomePage()
    {
        InitializeComponent();

        // Top-bar action buttons
        NewBtn.Click    += async (_, __) => await NewNoteAsync();
        OpenBtn.Click   += async (_, __) => await OpenExistingAsync();
        SettingsBtn.Click += (_, __) => MainWindow.Navigate<SettingsPage>();

        // Folder section
        NewFolderBtn.Click += async (_, __) => await CreateFolderAsync();

        // TreeView events
        FolderTree.ItemInvoked   += FolderTree_ItemInvoked;
        // Use handledEventsToo so the right-tap still reaches us even when a
        // TreeViewItem marks the event handled before it bubbles up.
        FolderTree.AddHandler(UIElement.RightTappedEvent,
            new RightTappedEventHandler(FolderTree_RightTapped), handledEventsToo: true);
        FolderTree.DragOver      += FolderTree_DragOver;
        FolderTree.Drop          += FolderTree_Drop;

        // Selection mode
        SelectBtn.Click       += (_, __) => SetSelectMode(true);
        CancelSelectBtn.Click += (_, __) => SetSelectMode(false);
        DeleteBtn.Click       += async (_, __) => await DeleteOrTrashSelectedAsync();
        RestoreBtn.Click      += async (_, __) => await RestoreSelectedAsync();
        ArchiveBtn.Click      += async (_, __) => await ArchiveSelectedAsync();
        UnarchiveBtn.Click    += async (_, __) => await UnarchiveSelectedAsync();
        RecentList.SelectionChanged += (_, __) =>
        {
            // SelectedItems is only valid in a multi-select mode; reading it in None/Single
            // can throw COMException (0x8000FFFF), and this fires during mode transitions.
            int n = RecentList.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended
                ? RecentList.SelectedItems.Count : 0;
            SelectionCountLabel.Text = $"{n} selected";
            DeleteBtn.IsEnabled    = n > 0;
            MoveToFolderBtn.IsEnabled = n > 0;
            RestoreBtn.IsEnabled   = n > 0;
            ArchiveBtn.IsEnabled   = n > 0;
            UnarchiveBtn.IsEnabled = n > 0;
        };
        RecentList.RightTapped += OnRecentRightTapped;

        // Drag from notes grid
        RecentList.DragItemsStarting += RecentList_DragItemsStarting;

        // Sidebar navigation
        SidebarToggleBtn.Click += (_, __) => ToggleSidebar();
        AllNotesNavBtn.Click   += (_, __) => SelectNav(NavSection.AllNotes);
        TrashNavBtn.Click      += (_, __) => SelectNav(NavSection.Trash);
        ArchiveNavBtn.Click    += (_, __) => SelectNav(NavSection.Archive);

        // Selection — move to folder
        MoveToFolderBtn.Click += async (_, __) => await MoveToFolderAsync();

        Loaded += (_, __) =>
        {
            ApplyNavSelection();
            LoadFolderTree();
            Refresh();
        };
    }

    // ── Sidebar toggle ────────────────────────────────────────────────────────

    private void ToggleSidebar()
    {
        _sidebarOpen = !_sidebarOpen;
        SidebarBorder.Visibility = _sidebarOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Sidebar navigation ────────────────────────────────────────────────────

    private void SelectNav(NavSection section)
    {
        _activeSection = section;
        _selectedFolderPath = null;
        ApplyNavSelection();
        SectionTitle.Text = section switch
        {
            NavSection.Trash   => "Trash",
            NavSection.Archive => "Archive",
            NavSection.Folders => "Folders",
            _                  => "All notes"
        };
        Refresh();
    }

    private void SelectFolder(string folderPath)
    {
        _activeSection = NavSection.Folders;
        _selectedFolderPath = folderPath;
        ApplyNavSelection();
        SectionTitle.Text = Path.GetFileName(folderPath);
        // Rebuild so IsSelected (and therefore the glyph) reflects the new selection.
        LoadFolderTree();
        Refresh();
    }

    private bool SelectTreeNode(IList<TreeViewNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (node.Content is FolderTreeItem item &&
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                FolderTree.SelectedNode = node;
                return true;
            }
            if (node.HasUnrealizedChildren || node.Children.Count > 0)
                if (SelectTreeNode(node.Children, path)) return true;
        }
        return false;
    }

    private void ApplyNavSelection()
    {
        var accent = (Brush)Application.Current.Resources["AppSurfaceVariantBrush"];
        var none   = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        AllNotesNavBtn.Background = _activeSection == NavSection.AllNotes ? accent : none;
        TrashNavBtn.Background    = _activeSection == NavSection.Trash    ? accent : none;
        ArchiveNavBtn.Background  = _activeSection == NavSection.Archive  ? accent : none;

        AllNotesIcon.Foreground  = _activeSection == NavSection.AllNotes
            ? (Brush)Application.Current.Resources["AppTextPrimaryBrush"]
            : (Brush)Application.Current.Resources["AppTextSecondaryBrush"];
        AllNotesLabel.FontWeight = _activeSection == NavSection.AllNotes
            ? new Windows.UI.Text.FontWeight { Weight = 600 }
            : new Windows.UI.Text.FontWeight { Weight = 400 };
    }

    // ── Select mode ───────────────────────────────────────────────────────────

    private void OnRecentRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null && d is not GridViewItem) d = VisualTreeHelper.GetParent(d);
        if (d is not GridViewItem container) return;
        if (container.Content is not RecentItem item) return;

        if (_activeSection == NavSection.Trash)
        {
            // Trash items get a dedicated context menu
            var flyout   = new MenuFlyout();
            var restore  = new MenuFlyoutItem { Text = "Restore" };
            restore.Click += async (_, __) => await RestoreFromTrashAsync(new[] { item });
            var deletePerm = new MenuFlyoutItem { Text = "Delete permanently" };
            deletePerm.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 58, 58));
            deletePerm.Click += async (_, __) => await DeletePermanentlyAsync(new[] { item });
            flyout.Items.Add(restore);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(deletePerm);
            flyout.ShowAt(container, new FlyoutShowOptions { Position = e.GetPosition(container) });
        }
        else if (_activeSection == NavSection.Archive)
        {
            // Archive items: unarchive or delete permanently
            var flyout    = new MenuFlyout();
            var unarchive = new MenuFlyoutItem { Text = "Unarchive" };
            unarchive.Click += async (_, __) => await UnarchiveAsync(new[] { item });
            var deletePerm = new MenuFlyoutItem { Text = "Delete permanently" };
            deletePerm.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 58, 58));
            deletePerm.Click += async (_, __) => await DeletePermanentlyAsync(new[] { item });
            flyout.Items.Add(unarchive);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(deletePerm);
            flyout.ShowAt(container, new FlyoutShowOptions { Position = e.GetPosition(container) });
        }
        else
        {
            if (!_selectMode) SetSelectMode(true);
            if (RecentList.SelectedItems.Contains(item))
                RecentList.SelectedItems.Remove(item);
            else
                RecentList.SelectedItems.Add(item);
        }
        e.Handled = true;
    }

    private void SetSelectMode(bool on)
    {
        _selectMode = on;
        if (on)
        {
            RecentList.SelectionMode      = ListViewSelectionMode.Multiple;
            RecentList.IsItemClickEnabled = false;
            HeaderNormal.Visibility       = Visibility.Collapsed;
            HeaderSelect.Visibility       = Visibility.Visible;
            SelectionCountLabel.Text      = "0 selected";
            DeleteBtn.IsEnabled           = false;
            MoveToFolderBtn.IsEnabled     = false;
            RestoreBtn.IsEnabled          = false;
            ArchiveBtn.IsEnabled          = false;
            UnarchiveBtn.IsEnabled        = false;

            bool inTrash   = _activeSection == NavSection.Trash;
            bool inArchive = _activeSection == NavSection.Archive;
            RestoreBtn.Visibility      = inTrash   ? Visibility.Visible : Visibility.Collapsed;
            UnarchiveBtn.Visibility    = inArchive ? Visibility.Visible : Visibility.Collapsed;
            MoveToFolderBtn.Visibility = (inTrash || inArchive) ? Visibility.Collapsed : Visibility.Visible;
            ArchiveBtn.Visibility      = (inTrash || inArchive) ? Visibility.Collapsed : Visibility.Visible;
            DeleteBtnLabel.Text        = (inTrash || inArchive) ? "Delete permanently" : "Move to Trash";
        }
        else
        {
            // SelectedItems may only be mutated while SelectionMode is Multiple/Extended;
            // in None/Single it throws COMException (0x8000FFFF) — and even in Multiple the
            // WinUI 3 GridView projection can throw. Guard the precondition AND swallow
            // defensively. Switching SelectionMode to None below also resets the selection,
            // so that is the real backstop here.
            if (RecentList.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended)
            {
                try { RecentList.SelectedItems.Clear(); }
                catch (COMException) { /* WinUI selection quirk — mode change below clears anyway */ }
            }
            RecentList.SelectionMode      = ListViewSelectionMode.None;
            RecentList.IsItemClickEnabled = true;
            HeaderNormal.Visibility       = Visibility.Visible;
            HeaderSelect.Visibility       = Visibility.Collapsed;
        }
    }

    // ── Delete / Trash ────────────────────────────────────────────────────────

    private async Task DeleteOrTrashSelectedAsync()
    {
        var picked = RecentList.SelectedItems.OfType<RecentItem>().ToList();
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
        var picked = RecentList.SelectedItems.OfType<RecentItem>().ToList();
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
        var picked = RecentList.SelectedItems.OfType<RecentItem>().ToList();
        if (picked.Count == 0) return;

        var recent = App.Services.Settings.Current.RecentDocuments;
        var errors = new List<string>();
        foreach (var item in picked)
        {
            try
            {
                File.Move(item.Path, ArchiveDestFor(item.Path));
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
        var picked = RecentList.SelectedItems.OfType<RecentItem>().ToList();
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

    // ── Refresh / load notes ──────────────────────────────────────────────────

    private async void Refresh()
    {
        // ── Trash ─────────────────────────────────────────────────────────────
        if (_activeSection == NavSection.Trash)
        {
            var trashItems = new List<RecentItem>();
            if (Directory.Exists(TrashFolder))
            {
                var paths = Directory.GetFiles(TrashFolder, "*.pdf")
                    .Concat(Directory.GetFiles(TrashFolder, "*.yunote"))
                    .OrderByDescending(File.GetLastWriteTimeUtc);
                foreach (var p in paths)
                {
                    try
                    {
                        var info = App.Services.Documents.ReadInfo(p);
                        var item = new RecentItem
                        {
                            Title      = info.Title,
                            Subtitle   = $"{info.PageCount} page{(info.PageCount == 1 ? "" : "s")} · {info.ModifiedAt.ToLocalTime():g}",
                            Path       = info.FilePath,
                            FolderName = GetFolderLabel(info.FilePath)
                        };
                        var thumb = App.Services.Documents.ReadThumbnail(p);
                        if (thumb is { Length: > 0 })
                        {
                            try
                            {
                                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                                using var ms = new System.IO.MemoryStream(thumb);
                                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                                item.Thumbnail = bmp;
                            }
                            catch { }
                        }
                        trashItems.Add(item);
                    }
                    catch { }
                }
            }
            RecentList.ItemsSource   = trashItems;
            EmptyHintTitle.Text      = "Trash is empty";
            EmptyHintSubtitle.Text   = "Deleted notes will appear here.";
            EmptyHint.Visibility     = trashItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        // ── Archive ───────────────────────────────────────────────────────────
        if (_activeSection == NavSection.Archive)
        {
            var archiveItems = new List<RecentItem>();
            foreach (var p in GetAllNotePaths(ArchiveFolder))
            {
                try
                {
                    var info = App.Services.Documents.ReadInfo(p);
                    var label = info.Title;
                    if (!string.IsNullOrEmpty(info.SourcePdfPath)) label += "  ·  PDF";
                    var item = new RecentItem
                    {
                        Title      = label,
                        Subtitle   = $"{info.PageCount} page{(info.PageCount == 1 ? "" : "s")} · {info.ModifiedAt.ToLocalTime():g}",
                        Path       = info.FilePath,
                        // Show the original folder structure (relative to the archive root).
                        FolderName = GetFolderLabelRelativeTo(info.FilePath, ArchiveFolder)
                    };
                    var thumb = App.Services.Documents.ReadThumbnail(p);
                    if (thumb is { Length: > 0 })
                    {
                        try
                        {
                            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                            using var ms = new System.IO.MemoryStream(thumb);
                            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                            item.Thumbnail = bmp;
                        }
                        catch { }
                    }
                    archiveItems.Add(item);
                }
                catch { }
            }
            RecentList.ItemsSource   = archiveItems;
            EmptyHintTitle.Text      = "Archive is empty";
            EmptyHintSubtitle.Text   = "Archived notes and folders will appear here.";
            EmptyHint.Visibility     = archiveItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        // ── Folders — no folder chosen yet ────────────────────────────────────
        if (_activeSection == NavSection.Folders && _selectedFolderPath is null)
        {
            RecentList.ItemsSource   = new List<RecentItem>();
            EmptyHintTitle.Text      = "No folder selected";
            EmptyHintSubtitle.Text   = "Choose a folder from the sidebar, or create one.";
            EmptyHint.Visibility     = Visibility.Visible;
            return;
        }

        // ── Folders — specific folder chosen (direct contents only) ───────────
        if (_activeSection == NavSection.Folders && _selectedFolderPath is not null)
        {
            var folderItems = new List<RecentItem>();
            foreach (var p in GetDirectNotePaths(_selectedFolderPath))
            {
                try
                {
                    var info  = App.Services.Documents.ReadInfo(p);
                    var label = info.Title;
                    if (!string.IsNullOrEmpty(info.SourcePdfPath)) label += "  ·  PDF";
                    var item = new RecentItem
                    {
                        Title      = label,
                        Subtitle   = $"{info.PageCount} page{(info.PageCount == 1 ? "" : "s")} · {info.ModifiedAt.ToLocalTime():g}",
                        Path       = info.FilePath,
                        FolderName = GetFolderLabel(info.FilePath)
                    };
                    var thumb = App.Services.Documents.ReadThumbnail(info.FilePath);
                    if (thumb is { Length: > 0 })
                    {
                        try
                        {
                            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                            using var ms = new System.IO.MemoryStream(thumb);
                            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                            item.Thumbnail = bmp;
                        }
                        catch { }
                    }
                    folderItems.Add(item);
                }
                catch { }
            }
            folderItems.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            RecentList.ItemsSource   = folderItems;
            EmptyHintTitle.Text      = "This folder is empty";
            EmptyHintSubtitle.Text   = "Tap Create note to add one, or move notes here.";
            EmptyHint.Visibility     = folderItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        // ── All notes ─────────────────────────────────────────────────────────
        var folder = App.Services.Settings.Current.DocumentsFolder;
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var infos  = new List<DocumentInfo>();

        foreach (var d in App.Services.Documents.ListRecent(folder))
            if (seen.Add(d.FilePath)) infos.Add(d);

        var recent = App.Services.Settings.Current.RecentDocuments;
        var stale  = new List<string>();
        foreach (var p in recent.ToList())
        {
            if (seen.Contains(p)) continue;
            if (!File.Exists(p)) { stale.Add(p); continue; }
            try
            {
                var info = App.Services.Documents.ReadInfo(p);
                if (seen.Add(info.FilePath)) infos.Add(info);
            }
            catch { stale.Add(p); }
        }
        if (stale.Count > 0)
        {
            foreach (var s in stale) recent.Remove(s);
            App.Services.Settings.Save();
        }

        infos.Sort((a, b) => b.ModifiedAt.CompareTo(a.ModifiedAt));

        var items = new List<RecentItem>();
        foreach (var d in infos)
        {
            var label = d.Title;
            if (!string.IsNullOrEmpty(d.SourcePdfPath)) label += "  ·  PDF";
            var item = new RecentItem
            {
                Title      = label,
                Subtitle   = $"{d.PageCount} page{(d.PageCount == 1 ? "" : "s")} · {d.ModifiedAt.ToLocalTime():g}",
                Path       = d.FilePath,
                FolderName = GetFolderLabel(d.FilePath)
            };
            var thumb = App.Services.Documents.ReadThumbnail(d.FilePath);
            if (thumb is { Length: > 0 })
            {
                try
                {
                    var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using var ms = new System.IO.MemoryStream(thumb);
                    await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                    item.Thumbnail = bmp;
                }
                catch { }
            }
            items.Add(item);
        }
        RecentList.ItemsSource   = items;
        EmptyHintTitle.Text      = "No notes";
        EmptyHintSubtitle.Text   = "Tap Create note to add one.";
        EmptyHint.Visibility     = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Returns the folder path relative to DocumentsFolder (e.g. "Work / Project A"),
    // or an empty string if the file lives directly in the root.
    private static string GetFolderLabel(string filePath)
        => GetFolderLabelRelativeTo(filePath, App.Services.Settings.Current.DocumentsFolder);

    // Same as GetFolderLabel but relative to an arbitrary root (e.g. the archive root).
    private static string GetFolderLabelRelativeTo(string filePath, string root)
    {
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (dir is null || string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            return "";
        var prefix = root + System.IO.Path.DirectorySeparatorChar;
        if (!dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
        return dir[prefix.Length..].Replace(System.IO.Path.DirectorySeparatorChar.ToString(), " / ");
    }

    // Enumerates note files directly inside a folder (no recursion into subfolders).
    private static IEnumerable<string> GetDirectNotePaths(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var f in Directory.GetFiles(folder, "*.pdf").OrderByDescending(File.GetLastWriteTimeUtc))
            yield return f;
        foreach (var f in Directory.GetFiles(folder, "*.yunote").OrderByDescending(File.GetLastWriteTimeUtc))
            yield return f;
    }

    // Recursively enumerates all note files under a folder, excluding system folders.
    private static IEnumerable<string> GetAllNotePaths(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var f in Directory.GetFiles(folder, "*.pdf").OrderByDescending(File.GetLastWriteTimeUtc))
            yield return f;
        foreach (var f in Directory.GetFiles(folder, "*.yunote").OrderByDescending(File.GetLastWriteTimeUtc))
            yield return f;
        foreach (var sub in Directory.GetDirectories(folder).Where(d => !IsSystemFolder(d)))
            foreach (var f in GetAllNotePaths(sub))
                yield return f;
    }

    // ── Create / open notes ───────────────────────────────────────────────────

    private static void TouchRecent(string path)
    {
        var recent = App.Services.Settings.Current.RecentDocuments;
        recent.Remove(path);
        recent.Insert(0, path);
        if (recent.Count > 30) recent.RemoveRange(30, recent.Count - 30);
        App.Services.Settings.Save();
    }

    private async Task NewNoteAsync()
    {
        var box = new TextBox { Text = "Untitled", Width = 320 };

        // Template picker — selectable preview cards (same previews as the editor).
        var selectedTemplate = TemplateKind.Blank;
        var accent = (Brush)Application.Current.Resources["AppAccentBrush"];
        var border = (Brush)Application.Current.Resources["AppBorderBrush"];
        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        foreach (TemplateKind k in (TemplateKind[])Enum.GetValues(typeof(TemplateKind)))
        {
            var card = new Border
            {
                BorderThickness = new Thickness(k == selectedTemplate ? 2.5 : 1),
                BorderBrush = k == selectedTemplate ? accent : border,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6),
                Background = (Brush)Application.Current.Resources["AppSurfaceBrush"],
            };
            var stack = new StackPanel();
            stack.Children.Add(EditorPage.BuildTemplatePreview(k, 80, 104));
            stack.Children.Add(new TextBlock
            {
                Text = k.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0)
            });
            card.Child = stack;

            var btn = new Button
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Content = card,
                Tag = k
            };
            btn.Click += (s, _) =>
            {
                selectedTemplate = (TemplateKind)((Button)s).Tag;
                foreach (Button cand in previewRow.Children.OfType<Button>())
                {
                    var b = (Border)cand.Content;
                    bool match = (TemplateKind)cand.Tag == selectedTemplate;
                    b.BorderThickness = new Thickness(match ? 2.5 : 1);
                    b.BorderBrush = match ? accent : border;
                }
            };
            previewRow.Children.Add(btn);
        }

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(new TextBlock { Text = "Name", FontSize = 13 });
        root.Children.Add(box);
        root.Children.Add(new TextBlock { Text = "Template", FontSize = 13, Margin = new Thickness(0, 6, 0, 0) });
        root.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = previewRow
        });

        var dlg = new ContentDialog
        {
            Title = "New note", Content = root,
            PrimaryButtonText = "Create", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var title  = string.IsNullOrWhiteSpace(box.Text) ? "Untitled" : box.Text.Trim();
        var folder = (_activeSection == NavSection.Folders && _selectedFolderPath is not null)
            ? _selectedFolderPath
            : App.Services.Settings.Current.DocumentsFolder;
        Directory.CreateDirectory(folder);
        var path = UniquePath(folder, title, ".pdf");
        var doc  = App.Services.Documents.CreatePdf(path, title, App.Services.PdfContainer, selectedTemplate);
        TouchRecent(doc.Info.FilePath);
        MainWindow.Navigate<EditorPage>(doc);
    }

    private async Task OpenExistingAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await OpenPathAsync(file.Path);
    }

    private async Task OpenPathAsync(string path)
    {
        try
        {
            Document doc;
            if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                doc = App.Services.Documents.OpenPdfContainer(path, App.Services.PdfContainer, App.Services.PdfImport);
            else
                doc = App.Services.Documents.Open(path);
            TouchRecent(doc.Info.FilePath);
            MainWindow.Navigate<EditorPage>(doc);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Open failed", ex.Message);
        }
    }

    private async void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        // Can't open directly from Trash or Archive — restore/unarchive first.
        if (_activeSection is NavSection.Trash or NavSection.Archive) return;
        if (e.ClickedItem is RecentItem r) await OpenPathAsync(r.Path);
    }

    private static string UniquePath(string folder, string title, string ext)
    {
        var safe = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
        var p    = Path.Combine(folder, safe + ext);
        int i    = 1;
        while (File.Exists(p)) p = Path.Combine(folder, $"{safe} ({i++}){ext}");
        return p;
    }

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
            IsSelected = string.Equals(dirPath, _selectedFolderPath, StringComparison.OrdinalIgnoreCase)
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
                    ParentId = parentId
                };
                settings.Folders.Add(existing);
            }
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
        var flyout   = new MenuFlyout();
        var newSub   = new MenuFlyoutItem { Text = "New subfolder" };
        newSub.Click += async (_, __) => await CreateFolderAsync(item.Path);
        var rename   = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += async (_, __) => await RenameFolderAsync(item.Path);
        var archive  = new MenuFlyoutItem { Text = "Archive folder" };
        archive.Click += async (_, __) => await ArchiveFolderAsync(item.Path);
        var deleteFi = new MenuFlyoutItem { Text = "Delete folder" };
        deleteFi.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 58, 58));
        deleteFi.Click += async (_, __) => await DeleteFolderAsync(item.Path);
        flyout.Items.Add(newSub);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(rename);
        flyout.Items.Add(archive);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteFi);
        return flyout;
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
                ParentId = parentInfo?.Id
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
        var picked = RecentList.SelectedItems.OfType<RecentItem>().ToList();
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task ShowDialogAsync(string title, string message)
    {
        var d = new ContentDialog
        {
            Title           = title,
            Content         = message,
            CloseButtonText = "OK",
            XamlRoot        = this.XamlRoot
        };
        await d.ShowAsync();
    }
}
