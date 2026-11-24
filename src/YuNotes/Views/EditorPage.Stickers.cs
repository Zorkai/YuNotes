using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StickerLibrary = YuNotes.Services.StickerLibrary;
using StickerInfo = YuNotes.Services.StickerInfo;

namespace YuNotes.Views;

public sealed partial class EditorPage
{
    // ─── Stickers ───────────────────────────────────────────────────────────────
    // Saved clips shared across notes. Lasso something (a diagram, a stamp, a
    // signature) and choose "Save as sticker" in the selection bar; the
    // Stickers button in the top bar lists them, and tapping one drops a fresh,
    // editable copy onto the page in view. Stored by Services.StickerLibrary.

    private void InitStickers()
    {
        SelSaveStickerBtn.Click += async (_, __) => await SaveSelectionAsStickerAsync();
        AreaSaveStickerBtn.Click += async (_, __) => await SaveAreaAsStickerAsync();
        AreaCloseBtn.Click += (_, __) => { Canvas.ClearSelectionArea(); UpdateSelectionUi(); };
        StickersFlyout.Opening += (_, __) => RebuildStickersFlyout();
    }

    // Selected ink, shapes, text or images. On an imported PDF page the dialog
    // also asks whether to keep the PDF behind them (added as a picture under
    // the ink, which stays editable).
    private async Task SaveSelectionAsStickerAsync()
    {
        if (_doc is null || !Canvas.HasSelection) return;
        bool onPdf = Canvas.SelectionIsOnPdfPage;
        var includeBg = new CheckBox
        {
            Content = "Include the PDF behind it (the whole area you selected)",
            IsChecked = false,
            Visibility = onPdf ? Visibility.Visible : Visibility.Collapsed,
        };
        // The preview shows exactly what will be saved and follows the checkbox.
        var preview = new Image { Stretch = Stretch.Uniform, MaxHeight = 220 };
        void Refresh() => SetPreview(preview, Canvas.RenderStickerPreview(true, onPdf && includeBg.IsChecked == true));
        includeBg.Checked += (_, __) => Refresh();
        includeBg.Unchecked += (_, __) => Refresh();
        Refresh();
        var name = await AskStickerNameAsync(preview, includeBg);
        if (name is null) return;
        bool withBg = onPdf && includeBg.IsChecked == true;
        var thumb = Canvas.RenderStickerPreview(true, withBg);
        var content = Canvas.CopySelectionAsSticker(includeBackground: withBg);
        if (content is null) { await Notify("Nothing to save", "Select some ink, shapes, text or images first."); return; }
        await StoreStickerAsync(name, content, thumb);
    }

