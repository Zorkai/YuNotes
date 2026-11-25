using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using YuNotes.Services;
using TextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace YuNotes.Views;

/// <summary>
/// "Your templates" row shared by the New note and Page template dialogs:
/// a card per custom template (right-click to rename or delete) and a card
/// that makes a new one from an image or a PDF page. Picking a card raises
/// Selected; the owning dialog clears this row's highlight when one of its
/// built-in templates is picked instead (ClearSelection).
/// </summary>
internal sealed class CustomTemplateRow
{
    private const double CardW = 80, CardH = 104;
    private readonly StackPanel _root = new() { Spacing = 6 };
    private readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly FrameworkElement _owner;
    private readonly Func<ContentDialog?> _hostDialog;
    private readonly Brush _accent = (Brush)Application.Current.Resources["AppAccentBrush"];
    private readonly Brush _border = (Brush)Application.Current.Resources["AppBorderBrush"];

    public CustomTemplate? Current { get; private set; }
    public event Action<CustomTemplate?>? Selected;
    public UIElement View => _root;

    // hostDialog: the open ContentDialog, which has to close while the file
    // picker and the name prompt show (one dialog at a time), then reopens.
    public CustomTemplateRow(FrameworkElement owner, Func<ContentDialog?> hostDialog, string? selectedId = null)
    {
        _owner = owner;
        _hostDialog = hostDialog;
        _root.Children.Add(new TextBlock { Text = "Your templates", FontSize = 13, Margin = new Thickness(0, 6, 0, 0) });
        _root.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _cards,
        });
        Current = selectedId is null ? null : TemplateLibrary.Get(selectedId);
        Rebuild();
    }

    public void ClearSelection()
    {
        Current = null;
        UpdateHighlight();
    }

    private void Rebuild()
    {
        _cards.Children.Clear();
        foreach (var t in TemplateLibrary.List())
            _cards.Children.Add(MakeCard(t));
        _cards.Children.Add(MakeAddCard());
        UpdateHighlight();
    }

    private Button MakeCard(CustomTemplate t)
    {
        var img = new Image { Stretch = Stretch.Uniform, Width = CardW, Height = CardH };
        try { img.Source = new BitmapImage(new Uri(t.ImagePath)) { DecodePixelWidth = 160 }; } catch { }
        var stack = new StackPanel();
        stack.Children.Add(new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.White), Child = img });
        stack.Children.Add(new TextBlock
        {
            Text = t.Name, FontSize = 12, MaxWidth = CardW, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
        });
        var btn = MakeButton(stack, t);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"{t.Name} template");
        ToolTipService.SetToolTip(btn, $"{t.Name} (right-click to rename or delete)");
        btn.Click += (_, __) => { Current = t; UpdateHighlight(); Selected?.Invoke(t); };

        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename…" };
        rename.Click += (_, __) => Rename(t);
        var delete = new MenuFlyoutItem { Text = "Delete" };
        delete.Click += (_, __) =>
        {
            TemplateLibrary.Delete(t.Id);
            if (Current?.Id == t.Id) { Current = null; Selected?.Invoke(null); }
            Rebuild();
        };
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        btn.ContextFlyout = menu;
        return btn;
    }

    private Button MakeAddCard()
    {
        var stack = new StackPanel { Width = CardW, Height = CardH + 22, VerticalAlignment = VerticalAlignment.Center };
        var inner = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 28, 0, 0) };
        inner.Children.Add(new FontIcon { Glyph = "", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center });
        inner.Children.Add(new TextBlock
        {
            Text = "From image or PDF", FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
        });
        stack.Children.Add(inner);
        var btn = MakeButton(stack, null);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, "New template from an image or PDF");
        ToolTipService.SetToolTip(btn, "Make a template from an image or the first page of a PDF");
        btn.Click += (_, __) =>
        {
            // The picker and name prompt need the host dialog out of the way
            // (one ContentDialog at a time); its owner sees PendingAdd after
            // ShowAsync returns, runs RunPendingAddAsync and shows it again.
            PendingAdd = true;
            _hostDialog()?.Hide();
        };
        return btn;
    }

    private Button MakeButton(UIElement content, CustomTemplate? tag) => new()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        Tag = tag,
        Content = new Border
        {
            BorderThickness = new Thickness(1), BorderBrush = _border, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6), Background = (Brush)Application.Current.Resources["AppSurfaceBrush"],
            Child = content,
        },
    };

    private void UpdateHighlight()
    {
        foreach (var b in _cards.Children.OfType<Button>())
        {
            if (b.Content is not Border border) continue;
            bool match = b.Tag is CustomTemplate t && t.Id == Current?.Id;
            border.BorderThickness = new Thickness(match ? 2.5 : 1);
            border.BorderBrush = match ? _accent : _border;
        }
    }

    /// <summary>Shows a dialog that contains this row, reopening it after the
    /// row closed it to add a template. Returns the dialog's final result.</summary>
    public async Task<ContentDialogResult> ShowHostAsync(ContentDialog dialog)
    {
        while (true)
        {
            var result = await dialog.ShowAsync();
            if (!PendingAdd) return result;
            await RunPendingAddAsync();
        }
    }

    // Set when the host dialog was closed to add a template; the dialog's
    // owner runs RunPendingAddAsync and then shows the dialog again.
    public bool PendingAdd { get; private set; }

    public async Task RunPendingAddAsync()
    {
        PendingAdd = false;
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".pdf" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Of(_owner)));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var box = new TextBox { Text = Path.GetFileNameWithoutExtension(file.Path), Width = 300 };
        var ask = new ContentDialog
        {
            Title = "Name this template",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _owner.XamlRoot,
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var path = file.Path;
            var name = box.Text;
            var t = await Task.Run(() => TemplateLibrary.Add(path, name));
            Current = t;
            Rebuild();
            Selected?.Invoke(t);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "TemplateLibrary.Add");
            var err = new ContentDialog
            {
                Title = "Couldn't make a template",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = _owner.XamlRoot,
            };
            await err.ShowAsync();
        }
    }

    private void Rename(CustomTemplate t)
    {
        // No second dialog while the host is open, so rename in a flyout.
        var box = new TextBox { Text = t.Name, Width = 220 };
        var ok = new Button { Content = "Rename", Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var flyout = new Flyout { Content = panel };
        ok.Click += (_, __) =>
        {
            TemplateLibrary.Rename(t.Id, box.Text);
            flyout.Hide();
            Rebuild();
        };
        var anchor = _cards.Children.OfType<Button>().FirstOrDefault(b => b.Tag is CustomTemplate x && x.Id == t.Id);
        if (anchor is not null) flyout.ShowAt(anchor);
    }
}
