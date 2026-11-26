using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;

namespace YuNotes.Views;

// Select mode extras: Select all / none, Shift-click to select a range,
// pin or unpin several notes at once, export copies of them to a folder,
// and the Ctrl+A / Esc shortcuts.
public sealed partial class HomePage
{
    // Index (in the shown list) of the card last ticked or unticked; a
    // Shift-click selects everything between it and the clicked card.
    private int _selectAnchor = -1;
    private bool _rangeSelecting;

    // ── Header fit ───────────────────────────────────────────────────────────
    // As the window narrows, the header steps down one level at a time until it
    // fits, so nothing is pushed under the caption buttons:
    //   normal: 0 full · 1 labels off (New note keeps its own) · 2 New note
    //           icon only · 3 Import and Select fold into a ⋯ menu ·
    //           4 the search box folds into a search button
    //   select: 0 full · 1 labels and count off · 2 Cancel becomes ✕ ·
    //           3 the search box folds into a search button · 4 the sidebar
    //           toggle hides (it's back once select mode ends)
    // Labels come back when there is room again.
    private const double SearchMinWidth = 150 + 16;   // search column MinWidth + its margins
    private const double SearchIconWidth = 48;        // collapsed search button + margin
    private bool _searchCollapsed;
    private bool _searchExpanded;   // collapsed search opened across the bar

    private void FitHeader()
    {
        if (HeaderBar.ActualWidth <= 0 || _searchExpanded) return;
        bool select = HeaderSelect.Visibility == Visibility.Visible;
        var bar = select ? HeaderSelect : HeaderNormal;
        int maxLevel = 4;
        double barAvail = HeaderBar.ActualWidth - HeaderBar.Padding.Left - HeaderBar.Padding.Right;
        var inf = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        for (int level = 0; level <= maxLevel; level++)
        {
            ApplyHeaderLevel(select, level);
            bar.Measure(inf);
            double search = _searchCollapsed ? SearchIconWidth : SearchMinWidth;
            double toggle = SidebarToggleBtn.Visibility == Visibility.Visible ? SidebarToggleBtn.Width + 6 : 0;
            if (bar.DesiredSize.Width <= barAvail - toggle - search) break;
        }
    }

    private void ApplyHeaderLevel(bool select, int level)
    {
        var bar = select ? HeaderSelect : HeaderNormal;
        bool labelsOff = level >= 1;
        if (select)
        {
            SelectionCountLabel.Visibility = labelsOff ? Visibility.Collapsed : Visibility.Visible;
            CancelSelectText.Visibility = level >= 2 ? Visibility.Collapsed : Visibility.Visible;
            CancelSelectIcon.Visibility = level >= 2 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var btn in bar.Children.OfType<Button>())
        {
            if (btn.Content is not StackPanel sp) continue;
            var label = sp.Children.OfType<TextBlock>().FirstOrDefault();
            if (label is null) continue;
            // The primary action (New note) keeps its label one level longer.
            bool off = btn == NewBtn ? level >= 2 : labelsOff;
            label.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
            if (off && ToolTipService.GetToolTip(btn) is null)
                ToolTipService.SetToolTip(btn, label.Text);
        }
        if (!select)
        {
            bool fold = level >= 3;
            OpenBtn.Visibility = SelectBtn.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
            HeaderMoreBtn.Visibility = fold ? Visibility.Visible : Visibility.Collapsed;
        }
        SetSearchCollapsed(level >= (select ? 3 : 4));
        SidebarToggleBtn.Visibility = select && level >= 4 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetSearchCollapsed(bool collapsed)
    {
        _searchCollapsed = collapsed;
        SearchColumn.MinWidth = collapsed ? SearchIconWidth : 150;
        SearchBox.Visibility = SearchBoxIcon.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SearchIconBtn.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        // A filter is still applied while the box is folded away: show it.
        SearchIconBtn.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            SearchBox.Text.Trim().Length > 0 ? "AppAccentBrush" : "AppTextPrimaryBrush"];
    }

