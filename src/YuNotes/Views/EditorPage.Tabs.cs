using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using YuNotes.Models;

namespace YuNotes.Views;

// Opens a note in a new editor tab (Ctrl+click on Home) instead of replacing
// the note in the current tab.
public sealed record EditorTabRequest(string Path);

// Shows the window's tabs as they are (a window made from a dragged-out tab).
public sealed class EditorResume
{
    public static readonly EditorResume Instance = new();
    private EditorResume() { }
}

internal sealed class OpenTab
{
    public string Path = "";
    public string Title = "";
    public Document? Doc;
}

// One window's editor tabs and which one is showing.
internal sealed class EditorTabs
{
    public readonly List<OpenTab> List = new();
    public int Active = -1;

    public OpenTab? Find(string path) =>
        List.FirstOrDefault(t => t.Path.Length > 0 && string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class EditorPage
{
    // ─── Tabs ────────────────────────────────────────────────────────────────────
    // Several notes open in one window. The tab list belongs to the window
    // (MainWindow.Tabs) so it survives going back to Home and opening the
    // editor again; each window has its own. Tabs can be reordered, dragged
    // out into a new window, or dropped onto another window's tab strip. Opening a note from Home
    // replaces the current tab (as before tabs existed); new tabs come from the
    // + at the end of the tab strip, Ctrl+T, Ctrl+click on Home, or "Open to
    // edit" in side by side. The strip is always shown, even with one tab.
    //
    // Only the active tab can hold unsaved changes: leaving a tab saves it
    // (or asks, when autosave is off), so background tabs are always clean.
    // Their loaded Document is kept, with its own undo history, so switching
    // back is instant.

    private EditorTabs _tabSet = new();
    private List<OpenTab> _tabs => _tabSet.List;
    private int _activeTab { get => _tabSet.Active; set => _tabSet.Active = value; }
    private bool _suppressTabEvents;
    private bool _switchingTab;

    private OpenTab? ActiveTab => _activeTab >= 0 && _activeTab < _tabs.Count ? _tabs[_activeTab] : null;

    private static bool SamePath(string a, string b) =>
        a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void InitTabs()
    {
        // Tabs are matched by their Tag, not their position: while a tab is
        // being dragged to a new place the strip's order runs ahead of ours.
        TabStrip.SelectionChanged += (sender, e) =>
        {
            if (_suppressTabEvents) return;
            if (TabStrip.SelectedItem is TabViewItem { Tag: OpenTab t } && _tabs.IndexOf(t) is var idx and >= 0)
                _ = SwitchTabAsync(idx);
        };
        TabStrip.TabCloseRequested += (sender, args) =>
        {
            if (args.Tab.Tag is OpenTab t && _tabs.IndexOf(t) is var idx and >= 0) _ = CloseTabAsync(idx);
        };
        TabStrip.TabItemsChanged += (_, __) => { if (!_suppressTabEvents) SyncOrderFromStrip(); };
        TabStrip.TabDragStarting += OnTabDragStarting;
        TabStrip.TabDragCompleted += (_, __) => s_drag = null;
        TabStrip.TabDroppedOutside += OnTabDroppedOutside;
        TabStrip.TabStripDragOver += OnTabStripDragOver;
        TabStrip.TabStripDrop += OnTabStripDrop;
        TabAddFlyout.Opening += (_, __) => RebuildNewTabFlyout(TabAddFlyout);
    }

    // The note a navigation to the editor would open, if it names one.
    internal static string? PathOf(object? param) => param switch
    {
        Document d => d.Info.FilePath,
        string p => p,
        EditorOpenRequest r => r.Path,
        EditorTabRequest t => t.Path,
        _ => null,
    };

    // Called from OnNavigatedTo with the navigation parameter.
    private void OpenFromNavigation(object? param)
    {
        _tabSet = MainWindow.Of(this)?.Tabs ?? new EditorTabs();
        if (param is EditorResume)
        {
            PruneMissingTabs();
            if (_activeTab < 0 && _tabs.Count > 0) _activeTab = 0;
            if (_activeTab >= 0) ActivateTab(_activeTab);
            return;
        }
        string? path = null;
        Document? doc = null;
        bool newTab = false;
        switch (param)
        {
            case Document d: doc = d; path = d.Info.FilePath ?? ""; break;
            case string p: path = p; break;
            case EditorOpenRequest r: _pendingSearch = (r.Query, r.PageIndex); path = r.Path; break;
            case EditorTabRequest t: path = t.Path; newTab = true; break;
        }
        PruneMissingTabs();
        if (path is null) return;

        // Coming back (e.g. from Settings) to a note that has since moved to
        // another window: show this window's tabs instead of a second copy.
        if (_tabs.FindIndex(t => SamePath(t.Path, path)) < 0
            && MainWindow.FindOpenTab(path) is { } elsewhere
            && !ReferenceEquals(elsewhere.Window, MainWindow.Of(this)))
        {
            if (_tabs.Count == 0) { MainWindow.GoBack(this); return; }
            if (_activeTab < 0) _activeTab = 0;
            ActivateTab(_activeTab);
            return;
        }

        int idx = _tabs.FindIndex(t => SamePath(t.Path, path));
        if (idx >= 0)
        {
            if (doc is not null) _tabs[idx].Doc = doc;
        }
        else
        {
            var tab = new OpenTab
            {
                Path = path,
                Doc = doc,
                Title = doc?.Info.Title ?? System.IO.Path.GetFileNameWithoutExtension(path),
            };
            if (newTab || ActiveTab is null)
            {
                _tabs.Add(tab);
                idx = _tabs.Count - 1;
            }
            else
            {
                // Replace the note in the current tab. It was already saved or
                // discarded when the editor was left.
                if (ActiveTab!.Doc is { } old) App.Services.History.Forget(old);
                _tabs[_activeTab] = tab;
                idx = _activeTab;
            }
        }
        ActivateTab(idx);
    }

    // Tabs whose note was moved, trashed or deleted from Home are closed.
    private void PruneMissingTabs()
    {
        var active = _activeTab >= 0 && _activeTab < _tabs.Count ? _tabs[_activeTab] : null;
        foreach (var t in _tabs.Where(t => t.Path.Length > 0 && !File.Exists(t.Path)).ToList())
        {
            if (t.Doc is { } d) App.Services.History.Forget(d);
            _tabs.Remove(t);
        }
        _activeTab = active is null ? -1 : _tabs.IndexOf(active);
        if (_activeTab < 0 && _tabs.Count > 0) _activeTab = _tabs.Count - 1;
    }

    // Shows tab `idx` without asking anything (the caller already left the
    // previous tab).
    private void ActivateTab(int idx)
    {
        _activeTab = idx;
        var tab = _tabs[idx];
        RebuildTabStrip();
        if (tab.Doc is { } d) { Load(d); RunPendingSearch(); }
        else _ = LoadFromPathAsync(tab.Path);
    }

    // Keeps the active tab's path, title and loaded document in step with the
    // editor (after a load or a rename).
    private void SyncActiveTabFromDoc()
    {
        if (ActiveTab is not { } tab || _doc is null) return;
        tab.Doc = _doc;
        if (!string.IsNullOrEmpty(_doc.Info.FilePath)) tab.Path = _doc.Info.FilePath;
        tab.Title = _doc.Info.Title;
        RebuildTabStrip();
    }

    private void RebuildTabStrip()
    {
        _suppressTabEvents = true;
        try
        {
            TabStrip.TabItems.Clear();
            var res = Application.Current.Resources;
            foreach (var t in _tabs)
            {
                var item = new TabViewItem
                {
                    Tag = t,
                    Header = string.IsNullOrEmpty(t.Title) ? "Untitled" : t.Title,
                    IsClosable = true,
                    IconSource = new FontIconSource
                    {
                        Glyph = t.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "" : "",
                        FontFamily = (FontFamily)res["MaterialIconsFamily"],
                    },
                };
                ToolTipService.SetToolTip(item, t.Path);
                item.ContextFlyout = BuildTabContextMenu(t);
                TabStrip.TabItems.Add(item);
            }
            TabStrip.SelectedIndex = _activeTab;
            TabStrip.Visibility = _tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _suppressTabEvents = false; }
    }

    private async Task SwitchTabAsync(int idx)
    {
        if (_switchingTab || idx == _activeTab || idx < 0 || idx >= _tabs.Count) return;
        _switchingTab = true;
        try
        {
            if (!await LeaveActiveTabAsync()) { RebuildTabStrip(); return; }
            ActivateTab(idx);
        }
        finally { _switchingTab = false; }
    }

    private Task CycleTabAsync(int dir)
    {
        if (_tabs.Count < 2) return Task.CompletedTask;
        return SwitchTabAsync(((_activeTab + dir) % _tabs.Count + _tabs.Count) % _tabs.Count);
    }

    // Saves (or asks about) the active note before another one takes the tab
    // strip's focus. False = stay (the user cancelled, or saving failed).
    private async Task<bool> LeaveActiveTabAsync()
    {
        if (_doc is null) return true;
        if (_saving || Canvas.Context.EditingSuspended) return false;
        if (_presenting) ExitPresentation();
        ExitStudy();
        Canvas.ClearSelection();
        UpdateSelectionUi();
        if (SearchPanel.Visibility == Visibility.Visible) CloseSearch();
        SaveView();
        SaveLastPage(force: true);
        _autosaveTimer?.Stop();

        if (_doc.IsDirty)
        {
            if (App.Services.Settings.Current.AutosaveEnabled)
            {
                await SaveAsync(toast: false);
                if (_doc.IsDirty) return false;   // save failed: keep the user here
            }
            else
            {
                if (!await ConfirmLeaveAsync()) return false;
                if (_doc.IsDirty) DropActiveTabDocument();   // "Don't save"
            }
        }
        return true;
    }

    // The active note's in-memory changes were discarded: forget them so the
    // tab reloads the note from disk next time.
    private void DropActiveTabDocument()
    {
        if (_doc is { } d) App.Services.History.Forget(d);
        if (ActiveTab is { } tab) tab.Doc = null;
    }

    private async Task CloseTabAsync(int idx)
    {
        if (_switchingTab || idx < 0 || idx >= _tabs.Count) return;
        _switchingTab = true;
        try
        {
            if (idx == _activeTab)
            {
                if (!await LeaveActiveTabAsync()) { RebuildTabStrip(); return; }
                if (_tabs[idx].Doc is { } d) App.Services.History.Forget(d);
                _tabs.RemoveAt(idx);
                if (_tabs.Count == 0)
                {
                    _activeTab = -1;
                    _doc = null;
                    MainWindow.GoBack(this);
                    return;
                }
                ActivateTab(Math.Min(idx, _tabs.Count - 1));
            }
            else
            {
                // Background tabs are always saved, so closing one asks nothing.
                if (_tabs[idx].Doc is { } d) App.Services.History.Forget(d);
                _tabs.RemoveAt(idx);
                if (idx < _activeTab) _activeTab--;
                RebuildTabStrip();
            }
        }
        finally { _switchingTab = false; }
    }

    // Opens `path` in a new tab, or switches to it if it is already open
    // (here or in another window).
    internal async Task OpenInNewTabAsync(string path)
    {
        if (_switchingTab) return;
        int existing = _tabs.FindIndex(t => SamePath(t.Path, path));
        if (existing >= 0) { await SwitchTabAsync(existing); return; }
        if (MainWindow.FindOpenTab(path) is { } elsewhere) { elsewhere.Window.ShowTab(elsewhere.Tab.Path); return; }
        _switchingTab = true;
        try
        {
            if (!await LeaveActiveTabAsync()) return;
            _tabs.Add(new OpenTab { Path = path, Title = System.IO.Path.GetFileNameWithoutExtension(path) });
            ActivateTab(_tabs.Count - 1);
        }
        finally { _switchingTab = false; }
    }

    // Notes that can be opened next: recently opened first, then the rest by
    // name; never the trash or archive. Shared with the side-by-side picker.
    private List<string> CandidateNotePaths(Func<string, bool>? exclude = null, int max = 25)
    {
        var s = App.Services.Settings.Current;
        var root = s.DocumentsFolder;
        var all = new List<string>();
        try
        {
            if (Directory.Exists(root))
                all.AddRange(Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yunote", StringComparison.OrdinalIgnoreCase))
                    .Where(p => !p.Contains($"{System.IO.Path.DirectorySeparatorChar}_trash", StringComparison.OrdinalIgnoreCase)
                             && !p.Contains($"{System.IO.Path.DirectorySeparatorChar}_archive", StringComparison.OrdinalIgnoreCase)));
        }
        catch { }
        return s.RecentDocuments.Where(all.Contains)
            .Concat(all.Except(s.RecentDocuments).OrderBy(p => System.IO.Path.GetFileNameWithoutExtension(p), StringComparer.CurrentCultureIgnoreCase))
            .Where(p => exclude is null || !exclude(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max).ToList();
    }

    // "Note name  ·  Folder" (folder left out for notes in the root).
    private static string NoteMenuLabel(string path)
    {
        var root = App.Services.Settings.Current.DocumentsFolder;
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        bool inRoot = string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);
        return System.IO.Path.GetFileNameWithoutExtension(path) + (inRoot ? "" : $"  ·  {System.IO.Path.GetFileName(dir)}");
    }

    private void RebuildNewTabFlyout(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        var open = CandidateNotePaths(p => _tabs.Any(t => SamePath(t.Path, p)));
        flyout.Items.Add(new MenuFlyoutItem { Text = "Open in a new tab:", IsEnabled = false });
        foreach (var p in open)
        {
            var item = new MenuFlyoutItem { Text = NoteMenuLabel(p) };
            string path = p;
            item.Click += async (_, __) => await OpenInNewTabAsync(path);
            flyout.Items.Add(item);
        }
        if (open.Count == 0)
            flyout.Items.Add(new MenuFlyoutItem { Text = "No other notes to open", IsEnabled = false });
        if (_tabs.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var close = new MenuFlyoutItem { Text = "Close this tab (Ctrl+W)" };
            close.Click += async (_, __) => await CloseTabAsync(_activeTab);
            flyout.Items.Add(close);
        }
    }

    // Ctrl+T: open the picker from the tab strip's + button.
    private void ShowNewTabPicker()
    {
        TabAddBtn.Flyout?.ShowAt(TabAddBtn);
    }

    // Shows the tab holding `path` (another window asked, e.g. for a file
    // opened from Explorer that this window already has).
    internal async Task SwitchToPathAsync(string path)
    {
        int idx = _tabs.FindIndex(t => SamePath(t.Path, path));
        if (idx >= 0) await SwitchTabAsync(idx);
    }

    // A restored version replaces the note on disk: any copy held by a tab is stale.
    private static void DropCachedTab(string path)
    {
        foreach (var w in MainWindow.All)
            foreach (var t in w.Tabs.List.Where(t => SamePath(t.Path, path)))
            {
                if (t.Doc is { } d) App.Services.History.Forget(d);
                t.Doc = null;
            }
    }

    // ─── Moving tabs between windows ────────────────────────────────────────────

    // The tab being dragged and the editor it came from. Drags only happen
    // within this one process, so a static is enough to hand it over.
    private static (EditorPage Source, OpenTab Tab)? s_drag;

    private MenuFlyout BuildTabContextMenu(OpenTab tab)
    {
        var menu = new MenuFlyout();
        var toNew = new MenuFlyoutItem
        {
            Text = "Move to new window",
            Icon = new FontIcon { Glyph = "\ue89e", FontFamily = (FontFamily)Application.Current.Resources["MaterialIconsFamily"] },
        };
        toNew.Click += async (_, __) => await MoveTabToNewWindowAsync(tab);
        menu.Opening += (_, __) => toNew.IsEnabled = _tabs.Count > 1;
        menu.Items.Add(toNew);

        var toWindow = new MenuFlyoutSubItem { Text = "Move to window" };
        menu.Opening += (_, __) =>
        {
            toWindow.Items.Clear();
            var mine = MainWindow.Of(this);
            foreach (var w in MainWindow.All.Where(w => !ReferenceEquals(w, mine)).Reverse())
            {
                var label = w.Tabs.List.Count > 0 && w.Tabs.Active >= 0 && w.Tabs.Active < w.Tabs.List.Count
                    ? $"{w.Tabs.List[w.Tabs.Active].Title}" + (w.Tabs.List.Count > 1 ? $" and {w.Tabs.List.Count - 1} more" : "")
                    : "Home";
                var item = new MenuFlyoutItem { Text = label };
                var target = w;
                item.Click += async (_, __) => await MoveTabToWindowAsync(tab, target);
                toWindow.Items.Add(item);
            }
            toWindow.Visibility = toWindow.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        menu.Items.Add(toWindow);

        menu.Items.Add(new MenuFlyoutSeparator());
        var close = new MenuFlyoutItem { Text = "Close tab" };
        close.Click += async (_, __) => { if (_tabs.IndexOf(tab) is var i and >= 0) await CloseTabAsync(i); };
        menu.Items.Add(close);
        return menu;
    }

    // After the user drags a tab to a new place in the strip, take that order.
    // Mid-drag the strip can be one tab short; wait until it is whole again.
    private void SyncOrderFromStrip()
    {
        var order = TabStrip.TabItems.OfType<TabViewItem>().Select(i => i.Tag as OpenTab).ToList();
        if (order.Count != _tabs.Count || order.Any(t => t is null || !_tabs.Contains(t))) return;
        var active = ActiveTab;
        _tabs.Clear();
        _tabs.AddRange(order!);
        _activeTab = active is null ? -1 : _tabs.IndexOf(active);
    }

    private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        if (args.Tab.Tag is not OpenTab tab) { args.Cancel = true; return; }
        s_drag = (this, tab);
        args.Data.Properties["YuNotesTab"] = tab.Path;
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    // Dropped somewhere that isn't a tab strip: the tab gets its own window,
    // where it was dropped. The last tab stays (its window is already "its own").
    private void OnTabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        s_drag = null;
        if (args.Tab.Tag is OpenTab tab && _tabs.Count > 1) _ = MoveTabToNewWindowAsync(tab, atCursor: true);
    }

    // Another window's tab dragged over this strip: offer to take it.
    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        if (s_drag is { } d && !ReferenceEquals(d.Source, this))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.DragUIOverride.Caption = "Move tab here";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        if (s_drag is not { } d || ReferenceEquals(d.Source, this)) return;
        s_drag = null;
        // Insert before the first tab whose middle is right of the drop point.
        int index = _tabs.Count;
        for (int i = 0; i < TabStrip.TabItems.Count; i++)
        {
            if (TabStrip.ContainerFromIndex(i) is TabViewItem item
                && e.GetPosition(item).X < item.ActualWidth / 2)
            { index = i; break; }
        }
        _ = ReceiveTabAsync(d.Source, d.Tab, index);
    }

    private async Task MoveTabToNewWindowAsync(OpenTab tab, bool atCursor = false)
    {
        if (_tabs.Count < 2) return;
        var size = MainWindow.Of(this)?.Size ?? default;
        if (!await DetachTabAsync(tab)) return;
        var set = new EditorTabs();
        set.List.Add(tab);
        set.Active = 0;
        var w = new MainWindow(set);
        if (atCursor) w.PlaceAtCursor(size);
        w.Activate();
    }

    private async Task MoveTabToWindowAsync(OpenTab tab, MainWindow target)
    {
        if (target.RootFrame.Content is EditorPage editor)
        {
            await editor.ReceiveTabAsync(this, tab, editor._tabs.Count);
            return;
        }
        // The other window is on Home or Settings: hand it the tab and show it there.
        var mine = MainWindow.Of(this);
        bool last = _tabs.Count == 1;
        if (!await DetachTabAsync(tab)) return;
        target.Tabs.List.Add(tab);
        target.Tabs.Active = target.Tabs.List.Count - 1;
        target.NavigateTo<EditorPage>(EditorResume.Instance, new SuppressNavigationTransitionInfo());
        target.BringToFront();
        if (last) mine?.Close();
    }

    // Takes `tab` from `source` (another window's editor) and shows it here at
    // `index`. The window it came from closes if that was its last tab.
    private async Task ReceiveTabAsync(EditorPage source, OpenTab tab, int index)
    {
        if (_switchingTab) return;
        _switchingTab = true;
        try
        {
            // Save this window's note first; it stays loaded if the move fails.
            if (!await LeaveActiveTabAsync()) return;
        }
        finally { _switchingTab = false; }

        var sourceWindow = MainWindow.Of(source);
        bool sourceEmpties = source._tabs.Count == 1;
        if (!await source.DetachTabAsync(tab)) return;
        if (sourceEmpties) sourceWindow?.Close();

        index = Math.Clamp(index, 0, _tabs.Count);
        _tabs.Insert(index, tab);
        ActivateTab(index);
        MainWindow.Of(this)?.BringToFront();
    }

    // Takes `tab` out of this window, saving it first if it is the one showing.
    // False = it stays (the user cancelled, or saving failed). When it was the
    // last tab the editor is left empty; the caller closes the window.
    private async Task<bool> DetachTabAsync(OpenTab tab)
    {
        if (_switchingTab) return false;
        int idx = _tabs.IndexOf(tab);
        if (idx < 0) return false;
        _switchingTab = true;
        try
        {
            if (idx == _activeTab)
            {
                if (!await LeaveActiveTabAsync()) { RebuildTabStrip(); return false; }
                _tabs.RemoveAt(idx);
                if (_tabs.Count == 0)
                {
                    _activeTab = -1;
                    _doc = null;
                    RebuildTabStrip();
                    return true;
                }
                ActivateTab(Math.Min(idx, _tabs.Count - 1));
            }
            else
            {
                _tabs.RemoveAt(idx);
                if (idx < _activeTab) _activeTab--;
                RebuildTabStrip();
            }
            return true;
        }
        finally { _switchingTab = false; }
    }
}
