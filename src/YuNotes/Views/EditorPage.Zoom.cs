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
    // As the window narrows the bar steps down one level at a time until it
    // fits, so nothing slides under the window's caption buttons:
    //   0 full · 1 Share/View/Add/Study drop their text · 2 Find and
    //   Screenshot fold into the View menu · 3 Redo hides (Ctrl+Y still works)
    // The title gets less guaranteed room at each level and trims with "…".
    private static readonly double[] TitleMinWidthAt = { 220, 220, 120, 0 };
    private bool _screenshotEnabled = true;   // Settings can hide the screenshot tool

    private void InitTopBarFit()
    {
        TopBarGrid.SizeChanged += (_, __) => FitTopBar();
        FindItem.Click += (_, __) => OpenSearch();
        ScreenshotItem.Click += (_, __) => EnterScreenshotMode();
    }

    private void FitTopBar()
    {
        if (TopBarGrid.ActualWidth <= 0) return;
        var inf = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        double available = TopBarGrid.ActualWidth - TopBarGrid.Padding.Left - TopBarGrid.Padding.Right;
        for (int level = 0; level < TitleMinWidthAt.Length; level++)
        {
            ApplyTopBarLevel(level);
            TopBarLeft.Measure(inf);
            ToolbarSecondary.Measure(inf);
            if (TopBarLeft.DesiredSize.Width + ToolbarSecondary.DesiredSize.Width + TitleMinWidthAt[level] <= available)
                break;
        }
    }

    private void ApplyTopBarLevel(int level)
    {
        foreach (var btn in new[] { ShareBtn, ViewMenuBtn, AddMenuBtn, StudyBtn })
        {
            if (btn.Content is not StackPanel sp) continue;
            foreach (var label in sp.Children.OfType<TextBlock>())
                label.Visibility = level >= 1 ? Visibility.Collapsed : Visibility.Visible;
        }

        bool fold = level >= 2;
        SearchBtn.Visibility = RightToolsDivider.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
        ScreenshotBtn.Visibility = !fold && _screenshotEnabled ? Visibility.Visible : Visibility.Collapsed;
        FindItem.Visibility = FoldedToolsSep.Visibility = fold ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotItem.Visibility = fold && _screenshotEnabled ? Visibility.Visible : Visibility.Collapsed;

        RedoBtn.Visibility = level >= 3 ? Visibility.Collapsed : Visibility.Visible;
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
