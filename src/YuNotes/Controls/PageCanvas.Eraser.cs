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

    // ── Eraser range preview ──────────────────────────────────────────────────
    //
    // A circle the size of the eraser follows the pointer whenever erasing is
    // what a press would do — the eraser tool is active, or the pen's eraser
    // button/tip is held — both while hovering and while erasing, so you can
    // see what will be removed before touching the page.

    private Microsoft.UI.Xaml.Shapes.Ellipse? _eraserCursor;

    private void UpdateEraserCursor(PointerRoutedEventArgs e)
    {
        bool show = false;
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch
            && !Context.EditingSuspended)
        {
            // Mid-gesture, the tool picked at press time decides; when hovering,
            // the same rules as a press (eraser button, eraser tip, active tool).
            var tool = _capturedPointerId == e.Pointer.PointerId ? _activeTool : PickTool(e);
            show = tool is EraserTool;
        }
        if (!show) { HideEraserCursor(); return; }

        // Stroke mode, hovering (not erasing): fade what a press here would remove.
        if (!Context.EraserPixelMode && _capturedPointerId != e.Pointer.PointerId
            && PickTool(e) is EraserTool hoverEraser)
        {
            _eraseHoverScratch.Clear();
            hoverEraser.CollectStrokeModeHits(Page, ToPageSpace(e), Context.EraserWidth, _eraseHoverScratch);
            SetEraseHover(_eraseHoverScratch);
        }
        else SetEraseHover(null);

        if (_eraserCursor is null)
        {
            _eraserCursor = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                // Dark ring + faint fill: readable on white paper, ink and PDFs.
                Stroke = new SolidColorBrush(Color.FromArgb(170, 40, 44, 56)),
                StrokeThickness = 1.2,
                Fill = new SolidColorBrush(Color.FromArgb(28, 91, 107, 255)),
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(_eraserCursor, 10000);
            _overlay.Children.Add(_eraserCursor);
        }
        var d = Math.Max(2f, Context.EraserWidth);
        var p = ToPageSpace(e);
        _eraserCursor.Width = d;
        _eraserCursor.Height = d;
        Canvas.SetLeft(_eraserCursor, p.X - d / 2);
        Canvas.SetTop(_eraserCursor, p.Y - d / 2);
        _eraserCursor.Visibility = Visibility.Visible;
    }

    private void HideEraserCursor()
    {
        if (_eraserCursor is not null) _eraserCursor.Visibility = Visibility.Collapsed;
        SetEraseHover(null);
    }

    // Strokes/shapes currently drawn faded as "would be erased" (null = none).
    private HashSet<string>? _eraseHoverIds;
    private readonly HashSet<string> _eraseHoverScratch = new();

    // Updates the faded set, repainting only the elements whose look changed.
    private void SetEraseHover(ISet<string>? ids)
    {
        bool none = ids is null || ids.Count == 0;
        if (none && _eraseHoverIds is null) return;
        if (!none && _eraseHoverIds is not null && _eraseHoverIds.SetEquals(ids!)) return;

        Bbox? dirty = null;
        void Touch(string id)
        {
            Bbox b;
            float pad;
            if (Page.Strokes.FirstOrDefault(x => x.Id == id) is { } st) { b = Bbox.RepaintOf(st); pad = 0f; }
            else if (Page.Shapes.FirstOrDefault(x => x.Id == id) is { } sh) { b = Bbox.RepaintOf(sh); pad = 0f; }
            else return;
            b = new Bbox(b.X - pad, b.Y - pad, b.W + pad * 2, b.H + pad * 2);
            dirty = dirty is null ? b : Bbox.Union(dirty.Value, b);
        }
        if (_eraseHoverIds is not null) foreach (var id in _eraseHoverIds) if (none || !ids!.Contains(id)) Touch(id);
        if (!none) foreach (var id in ids!) if (_eraseHoverIds?.Contains(id) != true) Touch(id);

        _eraseHoverIds = none ? null : new HashSet<string>(ids!);
        if (dirty is { } d) CommitStrokeRedraw(d);
    }
}
