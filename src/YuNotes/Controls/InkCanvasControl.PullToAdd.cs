using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Controls;

public sealed partial class InkCanvasControl
{

    // ── Pull to add page ────────────────────────────────────────────────────────
    //
    // Scrolling on past the end of the document fills a ring around the "+"
    // button; letting go once it's full adds a page. Implemented as real scroll
    // space (the "pull zone") below the button, so it works the same for mouse
    // wheel, precision touchpad, touch and the scrollbar: how far the view is
    // scrolled into the zone IS the progress.
    //
    //  • Touchpad / touch: only finger-driven movement fills the ring. Pushing
    //    past the end with fingers down always counts — wherever the swipe
    //    started, even if it began mid-fling — so it never "refuses" to fill.
    //    Momentum alone never fills: a fling that reaches the end stops there.
    //  • Mouse wheel: notches that arrive once the view is at the end fill the
    //    ring; a fast spin that reaches the end from above stops at the end.
    //  • Letting go: lifting fingers (the moment momentum takes over, or the
    //    manipulation ends), or pausing the wheel briefly. At 100% a page is
    //    added; below, the ring drains and the view springs back right away.

    // On-screen distance (DIPs) to push past the end for 100%.
    private const double PullDistance = 240;
    private const double PullFullThreshold = 0.995;

    private Grid? _pullZone;
    private Grid? _addBtnWrapper;
    private bool _addPagesAllowed = true;

    // False hides the "+" under the last page and the pull-to-add scroll space,
    // so scrolling stops at the end of the document (presentation mode).
    public bool AddPagesAllowed
    {
        get => _addPagesAllowed;
        set { _addPagesAllowed = value; ApplyAddPagesAllowed(); }
    }

    private void ApplyAddPagesAllowed()
    {
        var vis = _addPagesAllowed ? Visibility.Visible : Visibility.Collapsed;
        if (_addBtnWrapper is not null) _addBtnWrapper.Visibility = vis;
        if (_pullZone is not null) _pullZone.Visibility = vis;
        if (!_addPagesAllowed)
        {
            _pullArmed = false;
            _pullWheelCarry = false;
            _pullReleaseTimer?.Stop();
            SetPullProgress(0);
        }
    }

    // The pull gesture does nothing while there is no pull zone or adding
    // pages is switched off.
    private bool PullDisabled => _pullZone is null || !_addPagesAllowed;
    private Grid? _pullRingHost;
    private Microsoft.UI.Xaml.Shapes.Ellipse? _pullTrack;
    private Microsoft.UI.Xaml.Shapes.Ellipse? _pullFull;
    private Microsoft.UI.Xaml.Shapes.Path? _pullArc;
    private Button? _addBtn;
    private FontIcon? _addIcon;
    private TextBlock? _pullLabel;

    private bool _pullArmed;          // the current push may add a page
    private bool _pullInManipulation; // DirectManipulation (touch/touchpad) active
    private bool _pullViewMoving;     // last ViewChanged was intermediate
    private bool _pullSnappingBack;   // our own spring-back animation is running
    private DateTime _pullSnapAt;
    // Spring-back in flight. Time-boxed so a ChangeView that never reports back
    // (e.g. swallowed mid-manipulation) can't wedge the gesture.
    private bool PullSnapping =>
        _pullSnappingBack && (DateTime.UtcNow - _pullSnapAt).TotalMilliseconds < 1000;
    private bool _pullInertiaSeen;    // already handled "let go" for this manipulation
    private bool _pullWheelCarry;     // a wheel scroll began above the end: stop there
    private bool _pullCommitting;
    private DateTime _pullLastWheel = DateTime.MinValue;
    private double _pullProgress;
    private double _pullZoneZoom = -1;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pullReleaseTimer;
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush PullWhite = new(Microsoft.UI.Colors.White);

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private bool PullWheelRecent => (DateTime.UtcNow - _pullLastWheel).TotalMilliseconds < 600;

    private double PullProgressAt(double offset) =>
        Math.Clamp((offset - PullRestOffset()) / PullDistance, 0, 1);

