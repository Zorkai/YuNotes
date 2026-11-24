using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using YuNotes.Models;

namespace YuNotes.Views;

public sealed partial class EditorPage
{
    // ─── Share ──────────────────────────────────────────────────────────────────
    // Sends the note through the Windows share window (Mail, Teams, Nearby
    // sharing, …) as a PDF of every page, the current page as a PNG, or every
    // page as PNGs. Files are written to a temp folder first (the share target
    // reads them after this returns); folders older than a day are cleaned up
    // on the next share. What's shared is the note as it is on screen, unsaved
    // edits included.

    private enum ShareKind { Pdf, CurrentPagePng, AllPagesPng }

    // DataTransferManager is per window: hook DataRequested once, and hand it
    // whatever the latest Share prepared.
    private static readonly HashSet<IntPtr> s_shareHooked = new();
    private static (string Title, IReadOnlyList<IStorageItem> Files)? s_pendingShare;
    private bool _sharing;

    private void InitShare()
    {
        SharePdfItem.Click += async (_, __) => await ShareAsync(ShareKind.Pdf);
        SharePagePngItem.Click += async (_, __) => await ShareAsync(ShareKind.CurrentPagePng);
        ShareAllPngItem.Click += async (_, __) => await ShareAsync(ShareKind.AllPagesPng);
    }

    private static string ShareRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YuNotes", "Share");

    private static string SafeFileName(string title)
    {
        var name = string.Join("_", (string.IsNullOrWhiteSpace(title) ? "Note" : title.Trim())
            .Split(System.IO.Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return name.Length > 80 ? name[..80] : name;
    }

    private async Task ShareAsync(ShareKind kind)
    {
        if (_doc is null || _sharing || _doc.Pages.Count == 0) return;
        _sharing = true;
        try
        {
            CleanOldShares();
            var dir = System.IO.Path.Combine(ShareRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var title = _doc.Info.Title;
            var baseName = SafeFileName(title);
            var paths = new List<string>();

            switch (kind)
            {
                case ShareKind.Pdf:
                {
                    ShowToast("Preparing PDF…");
                    bool useOverlay = _doc.SourcePdfBytes is { Length: > 0 };
                    var pages = Enumerable.Range(0, _doc.Pages.Count)
                        .Select(i => Canvas.BuildVectorExport(i, useOverlay)).ToArray();
                    var path = System.IO.Path.Combine(dir, baseName + ".pdf");
                    var doc = _doc;
                    await Task.Run(() => App.Services.Documents.ExportPdf(doc, pages, path, App.Services.PdfContainer));
                    paths.Add(path);
                    break;
                }
                case ShareKind.CurrentPagePng:
                {
                    int current = Canvas.ActivePage is { } ap ? Math.Max(0, _doc.Pages.IndexOf(ap)) : 0;
                    var png = Canvas.RenderPageForExport(current, overlayOnly: false);
                    if (png is null) { await Notify("Couldn't share", "This page couldn't be rendered."); return; }
                    var path = System.IO.Path.Combine(dir, _doc.Pages.Count == 1 ? $"{baseName}.png" : $"{baseName} - page {current + 1}.png");
                    await File.WriteAllBytesAsync(path, png);
                    paths.Add(path);
                    break;
                }
                case ShareKind.AllPagesPng:
                {
                    ShowToast("Preparing images…");
                    for (int i = 0; i < _doc.Pages.Count; i++)
                    {
                        var png = Canvas.RenderPageForExport(i, overlayOnly: false);
                        if (png is null) continue;
                        var path = System.IO.Path.Combine(dir, $"{baseName} - page {i + 1:D3}.png");
                        await File.WriteAllBytesAsync(path, png);
                        paths.Add(path);
                    }
                    break;
                }
            }
            if (paths.Count == 0) { await Notify("Couldn't share", "Nothing could be rendered."); return; }

            var files = new List<IStorageItem>();
            foreach (var p in paths) files.Add(await StorageFile.GetFileFromPathAsync(p));
            s_pendingShare = (title, files);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Of(this));
            if (s_shareHooked.Add(hwnd))
            {
                var dtm = DataTransferManagerInterop.GetForWindow(hwnd);
                dtm.DataRequested += OnShareDataRequested;
            }
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Share");
            await Notify("Couldn't share", ex.Message);
        }
        finally { _sharing = false; }
    }

    private static void OnShareDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (s_pendingShare is not { } share) { args.Request.FailWithDisplayText("Nothing to share."); return; }
        var data = args.Request.Data;
        data.Properties.Title = string.IsNullOrWhiteSpace(share.Title) ? "Note" : share.Title;
        data.Properties.Description = share.Files.Count == 1 ? share.Files[0].Name : $"{share.Files.Count} files from YuNotes";
        data.SetStorageItems(share.Files);
    }

    private static void CleanOldShares()
    {
        try
        {
            if (!Directory.Exists(ShareRoot)) return;
            foreach (var d in Directory.GetDirectories(ShareRoot))
                if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1))
                    try { Directory.Delete(d, recursive: true); } catch { }
        }
        catch { }
    }
}
