using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using YuNotes.Controls;
using YuNotes.Input;
using YuNotes.Models;

namespace YuNotes.Views;

public sealed partial class SettingsPage
{

    // ─── Scroll suppression for the test areas ──────────────────────────────
    private readonly HashSet<uint> _suppressPointers = new();
    private bool _suppressing;
    private ScrollMode _savedHScroll;
    private ScrollMode _savedVScroll;
    private ZoomMode _savedZoom;

    private void BeginSuppress(uint id)
    {
        if (!_suppressPointers.Add(id)) return;
        if (_suppressing) return;
        _savedHScroll = RootScroller.HorizontalScrollMode;
        _savedVScroll = RootScroller.VerticalScrollMode;
        _savedZoom = RootScroller.ZoomMode;
        RootScroller.HorizontalScrollMode = ScrollMode.Disabled;
        RootScroller.VerticalScrollMode = ScrollMode.Disabled;
        RootScroller.ZoomMode = ZoomMode.Disabled;
        _suppressing = true;
    }

    private void EndSuppress(uint id)
    {
        if (!_suppressPointers.Remove(id)) return;
        if (_suppressPointers.Count > 0 || !_suppressing) return;
        RootScroller.HorizontalScrollMode = _savedHScroll;
        RootScroller.VerticalScrollMode = _savedVScroll;
        RootScroller.ZoomMode = _savedZoom;
        _suppressing = false;
    }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen) BeginSuppress(e.Pointer.PointerId);
    }

    private void OnPagePointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen) EndSuppress(e.Pointer.PointerId);
    }

    // ─── Pressure test area ─────────────────────────────────────────────────
    // Runs each sample through the same mapping as the editor, draws a circle
    // sized to the result, and moves the live dot on the curve editor.
    private void OnPressureSandbox(PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(PressureSandbox);
        if (!pt.IsInContact) return;

        var s = App.Services.Settings.Current;
        float raw = pt.Properties.Pressure;
        if (raw <= 0) raw = 0.5f;
        bool isPen = e.Pointer.PointerDeviceType == PointerDeviceType.Pen;

        float pressure;
        if (isPen && s.PressureEnabled)
        {
            if (!PressureCurve.TryMap(raw, s, out float input, out pressure))
            {
                PressureReadout.Text = $"{raw:0.00} · ignored (too light)";
                return;
            }
            CurveEditor.ShowLive(input);
            PressureReadout.Text = $"pen {raw:0.00} → width {pressure:0.00}";
        }
        else
        {
            pressure = isPen ? raw : 0.6f;
            PressureReadout.Text = isPen ? "Pressure is off" : "Use a pen to test pressure";
        }

        var radius = 3 + pressure * 16;
        byte a = (byte)(80 + pressure * 175);
        var accent = ((SolidColorBrush)Application.Current.Resources["AppAccentBrush"]).Color;
        var dot = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(a, accent.R, accent.G, accent.B)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(dot, pt.Position.X - radius);
        Canvas.SetTop(dot, pt.Position.Y - radius);
        PressureCanvas.Children.Add(dot);
        PressureHint.Visibility = Visibility.Collapsed;

        const int max = 1500;
        while (PressureCanvas.Children.Count > max)
            PressureCanvas.Children.RemoveAt(0);
    }

    // ─── Diagnostics ────────────────────────────────────────────────────────
    private void OnSandboxPressed(object sender, PointerRoutedEventArgs e)
    {
        // Disable outer-page pan/zoom before capturing so a second finger isn't
        // taken by DirectManipulation for pinch-zoom.
        BeginSuppress(e.Pointer.PointerId);
        Sandbox.CapturePointer(e.Pointer);
        TrackTouch(e, contact: true);
        OnSandbox(sender, e);
    }

    private void OnSandboxEnded(object sender, PointerRoutedEventArgs e)
    {
        EndSuppress(e.Pointer.PointerId);
        TrackTouch(e, contact: false);
        OnSandbox(sender, e);
    }

    private void OnSandbox(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(Sandbox);
        var props = pt.Properties;
        var kind = e.Pointer.PointerDeviceType;

        _log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {e.Pointer.PointerId} {kind} contact={pt.IsInContact} eraser={props.IsEraser} L={props.IsLeftButtonPressed} M={props.IsMiddleButtonPressed} R={props.IsRightButtonPressed} barrel={props.IsBarrelButtonPressed} P={props.Pressure:0.00}");
        LastDeviceText.Text = $"{kind} (id {e.Pointer.PointerId})";
        UpdateTouchDots(_activeTouchIds.Count);

        Highlight(IndMouseInUse, kind == PointerDeviceType.Mouse);
        Highlight(IndMouseL, kind == PointerDeviceType.Mouse && props.IsLeftButtonPressed);
        Highlight(IndMouseM, kind == PointerDeviceType.Mouse && props.IsMiddleButtonPressed);
        Highlight(IndMouseR, kind == PointerDeviceType.Mouse && props.IsRightButtonPressed);

        var isPen = kind == PointerDeviceType.Pen && !props.IsEraser;
        var isEraserTip = kind == PointerDeviceType.Pen && props.IsEraser;
        Highlight(IndPenHover, isPen && !pt.IsInContact);
        Highlight(IndPenTip, isPen && pt.IsInContact);
        Highlight(IndPenB1, isPen && props.IsBarrelButtonPressed);
        Highlight(IndEraserHover, isEraserTip && !pt.IsInContact);
        Highlight(IndEraserTip, isEraserTip && pt.IsInContact);
    }

    private static void Highlight(Border b, bool on)
    {
        b.Background = on
            ? (Brush)Application.Current.Resources["AppAccentBrush"]
            : (Brush)Application.Current.Resources["AppSurfaceBrush"];
        if (b.Child is TextBlock t)
            t.Foreground = on
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : (Brush)Application.Current.Resources["AppTextPrimaryBrush"];
    }

    private void TrackTouch(PointerRoutedEventArgs e, bool contact)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch) return;
        if (contact) _activeTouchIds.Add(e.Pointer.PointerId);
        else _activeTouchIds.Remove(e.Pointer.PointerId);
    }

    private void UpdateTouchDots(int active)
    {
        var on = (Brush)Application.Current.Resources["AppAccentBrush"];
        var off = (Brush)Application.Current.Resources["AppBorderBrush"];
        Touch1.Fill = active >= 1 ? on : off;
        Touch2.Fill = active >= 2 ? on : off;
        Touch3.Fill = active >= 3 ? on : off;
        Touch4.Fill = active >= 4 ? on : off;
        Touch5.Fill = active >= 5 ? on : off;
    }

    private async System.Threading.Tasks.Task ShowLogs()
    {
        var dlg = new ContentDialog
        {
            Title = "Diagnostics log",
            Content = new ScrollViewer
            {
                Height = 380,
                Content = new TextBlock { Text = _log.Length == 0 ? "No events yet." : _log.ToString(), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") }
            },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        await dlg.ShowAsync();
    }

    private async System.Threading.Tasks.Task ExportDebug()
    {
        try
        {
            var save = new FileSavePicker();
            save.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
            save.SuggestedFileName = $"YuNotes-debug-{DateTime.Now:yyyyMMdd-HHmmss}";
            WinRT.Interop.InitializeWithWindow.Initialize(save, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Of(this)));
            var file = await save.PickSaveFileAsync();
            if (file is null) return;
            var body = $"YuNotes debug info {DateTime.Now:O}\n\nPointer log:\n{_log}\n";
            File.WriteAllText(file.Path, body);
        }
        catch { }
    }

    private async System.Threading.Tasks.Task ShowCrashLog()
    {
        var path = App.CrashLogPath;
        if (!File.Exists(path))
        {
            var d = new ContentDialog
            {
                Title = "No crash log",
                Content = "No crashes have been recorded yet.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await d.ShowAsync();
            return;
        }

        var text = File.ReadAllText(path);
        var dlg = new ContentDialog
        {
            Title = "Crash log",
            Content = new ScrollViewer
            {
                Height = 400,
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                }
            },
            PrimaryButtonText = "Open folder",
            SecondaryButtonText = "Clear log",
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await Windows.System.Launcher.LaunchFolderPathAsync(System.IO.Path.GetDirectoryName(path)!);
        else if (result == ContentDialogResult.Secondary)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
