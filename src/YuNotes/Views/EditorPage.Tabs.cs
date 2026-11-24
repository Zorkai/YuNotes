using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using YuNotes.Models;

namespace YuNotes.Views;

// Opens a note in a new editor tab (Ctrl+click on Home) instead of replacing
// the note in the current tab.
public sealed record EditorTabRequest(string Path);

public sealed partial class EditorPage
{
    // ─── Tabs ────────────────────────────────────────────────────────────────────
    // Several notes open in one window. The tab list is static so it survives
    // going back to Home and opening the editor again. Opening a note from Home
    // replaces the current tab (as before tabs existed); new tabs come from the
    // tab button, Ctrl+T, Ctrl+click on Home, or "Open to edit" in side by side.
    //
    // Only the active tab can hold unsaved changes: leaving a tab saves it
    // (or asks, when autosave is off), so background tabs are always clean.
    // Their loaded Document is kept, with its own undo history, so switching
    // back is instant.

    private sealed class OpenTab
    {
        public string Path = "";
        public string Title = "";
        public Document? Doc;
    }

    private static readonly List<OpenTab> s_tabs = new();
    private static int s_activeTab = -1;
    private bool _suppressTabEvents;
    private bool _switchingTab;

    private OpenTab? ActiveTab => s_activeTab >= 0 && s_activeTab < s_tabs.Count ? s_tabs[s_activeTab] : null;

    private static bool SamePath(string a, string b) =>
        a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void InitTabs()
    {
        TabStrip.SelectionChanged += (sender, e) =>
        {
            if (_suppressTabEvents) return;
            _ = SwitchTabAsync(TabStrip.SelectedIndex);
        };
        TabStrip.TabCloseRequested += (sender, args) =>
        {
            int idx = TabStrip.TabItems.IndexOf(args.Tab);
            if (idx >= 0) _ = CloseTabAsync(idx);
        };
        NewTabFlyout.Opening += (_, __) => RebuildNewTabFlyout(NewTabFlyout);
        TabAddFlyout.Opening += (_, __) => RebuildNewTabFlyout(TabAddFlyout);
    }

