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

    // ── Ruler overlay ─────────────────────────────────────────────────────────
    //
    // The ruler body is a Win2D CanvasControl that draws real centimetre tick
    // marks and labels.  The rotate handle is a small Ellipse at the right tip.
    // Both live in _overlay (document coordinates, scales with zoom).
    //
    // Page pixels-per-mm is derived from the A4 assumption: page.Width / 210.
    // (A4 is 1240 px @ ~150 dpi → 1 mm ≈ 5.905 px.)
    //
    // Dragging the body translates; dragging the handle rotates.
    // EnsureRulerOverlay() is lazy — elements are created only on first show.

    private const float RulerLength     = 640f;   // document pixels
    private const float RulerBodyHeight = 36f;    // enough room for labels
    private const float RotHandleR      = 11f;

    private CanvasControl? _rulerCanvas;
    private Ellipse?       _rulerRotHandle;
    private uint?          _rulerDragPtrId;
    private uint?          _rulerRotPtrId;
    private Vector2        _rulerDragOffset;

    private void EnsureRulerOverlay()
    {
        if (_rulerCanvas is not null) return;

        // ── Ruler body ────────────────────────────────────────────────────────
        _rulerCanvas = new CanvasControl
        {
            Width                 = RulerLength,
            Height                = RulerBodyHeight,
            IsHitTestVisible      = true,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform       = new RotateTransform(),
        };
        Canvas.SetZIndex(_rulerCanvas, 20);
        _rulerCanvas.Draw              += OnRulerCanvasDraw;
        _rulerCanvas.PointerPressed    += OnRulerBodyPointerPressed;
        _rulerCanvas.PointerMoved      += OnRulerBodyPointerMoved;
        _rulerCanvas.PointerReleased   += OnRulerBodyPointerReleased;
        _rulerCanvas.PointerCaptureLost += (_, __) => _rulerDragPtrId = null;

        // ── Rotate handle ─────────────────────────────────────────────────────
        _rulerRotHandle = new Ellipse
        {
            Width            = RotHandleR * 2,
            Height           = RotHandleR * 2,
            Fill             = new SolidColorBrush(Color.FromArgb(230, 50, 90, 205)),
            Stroke           = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            StrokeThickness  = 1.5,
            IsHitTestVisible = true,
        };
        Canvas.SetZIndex(_rulerRotHandle, 21);
        _rulerRotHandle.PointerPressed    += OnRotHandlePointerPressed;
        _rulerRotHandle.PointerMoved      += OnRotHandlePointerMoved;
        _rulerRotHandle.PointerReleased   += OnRotHandlePointerReleased;
        _rulerRotHandle.PointerCaptureLost += (_, __) => _rulerRotPtrId = null;

        _overlay.Children.Add(_rulerCanvas);
        _overlay.Children.Add(_rulerRotHandle);

        // Tell PenTool where the drawing edge is.
        Context.RulerHalfHeight = RulerBodyHeight / 2f;
    }

    private void OnRulerCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float w = RulerLength;
        float h = RulerBodyHeight;

        var bgColor     = Color.FromArgb(220, 218, 232, 255);
        var borderColor = Color.FromArgb(255, 45, 85, 200);
        var tickColor   = Color.FromArgb(210, 45, 85, 200);
        var labelColor  = Color.FromArgb(200, 25, 60, 170);

        // Background
        ds.FillRoundedRectangle(0f, 0f, w, h, 4f, 4f, bgColor);

        // Tick marks — 1 mm = (Page.Width / 210) document pixels for A4.
        float pxPerMm = (float)(Page.Width / 210.0);
        int   totalMm = (int)(w / pxPerMm) + 1;

        using var fmt = new CanvasTextFormat
        {
            FontFamily          = "Segoe UI",
            FontSize            = 8f,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment   = CanvasVerticalAlignment.Center,
        };

        for (int mm = 0; mm <= totalMm; mm++)
        {
            float x = mm * pxPerMm;
            if (x > w + 0.5f) break;

            bool isCm  = (mm % 10 == 0);
            bool is5mm = (mm % 5  == 0) && !isCm;

            // Tick heights (from the top and bottom edge inward)
            float tickH = isCm ? h * 0.42f : is5mm ? h * 0.26f : h * 0.15f;
            float thick = isCm ? 1.3f       : is5mm ? 1.0f      : 0.75f;

            ds.DrawLine(x, 1.5f,       x, tickH,       tickColor, thick);  // from top
            ds.DrawLine(x, h - 1.5f,   x, h - tickH,   tickColor, thick);  // from bottom

            // Centimetre labels
            if (isCm && mm > 0)
            {
                int cm = mm / 10;
                ds.DrawText(
                    cm.ToString(),
                    new Windows.Foundation.Rect(x - 9, h / 2f - 7, 18, 14),
                    labelColor,
                    fmt);
            }
        }

        // Border drawn last so it sits on top of tick ends
        ds.DrawRoundedRectangle(0.75f, 0.75f, w - 1.5f, h - 1.5f, 4f, 4f, borderColor, 1.5f);
    }

    // Called by InkCanvasControl whenever RulerVisible/X/Y/Angle changes.
    public void SyncRulerOverlay()
    {
        bool show = Context.RulerVisible;

        if (!show)
        {
            if (_rulerCanvas is not null)
            {
                _rulerCanvas.Visibility     = Visibility.Collapsed;
                _rulerRotHandle!.Visibility = Visibility.Collapsed;
            }
            return;
        }

        EnsureRulerOverlay();

        _rulerCanvas!.Visibility    = Visibility.Visible;
        _rulerRotHandle!.Visibility = Visibility.Visible;

        // Centre the body at (RulerX, RulerY), rotate in place.
        Canvas.SetLeft(_rulerCanvas, Context.RulerX - RulerLength     / 2f);
        Canvas.SetTop (_rulerCanvas, Context.RulerY - RulerBodyHeight  / 2f);
        ((RotateTransform)_rulerCanvas.RenderTransform).Angle = Context.RulerAngle;

        // Rotate handle at the right tip.
        float rad = Context.RulerAngle * MathF.PI / 180f;
        float hx  = Context.RulerX + MathF.Cos(rad) * (RulerLength / 2f);
        float hy  = Context.RulerY + MathF.Sin(rad) * (RulerLength / 2f);
        Canvas.SetLeft(_rulerRotHandle, hx - RotHandleR);
        Canvas.SetTop (_rulerRotHandle, hy - RotHandleR);
    }

    // ── Ruler drag (translate) ────────────────────────────────────────────────

    private void OnRulerBodyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerDragPtrId.HasValue) return;
        var pos          = e.GetCurrentPoint(_overlay).Position;
        _rulerDragOffset = new Vector2(
            (float)pos.X - Context.RulerX,
            (float)pos.Y - Context.RulerY);
        _rulerDragPtrId = e.Pointer.PointerId;
        _rulerCanvas!.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnRulerBodyPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerDragPtrId != e.Pointer.PointerId) return;
        var pos        = e.GetCurrentPoint(_overlay).Position;
        Context.RulerX = (float)pos.X - _rulerDragOffset.X;
        Context.RulerY = (float)pos.Y - _rulerDragOffset.Y;
        Context.RulerChanged?.Invoke();
        e.Handled = true;
    }

    private void OnRulerBodyPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerDragPtrId != e.Pointer.PointerId) return;
        _rulerDragPtrId = null;
        _rulerCanvas!.ReleasePointerCaptures();
        e.Handled = true;
    }

    // ── Ruler rotate ──────────────────────────────────────────────────────────

    private void OnRotHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerRotPtrId.HasValue) return;
        _rulerRotPtrId = e.Pointer.PointerId;
        _rulerRotHandle!.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnRotHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerRotPtrId != e.Pointer.PointerId) return;
        var pos            = e.GetCurrentPoint(_overlay).Position;
        Context.RulerAngle = MathF.Atan2(
            (float)pos.Y - Context.RulerY,
            (float)pos.X - Context.RulerX) * 180f / MathF.PI;
        Context.RulerChanged?.Invoke();
        e.Handled = true;
    }

    private void OnRotHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerRotPtrId != e.Pointer.PointerId) return;
        _rulerRotPtrId = null;
        _rulerRotHandle!.ReleasePointerCaptures();
        e.Handled = true;
    }
}
