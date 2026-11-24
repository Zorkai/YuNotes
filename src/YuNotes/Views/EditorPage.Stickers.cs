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
        StickersFlyout.Opening += (_, __) => RebuildStickersFlyout();
    }

    private async Task SaveSelectionAsStickerAsync()
    {
        if (_doc is null || !Canvas.HasSelection) return;
        var content = Canvas.CopySelectionAsSticker();
        if (content is null) { await Notify("Nothing to save", "Select some ink, shapes, text or images first."); return; }
        // Thumbnail first: it renders the selection as it looks on the page.
        var thumb = Canvas.RenderSelectionRegionToPng();

        var box = new TextBox
        {
            Header = "Name",
            PlaceholderText = "Sticker",
            Text = "",
            MaxLength = 60,
        };
        var dlg = new ContentDialog
        {
            Title = "Save as sticker",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    box,
                    new TextBlock
                    {
                        Text = "Stickers are available in every note, from the Stickers button in the top bar.",
                        TextWrapping = TextWrapping.Wrap, FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
                    },
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        box.Loaded += (_, __) => box.Focus(FocusState.Programmatic);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var name = (box.Text ?? "").Trim();
        if (name.Length == 0) name = $"Sticker {StickerLibrary.List().Count + 1}";
        try
        {
            await Task.Run(() => StickerLibrary.Add(name, content, thumb));
            ShowToast($"Saved sticker “{name}”");
        }
        catch (Exception ex) { await Notify("Couldn't save the sticker", ex.Message); }
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
