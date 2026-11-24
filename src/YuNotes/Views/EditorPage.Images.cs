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
    private async Task PickImageAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg");
        InitPicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        _image.PendingPng = File.ReadAllBytes(file.Path);
        await ShowImageGhostAsync(_image.PendingPng);
    }

    // ─── Clipboard image paste ───────────────────────────────────────────────────

    private void OnClipboardChanged(object? sender, object e)
    {
        // ContentChanged can arrive off the UI thread — marshal the UI update.
        if (DispatcherQueue is null) { RefreshPasteAffordance(); return; }
        DispatcherQueue.TryEnqueue(RefreshPasteAffordance);
    }

    // Shows the toolbar paste button only while the clipboard holds an image.
    private void RefreshPasteAffordance()
    {
        bool hasImage = false;
        try
        {
            hasImage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
                .Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap);
        }
        catch { /* clipboard can momentarily be locked by another app */ }
        ToolPaste.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task PasteClipboardImageAsync()
    {
        if (_doc is null) return;
        var png = await TryGetClipboardImagePngAsync();
        if (png is null) { RefreshPasteAffordance(); return; }
        if (Canvas.PasteImageAtViewportCenter(png))
        {
            UpdateSelectionUi();
            ShowToast("Image pasted");
        }
    }

    // Reads a bitmap off the clipboard and transcodes it to PNG bytes (browsers hand
    // over DIB/PNG/JPEG in various formats; re-encoding normalises them for storage).
    private async Task<byte[]?> TryGetClipboardImagePngAsync()
    {
        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)) return null;

            var bmpRef = await view.GetBitmapAsync();
            using var src = await bmpRef.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(src);
            var pixels = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

            using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, outStream);
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                decoder.PixelWidth, decoder.PixelHeight,
                decoder.DpiX <= 0 ? 96 : decoder.DpiX,
                decoder.DpiY <= 0 ? 96 : decoder.DpiY,
                pixels.DetachPixelData());
            await encoder.FlushAsync();

            var bytes = new byte[outStream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(outStream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)outStream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) { App.LogError(ex, "TryGetClipboardImagePng"); return null; }
    }

    private async Task ShowImageGhostAsync(byte[] png)
    {
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(ms))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        ms.Seek(0);
        await bmp.SetSourceAsync(ms);
        ImageGhostImg.Source = bmp;
        // Size the ghost to the image's aspect ratio (longest side ~160) so a wide
        // screenshot doesn't preview as a squished 160×120 box.
        if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            double longest = 160.0;
            double scale = longest / Math.Max(bmp.PixelWidth, bmp.PixelHeight);
            ImageGhostImg.Width  = Math.Max(24, bmp.PixelWidth  * scale);
            ImageGhostImg.Height = Math.Max(24, bmp.PixelHeight * scale);
        }
        ImageGhost.Visibility = Visibility.Visible;
    }

    private void HideImageGhost()
    {
        ImageGhost.Visibility = Visibility.Collapsed;
        ImageGhostImg.Source = null;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ImageGhost.Visibility != Visibility.Visible) return;
        var pt = e.GetCurrentPoint(GhostLayer).Position;
        XamlCanvas.SetLeft(ImageGhost, pt.X - ImageGhostImg.Width / 2);
        XamlCanvas.SetTop(ImageGhost,  pt.Y - ImageGhostImg.Height / 2);
    }

    // ─── In-app screenshot region capture ────────────────────────────────────────

    private bool _shotDragging;
    private Windows.Foundation.Point _shotStart;
    private Windows.Foundation.Rect _shotRect;
    private byte[]? _shotPng;
    private uint _shotPointerId;

    // Enters region-select mode: a full-window overlay that dims the page and lets
    // the user drag out a rectangle. Replaces the old Windows Snipping Tool launch.
    private void EnterScreenshotMode()
    {
        if (_doc is null) return;
        _shotDragging = false;
        _shotPng = null;
        _shotRect = default;
        ScreenshotRect.Visibility = Visibility.Collapsed;
        ScreenshotActions.Visibility = Visibility.Collapsed;
        ScreenshotOverlay.Visibility = Visibility.Visible;
        // Force layout so ActualWidth/Height are valid, then dim the whole surface
        // (one full-cover mask panel until the first drag splits it into four).
        ScreenshotOverlay.UpdateLayout();
        SetMask(MaskTop, 0, 0, ScreenshotOverlay.ActualWidth, ScreenshotOverlay.ActualHeight);
        SetMask(MaskBottom, 0, 0, 0, 0);
        SetMask(MaskLeft, 0, 0, 0, 0);
        SetMask(MaskRight, 0, 0, 0, 0);
        ScreenshotOverlay.Focus(FocusState.Programmatic);
    }

    private void ExitScreenshotMode()
    {
        _shotDragging = false;
        _shotPng = null;
        ScreenshotOverlay.Visibility = Visibility.Collapsed;
        ScreenshotRect.Visibility = Visibility.Collapsed;
        ScreenshotActions.Visibility = Visibility.Collapsed;
    }

    private static void SetMask(Rectangle r, double x, double y, double w, double h)
    {
        XamlCanvas.SetLeft(r, x);
        XamlCanvas.SetTop(r, y);
        r.Width = Math.Max(0, w);
        r.Height = Math.Max(0, h);
    }

    private void OnShotKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { ExitScreenshotMode(); e.Handled = true; }
    }

    private void OnShotPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A fresh drag restarts selection (e.g. after picking a region but not acting).
        ScreenshotActions.Visibility = Visibility.Collapsed;
        _shotStart = e.GetCurrentPoint(ScreenshotCanvas).Position;
        _shotRect = new Windows.Foundation.Rect(_shotStart.X, _shotStart.Y, 0, 0);
        _shotDragging = true;
        _shotPointerId = e.Pointer.PointerId;
        ScreenshotOverlay.CapturePointer(e.Pointer);
        UpdateShotRect(_shotStart, _shotStart);
        e.Handled = true;
    }

    private void OnShotPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_shotDragging || e.Pointer.PointerId != _shotPointerId) return;
        UpdateShotRect(_shotStart, e.GetCurrentPoint(ScreenshotCanvas).Position);
        e.Handled = true;
    }

    private void OnShotPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_shotDragging || e.Pointer.PointerId != _shotPointerId) return;
        _shotDragging = false;
        ScreenshotOverlay.ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        // Too small to be a deliberate selection — treat as a cancel-ish reset.
        if (_shotRect.Width < 6 || _shotRect.Height < 6)
        {
            ScreenshotRect.Visibility = Visibility.Collapsed;
            SetMask(MaskTop, 0, 0, ScreenshotOverlay.ActualWidth, ScreenshotOverlay.ActualHeight);
            SetMask(MaskBottom, 0, 0, 0, 0); SetMask(MaskLeft, 0, 0, 0, 0); SetMask(MaskRight, 0, 0, 0, 0);
            return;
        }

        // Render the selected region from the document (crisp, not screen pixels).
        try
        {
            var region = ScreenshotCanvas.TransformToVisual(Canvas).TransformBounds(_shotRect);
            _shotPng = Canvas.CaptureRegionPng(region);
        }
        catch (Exception ex) { App.LogError(ex, "CaptureRegionPng"); _shotPng = null; }

        if (_shotPng is null) { ExitScreenshotMode(); return; }
        ShowShotActions();
    }

    private void UpdateShotRect(Windows.Foundation.Point a, Windows.Foundation.Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X), h = Math.Abs(a.Y - b.Y);
        _shotRect = new Windows.Foundation.Rect(x, y, w, h);

        XamlCanvas.SetLeft(ScreenshotRect, x);
        XamlCanvas.SetTop(ScreenshotRect, y);
        ScreenshotRect.Width = w;
        ScreenshotRect.Height = h;
        ScreenshotRect.Visibility = Visibility.Visible;

        // Split the dim mask into four panels framing the selection.
        double W = ScreenshotOverlay.ActualWidth, H = ScreenshotOverlay.ActualHeight;
        SetMask(MaskTop,    0,     0,     W,           y);
        SetMask(MaskBottom, 0,     y + h, W,           H - (y + h));
        SetMask(MaskLeft,   0,     y,     x,           h);
        SetMask(MaskRight,  x + w, y,     W - (x + w), h);
    }

    private void ShowShotActions()
    {
        ScreenshotActions.Visibility = Visibility.Visible;
        ScreenshotActions.UpdateLayout();
        double aw = ScreenshotActions.ActualWidth, ah = ScreenshotActions.ActualHeight;
        double W = ScreenshotOverlay.ActualWidth, H = ScreenshotOverlay.ActualHeight;

        // Right-align the bar to the selection, placed just below it; flip above when
        // there isn't room, and clamp inside the overlay.
        double x = _shotRect.X + _shotRect.Width - aw;
        double y = _shotRect.Y + _shotRect.Height + 10;
        if (y + ah > H - 8) y = _shotRect.Y - ah - 10;
        x = Math.Clamp(x, 8, Math.Max(8, W - aw - 8));
        y = Math.Clamp(y, 8, Math.Max(8, H - ah - 8));
        XamlCanvas.SetLeft(ScreenshotActions, x);
        XamlCanvas.SetTop(ScreenshotActions, y);
    }

    private async Task CopyShotAsync()
    {
        var png = _shotPng;
        if (png is null) { ExitScreenshotMode(); return; }
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var w = new Windows.Storage.Streams.DataWriter(stream))
            {
                w.WriteBytes(png);
                await w.StoreAsync();
                w.DetachStream();
            }
            stream.Seek(0);
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            };
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            ExitScreenshotMode();
            ShowToast("Copied to clipboard");
        }
        catch (Exception ex) { ExitScreenshotMode(); await Notify("Copy failed", ex.Message); }
    }

    private async Task InsertShotAsync()
    {
        var png = _shotPng;
        if (png is null) { ExitScreenshotMode(); return; }
        ExitScreenshotMode();
        _image.PendingPng = png;
        await ShowImageGhostAsync(png);
        SelectTool(ToolKind.Image);
        ShowToast("Tap where to place it");
    }

    private async Task SaveShotAsync()
    {
        var png = _shotPng;
        if (png is null) { ExitScreenshotMode(); return; }
        try
        {
            var save = new FileSavePicker();
            save.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            save.SuggestedFileName = $"{_doc?.Info.Title ?? "screenshot"} capture";
            InitPicker(save);
            var file = await save.PickSaveFileAsync();
            if (file is null) return;   // keep the selection so the user can retry
            await File.WriteAllBytesAsync(file.Path, png);
            ExitScreenshotMode();
            ShowToast("Saved");
        }
        catch (Exception ex) { ExitScreenshotMode(); await Notify("Save failed", ex.Message); }
    }
}
