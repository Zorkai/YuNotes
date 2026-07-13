using System;
using System.Collections.Generic;
using System.Numerics;
using YuNotes.Models;

namespace YuNotes.Tools;

public sealed class RectSelectTool : ITool
{
    public ToolKind Kind => ToolKind.RectSelect;
    private Vector2 _start;
    private bool _down;

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = true; _start = p;
        bool hadSelection = SelectionToolHelpers.HasSelection(ctx);
        ctx.SelectedStrokeIds.Clear();
        ctx.SelectedShapeIds.Clear();
        ctx.SelectedTextIds.Clear();
        ctx.SelectedImageIds.Clear();
        ctx.SelectionRect = (p.X, p.Y, 0, 0);
        ctx.SelectionLasso = null;
        // Old committed chrome needs erasing only if there WAS a selection;
        // the marquee itself is a XAML overlay element (InvalidateLive).
        if (hadSelection) ctx.Invalidate?.Invoke();
        ctx.InvalidateLive?.Invoke();
    }

    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure)
    {
        if (!_down) return;
        var x = Math.Min(_start.X, p.X);
        var y = Math.Min(_start.Y, p.Y);
        var w = Math.Abs(_start.X - p.X);
        var h = Math.Abs(_start.Y - p.Y);
        ctx.SelectionRect = (x, y, w, h);
        // Overlay-only marquee update — a full ctx.Invalidate here re-rendered
        // the whole page on every pointer move while dragging out the area.
        ctx.InvalidateLive?.Invoke();
    }

    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = false;
        Bbox? selBounds = null;
        if (ctx.SelectionRect is { } r && r.W > 2 && r.H > 2)
        {
            ctx.LastDrawnRectSelection = r;
            var sel = new Bbox(r.X, r.Y, r.W, r.H);
            foreach (var s in ctx.CurrentPage.Strokes)
                if (sel.Overlaps(Bbox.Of(s))) { ctx.SelectedStrokeIds.Add(s.Id); selBounds = SelectionToolHelpers.Union(selBounds, Bbox.Of(s)); }
            foreach (var sh in ctx.CurrentPage.Shapes)
                if (sel.Overlaps(Bbox.Of(sh))) { ctx.SelectedShapeIds.Add(sh.Id); selBounds = SelectionToolHelpers.Union(selBounds, Bbox.Of(sh)); }
            foreach (var t in ctx.CurrentPage.Texts)
                if (sel.Overlaps(Bbox.Of(t))) { ctx.SelectedTextIds.Add(t.Id); selBounds = SelectionToolHelpers.Union(selBounds, Bbox.Of(t)); }
            foreach (var i in ctx.CurrentPage.Images)
                if (sel.Overlaps(Bbox.Of(i))) { ctx.SelectedImageIds.Add(i.Id); selBounds = SelectionToolHelpers.Union(selBounds, Bbox.Of(i)); }
        }
        else
        {
            ctx.LastDrawnRectSelection = null;
        }
        ctx.SelectionRect = null;     // clear marquee so the committed selection can render
        ctx.InvalidateLive?.Invoke(); // remove the overlay marquee
        SelectionToolHelpers.InvalidateChrome(ctx, selBounds);
    }
}

internal static class SelectionToolHelpers
{
    public static bool HasSelection(EditorContext ctx) =>
        ctx.SelectedStrokeIds.Count + ctx.SelectedShapeIds.Count
        + ctx.SelectedTextIds.Count + ctx.SelectedImageIds.Count > 0;

    public static Bbox? Union(Bbox? acc, Bbox b) => acc is { } a ? Bbox.Union(a, b) : b;

    // Repaints just the region the committed selection chrome (tint, dashed
    // outline, handles, rotate stalk) occupies — nothing selected costs no
    // main-canvas work at all.
    public static void InvalidateChrome(EditorContext ctx, Bbox? selBounds)
    {
        if (selBounds is not { } b) return;
        const float pad = 56f;
        var region = new Bbox(b.X - pad, b.Y - pad, b.W + pad * 2, b.H + pad * 2);
        if (ctx.InvalidateRect is { } inv) inv(region);
        else ctx.Invalidate?.Invoke();
    }
}

public sealed class LassoTool : ITool
{
    public ToolKind Kind => ToolKind.Lasso;
    private bool _down;

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = true;
        bool hadSelection = SelectionToolHelpers.HasSelection(ctx);
        ctx.SelectedStrokeIds.Clear();
        ctx.SelectedShapeIds.Clear();
        ctx.SelectedTextIds.Clear();
        ctx.SelectedImageIds.Clear();
        ctx.SelectionLasso = new List<Vector2> { p };
        ctx.SelectionRect = null;
        if (hadSelection) ctx.Invalidate?.Invoke();
        ctx.InvalidateLive?.Invoke();
    }

    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure)
    {
        if (!_down || ctx.SelectionLasso is null) return;
        ctx.SelectionLasso.Add(p);
        // Overlay-only lasso update — see RectSelectTool.OnPointerMove.
        ctx.InvalidateLive?.Invoke();
    }

    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = false;
        Bbox? selBounds = null;
        if (ctx.SelectionLasso is { Count: >= 3 } lasso)
        {
            foreach (var s in ctx.CurrentPage.Strokes)
            {
                var b = Bbox.Of(s);
                if (Bbox.PointInPolygon(b.Center.X, b.Center.Y, lasso))
                { ctx.SelectedStrokeIds.Add(s.Id); selBounds = SelectionToolHelpers.Union(selBounds, b); }
            }
            foreach (var sh in ctx.CurrentPage.Shapes)
            {
                var b = Bbox.Of(sh);
                if (Bbox.PointInPolygon(b.Center.X, b.Center.Y, lasso))
                { ctx.SelectedShapeIds.Add(sh.Id); selBounds = SelectionToolHelpers.Union(selBounds, b); }
            }
            foreach (var t in ctx.CurrentPage.Texts)
            {
                var b = Bbox.Of(t);
                if (Bbox.PointInPolygon(b.Center.X, b.Center.Y, lasso))
                { ctx.SelectedTextIds.Add(t.Id); selBounds = SelectionToolHelpers.Union(selBounds, b); }
            }
            foreach (var i in ctx.CurrentPage.Images)
            {
                var b = Bbox.Of(i);
                if (Bbox.PointInPolygon(b.Center.X, b.Center.Y, lasso))
                { ctx.SelectedImageIds.Add(i.Id); selBounds = SelectionToolHelpers.Union(selBounds, b); }
            }
        }
        ctx.SelectionLasso = null;    // clear marquee
        ctx.InvalidateLive?.Invoke(); // remove the overlay lasso
        SelectionToolHelpers.InvalidateChrome(ctx, selBounds);
    }
}

public sealed class PanTool : ITool
{
    public ToolKind Kind => ToolKind.Pan;
    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure) { }
}
