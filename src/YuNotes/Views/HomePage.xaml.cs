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

// Data object for a folder node in the TreeView.
public sealed class FolderTreeItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsSelected { get; set; }
    public string Glyph => IsSelected ? "" : "";
    public Brush ColorBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.Gray);
    // Notes in this folder and its subfolders; updated in place after moves so
    // the sidebar count stays right without rebuilding the tree.
    private int _noteCount;
    public int NoteCount
    {
        get => _noteCount;
        set
        {
            if (_noteCount == value) return;
            _noteCount = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CountText)));
        }
    }
    public string CountText => _noteCount > 0 ? _noteCount.ToString() : "";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class HomePage : Page
{
    public sealed class RecentItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string Path { get; set; } = "";
        public string FolderName { get; set; } = "";
        public DateTime ModifiedAt { get; set; }
        public bool IsNewCard { get; set; }

        // Thumbnails load (and, for legacy docs, render) off-thread after the
        // list is shown, so this needs change notification to pop in late.
        private Microsoft.UI.Xaml.Media.ImageSource? _thumbnail;
        public Microsoft.UI.Xaml.Media.ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public Microsoft.UI.Xaml.Visibility NormalCardVisibility =>
            IsNewCard ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        public Microsoft.UI.Xaml.Visibility NewCardVisibility =>
            IsNewCard ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        public Microsoft.UI.Xaml.Visibility FolderLabelVisibility =>
            string.IsNullOrEmpty(FolderName) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

        // ── Card presentation ────────────────────────────────────────────────
        public int Pages { get; set; }
        public string PageCount => Pages.ToString();
        public Microsoft.UI.Xaml.Visibility PageBadgeVisibility =>
            Pages > 1 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        public bool IsPdf { get; set; }
        public Microsoft.UI.Xaml.Visibility PdfIconVisibility =>
            IsPdf ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        // " · 17 Nov" when another visible note has the same title, so two
        // "Untitled" notes can be told apart; empty otherwise.
        public string TitleHint { get; set; } = "";
        public string? FolderColor { get; set; }

        // Pinned notes stay first and show a filled pin; others show the pin
        // only while hovered (see Card_PointerEntered).
        private bool _isPinned;
        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                _isPinned = value;
                foreach (var n in new[] { nameof(IsPinned), nameof(PinOpacity), nameof(PinBackground), nameof(PinForeground), nameof(PinTooltip) })
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
            }
        }
        public double PinOpacity => IsPinned ? 1 : 0;
        public Brush PinBackground => IsPinned
            ? (Brush)Application.Current.Resources["AppAccentBrush"]
            : (Brush)Application.Current.Resources["AppSurfaceVariantBrush"];
        public Brush PinForeground => IsPinned
            ? new SolidColorBrush(Microsoft.UI.Colors.White)
            : (Brush)Application.Current.Resources["AppTextPrimaryBrush"];
        public string PinTooltip => IsPinned ? "Unpin" : "Pin to the top";
        public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
        public string TagsText => string.Join("  ", Tags.Select(t => "#" + t));
        public Microsoft.UI.Xaml.Visibility TagsVisibility =>
            Tags.Count > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        public string FolderDisplay => string.IsNullOrEmpty(FolderName) ? "No folder" : FolderName;
        public string FolderGlyph => string.IsNullOrEmpty(FolderName) ? "" : "";
        public Brush FolderChipBrush => string.IsNullOrEmpty(FolderName)
            ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            : new SolidColorBrush(FolderColors.Parse(FolderColor, 0x38));
        public Brush FolderTextBrush => string.IsNullOrEmpty(FolderName)
            ? (Brush)Application.Current.Resources["AppTextSecondaryBrush"]
            : new SolidColorBrush(FolderColors.Parse(FolderColor));
    }

    private enum NavSection { AllNotes, Trash, Archive, Folders }

    private enum SortOrder { Date, Name, Folder }
    private SortOrder _sortOrder = SortOrder.Date;
    // Date defaults to newest first; Name and Folder to A→Z.
    private bool _sortDescending = true;

    // Last list built by Refresh (sorted, unfiltered); the search box filters it.
    private List<RecentItem> _allItems = new();

    private NavSection _activeSection = NavSection.AllNotes;
    private bool _selectMode;
    private bool _sidebarOpen = true;
    private string? _selectedFolderPath;
    // Tag filter (All notes narrowed to one tag, across every folder); null = off.
    private string? _activeTag;

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
        SettingsBtn.Click += (_, __) => MainWindow.Navigate<SettingsPage>(this, 
            null, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

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
                ? RecentList.SelectedItems.OfType<RecentItem>().Count(r => !r.IsNewCard) : 0;
            SelectionCountLabel.Text = $"{n} selected";
            DeleteBtn.IsEnabled    = n > 0;
            MoveToFolderBtn.IsEnabled = n > 0;
            TagsBtn.IsEnabled      = n > 0;
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
        TagsBtn.Click         += async (_, __) => await EditTagsForSelectedAsync();
        InitSelectionExtras();
        InitSmartFolders();

        // Sort bar
        SortByDateBtn.Click   += (_, __) => SetSort(SortOrder.Date);
        SortByNameBtn.Click   += (_, __) => SetSort(SortOrder.Name);
        SortByFolderBtn.Click += (_, __) => SetSort(SortOrder.Folder);

        // Search: filter as you type; Ctrl+F focuses the box, Escape clears it.
        SearchBox.TextChanged += (_, __) => ApplyFilter();
        SizeChanged += (_, e) => OnPageWidthChanged(e.NewSize.Width);
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && SearchBox.Text.Length > 0)
            {
                SearchBox.Text = "";
                e.Handled = true;
            }
        };
        var findKey = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        findKey.Invoked += (_, e) =>
        {
            ExpandSearch();
            SearchBox.Focus(FocusState.Keyboard);
            SearchBox.SelectAll();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(findKey);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // Update notice: shown when the background updater has staged a new version.
        // (Re)subscribed on every Loaded since a cached page unloads and reloads.
        Unloaded += (_, __) =>
        {
            Services.UpdateService.StatusChanged -= ShowUpdateNotice;
            App.Services.Search.Updated -= OnIndexUpdated;
        };

        Loaded += (_, __) =>
        {
            Services.UpdateService.StatusChanged -= ShowUpdateNotice;
            Services.UpdateService.StatusChanged += ShowUpdateNotice;
            ShowUpdateNotice();
            MainWindow.SetDragRegion(HeaderDragRegion);
            HeaderBar.Padding = new Thickness(4, 0, 12 + MainWindow.CaptionButtonInset(this), 0);
            ApplyNavSelection();
            ApplySortSelection();
            LoadFolderTree();
            Refresh();
            App.Services.Search.Updated -= OnIndexUpdated;
            App.Services.Search.Updated += OnIndexUpdated;
            StartIndexing();
        };
    }

    // ── Sidebar toggle ────────────────────────────────────────────────────────

    // In a narrow window the sidebar hides on its own so the notes keep their
    // room; the toggle still opens it there (and that choice is forgotten once
    // the window is wide again, where the user's own setting applies).
    private const double SidebarAutoHideWidth = 760;
    private bool _narrow;
    private bool _narrowSidebarOpen;

    private void ToggleSidebar()
    {
        if (_narrow) _narrowSidebarOpen = !_narrowSidebarOpen;
        else _sidebarOpen = !_sidebarOpen;
        ApplySidebarVisibility();
    }

    private void OnPageWidthChanged(double width)
    {
        bool narrow = width > 0 && width < SidebarAutoHideWidth;
        if (narrow == _narrow) return;
        _narrow = narrow;
        _narrowSidebarOpen = false;
        ApplySidebarVisibility();
    }

    private void ApplySidebarVisibility()
    {
        bool open = _narrow ? _narrowSidebarOpen : _sidebarOpen;
        SidebarBorder.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Sidebar navigation ────────────────────────────────────────────────────

    private void SelectNav(NavSection section)
    {
        // A sidebar opened over a narrow window has done its job.
        if (_narrow && _narrowSidebarOpen) { _narrowSidebarOpen = false; ApplySidebarVisibility(); }
        _activeSection = section;
        _selectedFolderPath = null;
        _activeTag = null;
        _activeSmartId = null;
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
        _activeTag = null;
        _activeSmartId = null;
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

        bool allNotes = _activeSection == NavSection.AllNotes && _activeTag is null && _activeSmartId is null;
        AllNotesNavBtn.Background = allNotes ? accent : none;
        TrashNavBtn.Background    = _activeSection == NavSection.Trash    ? accent : none;
        ArchiveNavBtn.Background  = _activeSection == NavSection.Archive  ? accent : none;

        AllNotesIcon.Foreground  = allNotes
            ? (Brush)Application.Current.Resources["AppTextPrimaryBrush"]
            : (Brush)Application.Current.Resources["AppTextSecondaryBrush"];
        AllNotesLabel.FontWeight = allNotes
            ? new Windows.UI.Text.FontWeight { Weight = 600 }
            : new Windows.UI.Text.FontWeight { Weight = 400 };
    }

    // Clicking the active sort option flips its direction; picking another
    // option starts it in its natural direction (newest first / A→Z).
    private void SetSort(SortOrder order)
    {
        if (_sortOrder == order) _sortDescending = !_sortDescending;
        else
        {
            _sortOrder = order;
            _sortDescending = order == SortOrder.Date;
        }
        ApplySortSelection();
        Refresh();
    }

    private void ApplySortSelection()
    {
        var active   = (Brush)Application.Current.Resources["AppSurfaceBrush"];
        var inactive = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        SortByDateBtn.Background   = _sortOrder == SortOrder.Date   ? active : inactive;
        SortByNameBtn.Background   = _sortOrder == SortOrder.Name   ? active : inactive;
        SortByFolderBtn.Background = _sortOrder == SortOrder.Folder ? active : inactive;

        // Arrow only on the active option: down = newest first / Z→A.
        var arrow = _sortDescending ? "" : "";
        SortDateArrow.Glyph   = _sortOrder == SortOrder.Date   ? arrow : "";
        SortNameArrow.Glyph   = _sortOrder == SortOrder.Name   ? arrow : "";
        SortFolderArrow.Glyph = _sortOrder == SortOrder.Folder ? arrow : "";
        string dir = _sortOrder == SortOrder.Date
            ? (_sortDescending ? "newest first" : "oldest first")
            : (_sortDescending ? "Z to A" : "A to Z");
        ToolTipService.SetToolTip(_sortOrder switch
        {
            SortOrder.Name   => SortByNameBtn,
            SortOrder.Folder => SortByFolderBtn,
            _                => SortByDateBtn
        }, $"Sorted {dir} (click to reverse)");
    }

    private void ApplySort(List<RecentItem> items)
    {
        Comparison<RecentItem> byDate = (a, b) => b.ModifiedAt.CompareTo(a.ModifiedAt);
        Comparison<RecentItem> byName = (a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        int sign = _sortOrder == SortOrder.Date
            ? (_sortDescending ? 1 : -1)
            : (_sortDescending ? -1 : 1);
        switch (_sortOrder)
        {
            case SortOrder.Name:
                items.Sort((a, b) => sign * byName(a, b));
                break;
            case SortOrder.Folder:
                // Grouped by folder; notes without a folder always go last.
                // Inside a folder, newest first.
                items.Sort((a, b) =>
                {
                    bool na = string.IsNullOrEmpty(a.FolderName), nb = string.IsNullOrEmpty(b.FolderName);
                    if (na != nb) return na ? 1 : -1;
                    int c = sign * string.Compare(a.FolderName, b.FolderName, StringComparison.CurrentCultureIgnoreCase);
                    return c != 0 ? c : byDate(a, b);
                });
                break;
            default:
                items.Sort((a, b) => sign * byDate(a, b));
                break;
        }
        // Pinned notes first, keeping the chosen order within each group.
        if (items.Any(i => i.IsPinned))
        {
            var ordered = items.Where(i => i.IsPinned).Concat(items.Where(i => !i.IsPinned)).ToList();
            items.Clear();
            items.AddRange(ordered);
        }
    }

    // Shows _allItems filtered by the search text (title or folder name). The
    // "New note" card stays first and visible while not searching.
    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        bool hasNewCard = (_activeSection == NavSection.AllNotes && _activeTag is null && _activeSmartId is null) ||
                          (_activeSection == NavSection.Folders && _selectedFolderPath is not null);
        List<RecentItem> shown;
        if (q.Length == 0)
        {
            shown = new List<RecentItem>(_allItems);
            if (hasNewCard) shown.Insert(0, new RecentItem { IsNewCard = true });
        }
        else
        {
            // "#exa" matches tags starting with "exa"; plain text also matches tags.
            var tagQuery = q.StartsWith('#') ? Services.NoteTags.Normalize(q) : null;
            shown = _allItems.Where(i => tagQuery is not null
                ? i.Tags.Any(t => t.StartsWith(tagQuery, StringComparison.OrdinalIgnoreCase))
                : i.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                  i.FolderName.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                  i.Tags.Any(t => t.Contains(q, StringComparison.CurrentCultureIgnoreCase))).ToList();
        }
        RecentList.ItemsSource = shown;

        if (q.Length > 0)
        {
            EmptyHintTitle.Text    = "No matching notes";
            EmptyHintSubtitle.Text = $"Nothing matches \"{q}\".";
            EmptyHint.Visibility   = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateContentResults();
        }
        else
        {
            EmptyHintTitle.Text    = _emptyTitle;
            EmptyHintSubtitle.Text = _emptySubtitle;
            EmptyHint.Visibility   = _allItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateContentResults();
        }
        UpdateContinueRow();
    }

    private string _emptyTitle = "", _emptySubtitle = "";

    // Hands a freshly built list to the grid: sorts, marks duplicate titles,
    // remembers the empty-state text, then applies the search filter.
    private void ShowItems(List<RecentItem> items, string emptyTitle, string emptySubtitle)
    {
        ApplySort(items);
        foreach (var group in items.GroupBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            bool dup = group.Count() > 1;
            foreach (var i in group)
                i.TitleHint = dup ? $" · {i.ModifiedAt.ToLocalTime():d MMM}" : "";
        }
        _allItems      = items;
        _emptyTitle    = emptyTitle;
        _emptySubtitle = emptySubtitle;
        ApplyFilter();
    }

    // Card item for a document. folderRoot is what folder names are relative
    // to (the documents root, or the archive root for archived notes).
    private RecentItem MakeItem(DocumentInfo info, string folderRoot)
    {
        var dir = Path.GetDirectoryName(info.FilePath);
        var folder = App.Services.Settings.Current.Folders.FirstOrDefault(f =>
            string.Equals(f.Path, dir, StringComparison.OrdinalIgnoreCase));
        var item = new RecentItem
        {
            Title       = info.Title,
            Subtitle    = FriendlyDate(info.ModifiedAt.ToLocalTime()),
            Path        = info.FilePath,
            FolderName  = GetFolderLabelRelativeTo(info.FilePath, folderRoot),
            FolderColor = folder?.Color,
            ModifiedAt  = info.ModifiedAt,
            Pages       = info.PageCount,
            IsPdf       = !string.IsNullOrEmpty(info.SourcePdfPath),
            IsPinned    = IsPinnedPath(info.FilePath),
            Tags        = Services.NoteTags.Get(info.FilePath),
        };
        _ = LoadThumbnailAsync(item, info.FilePath);
        return item;
    }

    // "Today 17:18", "Yesterday 17:18", "4 days ago", "Last week", "3 Nov", "3 Nov 2025".
    private static string FriendlyDate(DateTime local)
    {
        var today = DateTime.Today;
        var days  = (today - local.Date).Days;
        if (days <= 0)  return $"Today {local:HH:mm}";
        if (days == 1)  return $"Yesterday {local:HH:mm}";
        if (days < 7)   return $"{days} days ago";
        if (days < 14)  return "Last week";
        return local.Year == today.Year ? local.ToString("d MMM") : local.ToString("d MMM yyyy");
    }

    // ── Select mode ───────────────────────────────────────────────────────────

    private void OnRecentRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null && d is not GridViewItem) d = VisualTreeHelper.GetParent(d);
        if (d is not GridViewItem container) return;
        if (container.Content is not RecentItem item) return;
        if (item.IsNewCard) return;

        if (_activeSection == NavSection.Trash)
        {
            // Trash items get a dedicated context menu. Constrained so the
            // in-app acrylic menu background can blur the app content beneath.
            var flyout   = new MenuFlyout { ShouldConstrainToRootBounds = true };
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
            var flyout    = new MenuFlyout { ShouldConstrainToRootBounds = true };
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
            TagsBtn.IsEnabled             = false;
            RestoreBtn.IsEnabled          = false;
            ArchiveBtn.IsEnabled          = false;
            UnarchiveBtn.IsEnabled        = false;

            bool inTrash   = _activeSection == NavSection.Trash;
            bool inArchive = _activeSection == NavSection.Archive;
            RestoreBtn.Visibility      = inTrash   ? Visibility.Visible : Visibility.Collapsed;
            UnarchiveBtn.Visibility    = inArchive ? Visibility.Visible : Visibility.Collapsed;
            MoveToFolderBtn.Visibility = (inTrash || inArchive) ? Visibility.Collapsed : Visibility.Visible;
            TagsBtn.Visibility         = (inTrash || inArchive) ? Visibility.Collapsed : Visibility.Visible;
            ArchiveBtn.Visibility      = (inTrash || inArchive) ? Visibility.Collapsed : Visibility.Visible;
            DeleteBtnLabel.Text        = (inTrash || inArchive) ? "Delete permanently" : "Move to Trash";
            var live = (inTrash || inArchive) ? Visibility.Collapsed : Visibility.Visible;
            SelectMoreSep.Visibility = PinSelectedItem.Visibility = UnpinSelectedItem.Visibility = live;
            _selectAnchor = -1;
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
        FitHeader();
    }

    // ── Refresh / load notes ──────────────────────────────────────────────────

    // Loads the saved thumbnail off the UI thread; when a document has none
    // (plain PDFs never annotated, .yunote files never saved since thumbnails
    // were added — most trash items), renders a first-page fallback. Called
    // fire-and-forget: cards appear immediately, previews pop in via binding.
    private static async Task LoadThumbnailAsync(RecentItem item, string path)
    {
        byte[]? thumb = null;
        try
        {
            thumb = await Task.Run(() =>
                App.Services.Documents.ReadThumbnail(path)
                ?? App.Services.Documents.RenderFallbackThumbnail(path, App.Services.Templates));
        }
        catch { }
        if (thumb is not { Length: > 0 }) return;
        try
        {
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var ms = new System.IO.MemoryStream(thumb);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            item.Thumbnail = bmp;
        }
        catch { }
    }

    private void Refresh()
    {
        UpdateFolderCounts();
        RebuildTagList();
        RebuildSmartFolderList();

        // ── Trash ─────────────────────────────────────────────────────────────
        if (_activeSection == NavSection.Trash)
        {
            var trashItems = new List<RecentItem>();
            if (Directory.Exists(TrashFolder))
            {
                var paths = Directory.GetFiles(TrashFolder, "*.pdf")
                    .Concat(Directory.GetFiles(TrashFolder, "*.yunote"));
                foreach (var p in paths)
                {
                    try { trashItems.Add(MakeItem(App.Services.Documents.ReadInfo(p), App.Services.Settings.Current.DocumentsFolder)); }
                    catch { }
                }
            }
            ShowItems(trashItems, "Trash is empty", "Deleted notes will appear here.");
            return;
        }

        // ── Archive ───────────────────────────────────────────────────────────
        if (_activeSection == NavSection.Archive)
        {
            var archiveItems = new List<RecentItem>();
            foreach (var p in GetAllNotePaths(ArchiveFolder))
            {
                try { archiveItems.Add(MakeItem(App.Services.Documents.ReadInfo(p), ArchiveFolder)); }
                catch { }
            }
            ShowItems(archiveItems, "Archive is empty", "Archived notes and folders will appear here.");
            return;
        }

        // ── Folders — no folder chosen yet ────────────────────────────────────
        if (_activeSection == NavSection.Folders && _selectedFolderPath is null)
        {
            ShowItems(new List<RecentItem>(), "No folder selected", "Choose a folder from the sidebar, or create one.");
            return;
        }

        // ── Folders — specific folder chosen (direct contents only) ───────────
        if (_activeSection == NavSection.Folders && _selectedFolderPath is not null)
        {
            var folderItems = new List<RecentItem>();
            foreach (var p in GetDirectNotePaths(_selectedFolderPath))
            {
                try { folderItems.Add(MakeItem(App.Services.Documents.ReadInfo(p), App.Services.Settings.Current.DocumentsFolder)); }
                catch { }
            }
            ShowItems(folderItems, "This folder is empty", "Tap New note to add one, or move notes here.");
            return;
        }

        // ── All notes ─────────────────────────────────────────────────────────
        var items = BuildAllNoteItems();
        _allLiveItems = items;
        RebuildSmartFolderList();
        if (ActiveSmartFolder is { } smart)
        {
            items = items.Where(i => MatchesSmartFolder(smart, i)).ToList();
            ShowItems(items, $"Nothing in \u201c{smart.Name}\u201d yet", smart.Describe());
            return;
        }
        if (_activeTag is { } tag)
        {
            items = items.Where(i => i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
            ShowItems(items, $"No notes tagged #{tag}", "Select notes and choose Tags to add this tag.");
            return;
        }
        ShowItems(items, "No notes", "Tap New note to add one.");
    }

    // Every live note: everything under the notes folder (subfolders included,
    // Trash and Archive left out) plus recently opened notes that live elsewhere.
    private List<RecentItem> BuildAllNoteItems()
    {
        var folder = App.Services.Settings.Current.DocumentsFolder;
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var infos  = new List<DocumentInfo>();

        // Every note under the notes folder, subfolders included (Trash and
        // Archive are system folders and stay out). Recently opened notes that
        // live elsewhere are added below.
        foreach (var p in GetAllNotePaths(folder))
        {
            if (!seen.Add(p)) continue;
            try { infos.Add(App.Services.Documents.ReadInfo(p)); }
            catch { }
        }

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

        return infos.Select(d => MakeItem(d, folder)).ToList();
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
        var folder = (_activeSection == NavSection.Folders && _selectedFolderPath is not null)
            ? _selectedFolderPath
            : App.Services.Settings.Current.DocumentsFolder;
        var folderInfo = App.Services.Settings.Current.Folders.FirstOrDefault(f =>
            string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase));

        // Starts on the folder's default template, if it has one.
        var selectedTemplate = TemplateKind.Blank;
        var defaultCustom = Services.TemplateLibrary.FromKey(folderInfo?.DefaultTemplate);
        if (defaultCustom is null && Enum.TryParse<TemplateKind>(folderInfo?.DefaultTemplate, out var defKind))
            selectedTemplate = defKind;
        bool customSelected = defaultCustom is not null;

        // Template picker — selectable preview cards (same previews as the editor).
        var accent = (Brush)Application.Current.Resources["AppAccentBrush"];
        var border = (Brush)Application.Current.Resources["AppBorderBrush"];
        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var sizePicker = new TemplateSizePicker(selectedTemplate,
            folderInfo?.DefaultTemplateSpacing ?? TemplateSizePicker.DefaultSpacing);
        ContentDialog? dlg = null;
        var customRow = new CustomTemplateRow(this, () => dlg, defaultCustom?.Id);

        void HighlightBuiltIn()
        {
            foreach (Button cand in previewRow.Children.OfType<Button>())
            {
                var b = (Border)cand.Content;
                bool match = !customSelected && (TemplateKind)cand.Tag == selectedTemplate;
                b.BorderThickness = new Thickness(match ? 2.5 : 1);
                b.BorderBrush = match ? accent : border;
            }
            sizePicker.View.Visibility = customSelected ? Visibility.Collapsed : Visibility.Visible;
        }
        customRow.Selected += t =>
        {
            customSelected = t is not null;
            HighlightBuiltIn();
        };

        foreach (TemplateKind k in (TemplateKind[])Enum.GetValues(typeof(TemplateKind)))
        {
            bool isSel = !customSelected && k == selectedTemplate;
            var card = new Border
            {
                BorderThickness = new Thickness(isSel ? 2.5 : 1),
                BorderBrush = isSel ? accent : border,
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"{k} template");
            btn.Click += (s, _) =>
            {
                selectedTemplate = (TemplateKind)((Button)s).Tag;
                customSelected = false;
                customRow.ClearSelection();
                sizePicker.SetKind(selectedTemplate);
                HighlightBuiltIn();
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
        root.Children.Add(customRow.View);
        root.Children.Add(sizePicker.View);
        // Only inside a folder: remember this choice for the folder's next notes.
        CheckBox? makeDefault = null;
        if (folderInfo is not null)
        {
            makeDefault = new CheckBox
            {
                Content = $"Use this template for new notes in “{folderInfo.Name}”",
                IsChecked = folderInfo.DefaultTemplate is not null,
            };
            root.Children.Add(makeDefault);
        }
        HighlightBuiltIn();

        dlg = new ContentDialog
        {
            Title = "New note", Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Create", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        if (await customRow.ShowHostAsync(dlg) != ContentDialogResult.Primary) return;
        var title  = string.IsNullOrWhiteSpace(box.Text) ? "Untitled" : box.Text.Trim();
        var custom = customSelected ? customRow.Current : null;

        if (folderInfo is not null && makeDefault is not null)
        {
            if (makeDefault.IsChecked == true)
            {
                folderInfo.DefaultTemplate = custom is not null
                    ? Services.TemplateLibrary.KeyFor(custom)
                    : selectedTemplate.ToString();
                folderInfo.DefaultTemplateSpacing = custom is null ? sizePicker.Spacing : null;
            }
            else
            {
                folderInfo.DefaultTemplate = null;
                folderInfo.DefaultTemplateSpacing = null;
            }
            App.Services.Settings.Save();
        }

        Directory.CreateDirectory(folder);
        var path = UniquePath(folder, title, ".pdf");
        var doc  = App.Services.Documents.CreatePdf(path, title, App.Services.PdfContainer,
            custom is null ? selectedTemplate : TemplateKind.Blank, sizePicker.Spacing,
            custom is null ? null : page => Services.TemplateLibrary.ApplyTo(page, custom));
        if (custom is not null)
        {
            // The PDF pages other apps see get the paper on the first full save.
            doc.IsDirty = true;
            doc.MarkFlattenDirty(null);
        }
        TouchRecent(doc.Info.FilePath);
        MainWindow.Navigate<EditorPage>(this, doc, new DrillInNavigationTransitionInfo());
    }

    private async Task OpenExistingAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Of(this)));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        TouchRecent(file.Path);
        OpenPath(file.Path);
    }

    // Navigates immediately; EditorPage opens the document on a background
    // thread so the drill-in transition plays over the editor shell.
    private void OpenPath(string path)
    {
        MainWindow.Navigate<EditorPage>(this, path, new SuppressNavigationTransitionInfo());
    }

    private async void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_activeSection is NavSection.Trash or NavSection.Archive) return;
        if (e.ClickedItem is RecentItem r)
        {
            if (r.IsNewCard) { await NewNoteAsync(); return; }
            TouchRecent(r.Path);
            // Ctrl+click opens the note in a new editor tab.
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                MainWindow.Navigate<EditorPage>(this, new EditorTabRequest(r.Path), new SuppressNavigationTransitionInfo());
            else
                OpenPath(r.Path);
        }
    }

    // ── Card hover lift ───────────────────────────────────────────────────────

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        el.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        el.TranslationTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        el.CenterPoint = new System.Numerics.Vector3((float)el.ActualWidth / 2f, (float)el.ActualHeight / 2f, 0);
        el.Scale = new System.Numerics.Vector3(1.03f, 1.03f, 1f);
        el.Translation = new System.Numerics.Vector3(0, 0, 16);
        if (el.FindName("PinBtn") is Button pin) pin.Opacity = 1;
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        el.Scale = System.Numerics.Vector3.One;
        el.Translation = System.Numerics.Vector3.Zero;
        if (el.FindName("PinBtn") is Button pin && el.DataContext is RecentItem item) pin.Opacity = item.PinOpacity;
    }

    // ── Pinned notes ──────────────────────────────────────────────────────────

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        var pins = App.Services.Settings.Current.PinnedNotes;
        bool pin = !pins.Contains(path, StringComparer.OrdinalIgnoreCase);
        if (pin) pins.Add(path); else pins.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        App.Services.Settings.Save();
        Refresh();
    }

    private static bool IsPinnedPath(string path) =>
        App.Services.Settings.Current.PinnedNotes.Contains(path, StringComparer.OrdinalIgnoreCase);

    // ── Continue where you left off ───────────────────────────────────────────
    // Up to three recently opened notes with the page you were on; opening one
    // returns to that page (the editor restores it).
    private void UpdateContinueRow()
    {
        ContinueList.Children.Clear();
        bool show = _activeSection == NavSection.AllNotes && SearchBox.Text.Trim().Length == 0;
        var s = App.Services.Settings.Current;
        var res = Application.Current.Resources;
        foreach (var path in s.RecentDocuments)
        {
            if (ContinueList.Children.Count >= 3 || !show) break;
            var item = _allItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (item is null) continue;
            int page = s.LastPageByDocument.TryGetValue(path, out var p) ? p + 1 : 1;

            var thumb = new Image { Width = 34, Height = 46, Stretch = Stretch.Uniform };
            thumb.SetBinding(Image.SourceProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath("Thumbnail"), Source = item });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            text.Children.Add(new TextBlock { Text = item.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 });
            text.Children.Add(new TextBlock
            {
                Text = $"Page {page}" + (item.Pages > 1 ? $" of {item.Pages}" : "") + $" · {item.Subtitle}",
                FontSize = 11, Foreground = (Brush)res["AppTextSecondaryBrush"], TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170,
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Border
            {
                Child = thumb, Background = new SolidColorBrush(Microsoft.UI.Colors.White), CornerRadius = new CornerRadius(3),
                BorderBrush = (Brush)res["AppBorderBrush"], BorderThickness = new Thickness(1),
            });
            row.Children.Add(text);

            var btn = new Button
            {
                Content = row, Padding = new Thickness(8, 6, 14, 6), CornerRadius = new CornerRadius(10),
                Background = (Brush)res["AppSurfaceBrush"], BorderBrush = (Brush)res["AppBorderBrush"], BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(btn, $"Continue {item.Title} on page {page}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"Continue {item.Title} on page {page}");
            string target = item.Path;
            btn.Click += (_, __) => { TouchRecent(target); OpenPath(target); };
            ContinueList.Children.Add(btn);
        }
        ContinuePanel.Visibility = ContinueList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string UniquePath(string folder, string title, string ext)
    {
        var safe = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
        var p    = Path.Combine(folder, safe + ext);
        int i    = 1;
        while (File.Exists(p)) p = Path.Combine(folder, $"{safe} ({i++}){ext}");
        return p;
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
