using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.Text;
using Colors = Microsoft.UI.Colors;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Controls;

public sealed partial class PageCanvas
{

    // ── Laser pointer ─────────────────────────────────────────────────────────
    //
    // Each press draws a glowing trail into the XAML overlay (never the
    // document). Trails stay while the presenter keeps pointing; a short pause
    // after the last lift fades them all out together. Pressing again during
    // the pause or the fade brings the earlier trails back, so a circle and an
    // arrow drawn one after the other stay on screen as one gesture.
    //
    // Dot mode (Context.LaserMode) instead shows a large glowing dot with a
    // short comet tail of the last fraction of a second, and removes both the
    // moment the pen lifts — pointing only, nothing lingers.

    private bool _lasering;
    private bool _laserDotMode;                    // mode of the gesture in flight
    private readonly List<Canvas> _laserTrails = new();
    private Polyline[]? _laserLines;               // lines of the trail being drawn
    private Canvas? _laserDotTail;                 // dot mode: the comet tail
    private readonly Queue<long> _laserTailTimes = new();
    private const long LaserTailMs = 140;
    private Ellipse? _laserDot;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _laserHoldTimer;
    private Storyboard? _laserFade;
    private static readonly TimeSpan LaserHold = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan LaserFadeTime = TimeSpan.FromMilliseconds(600);
    private const int LaserMaxPoints = 1500;

    private void BeginLaser(Vector2 p)
    {
        _laserDotMode = Context.LaserMode == LaserMode.Dot;
        if (!_laserDotMode)
        {
            _laserHoldTimer?.Stop();
            if (_laserFade is not null) { _laserFade.Stop(); _laserFade = null; }   // revives fading trails
        }

        var c = Context.CurrentColor;
        static Polyline Line(byte a, Color rgb, double thickness) => new()
        {
            Stroke = new SolidColorBrush(Color.FromArgb(a, rgb.R, rgb.G, rgb.B)),
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        // Wide soft glow, solid core, bright centre: reads as light on any paper.
        _laserLines = new[]
        {
            Line(60, c, 16),
            Line(255, c, 5),
            Line(210, Colors.White, 1.6),
        };
        var trail = new Canvas { IsHitTestVisible = false };
        foreach (var l in _laserLines) trail.Children.Add(l);
        Canvas.SetZIndex(trail, 9000);
        _overlay.Children.Add(trail);
        if (_laserDotMode)
        {
            // The tail is thinner than a trail and owned by this gesture only.
            _laserLines[0].StrokeThickness = 12;
            _laserLines[1].StrokeThickness = 4;
            _laserDotTail = trail;
            _laserTailTimes.Clear();
        }
        else _laserTrails.Add(trail);
        _lasering = true;
        // Two points so a tap without movement still shows a round dot.
        AddLaserPoint(p);
        AddLaserPoint(p);
    }

    private void AddLaserPoint(Vector2 p)
    {
        if (_laserLines is null) return;
        var pt = new Windows.Foundation.Point(p.X, p.Y);
        foreach (var l in _laserLines)
        {
            l.Points.Add(pt);
            if (l.Points.Count > LaserMaxPoints) l.Points.RemoveAt(0);
        }
        if (_laserDotMode)
        {
            // Keep only the last LaserTailMs of movement (at least the tip).
            long now = Environment.TickCount64;
            _laserTailTimes.Enqueue(now);
            while (_laserTailTimes.Count > 2 && now - _laserTailTimes.Peek() > LaserTailMs)
            {
                _laserTailTimes.Dequeue();
                foreach (var l in _laserLines) if (l.Points.Count > 2) l.Points.RemoveAt(0);
            }
        }
        UpdateLaserDot(p, true);
    }

    private void EndLaser()
    {
        _lasering = false;
        _laserLines = null;
        if (_laserDotMode)
        {
            // Dot mode: nothing stays behind once the pen lifts.
            if (_laserDotTail is not null) _overlay.Children.Remove(_laserDotTail);
            _laserDotTail = null;
            _laserTailTimes.Clear();
            HideLaserDot();
            return;
        }
        if (_laserHoldTimer is null)
        {
            _laserHoldTimer = DispatcherQueue.CreateTimer();
            _laserHoldTimer.IsRepeating = false;
            _laserHoldTimer.Interval = LaserHold;
            _laserHoldTimer.Tick += (_, __) => FadeLaserTrails();
        }
        _laserHoldTimer.Stop();
        _laserHoldTimer.Start();
    }

    private void FadeLaserTrails()
    {
        if (_lasering || _laserTrails.Count == 0) return;
        var trails = _laserTrails.ToList();
        var sb = new Storyboard();
        foreach (var t in trails)
        {
            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(LaserFadeTime),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(anim, t);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
        }
        sb.Completed += (_, __) =>
        {
            if (_laserFade != sb) return;   // revived by a new press
            _laserFade = null;
            foreach (var t in trails) { _overlay.Children.Remove(t); _laserTrails.Remove(t); }
        };
        _laserFade = sb;
        sb.Begin();
    }

    // Removes every trail at once (leaving the tool or presentation mode).
    public void ClearLaser()
    {
        _laserHoldTimer?.Stop();
        if (_laserFade is not null) { _laserFade.Stop(); _laserFade = null; }
        foreach (var t in _laserTrails) _overlay.Children.Remove(t);
        _laserTrails.Clear();
        if (_laserDotTail is not null) _overlay.Children.Remove(_laserDotTail);
        _laserDotTail = null;
        _laserTailTimes.Clear();
        _laserLines = null;
        _lasering = false;
        HideLaserDot();
    }

    // Hover dot: shows where the laser will land before pressing.
    private void UpdateLaserDot(PointerRoutedEventArgs e)
    {
        if (_lasering) return;   // the trail moves the dot itself
        bool show = e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch
                    && !Context.EditingSuspended
                    && _capturedPointerId is null
                    && PickTool(e) is LaserTool;
        if (!show) { HideLaserDot(); return; }
        UpdateLaserDot(ToPageSpace(e), true);
    }

    private void UpdateLaserDot(Vector2 p, bool show)
    {
        if (!show) { HideLaserDot(); return; }
        var c = Context.CurrentColor;
        if (_laserDot is null)
        {
            _laserDot = new Ellipse { Width = 12, Height = 12, StrokeThickness = 4, IsHitTestVisible = false };
            Canvas.SetZIndex(_laserDot, 9500);
            _overlay.Children.Add(_laserDot);
        }
        if (_laserDot.Fill is not SolidColorBrush fb || fb.Color.R != c.R || fb.Color.G != c.G || fb.Color.B != c.B)
        {
            _laserDot.Fill = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
            _laserDot.Stroke = new SolidColorBrush(Color.FromArgb(80, c.R, c.G, c.B));
        }
        // Pressing in dot mode grows the dot into the pointer itself.
        double d = _lasering && _laserDotMode ? 22 : 12;
        if (_laserDot.Width != d)
        {
            _laserDot.Width = d;
            _laserDot.Height = d;
            _laserDot.StrokeThickness = d > 12 ? 7 : 4;
        }
        Canvas.SetLeft(_laserDot, p.X - d / 2);
        Canvas.SetTop(_laserDot, p.Y - d / 2);
        _laserDot.Visibility = Visibility.Visible;
    }

    private void HideLaserDot()
    {
        if (_laserDot is not null) _laserDot.Visibility = Visibility.Collapsed;
    }
}
