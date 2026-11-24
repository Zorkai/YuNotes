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
    // ─── Presentation mode ──────────────────────────────────────────────────────
    //
    // Full screen, all chrome hidden, the current page fitted to the screen and
    // the laser pointer picked. A dim floating bar switches laser / pen / hand,
    // steps pages and exits; the arrow keys, Page Up/Down and Space step pages,
    // and Esc or F5 exits. Leaving restores the tool, zoom and every panel.

    private bool _presenting;
    private ToolKind _toolBeforePresent;
    private double _zoomBeforePresent = 100;
    private readonly Dictionary<UIElement, Visibility> _hiddenForPresent = new();
    private readonly List<KeyboardAccelerator> _presentAccels = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _presentFitTimer;

    private void EnterPresentation()
    {
        if (_presenting || _doc is null || Canvas.PageCount == 0) return;
        if (ScreenshotOverlay.Visibility == Visibility.Visible) ExitScreenshotMode();
        _presenting = true;
        int page = Math.Max(0, Canvas.ActivePageIndex);
        _toolBeforePresent = _selected;
        _zoomBeforePresent = ZoomSlider.Value;

        Canvas.ClearSelection();
        UpdateSelectionUi();
        _hiddenForPresent.Clear();
        foreach (var el in new UIElement[] { TopBar, ToolbarHost, StatusBar, PagePanel, SearchPanel, SelectionActions })
        {
            _hiddenForPresent[el] = el.Visibility;
            el.Visibility = Visibility.Collapsed;
        }
        PresentBar.Opacity = 0.35;
        PresentBar.Visibility = Visibility.Visible;

        // Page-stepping keys exist only while presenting so they never steal
        // arrow keys from text editing or the canvas the rest of the time.
        foreach (var key in new[] { VirtualKey.Left, VirtualKey.Right, VirtualKey.Up, VirtualKey.Down,
                                    VirtualKey.PageUp, VirtualKey.PageDown, VirtualKey.Space })
        {
            var a = new KeyboardAccelerator { Key = key };
            a.Invoked += OnPresentAccelerator;
            _presentAccels.Add(a);
            KeyboardAccelerators.Add(a);
        }

        MainWindow.SetFullScreen(true);
        SelectTool(ToolKind.Laser);
        UpdatePresentPageLabel(page);
        FitPresentedPageSoon(page);
    }

    private void ExitPresentation()
    {
        if (!_presenting) return;
        _presenting = false;
        _presentFitTimer?.Stop();
        int page = Math.Max(0, Canvas.ActivePageIndex);

        foreach (var a in _presentAccels)
        {
            a.Invoked -= OnPresentAccelerator;
            KeyboardAccelerators.Remove(a);
        }
        _presentAccels.Clear();
        foreach (var (el, vis) in _hiddenForPresent) el.Visibility = vis;
        _hiddenForPresent.Clear();
        PresentBar.Visibility = Visibility.Collapsed;

        Canvas.ClearLaser();
        MainWindow.SetFullScreen(false);
        SelectTool(_toolBeforePresent);

        // Back to the zoom the user had, on the page they ended on.
        void Restore()
        {
            Canvas.SetZoom(_zoomBeforePresent / 100.0);
            Canvas.ScrollToPage(page);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Restore);
    }

    private void OnPresentAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_presenting) return;
        int delta = sender.Key is VirtualKey.Left or VirtualKey.Up or VirtualKey.PageUp ? -1 : +1;
        StepPresentationPage(delta);
        args.Handled = true;
    }

    private void StepPresentationPage(int delta)
    {
        if (!_presenting) return;
        int count = Canvas.PageCount;
        if (count == 0) return;
        int page = Math.Clamp(Math.Max(0, Canvas.ActivePageIndex) + delta, 0, count - 1);
        Canvas.ClearLaser();
        Canvas.FitPageToViewport(page);
        UpdatePresentPageLabel(page);
    }

    // Window resize to full screen lands a few layout passes later; fit once
    // right away and again once the new size has settled.
    private void FitPresentedPageSoon(int page)
    {
        void Fit() { if (_presenting) { Canvas.FitPageToViewport(page); UpdatePresentPageLabel(page); } }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Fit);
        if (_presentFitTimer is null)
        {
            _presentFitTimer = DispatcherQueue.CreateTimer();
            _presentFitTimer.IsRepeating = false;
            _presentFitTimer.Interval = TimeSpan.FromMilliseconds(300);
        }
        _presentFitTimer.Stop();
        _presentFitTimer.Tick -= OnPresentFitTick;
        _presentFitPage = page;
        _presentFitTimer.Tick += OnPresentFitTick;
        _presentFitTimer.Start();
    }

    private int _presentFitPage;
    private void OnPresentFitTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_presenting) return;
        Canvas.FitPageToViewport(_presentFitPage);
        UpdatePresentPageLabel(_presentFitPage);
    }

    private void UpdatePresentPageLabel(int? page = null)
    {
        int count = Canvas.PageCount;
        int idx = page ?? Canvas.ActivePageIndex;
        PresentPageLabel.Text = count == 0 ? "" : $"{Math.Clamp(idx, 0, count - 1) + 1} / {count}";
        PresentPrevBtn.IsEnabled = idx > 0;
        PresentNextBtn.IsEnabled = idx < count - 1;
    }
}