    private void BuildAddPageControls(double stdW)
    {
        const double ring = 64, track = 58;

        _addIcon = new FontIcon
        {
            Glyph = "", FontSize = 20,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform(),
        };
        _addBtn = new Button
        {
            Width = 52, Height = 52,
            CornerRadius = new CornerRadius(26),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Res("AppSurfaceVariantBrush"),
            BorderBrush = Res("AppBorderBrush"),
            BorderThickness = new Thickness(1.5),
            Content = _addIcon,
            // Little "pop" when the ring completes.
            CenterPoint = new System.Numerics.Vector3(26, 26, 0),
            ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) },
        };
        ToolTipService.SetToolTip(_addBtn, "Add page (or keep scrolling down past the end)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_addBtn, "Add page (or keep scrolling down past the end)");
        _addBtn.Click += (_, __) => AddPageRequested?.Invoke(this, EventArgs.Empty);

        _pullTrack = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = track, Height = track,
            Stroke = Res("AppBorderBrush"), StrokeThickness = 3,
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
        };
        _pullArc = new Microsoft.UI.Xaml.Shapes.Path
        {
            Stroke = Res("AppAccentBrush"), StrokeThickness = 3,
            StrokeStartLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
            StrokeEndLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
            Visibility = Visibility.Collapsed,
        };
        _pullFull = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = track, Height = track,
            Stroke = Res("AppAccentBrush"), StrokeThickness = 3,
            Visibility = Visibility.Collapsed,
        };
        _pullRingHost = new Grid
        {
            Width = ring, Height = ring,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = SeamlessPages ? new Thickness(0, 2, 0, 0) : new Thickness(0, 2, 0, 18),
        };
        _pullRingHost.Children.Add(_pullTrack);
        _pullRingHost.Children.Add(_pullArc);
        _pullRingHost.Children.Add(_pullFull);
        _pullRingHost.Children.Add(_addBtn);

        var addBtnWrapper = new Grid { Width = stdW, HorizontalAlignment = HorizontalAlignment.Left };
        addBtnWrapper.Children.Add(_pullRingHost);
        PagesPanel.Children.Add(addBtnWrapper);
        _addBtnWrapper = addBtnWrapper;

        // The pull zone: empty scroll space revealed as the user pushes past
        // the end. Its hint label sits at the top, right under the button.
        _pullLabel = new TextBlock
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Res("AppTextSecondaryBrush"),
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
            IsHitTestVisible = false,
        };
        _pullZone = new Grid
        {
            Width = stdW,
            Height = PullDistance,   // corrected by UpdatePullZoneHeight once laid out
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _pullZone.Children.Add(_pullLabel);
        PagesPanel.Children.Add(_pullZone);
        ApplyAddPagesAllowed();

        _pullArmed = false;
        _pullWheelCarry = false;
        _pullSnappingBack = false;
        _pullZoneZoom = -1;
        _pullProgress = 0;
    }

    private void HookPullToAddPage()
    {
        Scroller.ViewChanging += OnPullViewChanging;
        Scroller.DirectManipulationStarted += (_, __) =>
        {
            // Fingers down: the user takes over from any spring-back. Arming
            // happens in OnPullViewChanging, from finger-driven movement only.
            _pullInManipulation = true;
            _pullInertiaSeen = false;
            _pullSnappingBack = false;
            _pullReleaseTimer?.Stop();
        };
        Scroller.DirectManipulationCompleted += (_, __) =>
        {
            _pullInManipulation = false;
            SchedulePullRelease();
        };
        Scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPullWheel), handledEventsToo: true);
        Scroller.SizeChanged += (_, __) => UpdatePullZoneHeight();
        PagesPanel.SizeChanged += (_, __) => UpdatePullZoneHeight();
    }

    // Scroll offset at which the end of the document (just above the pull zone)
    // meets the bottom of the viewport — where the view rests.
    private double PullRestOffset()
    {
        if (PullDisabled) return double.MaxValue;
        try
        {
            double top = _pullZone.TransformToVisual(ContentRoot).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            return Math.Max(0, top * Scroller.ZoomFactor - Scroller.ViewportHeight);
        }
        catch { return double.MaxValue; }
    }

    // Size the zone so the farthest the view can scroll is exactly 100%,
    // whatever the zoom and even when the note is shorter than the window.
    private void UpdatePullZoneHeight()
    {
        if (_pullZone is null) return;
        double zoom = Scroller.ZoomFactor <= 0 ? 1 : Scroller.ZoomFactor;
        double top;
        try { top = _pullZone.TransformToVisual(ContentRoot).TransformPoint(new Windows.Foundation.Point(0, 0)).Y; }
        catch { return; }
        double v = Scroller.ViewportHeight;
        double h = Math.Max(0, (PullDistance + Math.Max(v, top * zoom)) / zoom - top - ContentRoot.Padding.Bottom);
        if (double.IsNaN(_pullZone.Height) || Math.Abs(_pullZone.Height - h) > 0.5)
            _pullZone.Height = h;
        _pullZoneZoom = zoom;
    }

    private void OnPullWheel(object sender, PointerRoutedEventArgs e)
    {
        if (PullDisabled) return;
        var props = e.GetCurrentPoint(Scroller).Properties;
        // Ctrl = zoom, Shift / tilt = horizontal scroll.
        if (props.IsHorizontalMouseWheel ||
            (e.KeyModifiers & (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift)) != 0)
            return;
        _pullLastWheel = DateTime.UtcNow;
        _pullSnappingBack = false;
        if (props.MouseWheelDelta < 0)   // scrolling down
        {
            if (Scroller.VerticalOffset >= PullRestOffset() - 1) { _pullArmed = true; _pullWheelCarry = false; }
            else if (!_pullArmed) _pullWheelCarry = true;
        }
        // Even if the view can't move (already at 100%), the pause after the
        // last notch is the "let go".
        SchedulePullRelease();
    }

    private void OnPullViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        if (PullDisabled || _pullCommitting) return;
        double rest = PullRestOffset();
        double next = e.NextView.VerticalOffset;
        bool inZone = next > rest + 0.5;

        if (inZone && !PullSnapping && _pullWheelCarry && !_pullArmed)
        {
            // A wheel scroll that began above the end stops AT the end.
            _pullWheelCarry = false;
            DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, PullRestOffset(), null, disableAnimation: true));
            return;
        }

        if (_pullInManipulation && !PullWheelRecent)
        {
            if (!e.IsInertial)
            {
                // Fingers on the touchpad / screen. Pushing past the end with
                // them IS insisting — arm, wherever this swipe started.
                _pullInertiaSeen = false;
                // (Not while our spring-back animation drives the view — a real
                // new touch clears that flag in DirectManipulationStarted.)
                if (inZone && !PullSnapping) _pullArmed = true;
            }
            else if (!_pullInertiaSeen)
            {
                // Fingers just lifted and momentum took over: "letting go".
                _pullInertiaSeen = true;
                if (_pullArmed && _pullProgress >= PullFullThreshold) { CommitPull(); return; }
                _pullArmed = false;
                if (inZone || _pullProgress > 0) { SpringBack(); return; }
            }
            else if (inZone && !_pullArmed && !PullSnapping)
            {
                // A fling's momentum reached the end: stop there instead of
                // sailing on into the empty pull zone.
                SpringBack();
                return;
            }
        }

        SetPullProgress(Math.Clamp((next - rest) / PullDistance, 0, 1));
    }

    // Drain the ring and glide back to the end of the document, interrupting
    // any momentum still carrying the view into the pull zone.
    private void SpringBack()
    {
        _pullArmed = false;
        _pullWheelCarry = false;
        _pullSnappingBack = true;
        _pullSnapAt = DateTime.UtcNow;
        SetPullProgress(PullProgressAt(Scroller.VerticalOffset));
        DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, PullRestOffset(), null, disableAnimation: false));
    }

    // Called from OnScrollerViewChanged.
    private void OnPullViewChanged(bool isIntermediate)
    {
        _pullViewMoving = isIntermediate;
        if (isIntermediate) return;
        if (Math.Abs(Scroller.ZoomFactor - _pullZoneZoom) > 0.0005) UpdatePullZoneHeight();
        if (_pullSnappingBack)
        {
            // Spring-back finished (or was cut short). If the view still isn't
            // at the end, the release check below sends it back again.
            _pullSnappingBack = false;
            SetPullProgress(PullProgressAt(Scroller.VerticalOffset));
        }
        if (!_pullInManipulation) SchedulePullRelease();
    }

    private void SchedulePullRelease()
    {
        if (PullDisabled) return;
        if (_pullReleaseTimer is null)
        {
            _pullReleaseTimer = DispatcherQueue.CreateTimer();
            _pullReleaseTimer.IsRepeating = false;
            _pullReleaseTimer.Tick += (_, __) => EvaluatePullRelease();
        }
        _pullReleaseTimer.Stop();
        // A wheel has no "release": a short pause after the last notch is it.
        _pullReleaseTimer.Interval = TimeSpan.FromMilliseconds(PullWheelRecent ? 420 : 40);
        _pullReleaseTimer.Start();
    }

    private void EvaluatePullRelease()
    {
        if (PullDisabled || _pullCommitting || _pullInManipulation || PullSnapping) return;
        if (_pullViewMoving) return;   // the final ViewChanged reschedules us
        double rest = PullRestOffset();
        double p = PullProgressAt(Scroller.VerticalOffset);
        if (_pullArmed && p >= PullFullThreshold) { CommitPull(); return; }

        _pullArmed = false;
        _pullWheelCarry = false;
        // Not far enough: drain the ring and spring back to the end.
        if (Scroller.VerticalOffset > rest + 0.5) SpringBack();
        else SetPullProgress(0);
    }

    private void CommitPull()
    {
        if (_pullCommitting) return;
        _pullCommitting = true;
        _pullArmed = false;
        _pullWheelCarry = false;
        _pullReleaseTimer?.Stop();
        // Rebuild replaces these controls; defer out of the scroll event.
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                AddPageRequested?.Invoke(this, EventArgs.Empty);
                // Glide to the new page so it's clearly there.
                ScrollToPageDeferred(_pageCanvases.Count - 1);
            }
            finally { _pullCommitting = false; }
        });
    }

    private void SetPullProgress(double p)
    {
        _pullProgress = p;
        if (_pullRingHost is null || _pullTrack is null || _pullArc is null || _pullFull is null
            || _addBtn is null || _addIcon is null || _pullLabel is null) return;

        // Only a real (finger/wheel) push shows the ring; momentum bouncing off
        // the end stays quiet. While springing back the ring just drains.
        bool active = p > 0.001 && (_pullArmed || PullSnapping);
        bool full = _pullArmed && p >= PullFullThreshold;

        _pullTrack.Opacity = active ? 1 : 0;
        if (active && !full)
        {
            _pullArc.Data = PullArcGeometry(p, 32, 29);
            _pullArc.Visibility = Visibility.Visible;
        }
        else _pullArc.Visibility = Visibility.Collapsed;
        _pullFull.Visibility = full ? Visibility.Visible : Visibility.Collapsed;

        // The "+" turns with the pull and lights up when it's ready.
        if (_addIcon.RenderTransform is Microsoft.UI.Xaml.Media.RotateTransform rt)
            rt.Angle = active ? p * 90 : 0;
        _addBtn.Scale = full ? new System.Numerics.Vector3(1.12f, 1.12f, 1) : System.Numerics.Vector3.One;
        _addBtn.Background = Res(full ? "AppAccentBrush" : "AppSurfaceVariantBrush");
        if (full) _addIcon.Foreground = PullWhite;
        else _addIcon.ClearValue(IconElement.ForegroundProperty);

        if (!active || !_pullArmed || PullSnapping)
        {
            _pullLabel.Opacity = 0;
            return;
        }
        _pullLabel.Text = full ? "Let go to add a page" : "Keep scrolling to add a page";
        _pullLabel.Foreground = Res(full ? "AppAccentBrush" : "AppTextSecondaryBrush");
        _pullLabel.Opacity = 1;
    }

    // Clockwise arc from 12 o'clock covering fraction p of the circle.
    private static Microsoft.UI.Xaml.Media.Geometry PullArcGeometry(double p, double c, double r)
    {
        double a = p * 2 * Math.PI;
        var fig = new Microsoft.UI.Xaml.Media.PathFigure
        {
            StartPoint = new Windows.Foundation.Point(c, c - r),
            IsClosed = false,
        };
        fig.Segments.Add(new Microsoft.UI.Xaml.Media.ArcSegment
        {
            Point = new Windows.Foundation.Point(c + r * Math.Sin(a), c - r * Math.Cos(a)),
            Size = new Windows.Foundation.Size(r, r),
            IsLargeArc = a > Math.PI,
            SweepDirection = Microsoft.UI.Xaml.Media.SweepDirection.Clockwise,
        });
        var g = new Microsoft.UI.Xaml.Media.PathGeometry();
        g.Figures.Add(fig);
        return g;
    }
}
