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

    // ── Select-tool overlay visuals ──────────────────────────────────────────
    // The marquee (rect / lasso) and the drag ghost live in the XAML overlay
    // like live ink: per-pointer-move updates cost no Win2D redraw. Before
    // this, every marquee move full-page-invalidated the main canvas, and
    // every drag move rewrote the selected elements' geometry AND full-page-
    // redrew (which also busted the stroke geometry cache each sample).
    private Rectangle? _marqueeRect;
    private Polyline? _marqueeLasso;

    private bool TrySelectElementAt(Vector2 p)
    {
        // A tap draws no area, so an older one must not be used for a sticker.
        Context.LastSelectionArea = null;
        // Hit-test in reverse render order: tape (drawn over everything) → text
        // → strokes → shapes → images.
        if (TapeAt(p) is { } tape)
        {
            Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
            Context.SelectedImageIds.Clear();  Context.SelectedTextIds.Clear();
            Context.SelectedShapeIds.Add(tape.Id);
            Context.SelectionChanged?.Invoke();
            return true;
        }
        foreach (var t in Page.Texts.AsEnumerable().Reverse())
        {
            if (Bbox.Of(t).Contains(p.X, p.Y))
            {
                Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
                Context.SelectedImageIds.Clear();  Context.SelectedTextIds.Clear();
                Context.SelectedTextIds.Add(t.Id);
                Context.SelectionChanged?.Invoke();
                return true;
            }
        }
        foreach (var s in Page.Strokes.AsEnumerable().Reverse())
        {
            if (Bbox.Of(s).Contains(p.X, p.Y))
            {
                Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
                Context.SelectedImageIds.Clear();  Context.SelectedTextIds.Clear();
                Context.SelectedStrokeIds.Add(s.Id);
                Context.SelectionChanged?.Invoke();
                return true;
            }
        }
        foreach (var sh in Page.Shapes.AsEnumerable().Reverse())
        {
            if (Bbox.Of(sh).Contains(p.X, p.Y))
            {
                Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
                Context.SelectedImageIds.Clear();  Context.SelectedTextIds.Clear();
                Context.SelectedShapeIds.Add(sh.Id);
                Context.SelectionChanged?.Invoke();
                return true;
            }
        }
        foreach (var im in Page.Images.AsEnumerable().Reverse())
        {
            if (Bbox.Of(im).Contains(p.X, p.Y))
            {
                Context.SelectedStrokeIds.Clear(); Context.SelectedShapeIds.Clear();
                Context.SelectedImageIds.Clear();  Context.SelectedTextIds.Clear();
                Context.SelectedImageIds.Add(im.Id);
                Context.SelectionChanged?.Invoke();
                return true;
            }
        }
        return false;
    }

    // Shows/updates/removes the select-tool marquee (rect or lasso) as XAML
    // overlay shapes driven by Context.SelectionRect/SelectionLasso. Runs on
    // every live sync; all no-op paths are cheap null checks.
    private void SyncMarqueeOverlay()
    {
        bool isCurrent = ReferenceEquals(Context.CurrentPage, Page);

        if (isCurrent && Context.SelectionRect is { } r)
        {
            if (_marqueeRect is null)
            {
                _marqueeRect = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(180, 91, 107, 255)),
                    StrokeThickness = 1.4,
                    Fill = new SolidColorBrush(Color.FromArgb(40, 91, 107, 255)),
                    IsHitTestVisible = false
                };
                _overlay.Children.Add(_marqueeRect);
            }
            Canvas.SetLeft(_marqueeRect, r.X);
            Canvas.SetTop(_marqueeRect, r.Y);
            _marqueeRect.Width = r.W;
            _marqueeRect.Height = r.H;
        }
        else if (_marqueeRect is not null)
        {
            _overlay.Children.Remove(_marqueeRect);
            _marqueeRect = null;
        }

        if (isCurrent && Context.SelectionLasso is { Count: > 1 } poly)
        {
            if (_marqueeLasso is null)
            {
                _marqueeLasso = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(220, 91, 107, 255)),
                    StrokeThickness = 1.4,
                    IsHitTestVisible = false
                };
                _overlay.Children.Add(_marqueeLasso);
            }
            var pc = _marqueeLasso.Points;
            if (pc.Count > poly.Count) pc.Clear();   // new gesture reused the element
            for (int i = pc.Count; i < poly.Count; i++)
                pc.Add(new Windows.Foundation.Point(poly[i].X, poly[i].Y));
        }
        else if (_marqueeLasso is not null)
        {
            _overlay.Children.Remove(_marqueeLasso);
            _marqueeLasso = null;
        }
    }

    private void DrawSelectionVisuals(CanvasDrawingSession ds)
    {
        var fill = Color.FromArgb(38, 91, 107, 255);
        var line = Color.FromArgb(220, 91, 107, 255);

        foreach (var id in Context.SelectedStrokeIds)
        {
            var stroke = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (stroke is null) continue;
            var b = Bbox.Of(stroke);
            ds.FillRectangle(b.X, b.Y, b.W, b.H, fill);
            ds.DrawRectangle(b.X, b.Y, b.W, b.H, line, 1f);
        }
        foreach (var id in Context.SelectedShapeIds)
        {
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            var b = Bbox.Of(sh);
            ds.FillRectangle(b.X, b.Y, b.W, b.H, fill);
            ds.DrawRectangle(b.X, b.Y, b.W, b.H, line, 1f);
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            var b = Bbox.Of(t);
            ds.FillRectangle(b.X, b.Y, b.W, b.H, fill);
            ds.DrawRectangle(b.X, b.Y, b.W, b.H, line, 1f);
        }
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            var b = Bbox.Of(im);
            ds.FillRectangle(b.X, b.Y, b.W, b.H, fill);
            ds.DrawRectangle(b.X, b.Y, b.W, b.H, line, 1f);
        }

        if (!SelectionBbox(out var bbox)) return;

        var pad = 8f;
        var outer = new Bbox(bbox.X - pad, bbox.Y - pad, bbox.W + pad * 2, bbox.H + pad * 2);
        var dash = new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
        {
            DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Dash
        };
        ds.DrawRectangle(outer.X, outer.Y, outer.W, outer.H, Color.FromArgb(255, 91, 107, 255), 1.5f, dash);

        // Corner handles + (single-element only) rotate handle
        var handles = HandlePositions(outer);
        foreach (var (mode, pos) in handles)
        {
            if (mode is DragMode.ResizeW or DragMode.ResizeE or DragMode.ResizeN or DragMode.ResizeS)
            {
                // Edge handles are pills along their edge: tall on the sides
                // (width only), wide on top and bottom (height only).
                bool side = mode is DragMode.ResizeW or DragMode.ResizeE;
                float hw = side ? HandleSize * 0.32f : HandleSize * 0.8f;
                float hh = side ? HandleSize * 0.8f : HandleSize * 0.32f;
                var r = new Windows.Foundation.Rect(pos.X - hw, pos.Y - hh, hw * 2, hh * 2);
                float rr = Math.Min(hw, hh);
                ds.FillRoundedRectangle(r, rr, rr, Colors.White);
                ds.DrawRoundedRectangle(r, rr, rr, Color.FromArgb(255, 91, 107, 255), 2f);
                continue;
            }
            ds.FillCircle(pos, HandleSize * 0.5f, Colors.White);
            ds.DrawCircle(pos, HandleSize * 0.5f, Color.FromArgb(255, 91, 107, 255), 2f);
        }
        if (TryRotateHandle(outer, out var rotPos))
        {
            var top = new Vector2(outer.X + outer.W * 0.5f, outer.Y);
            ds.DrawLine(top, rotPos, Color.FromArgb(255, 91, 107, 255), 1.5f);
            ds.FillCircle(rotPos, HandleSize * 0.55f, Colors.White);
            ds.DrawCircle(rotPos, HandleSize * 0.55f, Color.FromArgb(255, 91, 107, 255), 2f);
        }
    }

    private List<(DragMode mode, Vector2 pos)> HandlePositions(Bbox b)
    {
        var list = new List<(DragMode, Vector2)>
        {
            (DragMode.ResizeNW, new Vector2(b.X, b.Y)),
            (DragMode.ResizeNE, new Vector2(b.Right, b.Y)),
            (DragMode.ResizeSW, new Vector2(b.X, b.Bottom)),
            (DragMode.ResizeSE, new Vector2(b.Right, b.Bottom)),
        };
        // Side handles change only the width (text rewraps at the same size);
        // top/bottom handles change only the height. Text boxes get no
        // top/bottom handles: their height always fits their text.
        list.Add((DragMode.ResizeW, new Vector2(b.X, b.Y + b.H / 2)));
        list.Add((DragMode.ResizeE, new Vector2(b.Right, b.Y + b.H / 2)));
        if (!SelectionIsTextOnly)
        {
            list.Add((DragMode.ResizeN, new Vector2(b.X + b.W / 2, b.Y)));
            list.Add((DragMode.ResizeS, new Vector2(b.X + b.W / 2, b.Bottom)));
        }
        return list;
    }

    // Only text boxes are selected (no ink, shapes or images).
    private bool SelectionIsTextOnly =>
        Context.SelectedTextIds.Count > 0 && Context.SelectedStrokeIds.Count == 0
        && Context.SelectedShapeIds.Count == 0 && Context.SelectedImageIds.Count == 0;

    private bool TryRotateHandle(Bbox outer, out Vector2 pos)
    {
        pos = default;
        // Only enable rotate when exactly one element is selected.
        var count = Context.SelectedStrokeIds.Count + Context.SelectedShapeIds.Count
                  + Context.SelectedTextIds.Count + Context.SelectedImageIds.Count;
        if (count != 1) return false;
        pos = new Vector2(outer.X + outer.W * 0.5f, outer.Y - RotateHandleOffset);
        return true;
    }

    private DragMode HitTestHandles(Vector2 p, out Bbox outer)
    {
        outer = default;
        if (!SelectionBbox(out var bbox)) return DragMode.None;
        outer = new Bbox(bbox.X - 8, bbox.Y - 8, bbox.W + 16, bbox.H + 16);
        var r2 = (HandleSize * 0.7f) * (HandleSize * 0.7f);
        foreach (var (mode, pos) in HandlePositions(outer))
            if ((p - pos).LengthSquared() <= r2) return mode;
        if (TryRotateHandle(outer, out var rotPos))
            if ((p - rotPos).LengthSquared() <= r2) return DragMode.Rotate;
        return DragMode.None;
    }

    private bool HasCommittedSelection() =>
        (Context.SelectionRect is null && Context.SelectionLasso is null) &&
        (Context.SelectedStrokeIds.Count + Context.SelectedShapeIds.Count
         + Context.SelectedTextIds.Count + Context.SelectedImageIds.Count) > 0;

    public bool SelectionBbox(out Bbox b)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;
        foreach (var id in Context.SelectedStrokeIds)
        {
            var s = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (s is null) continue;
            var bb = Bbox.Of(s);
            if (bb.X < minX) minX = bb.X; if (bb.Y < minY) minY = bb.Y;
            if (bb.Right > maxX) maxX = bb.Right; if (bb.Bottom > maxY) maxY = bb.Bottom;
            any = true;
        }
        foreach (var id in Context.SelectedShapeIds)
        {
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            var bb = Bbox.Of(sh);
            if (bb.X < minX) minX = bb.X; if (bb.Y < minY) minY = bb.Y;
            if (bb.Right > maxX) maxX = bb.Right; if (bb.Bottom > maxY) maxY = bb.Bottom;
            any = true;
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            var bb = Bbox.Of(t);
            if (bb.X < minX) minX = bb.X; if (bb.Y < minY) minY = bb.Y;
            if (bb.Right > maxX) maxX = bb.Right; if (bb.Bottom > maxY) maxY = bb.Bottom;
            any = true;
        }
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            var bb = Bbox.Of(im);
            if (bb.X < minX) minX = bb.X; if (bb.Y < minY) minY = bb.Y;
            if (bb.Right > maxX) maxX = bb.Right; if (bb.Bottom > maxY) maxY = bb.Bottom;
            any = true;
        }
        b = new Bbox(minX, minY, maxX - minX, maxY - minY);
        return any;
    }

    private bool PointInSelectionBounds(Vector2 p)
    {
        if (!SelectionBbox(out var b)) return false;
        return p.X >= b.X - 8 && p.X <= b.Right + 8 && p.Y >= b.Y - 8 && p.Y <= b.Bottom + 8;
    }

    public void TranslateSelection(float dx, float dy)
    {
        foreach (var id in Context.SelectedStrokeIds)
        {
            var s = Page.Strokes.FirstOrDefault(z => z.Id == id);
            if (s is null) continue;
            for (int i = 0; i < s.Points.Count; i++)
            {
                var pt = s.Points[i];
                s.Points[i] = new InkPoint(pt.X + dx, pt.Y + dy, pt.Pressure);
            }
        }
        foreach (var id in Context.SelectedShapeIds)
        {
            var sh = Page.Shapes.FirstOrDefault(z => z.Id == id);
            if (sh is null) continue;
            sh.X1 += dx; sh.Y1 += dy;
            sh.X2 += dx; sh.Y2 += dy;
            // Triangles carry a third vertex; move it too or the shape distorts.
            if (sh.Kind == ShapeKind.Triangle) { sh.X3 += dx; sh.Y3 += dy; }
        }
        foreach (var id in Context.SelectedTextIds)
        {
            var t = Page.Texts.FirstOrDefault(z => z.Id == id);
            if (t is null) continue;
            t.X += dx; t.Y += dy;
        }
        foreach (var id in Context.SelectedImageIds)
        {
            var im = Page.Images.FirstOrDefault(z => z.Id == id);
            if (im is null) continue;
            im.X += dx; im.Y += dy;
        }
    }

    public void DeleteSelection()
    {
        if (!HasCommittedSelection()) return;
        Page.Strokes.RemoveAll(s => Context.SelectedStrokeIds.Contains(s.Id));
        Page.Shapes.RemoveAll(s => Context.SelectedShapeIds.Contains(s.Id));
        foreach (var id in Context.SelectedImageIds) _imageCache.Remove(id);
        Page.Texts.RemoveAll(t => Context.SelectedTextIds.Contains(t.Id));
        Page.Images.RemoveAll(i => Context.SelectedImageIds.Contains(i.Id));
        ClearSelection();
        Context.Mutated?.Invoke();
        _canvas.Invalidate();
    }

    // True when the selection holds anything that has a color (ink, shapes, text).
    public bool SelectionHasRecolorable =>
        Context.SelectedStrokeIds.Count + Context.SelectedShapeIds.Count + Context.SelectedTextIds.Count > 0;

    // True when every selected colorable item is a highlighter stroke — the
    // selection bar then offers the highlighter presets instead of the pen ones.
    public bool SelectionIsAllHighlighter =>
        Context.SelectedStrokeIds.Count > 0
        && Context.SelectedShapeIds.Count == 0 && Context.SelectedTextIds.Count == 0
        && Page.Strokes.Where(s => Context.SelectedStrokeIds.Contains(s.Id))
                       .All(s => s.Kind == StrokeKind.Highlighter);

    // Color of the first selected colorable item, used to mark the active swatch.
    public Color? SelectionColor()
    {
        foreach (var id in Context.SelectedStrokeIds)
            if (Page.Strokes.FirstOrDefault(s => s.Id == id) is { } s) return s.Color;
        foreach (var id in Context.SelectedShapeIds)
            if (Page.Shapes.FirstOrDefault(s => s.Id == id) is { } sh) return sh.Color;
        foreach (var id in Context.SelectedTextIds)
            if (Page.Texts.FirstOrDefault(t => t.Id == id) is { } t) return t.Color;
        return null;
    }

    // Recolors the selected strokes, shapes and text boxes in place. Highlighter
    // strokes keep their own alpha so they stay translucent; pen strokes stay
    // opaque even if a (translucent) highlighter swatch was picked. Recorded as
    // one undoable mutation via Context.Mutated, like Delete/Duplicate.
    public void RecolorSelection(Color c)
    {
        bool changed = false;
        foreach (var s in Page.Strokes.Where(s => Context.SelectedStrokeIds.Contains(s.Id)))
        {
            var nc = Color.FromArgb(s.Kind == StrokeKind.Highlighter ? s.Color.A : (byte)255, c.R, c.G, c.B);
            if (!nc.Equals(s.Color)) { s.Color = nc; changed = true; }
        }
        foreach (var sh in Page.Shapes.Where(s => Context.SelectedShapeIds.Contains(s.Id)))
            if (!sh.Color.Equals(c)) { sh.Color = c; changed = true; }
        foreach (var t in Page.Texts.Where(t => Context.SelectedTextIds.Contains(t.Id)))
            if (!t.Color.Equals(c)) { t.Color = c; changed = true; }
        if (!changed) return;

        Context.Mutated?.Invoke();
        if (SelectionBbox(out var b)) InvalidateSelectionRegion(b, 0);
        else _canvas.Invalidate();
    }

    // True when the selection holds anything with a line width (ink, shapes).
    public bool SelectionHasResizableWidth =>
        Context.SelectedStrokeIds.Count + Context.SelectedShapeIds.Count > 0;

    // Width of the first selected pen stroke (or highlighter / shape when there
    // is none), used to mark the active width dot on the selection bar.
    public float? SelectionWidth()
    {
        bool allHl = SelectionIsAllHighlighter;
        foreach (var id in Context.SelectedStrokeIds)
            if (Page.Strokes.FirstOrDefault(s => s.Id == id) is { } s
                && (allHl || s.Kind != StrokeKind.Highlighter)) return s.Width;
        foreach (var id in Context.SelectedShapeIds)
            if (Page.Shapes.FirstOrDefault(s => s.Id == id) is { } sh) return sh.StrokeWidth;
        return null;
    }

    // Sets the line width of the selected ink and shapes in place. The bar
    // offers pen widths unless everything selected is highlighter, so in a
    // mixed selection highlighter strokes keep their (much wider) width rather
    // than shrinking to a pen size. One undoable mutation, like Recolor.
    public void SetSelectionWidth(float width)
    {
        bool allHl = SelectionIsAllHighlighter;
        float oldMax = 0f;
        bool changed = false;
        foreach (var s in Page.Strokes.Where(s => Context.SelectedStrokeIds.Contains(s.Id)))
        {
            if (!allHl && s.Kind == StrokeKind.Highlighter) continue;
            if (s.Width == width) continue;
            oldMax = MathF.Max(oldMax, s.Width);
            s.Width = width;
            changed = true;
        }
        foreach (var sh in Page.Shapes.Where(s => Context.SelectedShapeIds.Contains(s.Id)))
        {
            if (allHl || sh.StrokeWidth == width) continue;
            oldMax = MathF.Max(oldMax, sh.StrokeWidth);
            sh.StrokeWidth = width;
            changed = true;
        }
        if (!changed) return;

        Context.Mutated?.Invoke();
        // Pad by the wider of old/new so a thinning stroke's old edge repaints too.
        if (SelectionBbox(out var b)) InvalidateSelectionRegion(b, MathF.Max(oldMax, width));
        else _canvas.Invalidate();
        // Selection chrome hugs the ink; refresh it for the new extent.
        Context.SelectionChanged?.Invoke();
    }

    // True when the selection holds handwriting that can be turned into text.
    public bool SelectionHasPenInk
    {
        get
        {
            if (Context.SelectedStrokeIds.Count == 0) return false;
            var sel = new HashSet<string>(Context.SelectedStrokeIds);
            return Page.Strokes.Any(s => s.Kind == StrokeKind.Pen && sel.Contains(s.Id));
        }
    }

    // The selected pen strokes, in the order they were written (the handwriting
    // recognizer relies on that order). Highlighter strokes are left out.
    public List<Stroke> SelectedPenStrokes()
    {
        var sel = new HashSet<string>(Context.SelectedStrokeIds);
        return Page.Strokes.Where(s => s.Kind == StrokeKind.Pen && sel.Contains(s.Id)).ToList();
    }

    // Swaps the selected pen ink for a text box holding `text`, placed where the
    // ink was. Other selected items (highlighter, shapes, text, images) stay.
    // One undoable mutation; the new text box ends up selected.
    public bool ReplaceSelectedPenInkWithText(string text, Color color, float fontSize)
    {
        var pen = SelectedPenStrokes();
        if (pen.Count == 0 || string.IsNullOrWhiteSpace(text)) return false;
        bool hadOld = SelectionBbox(out var oldRegion);

        var ink = Bbox.Of(pen[0]);
        foreach (var s in pen.Skip(1)) ink = Bbox.Union(ink, Bbox.Of(s));

        var ids = new HashSet<string>(pen.Select(s => s.Id));
        Page.Strokes.RemoveAll(s => ids.Contains(s.Id));
        Context.SelectedStrokeIds.RemoveAll(ids.Contains);

        var t = new TextElement
        {
            X = ink.X, Y = ink.Y,
            Width = Math.Max(ink.W + 24, 160),
            Text = text.Trim(), Color = color,
            FontSize = Math.Clamp(fontSize, 10f, 96f),
        };
        t.Height = Math.Max(PageRenderer.MeasureTextHeight(t) + 8, t.FontSize * 1.5);
        Page.Texts.Add(t);
        Context.SelectedTextIds.Add(t.Id);

        Context.Mutated?.Invoke();
        Context.SelectionChanged?.Invoke();
        var dirty = Bbox.Of(t);
        if (hadOld) dirty = Bbox.Union(dirty, oldRegion);
        InvalidateSelectionRegion(dirty, pen.Max(s => s.Width));
        return true;
    }

    public void DuplicateSelection(float offsetX = 24, float offsetY = 24)
    {
        var newStrokeIds = new List<string>();
        var newShapeIds = new List<string>();
        var newTextIds = new List<string>();
        var newImageIds = new List<string>();
        foreach (var s in Page.Strokes.Where(s => Context.SelectedStrokeIds.Contains(s.Id)).ToList())
        {
            var clone = new Stroke { Kind = s.Kind, Color = s.Color, Width = s.Width, PressureMode = s.PressureMode };
            foreach (var pt in s.Points)
                clone.Points.Add(new InkPoint(pt.X + offsetX, pt.Y + offsetY, pt.Pressure));
            Page.Strokes.Add(clone);
            newStrokeIds.Add(clone.Id);
        }
        foreach (var sh in Page.Shapes.Where(s => Context.SelectedShapeIds.Contains(s.Id)).ToList())
        {
            var clone = new ShapeElement
            {
                Kind = sh.Kind, Color = sh.Color, StrokeWidth = sh.StrokeWidth, Filled = sh.Filled,
                X1 = sh.X1 + offsetX, Y1 = sh.Y1 + offsetY,
                X2 = sh.X2 + offsetX, Y2 = sh.Y2 + offsetY,
                // Third vertex only means anything for triangles; leave the
                // unused zeros alone for other kinds.
                X3 = sh.Kind == ShapeKind.Triangle ? sh.X3 + offsetX : sh.X3,
                Y3 = sh.Kind == ShapeKind.Triangle ? sh.Y3 + offsetY : sh.Y3,
                Rotation = sh.Rotation
            };
            Page.Shapes.Add(clone);
            newShapeIds.Add(clone.Id);
        }
        foreach (var t in Page.Texts.Where(t => Context.SelectedTextIds.Contains(t.Id)).ToList())
        {
            var clone = new TextElement
            {
                X = t.X + offsetX, Y = t.Y + offsetY, Width = t.Width, Height = t.Height,
                Rotation = t.Rotation, Text = t.Text, FontSize = t.FontSize, Color = t.Color,
                FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic
            };
            Page.Texts.Add(clone);
            newTextIds.Add(clone.Id);
        }
        foreach (var im in Page.Images.Where(i => Context.SelectedImageIds.Contains(i.Id)).ToList())
        {
            var clone = new ImageElement
            {
                X = im.X + offsetX, Y = im.Y + offsetY, Width = im.Width, Height = im.Height,
                Rotation = im.Rotation, PngData = im.PngData
            };
            Page.Images.Add(clone);
            newImageIds.Add(clone.Id);
        }
        Context.SelectedStrokeIds.Clear(); Context.SelectedStrokeIds.AddRange(newStrokeIds);
        Context.SelectedShapeIds.Clear();  Context.SelectedShapeIds.AddRange(newShapeIds);
        Context.SelectedTextIds.Clear();   Context.SelectedTextIds.AddRange(newTextIds);
        Context.SelectedImageIds.Clear();  Context.SelectedImageIds.AddRange(newImageIds);
        Context.Mutated?.Invoke();
        // Repaint the clones' region and the originals' (chrome moved off it).
        if (SelectionBbox(out var nb))
            InvalidateSelectionRegion(
                Bbox.Union(nb, new Bbox(nb.X - offsetX, nb.Y - offsetY, nb.W, nb.H)), 0);
        else
            _canvas.Invalidate();
    }

    public void ClearSelection()
    {
        bool hadSelection = HasCommittedSelection();
        bool hadBbox = SelectionBbox(out var oldBbox);
        Context.SelectedStrokeIds.Clear();
        Context.SelectedShapeIds.Clear();
        Context.SelectedTextIds.Clear();
        Context.SelectedImageIds.Clear();
        Context.SelectionRect = null;
        Context.SelectionLasso = null;
        SyncMarqueeOverlay();
        // Only the old chrome region needs repainting — a full-page invalidate
        // here re-rendered everything on every outside-click.
        if (hadBbox) InvalidateSelectionRegion(oldBbox, 0);
        if (hadSelection) Context.SelectionChanged?.Invoke();
    }
}