    // Collapsed search: hide the actions so the box gets their room (it stays
    // in its own column, clear of the window drag region) until it loses focus.
    private void ExpandSearch()
    {
        if (!_searchCollapsed) { SearchBox.Focus(FocusState.Keyboard); return; }
        _searchExpanded = true;
        HeaderNormal.Visibility = HeaderSelect.Visibility = Visibility.Collapsed;
        SearchIconBtn.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = SearchBoxIcon.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void CollapseExpandedSearch()
    {
        if (!_searchExpanded) return;
        _searchExpanded = false;
        HeaderNormal.Visibility = _selectMode ? Visibility.Collapsed : Visibility.Visible;
        HeaderSelect.Visibility = _selectMode ? Visibility.Visible : Visibility.Collapsed;
        FitHeader();
    }

    private void InitSelectionExtras()
    {
        HeaderBar.SizeChanged += (_, __) => FitHeader();
        SearchIconBtn.Click += (_, __) => ExpandSearch();
        SearchBox.LostFocus += (_, __) => CollapseExpandedSearch();
        MoreImportItem.Click += async (_, __) => await OpenExistingAsync();
        MoreSelectItem.Click += (_, __) => SetSelectMode(true);
        SelectAllItem.Click      += (_, __) => SelectAllNotes();
        SelectNoneItem.Click     += (_, __) => SelectNoNotes();
        PinSelectedItem.Click    += (_, __) => PinSelected(true);
        UnpinSelectedItem.Click  += (_, __) => PinSelected(false);
        ExportSelectedItem.Click += async (_, __) => await ExportSelectedAsync();
        RecentList.SelectionChanged += OnSelectionRange;

        var selectAll = new KeyboardAccelerator { Key = VirtualKey.A, Modifiers = VirtualKeyModifiers.Control };
        selectAll.Invoked += (_, e) =>
        {
            // Leave Ctrl+A to a focused text box (search).
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
            if (!_selectMode) SetSelectMode(true);
            SelectAllNotes();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(selectAll);

        var esc = new KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += (_, e) =>
        {
            if (!_selectMode || FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
            SetSelectMode(false);
            e.Handled = true;
        };
        KeyboardAccelerators.Add(esc);
    }

    // Selected notes (never the "New note" card). Empty outside select mode.
    private List<RecentItem> SelectedNotes()
    {
        if (RecentList.SelectionMode is not (ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended))
            return new List<RecentItem>();
        try { return RecentList.SelectedItems.OfType<RecentItem>().Where(r => !r.IsNewCard).ToList(); }
        catch (COMException) { return new List<RecentItem>(); }
    }

    private void SelectAllNotes()
    {
        if (!_selectMode) return;
        if (RecentList.ItemsSource is not IList<RecentItem> shown) return;
        _rangeSelecting = true;
        try
        {
            foreach (var item in shown)
                if (!item.IsNewCard && !RecentList.SelectedItems.Contains(item))
                    RecentList.SelectedItems.Add(item);
        }
        catch (COMException) { }
        finally { _rangeSelecting = false; }
    }

    private void SelectNoNotes()
    {
        if (!_selectMode) return;
        try { RecentList.SelectedItems.Clear(); }
        catch (COMException) { }
        _selectAnchor = -1;
    }

    private void OnSelectionRange(object sender, SelectionChangedEventArgs e)
    {
        if (_rangeSelecting || !_selectMode) return;
        if (RecentList.ItemsSource is not IList<RecentItem> shown) return;
        var changed = e.AddedItems.OfType<RecentItem>().LastOrDefault()
                      ?? e.RemovedItems.OfType<RecentItem>().LastOrDefault();
        if (changed is null) return;
        int index = shown.IndexOf(changed);
        if (index < 0) return;

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        bool shiftDown = shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shiftDown && _selectAnchor >= 0 && _selectAnchor < shown.Count && e.AddedItems.Count > 0)
        {
            int from = Math.Min(_selectAnchor, index), to = Math.Max(_selectAnchor, index);
            _rangeSelecting = true;
            try
            {
                for (int i = from; i <= to; i++)
                    if (!shown[i].IsNewCard && !RecentList.SelectedItems.Contains(shown[i]))
                        RecentList.SelectedItems.Add(shown[i]);
            }
            catch (COMException) { }
            finally { _rangeSelecting = false; }
        }
        _selectAnchor = index;
    }

    private void PinSelected(bool pin)
    {
        var items = SelectedNotes();
        if (items.Count == 0) return;
        var pins = App.Services.Settings.Current.PinnedNotes;
        foreach (var item in items)
        {
            pins.RemoveAll(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
            if (pin) pins.Add(item.Path);
        }
        App.Services.Settings.Save();
        SetSelectMode(false);
        Refresh();
    }

    // Copies the selected notes into a folder the user picks. YuNotes notes
    // are .pdf files that open in any PDF viewer, so a copy is the export;
    // older .yunote notes are copied as they are (they open in YuNotes).
    private async Task ExportSelectedAsync()
    {
        var items = SelectedNotes();
        if (items.Count == 0) return;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Of(this)));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        int copied = 0;
        var failed = new List<string>();
        foreach (var item in items)
        {
            try
            {
                var ext = Path.GetExtension(item.Path);
                var dest = UniquePath(folder.Path, item.Title, ext);
                await Task.Run(() => File.Copy(item.Path, dest));
                copied++;
            }
            catch { failed.Add(item.Title); }
        }
        SetSelectMode(false);
        string msg = copied == 1 ? "1 note was copied" : $"{copied} notes were copied";
        msg += $" to {folder.Path}.";
        if (failed.Count > 0) msg += $"\n\nCouldn't copy: {string.Join(", ", failed)}";
        await ShowDialogAsync("Export finished", msg);
    }
}
