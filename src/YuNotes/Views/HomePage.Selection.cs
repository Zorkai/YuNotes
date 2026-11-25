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
    // When the window is too narrow for the action buttons (Select mode has
    // up to eight), they drop their labels and show icons only, with the
    // label as a tooltip, so nothing is pushed off the edge. Labels come back
    // when there is room again.
    private const double SearchMinWidth = 150 + 16;   // search column MinWidth + its margins

    private void FitHeader()
    {
        if (HeaderBar.ActualWidth <= 0) return;
        var bar = HeaderSelect.Visibility == Visibility.Visible ? HeaderSelect : HeaderNormal;
        double available = HeaderBar.ActualWidth - HeaderBar.Padding.Left - HeaderBar.Padding.Right
                           - SidebarToggleBtn.ActualWidth - 6 - SearchMinWidth;
        SetHeaderCompact(bar, false);
        bar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        if (bar.DesiredSize.Width > available) SetHeaderCompact(bar, true);
    }

    private void SetHeaderCompact(StackPanel bar, bool compact)
    {
        if (bar == HeaderSelect)
            SelectionCountLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var btn in bar.Children.OfType<Button>())
        {
            // The primary action (New note) keeps its label.
            if (btn == NewBtn || btn.Content is not StackPanel sp) continue;
            var label = sp.Children.OfType<TextBlock>().FirstOrDefault();
            if (label is null) continue;
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            if (compact && ToolTipService.GetToolTip(btn) is null)
                ToolTipService.SetToolTip(btn, label.Text);
        }
    }

    private void InitSelectionExtras()
    {
        HeaderBar.SizeChanged += (_, __) => FitHeader();
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
