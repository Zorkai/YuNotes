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

    // Drag modes when interacting with selection
    private enum DragMode { None, Move, ResizeNW, ResizeNE, ResizeSW, ResizeSE, ResizeW, ResizeE, ResizeN, ResizeS, Rotate }
    private DragMode _drag = DragMode.None;
    private Bbox _dragStartBbox;
    private Vector2 _dragStartCenter;
    private double _dragStartRotation;

    // Drag ghost: the selection rendered ONCE into a CanvasImageSource; each
    // move only repositions/transforms this image (composition-only work).
    // The originals are hidden from the main canvas via _hiddenElementIds and
    // the model mutates ONCE on release.
    private Image? _dragGhost;
    private Rectangle? _dragGhostBorder;
    // While resizing a selection with text in it, the text is drawn here at its
    // new size and wrapping on every move (see UpdateTextResizeGhost), not
    // stretched with the rest of the ghost — so the preview matches the result.
    private Image? _textResizeGhost;
    private readonly Dictionary<string, float> _origTextFont = new();
    private RotateTransform? _dragGhostRotate;
    private RotateTransform? _dragGhostBorderRotate;
    private Bbox _dragGhostBbox;          // padded doc-space rect the ghost image covers
    private Vector2 _dragStartPoint;
    private Vector2 _lastDragPoint;
    private bool _dragMoved;
    // True when this move drag began by pressing inside an ALREADY-committed
    // selection (not by freshly selecting an element). A tap here — press and
    // release with negligible travel — dismisses the selection instead of
    // moving it, so the user can click to deselect without the sidebar button.
    private bool _dragTapDismisses;
    // Squared page-space travel below which a move counts as a tap, not a drag.
    private const float TapDismissThresholdSq = 16f;   // 4px
    private HashSet<string>? _hiddenElementIds;

    // Snapshot of original element transforms at drag start, keyed by id
    private readonly Dictionary<string, Bbox> _origImageBoxes = new();
    private readonly Dictionary<string, Bbox> _origTextBoxes = new();
    private readonly Dictionary<string, (float X1, float Y1, float X2, float Y2, float X3, float Y3)> _origShapePoints = new();
    private readonly Dictionary<string, double> _origImageRot = new();
    private readonly Dictionary<string, double> _origTextRot = new();

    private const float HandleSize = 14f;
    private const float RotateHandleOffset = 36f;

    private void StartDrag(DragMode mode, Vector2 p, bool tapDismisses = false)
    {
        _drag = mode;
        _dragTapDismisses = tapDismisses;
        SelectionBbox(out _dragStartBbox);
        _dragStartCenter = _dragStartBbox.Center;
        _origImageBoxes.Clear();
        _origTextBoxes.Clear();
        _origTextFont.Clear();
        _origShapePoints.Clear();
        _origImageRot.Clear();
        _origTextRot.Clear();
        // For Rotate, snapshot existing rotations and bounds.
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            _origImageBoxes[id] = Bbox.Of(im);
            _origImageRot[id] = im.Rotation;
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            _origTextBoxes[id] = Bbox.Of(t);
            _origTextFont[id] = t.FontSize;
            _origTextRot[id] = t.Rotation;
        }
        foreach (var id in Context.SelectedShapeIds)
        {
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            _origShapePoints[id] = (sh.X1, sh.Y1, sh.X2, sh.Y2, sh.X3, sh.Y3);
        }
        _dragStartRotation = Math.Atan2(p.Y - _dragStartCenter.Y, p.X - _dragStartCenter.X);
        _dragStartPoint = p;
        _lastDragPoint = p;
        _dragMoved = false;
        // Paint this page (and its drag ghost) above sibling pages so a ghost
        // dragged past the page edge stays visible over the next page instead
        // of being occluded by it. Restored in EndDrag.
        if (mode == DragMode.Move) Canvas.SetZIndex(this, 1);
        BeginDragGhost();
    }

    // During the drag only the overlay ghost is transformed — pure composition
    // work, no Win2D redraw and no model mutation. The model changes once, in
    // EndDrag. (Mutating per move also re-baked every selected stroke's cached
    // geometry each sample, and repeated ApplyResize over already-scaled
    // stroke points compounded the scale.)
    private void ContinueDrag(Vector2 p)
    {
        _lastDragPoint = p;
        if ((p - _dragStartPoint).LengthSquared() > 0.25f) _dragMoved = true;
        if (_dragGhost is null) return;   // ghost failed — release still applies the drag

        switch (_drag)
        {
            case DragMode.Move:
            {
                float tx = p.X - _dragStartPoint.X;
                float ty = p.Y - _dragStartPoint.Y;
                PositionGhost(_dragGhostBbox.X + tx, _dragGhostBbox.Y + ty,
                              _dragGhostBbox.W, _dragGhostBbox.H);
                break;
            }
            case DragMode.ResizeNW:
            case DragMode.ResizeNE:
            case DragMode.ResizeSW:
            case DragMode.ResizeSE:
            case DragMode.ResizeW:
            case DragMode.ResizeE:
            case DragMode.ResizeN:
            case DragMode.ResizeS:
            {
                var nb = ResizedBox(_drag, p);
                if (SelectionIsTextOnly)
                {
                    // The outline follows the text as it will really be (its
                    // height fits the rewrapped text), not the dragged box.
                    if (UpdateTextResizeGhost(nb) is { } tb) PositionGhost(tb.X, tb.Y, tb.W, tb.H);
                    break;
                }
                float sx = nb.W / Math.Max(1e-3f, _dragStartBbox.W);
                float sy = nb.H / Math.Max(1e-3f, _dragStartBbox.H);
                PositionGhost(
                    nb.X + (_dragGhostBbox.X - _dragStartBbox.X) * sx,
                    nb.Y + (_dragGhostBbox.Y - _dragStartBbox.Y) * sy,
                    _dragGhostBbox.W * sx,
                    _dragGhostBbox.H * sy);
                UpdateTextResizeGhost(nb);
                break;
            }
            case DragMode.Rotate:
            {
                var ang = Math.Atan2(p.Y - _dragStartCenter.Y, p.X - _dragStartCenter.X);
                var deltaDeg = (ang - _dragStartRotation) * 180.0 / Math.PI;
                _dragGhostRotate!.Angle = deltaDeg;
                _dragGhostBorderRotate!.Angle = deltaDeg;
                break;
            }
        }
    }

    // New selection bbox implied by dragging `mode`'s corner to `p`.
    private Bbox ResizedBox(DragMode mode, Vector2 p)
    {
        var orig = _dragStartBbox;
        float left = orig.X, top = orig.Y, right = orig.Right, bottom = orig.Bottom;

        // Text: corners scale the text as a whole (box and font together), so
        // the box keeps its shape and the width alone decides the size.
        if (SelectionIsTextOnly && mode is DragMode.ResizeNW or DragMode.ResizeNE
                                         or DragMode.ResizeSW or DragMode.ResizeSE)
        {
            bool fromLeft = mode is DragMode.ResizeNW or DragMode.ResizeSW;
            bool fromTop = mode is DragMode.ResizeNW or DragMode.ResizeNE;
            float w = Math.Max(24, fromLeft ? orig.Right - p.X : p.X - orig.X);
            float h = orig.H * w / Math.Max(1e-3f, orig.W);
            float x = fromLeft ? orig.Right - w : orig.X;
            float y = fromTop ? orig.Bottom - h : orig.Y;
            return new Bbox(x, y, w, h);
        }

        switch (mode)
        {
            case DragMode.ResizeNW: left = p.X; top = p.Y; break;
            case DragMode.ResizeNE: right = p.X; top = p.Y; break;
            case DragMode.ResizeSW: left = p.X; bottom = p.Y; break;
            case DragMode.ResizeSE: right = p.X; bottom = p.Y; break;
            case DragMode.ResizeW: left = p.X; break;
            case DragMode.ResizeE: right = p.X; break;
            case DragMode.ResizeN: top = p.Y; break;
            case DragMode.ResizeS: bottom = p.Y; break;
        }
        float minW = mode is DragMode.ResizeW or DragMode.ResizeE ? 24 : 8;
        if (right - left < minW)
        {
            if (mode is DragMode.ResizeW or DragMode.ResizeNW or DragMode.ResizeSW) left = right - minW;
            else right = left + minW;
        }
        if (bottom - top < 8)
        {
            if (mode is DragMode.ResizeN or DragMode.ResizeNW or DragMode.ResizeNE) top = bottom - 8;
            else bottom = top + 8;
        }
        return new Bbox(left, top, right - left, bottom - top);
    }

    // A selected text box as a resize to selection box `nb` leaves it:
    //   side handles — new width, text rewraps, font unchanged;
    //   corners on text only — box and font scale together;
    //   corners on a mixed selection — scaled with the rest, font by the
    //   smaller of the two scales so it never overflows.
    // The height always fits the text, so nothing is cut off and there is no
    // empty space left at the bottom.
    private TextElement ResizedText(TextElement t, Bbox ob, float origFont, Bbox nb, DragMode mode)
    {
        var orig = _dragStartBbox;
        float sx = nb.W / Math.Max(1e-3f, orig.W);
        float sy = nb.H / Math.Max(1e-3f, orig.H);
        bool sides = mode is DragMode.ResizeW or DragMode.ResizeE or DragMode.ResizeN or DragMode.ResizeS;
        float font = sides ? origFont
                   : Math.Clamp(origFont * (SelectionIsTextOnly ? sx : Math.Min(sx, sy)), 6f, 400f);
        var r = new TextElement
        {
            X = nb.X + (ob.X - orig.X) * sx,
            Y = nb.Y + (ob.Y - orig.Y) * sy,
            Width = Math.Max(font * 1.2, ob.W * sx),
            Height = ob.H,
            FontSize = font,
            Rotation = t.Rotation, Text = t.Text, Color = t.Color,
            FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic,
        };
        r.Height = FitTextHeight(r);
        return r;
    }

    // Height a text box needs for its text at its width (at least one line).
    private static double FitTextHeight(TextElement t)
    {
        var probe = new TextElement
        {
            Width = t.Width, Height = t.Height, FontSize = t.FontSize, FontFamily = t.FontFamily,
            Bold = t.Bold, Italic = t.Italic,
            Text = string.IsNullOrEmpty(t.Text) ? "Ag" : t.Text,
        };
        return Math.Max(t.FontSize * 1.3, PageRenderer.MeasureTextHeight(probe) + 4);
    }

    private void ApplyResize(DragMode mode, Vector2 p)
    {
        var orig = _dragStartBbox;
        var newBox = ResizedBox(mode, p);

        // Scale each selected element from its original bbox to its new position within newBox,
        // preserving relative position/size within the selection.
        float sx = newBox.W / Math.Max(1e-3f, orig.W);
        float sy = newBox.H / Math.Max(1e-3f, orig.H);

        foreach (var id in Context.SelectedImageIds)
        {
            if (!_origImageBoxes.TryGetValue(id, out var ob)) continue;
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            im.X = newBox.X + (ob.X - orig.X) * sx;
            im.Y = newBox.Y + (ob.Y - orig.Y) * sy;
            im.Width = ob.W * sx;
            im.Height = ob.H * sy;
        }
        foreach (var id in Context.SelectedTextIds)
        {
            if (!_origTextBoxes.TryGetValue(id, out var ob)) continue;
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            var r = ResizedText(t, ob, _origTextFont.TryGetValue(id, out var f) ? f : t.FontSize, newBox, mode);
            t.X = r.X; t.Y = r.Y; t.Width = r.Width; t.Height = r.Height; t.FontSize = r.FontSize;
        }
        // Shapes: scale each endpoint relative to the original selection bbox.
        foreach (var id in Context.SelectedShapeIds)
        {
            if (!_origShapePoints.TryGetValue(id, out var op)) continue;
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            sh.X1 = newBox.X + (op.X1 - orig.X) * sx;
            sh.Y1 = newBox.Y + (op.Y1 - orig.Y) * sy;
            sh.X2 = newBox.X + (op.X2 - orig.X) * sx;
            sh.Y2 = newBox.Y + (op.Y2 - orig.Y) * sy;
            if (sh.Kind == ShapeKind.Triangle)
            {
                sh.X3 = newBox.X + (op.X3 - orig.X) * sx;
                sh.Y3 = newBox.Y + (op.Y3 - orig.Y) * sy;
            }
        }
        // Strokes: scale all points relative to original bbox.
        foreach (var id in Context.SelectedStrokeIds)
        {
            var s = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (s is null) continue;
            for (int i = 0; i < s.Points.Count; i++)
            {
                var pt = s.Points[i];
                var nx = newBox.X + (pt.X - orig.X) * sx;
                var ny = newBox.Y + (pt.Y - orig.Y) * sy;
                s.Points[i] = new InkPoint(nx, ny, pt.Pressure);
            }
        }
    }

    private void ApplyRotate(Vector2 p)
    {
        var ang = Math.Atan2(p.Y - _dragStartCenter.Y, p.X - _dragStartCenter.X);
        var deltaDeg = (ang - _dragStartRotation) * 180.0 / Math.PI;
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            if (!_origImageRot.TryGetValue(id, out var orig)) orig = 0;
            im.Rotation = orig + deltaDeg;
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            if (!_origTextRot.TryGetValue(id, out var orig)) orig = 0;
            t.Rotation = orig + deltaDeg;
        }
    }

    // Renders the selected elements once into an overlay image and hides the
    // originals from the main canvas (one partial redraw). The ghost pixel-
    // covers the originals, so nothing visibly changes at drag start.
    private void BeginDragGhost()
    {
        TearDownDragGhost();
        if (!SelectionBbox(out var sel)) return;

        // Pad so stroke half-widths + Catmull-Rom overshoot render inside the
        // image instead of clipping at the selection's point-hull edge.
        float pad = 12f;
        foreach (var id in Context.SelectedStrokeIds)
        {
            var s = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (s is not null) pad = Math.Max(pad, s.Width * 0.5f + 12f);
        }
        var box = new Bbox(sel.X - pad, sel.Y - pad, sel.W + pad * 2, sel.H + pad * 2);
        if (box.W < 1 || box.H < 1) return;

        try
        {
            var device = TryGetCanvasDevice() ?? CanvasDevice.GetSharedDevice();
            // Render at the canvas backing scale so the ghost is crisp at the
            // current zoom; cap total pixels for page-sized selections.
            float scale = Math.Clamp(_canvas.DpiScale, 1f, 4f);
            const float maxPixels = 16_000_000f;
            if (box.W * box.H * scale * scale > maxPixels)
                scale = MathF.Sqrt(maxPixels / (box.W * box.H));
            scale = Math.Max(0.25f, scale);

            var src = new CanvasImageSource(device, box.W, box.H, 96f * scale);
            using (var ds = src.CreateDrawingSession(Colors.Transparent))
            {
                ds.Transform = Matrix3x2.CreateTranslation(-box.X, -box.Y);
                Renderer.DrawElements(ds, Page,
                    Context.SelectedStrokeIds, Context.SelectedShapeIds,
                    IsResizeDrag(_drag) ? new List<string>() : Context.SelectedTextIds,
                    Context.SelectedImageIds, _imageCache);
            }

            _dragGhostRotate = new RotateTransform();
            _dragGhost = new Image
            {
                Source = src,
                Width = box.W,
                Height = box.H,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = _dragGhostRotate
            };
            Canvas.SetLeft(_dragGhost, box.X);
            Canvas.SetTop(_dragGhost, box.Y);
            Canvas.SetZIndex(_dragGhost, 10);
            _overlay.Children.Add(_dragGhost);

            _dragGhostBorderRotate = new RotateTransform();
            _dragGhostBorder = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(255, 91, 107, 255)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Width = box.W,
                Height = box.H,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = _dragGhostBorderRotate
            };
            Canvas.SetLeft(_dragGhostBorder, box.X);
            Canvas.SetTop(_dragGhostBorder, box.Y);
            Canvas.SetZIndex(_dragGhostBorder, 11);
            _overlay.Children.Add(_dragGhostBorder);

            _dragGhostBbox = box;
            if (IsResizeDrag(_drag)) UpdateTextResizeGhost(_dragStartBbox);

            // Hide the originals (and the Win2D chrome — DrawSelectionVisuals
            // skips while _drag is set) with one partial redraw.
            _hiddenElementIds = new HashSet<string>(
                Context.SelectedStrokeIds
                    .Concat(Context.SelectedShapeIds)
                    .Concat(Context.SelectedTextIds)
                    .Concat(Context.SelectedImageIds));
            InvalidateSelectionRegion(box, 0);
        }
        catch (Exception ex)
        {
            // Fail-soft: no ghost means no live preview, but the drag still
            // applies correctly on release.
            App.LogError(ex, "BeginDragGhost");
            _hiddenElementIds = null;
            TearDownDragGhost();
        }
    }

    private void PositionGhost(float x, float y, float w, float h)
    {
        if (_dragGhost is null || _dragGhostBorder is null) return;
        Canvas.SetLeft(_dragGhost, x);
        Canvas.SetTop(_dragGhost, y);
        _dragGhost.Width = w;
        _dragGhost.Height = h;
        Canvas.SetLeft(_dragGhostBorder, x);
        Canvas.SetTop(_dragGhostBorder, y);
        _dragGhostBorder.Width = w;
        _dragGhostBorder.Height = h;
    }

    private static bool IsResizeDrag(DragMode m) =>
        m is DragMode.ResizeNW or DragMode.ResizeNE or DragMode.ResizeSW or DragMode.ResizeSE
          or DragMode.ResizeW or DragMode.ResizeE or DragMode.ResizeN or DragMode.ResizeS;

    // Draws the selected text boxes as ApplyResize will leave them for the
    // selection box `nb`: same position/size/font-size formula, text laid out
    // and wrapped for real rather than stretched.
    // Returns the resized text boxes' outline (null if nothing to draw).
    private Bbox? UpdateTextResizeGhost(Bbox nb)
    {
        if (Context.SelectedTextIds.Count == 0) return null;
        var texts = new List<TextElement>();
        foreach (var id in Context.SelectedTextIds)
        {
            if (!_origTextBoxes.TryGetValue(id, out var ob)) continue;
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            float font = _origTextFont.TryGetValue(id, out var f) ? f : t.FontSize;
            texts.Add(ResizedText(t, ob, font, nb, _drag));
        }
        if (texts.Count == 0) return null;
        var outline = Bbox.Of(texts[0]);
        foreach (var t in texts.Skip(1)) outline = Bbox.Union(outline, Bbox.Of(t));

        // Area to draw into: the boxes, grown for rotation and for text that
        // runs past the bottom of its box.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var t in texts)
        {
            float r = t.Rotation != 0 ? 0.5f * MathF.Sqrt((float)(t.Width * t.Width + t.Height * t.Height)) : 0f;
            float cx = (float)(t.X + t.Width / 2), cy = (float)(t.Y + t.Height / 2);
            minX = Math.Min(minX, Math.Min((float)t.X, cx - r));
            minY = Math.Min(minY, Math.Min((float)t.Y, cy - r));
            maxX = Math.Max(maxX, Math.Max((float)(t.X + t.Width), cx + r));
            maxY = Math.Max(maxY, Math.Max((float)(t.Y + t.Height), cy + r));
        }
        var box = new Bbox(minX - 4, minY - 4, maxX - minX + 8, maxY - minY + 8);
        if (box.W < 1 || box.H < 1) return outline;

        try
        {
            var device = TryGetCanvasDevice() ?? CanvasDevice.GetSharedDevice();
            float scale = Math.Clamp(_canvas.DpiScale, 1f, 4f);
            const float maxPixels = 8_000_000f;
            if (box.W * box.H * scale * scale > maxPixels)
                scale = Math.Max(0.25f, MathF.Sqrt(maxPixels / (box.W * box.H)));
            var src = new CanvasImageSource(device, box.W, box.H, 96f * scale);
            using (var ds = src.CreateDrawingSession(Colors.Transparent))
            {
                ds.Transform = Matrix3x2.CreateTranslation(-box.X, -box.Y);
                foreach (var t in texts) Renderer.DrawTextElement(ds, t);
            }
            if (_textResizeGhost is null)
            {
                _textResizeGhost = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                Canvas.SetZIndex(_textResizeGhost, 10);
                _overlay.Children.Add(_textResizeGhost);
            }
            _textResizeGhost.Source = src;
            _textResizeGhost.Width = box.W;
            _textResizeGhost.Height = box.H;
            Canvas.SetLeft(_textResizeGhost, box.X);
            Canvas.SetTop(_textResizeGhost, box.Y);
        }
        catch (Exception ex)
        {
            // Preview only: the resize still applies on release.
            App.LogError(ex, "UpdateTextResizeGhost");
        }
        return outline;
    }

    private void TearDownDragGhost()
    {
        if (_textResizeGhost is not null) _overlay.Children.Remove(_textResizeGhost);
        _textResizeGhost = null;
        if (_dragGhost is not null) _overlay.Children.Remove(_dragGhost);
        if (_dragGhostBorder is not null) _overlay.Children.Remove(_dragGhostBorder);
        _dragGhost = null;
        _dragGhostBorder = null;
        _dragGhostRotate = null;
        _dragGhostBorderRotate = null;
    }

    // Partial main-canvas invalidate covering `b` plus the selection chrome
    // (dashed outline pad, handles, rotate stalk) and any extra overhang.
    private void InvalidateSelectionRegion(Bbox b, float extraPad)
    {
        float p = 56f + extraPad;
        CommitStrokeRedraw(new Bbox(b.X - p, b.Y - p, b.W + p * 2, b.H + p * 2));
    }

    // Applies the drag's accumulated transform to the model ONCE, restores the
    // hidden originals and repaints only the affected region.
    private void EndDrag()
    {
        if (_dragMoved) Context.LastSelectionArea = null;   // the area no longer matches the selection
        var mode = _drag;
        _drag = DragMode.None;
        Canvas.SetZIndex(this, 0);   // undo the drag-time raise (see StartDrag)
        var oldRegion = _dragGhost is not null ? _dragGhostBbox : _dragStartBbox;

        // A tap (no real travel) inside an existing selection is "click to
        // deselect": tear down the ghost, restore the originals and clear —
        // rather than nudging the selection by a jittery pixel or two.
        bool tapped = (_lastDragPoint - _dragStartPoint).LengthSquared() <= TapDismissThresholdSq;
        if (mode == DragMode.Move && _dragTapDismisses && tapped)
        {
            _hiddenElementIds = null;
            TearDownDragGhost();
            InvalidateSelectionRegion(oldRegion, 0);
            ClearSelection();
            return;
        }

        // Cross-page move: if the pointer ended over a different page, hand the
        // selection to that page (InkCanvasControl does the reparenting + coord
        // translation) instead of translating it off the bottom of this one.
        if (mode == DragMode.Move && _dragMoved && SelectionDragDropped is not null)
        {
            var delta = new Vector2(_lastDragPoint.X - _dragStartPoint.X,
                                    _lastDragPoint.Y - _dragStartPoint.Y);
            var drop = new SelectionDropEventArgs { ReleaseLocal = _lastDragPoint, Delta = delta };
            SelectionDragDropped.Invoke(this, drop);
            if (drop.Transferred)
            {
                _hiddenElementIds = null;
                TearDownDragGhost();
                RequestRedraw();   // repaint this (source) page: the elements are gone
                return;
            }
        }

        if (_dragMoved)
        {
            switch (mode)
            {
                case DragMode.Move:
                    TranslateSelection(_lastDragPoint.X - _dragStartPoint.X,
                                       _lastDragPoint.Y - _dragStartPoint.Y);
                    break;
                case DragMode.ResizeNW:
                case DragMode.ResizeNE:
                case DragMode.ResizeSW:
                case DragMode.ResizeSE:
                case DragMode.ResizeW:
                case DragMode.ResizeE:
                case DragMode.ResizeN:
                case DragMode.ResizeS:
                    ApplyResize(mode, _lastDragPoint);
                    break;
                case DragMode.Rotate:
                    ApplyRotate(_lastDragPoint);
                    break;
            }
        }

        _hiddenElementIds = null;
        TearDownDragGhost();

        var dirty = oldRegion;
        if (SelectionBbox(out var nb)) dirty = Bbox.Union(dirty, nb);
        // Rotated images/texts overhang their axis-aligned bbox by up to the
        // half-diagonal; pad generously since this repaint happens once.
        float extra = mode == DragMode.Rotate ? 0.5f * Math.Max(dirty.W, dirty.H) : 0f;
        InvalidateSelectionRegion(dirty, extra);

        if (_dragMoved) Context.Mutated?.Invoke();
    }
}
