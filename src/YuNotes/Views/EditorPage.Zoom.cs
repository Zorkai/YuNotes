using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace YuNotes.Views;

// Zoom in the title bar's View menu (current zoom, presets, lock), its shortcuts,
// and remembering each note's zoom and position.
public sealed partial class EditorPage
{
    private void InitZoomMenu()
    {
        ZoomInItem.Click       += (_, __) => Canvas.ZoomStep(+1);
        ZoomOutItem.Click      += (_, __) => Canvas.ZoomStep(-1);
        ZoomActualItem.Click   += (_, __) => Canvas.SetZoom(1.0);
        ZoomFitWidthItem.Click += (_, __) => Canvas.FitWidth();
        ZoomFitPageItem.Click  += (_, __) => Canvas.FitPage();
        ZoomLockItem.Click     += (_, __) => SetZoomLocked(ZoomLockItem.IsChecked);

        Canvas.ZoomChanged += (_, factor) => ZoomLevelItem.Text = $"Zoom {factor * 100:0}%";
        SetZoomLocked(App.Services.Settings.Current.ZoomLocked, save: false);

        // Ctrl+= and Ctrl+- on the main keyboard and the number pad, Ctrl+0
        // for 100 %, Ctrl+9 fit width, Ctrl+8 fit page. (Ctrl+1–5 are pens.)
        void Add(VirtualKey key, Action action)
        {
            var a = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            a.Invoked += (_, e) =>
            {
                if (_presenting || ScreenshotOverlay.Visibility == Visibility.Visible) return;
                action();
                e.Handled = true;
            };
            KeyboardAccelerators.Add(a);
        }
        Add((VirtualKey)187, () => Canvas.ZoomStep(+1));   // = / +
        Add(VirtualKey.Add, () => Canvas.ZoomStep(+1));
        Add((VirtualKey)189, () => Canvas.ZoomStep(-1));   // -
        Add(VirtualKey.Subtract, () => Canvas.ZoomStep(-1));
        Add(VirtualKey.Number0, () => Canvas.SetZoom(1.0));
        Add(VirtualKey.NumberPad0, () => Canvas.SetZoom(1.0));
        Add(VirtualKey.Number9, () => Canvas.FitWidth());
        Add(VirtualKey.Number8, () => Canvas.FitPage());
    }

    // ── Title bar fit ────────────────────────────────────────────────────────
    // In a narrow window the labelled buttons (Share, View, Add, Study) drop their
    // text and keep their icons, so the right-hand cluster never slides under
    // the window's caption buttons and the title keeps some room.
    private const double TitleMinWidth = 220;

    private void InitTopBarFit()
    {
        TopBarGrid.SizeChanged += (_, __) => FitTopBar();
    }

    private void FitTopBar()
    {
        if (TopBarGrid.ActualWidth <= 0) return;
        SetTopBarCompact(false);
        var inf = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        var left = TopBarLeft;
        left.Measure(inf);
        ToolbarSecondary.Measure(inf);
        double available = TopBarGrid.ActualWidth - TopBarGrid.Padding.Left - TopBarGrid.Padding.Right;
        if (left.DesiredSize.Width + ToolbarSecondary.DesiredSize.Width + TitleMinWidth > available)
            SetTopBarCompact(true);
    }

    private void SetTopBarCompact(bool compact)
    {
        foreach (var btn in new[] { ShareBtn, ViewMenuBtn, AddMenuBtn, StudyBtn })
        {
            if (btn.Content is not StackPanel sp) continue;
            foreach (var label in sp.Children.OfType<TextBlock>())
                label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void SetZoomLocked(bool locked, bool save = true)
    {
        Canvas.ZoomLocked = locked;
        ZoomLockItem.IsChecked = locked;
        ZoomLockIcon.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        var tip = locked
            ? "Zoom is locked: pinch and Ctrl+scroll won't change it"
            : "Page sorter, side by side, zoom and present";
        ToolTipService.SetToolTip(ViewMenuBtn, tip);
        if (save && App.Services.Settings.Current.ZoomLocked != locked)
        {
            App.Services.Settings.Current.ZoomLocked = locked;
            App.Services.Settings.Save();
        }
    }

    // Remembers zoom and position for this note (on leaving it), so it
    // reopens exactly as it was.
    private void SaveView()
    {
        if (_doc is null || string.IsNullOrEmpty(_doc.Info.FilePath) || _presenting) return;
        if (Canvas.CaptureView() is not { } view) return;
        App.Services.Settings.Current.NoteViews[_doc.Info.FilePath] = view;
    }

    // On open: the saved view if there is one, else the last page.
    private bool RestoreView()
    {
        if (_doc is null || string.IsNullOrEmpty(_doc.Info.FilePath)) return false;
        if (!App.Services.Settings.Current.NoteViews.TryGetValue(_doc.Info.FilePath, out var view)) return false;
        if (view.Page < 0 || view.Page >= _doc.Pages.Count) return false;
        Canvas.RestoreViewDeferred(view);
        ZoomLevelItem.Text = $"Zoom {view.Zoom * 100:0}%";
        return true;
    }
}