    // Called from OnNavigatedTo with the navigation parameter.
    private void OpenFromNavigation(object? param)
    {
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

        int idx = s_tabs.FindIndex(t => SamePath(t.Path, path));
        if (idx >= 0)
        {
            if (doc is not null) s_tabs[idx].Doc = doc;
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
                s_tabs.Add(tab);
                idx = s_tabs.Count - 1;
            }
            else
            {
                // Replace the note in the current tab. It was already saved or
                // discarded when the editor was left.
                if (ActiveTab!.Doc is { } old) App.Services.History.Forget(old);
                s_tabs[s_activeTab] = tab;
                idx = s_activeTab;
            }
        }
        ActivateTab(idx);
    }

    // Tabs whose note was moved, trashed or deleted from Home are closed.
    private static void PruneMissingTabs()
    {
        var active = s_activeTab >= 0 && s_activeTab < s_tabs.Count ? s_tabs[s_activeTab] : null;
        foreach (var t in s_tabs.Where(t => t.Path.Length > 0 && !File.Exists(t.Path)).ToList())
        {
            if (t.Doc is { } d) App.Services.History.Forget(d);
            s_tabs.Remove(t);
        }
        s_activeTab = active is null ? -1 : s_tabs.IndexOf(active);
        if (s_activeTab < 0 && s_tabs.Count > 0) s_activeTab = s_tabs.Count - 1;
    }

    // Shows tab `idx` without asking anything (the caller already left the
    // previous tab).
    private void ActivateTab(int idx)
    {
        s_activeTab = idx;
        var tab = s_tabs[idx];
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
            foreach (var t in s_tabs)
            {
                var item = new TabViewItem
                {
                    Header = string.IsNullOrEmpty(t.Title) ? "Untitled" : t.Title,
                    IsClosable = true,
                    IconSource = new FontIconSource
                    {
                        Glyph = t.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "" : "",
                        FontFamily = (FontFamily)res["MaterialIconsFamily"],
                    },
                };
                ToolTipService.SetToolTip(item, t.Path);
                TabStrip.TabItems.Add(item);
            }
            TabStrip.SelectedIndex = s_activeTab;
            TabStrip.Visibility = s_tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _suppressTabEvents = false; }
    }

    private async Task SwitchTabAsync(int idx)
    {
        if (_switchingTab || idx == s_activeTab || idx < 0 || idx >= s_tabs.Count) return;
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
        if (s_tabs.Count < 2) return Task.CompletedTask;
        return SwitchTabAsync(((s_activeTab + dir) % s_tabs.Count + s_tabs.Count) % s_tabs.Count);
    }

    // Saves (or asks about) the active note before another one takes the tab
    // strip's focus. False = stay (the user cancelled, or saving failed).
    private async Task<bool> LeaveActiveTabAsync()
    {
        if (_doc is null) return true;
        if (_saving || Canvas.Context.EditingSuspended) return false;
        if (_presenting) ExitPresentation();
        Canvas.ClearSelection();
        UpdateSelectionUi();
        if (SearchPanel.Visibility == Visibility.Visible) CloseSearch();
        SaveLastPage();
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
        if (_switchingTab || idx < 0 || idx >= s_tabs.Count) return;
        _switchingTab = true;
        try
        {
            if (idx == s_activeTab)
            {
                if (!await LeaveActiveTabAsync()) { RebuildTabStrip(); return; }
                if (s_tabs[idx].Doc is { } d) App.Services.History.Forget(d);
                s_tabs.RemoveAt(idx);
                if (s_tabs.Count == 0)
                {
                    s_activeTab = -1;
                    _doc = null;
                    MainWindow.GoBack();
                    return;
                }
                ActivateTab(Math.Min(idx, s_tabs.Count - 1));
            }
            else
            {
                // Background tabs are always saved, so closing one asks nothing.
                if (s_tabs[idx].Doc is { } d) App.Services.History.Forget(d);
                s_tabs.RemoveAt(idx);
                if (idx < s_activeTab) s_activeTab--;
                RebuildTabStrip();
            }
        }
        finally { _switchingTab = false; }
    }

    // Opens `path` in a new tab, or switches to it if it is already open.
    private async Task OpenInNewTabAsync(string path)
    {
        if (_switchingTab) return;
        int existing = s_tabs.FindIndex(t => SamePath(t.Path, path));
        if (existing >= 0) { await SwitchTabAsync(existing); return; }
        _switchingTab = true;
        try
        {
            if (!await LeaveActiveTabAsync()) return;
            s_tabs.Add(new OpenTab { Path = path, Title = System.IO.Path.GetFileNameWithoutExtension(path) });
            ActivateTab(s_tabs.Count - 1);
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
        var open = CandidateNotePaths(p => s_tabs.Any(t => SamePath(t.Path, p)));
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
        if (s_tabs.Count > 1)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var close = new MenuFlyoutItem { Text = "Close this tab (Ctrl+W)" };
            close.Click += async (_, __) => await CloseTabAsync(s_activeTab);
            flyout.Items.Add(close);
        }
    }

    // Ctrl+T: the picker opens from whichever button is on screen.
    private void ShowNewTabPicker()
    {
        var btn = TabStrip.Visibility == Visibility.Visible ? TabAddBtn : NewTabBtn;
        btn.Flyout?.ShowAt(btn);
    }

    // A restored version replaces the note on disk: any copy held by a tab is stale.
    private static void DropCachedTab(string path)
    {
        foreach (var t in s_tabs.Where(t => SamePath(t.Path, path)))
        {
            if (t.Doc is { } d) App.Services.History.Forget(d);
            t.Doc = null;
        }
    }
}