    private static void SetPreview(Image img, byte[]? png)
    {
        if (png is null) { img.Source = null; return; }
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(png);
            bmp.SetSource(ms.AsRandomAccessStream());
            img.Source = bmp;
        }
        catch { img.Source = null; }
    }

    // A rectangle/lasso over an imported PDF with no ink in it: that piece of
    // the PDF, cut to the lasso's shape.
    private async Task SaveAreaAsStickerAsync()
    {
        if (_doc is null || !Canvas.HasStickerArea) return;
        var content = Canvas.CopySelectionAreaAsSticker(out var png);
        if (content is null) { await Notify("Nothing to save", "That area is too small or off the page."); return; }
        var preview = new Image { Stretch = Stretch.Uniform, MaxHeight = 220 };
        SetPreview(preview, Canvas.RenderStickerPreview(false, true));
        var name = await AskStickerNameAsync(preview, null);
        if (name is null) return;
        await StoreStickerAsync(name, content, png);
        Canvas.ClearSelectionArea();
        UpdateSelectionUi();
    }

    // Null = cancelled. Empty input gets a numbered default name.
    private async Task<string?> AskStickerNameAsync(Image? preview, UIElement? extra)
    {
        var box = new TextBox { Header = "Name", PlaceholderText = "Sticker", MaxLength = 60 };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        if (preview is not null)
        {
            // White card with a faint border, so a transparent cut-out reads clearly.
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Child = preview,
            });
        }
        panel.Children.Add(box);
        if (extra is not null) panel.Children.Add(extra);
        panel.Children.Add(new TextBlock
        {
            Text = "Stickers are available in every note, from the Stickers button in the top bar.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
        });
        var dlg = new ContentDialog
        {
            Title = "Save as sticker",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        box.Loaded += (_, __) => box.Focus(FocusState.Programmatic);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        var name = (box.Text ?? "").Trim();
        return name.Length > 0 ? name : $"Sticker {StickerLibrary.List().Count + 1}";
    }

    private async Task StoreStickerAsync(string name, YuNotes.Services.StickerContent content, byte[]? thumb)
    {
        try
        {
            await Task.Run(() => StickerLibrary.Add(name, content, thumb));
            ShowToast($"Saved sticker “{name}”");
        }
        catch (Exception ex) { await Notify("Couldn't save the sticker", ex.Message); }
    }

    // The area bar sits just above the covered area (below it near the top).
    private void PositionAreaActions()
    {
        if (!Canvas.TryGetAreaScreenBounds(RootGrid, out var area)) return;
        AreaActions.UpdateLayout();
        double bw = AreaActions.ActualWidth, bh = AreaActions.ActualHeight;
        if (bw <= 0 || bh <= 0) return;
        const double gap = 10, edge = 8;
        double areaLeft = ColLeft.ActualWidth, areaTop = RowTop.ActualHeight;
        double areaRight = areaLeft + Canvas.ActualWidth, areaBottom = areaTop + Canvas.ActualHeight;
        double left = area.X + area.Width / 2 - bw / 2;
        double top = area.Y - bh - gap;
        if (top < areaTop + edge) top = area.Y + area.Height + gap;
        left = Math.Clamp(left, areaLeft + edge, Math.Max(areaLeft + edge, areaRight - bw - edge));
        top = Math.Clamp(top, areaTop + edge, Math.Max(areaTop + edge, areaBottom - bh - edge));
        AreaActions.Margin = new Thickness(left, top - areaTop, 0, 0);
    }

    private void RebuildStickersFlyout()
    {
        StickersGrid.Children.Clear();
        var stickers = StickerLibrary.List();
        StickersEmptyText.Visibility = stickers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StickersHintText.Visibility = stickers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var res = Application.Current.Resources;
        foreach (var info in stickers)
        {
            var image = new Image { Stretch = Stretch.Uniform, Height = 70 };
            if (File.Exists(info.ThumbnailPath))
            {
                try
                {
                    // Load from bytes so the file isn't locked (rename/delete stay possible).
                    var bmp = new BitmapImage();
                    using var ms = new MemoryStream(File.ReadAllBytes(info.ThumbnailPath));
                    bmp.SetSource(ms.AsRandomAccessStream());
                    image.Source = bmp;
                }
                catch { }
            }
            var face = new StackPanel { Spacing = 4 };
            face.Children.Add(new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = image,
            });
            face.Children.Add(new TextBlock
            {
                Text = info.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 92,
            });
            var btn = new Button
            {
                Content = face, Width = 102, Height = 106, Padding = new Thickness(4),
                CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(btn, $"Add “{info.Name}” to this page");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"Sticker {info.Name}");
            var sticker = info;
            btn.Click += async (_, __) => await InsertStickerAsync(sticker);

            var menu = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = "Rename", Icon = new FontIcon { Glyph = "", FontFamily = (FontFamily)res["MaterialIconsFamily"] } };
            rename.Click += async (_, __) => await RenameStickerAsync(sticker);
            var delete = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "", FontFamily = (FontFamily)res["MaterialIconsFamily"] } };
            delete.Click += async (_, __) => await DeleteStickerAsync(sticker);
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            btn.ContextFlyout = menu;

            StickersGrid.Children.Add(btn);
        }
    }

    private async Task InsertStickerAsync(StickerInfo info)
    {
        StickersFlyout.Hide();
        if (_doc is null || Canvas.Context.EditingSuspended) return;
        var content = await Task.Run(() => StickerLibrary.Load(info.Id));
        if (content is null || content.IsEmpty)
        {
            await Notify("Couldn't add the sticker", "Its file is missing or damaged.");
            return;
        }
        Canvas.ClearSelection();
        if (Canvas.InsertSticker(content)) UpdateSelectionUi();
    }

    private async Task RenameStickerAsync(StickerInfo info)
    {
        StickersFlyout.Hide();
        var box = new TextBox { Text = info.Name, MaxLength = 60 };
        var dlg = new ContentDialog
        {
            Title = "Rename sticker", Content = box,
            PrimaryButtonText = "Rename", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
        };
        box.Loaded += (_, __) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var name = (box.Text ?? "").Trim();
        if (name.Length > 0 && name != info.Name) StickerLibrary.Rename(info.Id, name);
    }

    private async Task DeleteStickerAsync(StickerInfo info)
    {
        StickersFlyout.Hide();
        var dlg = new ContentDialog
        {
            Title = $"Delete sticker “{info.Name}”?",
            Content = "It's removed from the Stickers list. Copies already placed in notes stay.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        StickerLibrary.Delete(info.Id);
    }
}
