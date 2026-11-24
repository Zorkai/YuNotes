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
    // Restart the debounce timer; the actual save fires once the user pauses
    // editing for the configured delay. Cheap to call on every mutation.
    private void ScheduleAutosave()
    {
        if (_doc is null) return;
        // Respect the user's autosave setting; disabled = manual save / on-close only.
        if (!App.Services.Settings.Current.AutosaveEnabled)
        {
            _autosaveTimer?.Stop();
            return;
        }
        _autosaveTimer ??= CreateAutosaveTimer();
        _autosaveTimer.Stop();
        _autosaveTimer.Interval = AutosaveInterval();
        _autosaveTimer.Start();
    }

    // Debounce delay from settings, clamped to a sane range.
    private static TimeSpan AutosaveInterval() =>
        TimeSpan.FromSeconds(Math.Clamp(App.Services.Settings.Current.AutosaveDelaySeconds, 0.5, 120));

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateAutosaveTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = AutosaveInterval();
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            _autosaveTimer?.Stop();
            if (!App.Services.Settings.Current.AutosaveEnabled) return;
            if (_doc is not { IsDirty: true } || _saving) return;
            // Never save mid-gesture: the save runs on the UI thread, and a pen
            // stroke in flight would visibly freeze while it runs. Re-arm and
            // retry once the user actually pauses.
            if (Canvas.IsUserInteracting) { _autosaveTimer?.Start(); return; }
            _ = SaveAsync(toast: false, autosave: true);
        };
        return t;
    }

    private async Task SaveAsync(bool toast = true, bool autosave = false)
    {
        if (_doc is null || _saving) return;
        _saving = true;
        UpdateSaveStateUi();
        // A save captures the current state — cancel any pending debounce so we
        // don't immediately re-save the same thing.
        _autosaveTimer?.Stop();
        try
        {
            // Keep the version on disk before overwriting it (throttled; see VersionHistory).
            var versionPath = _doc.Info.FilePath;
            if (!string.IsNullOrEmpty(versionPath))
                await Task.Run(() => App.Services.Versions.SnapshotIfDue(versionPath));

            bool isPdf = string.Equals(System.IO.Path.GetExtension(_doc.Info.FilePath), ".pdf", StringComparison.OrdinalIgnoreCase);

            if (isPdf && autosave)
            {
                // Recovery save: update only the embedded editable-state blob.
                // The full path below re-flattens EVERY page (offscreen render
                // + PNG encode + a complete PDF rebuild) synchronously on the
                // UI thread — seconds of freeze on large documents, timed
                // exactly for when the user pauses writing. The flattened page
                // images in the file stay as of the last full save; manual
                // save and close still run the full flatten.
                var blob = App.Services.Documents.SerializeToBlob(_doc);
                var path = _doc.Info.FilePath;
                // Mutations made while the background write runs re-dirty the
                // doc (and re-arm the timer), so clear the flag up front.
                _doc.IsDirty = false;
                bool updated = await Task.Run(() => App.Services.PdfContainer.TryUpdateEmbeddedData(path, blob));
                if (updated) return;
                // No container/blob in the file yet (fresh import never fully
                // saved) — fall through to the one-time full save.
                _doc.IsDirty = true;
            }

            if (isPdf)
            {
                // When a source PDF is embedded we keep the original vector pages and
                // only flatten the user's drawings as transparent overlays — otherwise
                // we flatten the whole page (white background + content).
                bool useOverlay = _doc.SourcePdfBytes is { Length: > 0 };
                int n = Canvas.PageCount;

                ShowSaveProgress();
                SetSaveInteractionLock(true);   // block edits; scroll/zoom stay free
                // Force the overlay through layout now, then wait for it to paint,
                // so the window is visible BEFORE the first (blocking) page render.
                SaveOverlay.UpdateLayout();
                await AfterRenderAsync();

                // Per-page reuse plan: pages untouched since the last full save keep
                // their already-flattened image (cloned from the current container
                // file) instead of being re-rendered — the render loop is what makes
                // saving a 30-page document slow when only a couple pages changed.
                // Planned AFTER the interaction lock: an edit landing while the
                // previous container loads off-thread would otherwise be planned as
                // "clean" and its stale flatten marked current.
                var reuse = new int?[n];
                byte[]? prevContainer = null;
                if (_doc.FlattenPageOrder is { Count: > 0 } order && _doc.Pages.Count == n)
                {
                    var path = _doc.Info.FilePath;
                    var prev = await Task.Run(() => App.Services.PdfContainer.TryLoadForReuse(path));
                    if (prev is { } p && p.PageCount == order.Count)
                    {
                        prevContainer = p.Bytes;
                        for (int i = 0; i < n; i++) reuse[i] = ReusableFlattenIndex(i, order);
                    }
                }
                // Flatten page-by-page, letting each progress tick paint between
                // pages so the bar advances and the window stays responsive instead
                // of hard-freezing. (Win2D render stays on the UI thread — its
                // geometry cache isn't thread-safe — but the input-blocking overlay
                // stops concurrent edits.) Reused pages render nothing at all.
                var pngs = new Rendering.PageVectorExport?[n];
                int toRender = reuse.Count(r => r is null);
                int done = 0;
                for (int i = 0; i < n; i++)
                {
                    if (reuse[i] is not null) continue;
                    // Vectors come straight from the model; only text is
                    // rasterized here (Win2D, UI thread), cropped to its bounds.
                    pngs[i] = Canvas.BuildVectorExport(i, useOverlay);
                    UpdateSaveProgress(++done, toRender);
                    await AfterRenderAsync();
                }
                byte[]? thumb = null;
                try { thumb = Canvas.RenderCurrentPagePng(0, scale: 0.2f); } catch { }
                SaveProgressFinalizing();
                await AfterRenderAsync();
                // The file now reflects this page order with no stale flattens.
                // Update BEFORE the write so the embedded blob (serialized inside
                // SavePdfContainer) carries the new state; restore it if the write
                // fails so the next save re-renders what this one should have.
                var prevOrder = _doc.FlattenPageOrder;
                var prevDirty = new HashSet<string>(_doc.FlattenDirtyPageIds);
                _doc.FlattenPageOrder = _doc.Pages.Select(pg => pg.Id).ToList();
                _doc.FlattenDirtyPageIds.Clear();
                // The PDFsharp embed + write is pure CPU/IO (no Win2D) — run it off
                // the UI thread so the app stays fully responsive while it writes.
                var docRef = _doc;
                try
                {
                    await Task.Run(() => App.Services.Documents.SavePdfContainer(
                        docRef, pngs, thumb, App.Services.PdfContainer, reuse, prevContainer));
                }
                catch
                {
                    _doc.FlattenPageOrder = prevOrder;
                    _doc.FlattenDirtyPageIds.Clear();
                    foreach (var id in prevDirty) _doc.FlattenDirtyPageIds.Add(id);
                    throw;
                }
            }
            else if (autosave)
            {
                // .yunote autosave: clone the model cheaply on the UI thread
                // (large blobs shared by reference) and run the SQLite rewrite
                // on a background thread — the full DELETE+reinsert of every
                // row froze the UI right when the user paused writing. The
                // write is a single SQLite transaction, so a crash mid-save
                // rolls back rather than corrupting the file.
                var clone = YuNotes.Services.DocumentService.CloneForSave(_doc);
                byte[]? autoThumb = null;
                try { autoThumb = Canvas.RenderCurrentPagePng(0, scale: 0.2f); } catch { }
                _doc.IsDirty = false;   // edits during the write re-dirty + re-arm
                await Task.Run(() =>
                {
                    App.Services.Documents.Save(clone);
                    if (autoThumb is not null)
                        App.Services.Documents.SaveThumbnail(clone.Info.FilePath, autoThumb);
                });
                return;
            }
            else
            {
                // Manual .yunote save: same clone + background write as the
                // autosave — the full SQLite rewrite (incl. every page's
                // background blob) used to run synchronously on the UI thread,
                // freezing it for the whole write on long documents. No
                // interaction lock here, so edits made during the write re-dirty
                // the doc — clear the flag up front, not after the await.
                var manualClone = YuNotes.Services.DocumentService.CloneForSave(_doc);
                byte[]? manualThumb = null;
                try { manualThumb = Canvas.RenderCurrentPagePng(0, scale: 0.2f); } catch { }
                _doc.IsDirty = false;
                UpdateSaveStateUi();
                try
                {
                    await Task.Run(() =>
                    {
                        App.Services.Documents.Save(manualClone);
                        if (manualThumb is not null)
                            App.Services.Documents.SaveThumbnail(manualClone.Info.FilePath, manualThumb);
                    });
                }
                catch { _doc.IsDirty = true; throw; }
                if (toast) ShowToast("Saved");
                return;
            }

            _doc.IsDirty = false;
            if (toast) ShowToast("Saved");
        }
        catch (Exception ex) { HideSaveProgress(); await Notify("Save failed", ex.Message); }
        finally { _saving = false; UpdateSaveStateUi(); HideSaveProgress(); SetSaveInteractionLock(false); }
    }

    // Container page index whose flattened image page i can reuse, or null if it
    // must be re-rendered. Reusable ⇔ the page is untouched since the last full
    // save AND both its vertical neighbours are the same untouched pages as back
    // then — neighbour content bleeds across the seam into this page's render
    // (DrawNeighborBleedForExport), so an edited/moved neighbour makes even an
    // untouched page's old image stale.
    private int? ReusableFlattenIndex(int i, List<string> order)
    {
        var pages = _doc!.Pages;
        if (_doc.FlattenDirtyPageIds.Contains(pages[i].Id)) return null;
        int idx = order.IndexOf(pages[i].Id);
        if (idx < 0) return null;
        return SameCleanNeighbor(pages, i, order, idx, -1) && SameCleanNeighbor(pages, i, order, idx, +1)
            ? idx : null;
    }

    private bool SameCleanNeighbor(IList<NotePage> pages, int i, List<string> order, int idx, int dir)
    {
        string? cur = (uint)(i + dir) < (uint)pages.Count ? pages[i + dir].Id : null;
        string? old = (uint)(idx + dir) < (uint)order.Count ? order[idx + dir] : null;
        if (cur != old) return false;
        return cur is null || !_doc!.FlattenDirtyPageIds.Contains(cur);
    }

    // True when there is an open document with edits not yet persisted. Used by
    // the window-close guard (MainWindow) to decide whether to prompt before exit.
    public bool HasUnsavedChanges => _doc is { IsDirty: true };

    // Returns true if it's OK to leave the editor (saved, discarded, or no changes).
    // Returns false if the user cancels.
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (_doc is null || !_doc.IsDirty) return true;
        var dlg = new ContentDialog
        {
            Title = "Unsaved changes",
            Content = $"Save changes to “{_doc.Info.Title}” before leaving?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.None) return false;          // Cancel
        if (res == ContentDialogResult.Primary) await SaveAsync(toast: false);
        return true;                                                // Save or Discard → proceed
    }

    // ─── Save progress overlay ───────────────────────────────────────────────

    private void ShowSaveProgress()
    {
        SaveOverlayText.Text = "Saving…";
        SaveOverlaySub.Text = "Preparing…";
        SaveProgressBar.IsIndeterminate = false;
        SaveProgressBar.Value = 0;
        SaveOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateSaveProgress(int done, int total)
    {
        SaveProgressBar.IsIndeterminate = false;
        double frac = total > 0 ? (double)done / total : 0;
        SaveProgressBar.Value = frac;
        int pct = (int)System.Math.Round(frac * 100);
        SaveOverlaySub.Text = $"Page {done} of {total} · {pct}%";
    }

    // Awaits a render pass so a just-shown UI change (the overlay, a progress
    // tick) is actually painted before the next blocking render step runs —
    // Task.Yield alone posts at Normal priority and can run before the frame.
    private Task AfterRenderAsync()
    {
        var tcs = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.TrySetResult()))
            tcs.TrySetResult();
        return tcs.Task;
    }

    private void SaveProgressFinalizing()
    {
        SaveProgressBar.IsIndeterminate = true;
        SaveOverlaySub.Text = "Finalizing…";
    }

    private void HideSaveProgress() => SaveOverlay.Visibility = Visibility.Collapsed;

    // Blocks document-mutating input during a save (canvas drawing, toolbar
    // buttons, shortcuts) while leaving scroll/zoom free, so the user can look
    // around the document while it saves.
    private void SetSaveInteractionLock(bool locked)
    {
        Canvas.Context.EditingSuspended = locked;
        // IsHitTestVisible (not IsEnabled — these are Borders) blocks clicks on the
        // toolbar/top-bar buttons without greying them; shortcuts are guarded in
        // OnAccelerator, and the ScrollViewer's scroll/zoom is untouched.
        ToolbarHost.IsHitTestVisible = !locked;
        TopBar.IsHitTestVisible = !locked;
    }
}
