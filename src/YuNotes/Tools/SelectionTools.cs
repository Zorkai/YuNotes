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
        ctx.SelectedStrokeIds.Clear();
        ctx.SelectedShapeIds.Clear();
        ctx.SelectedTextIds.Clear();
        ctx.SelectedImageIds.Clear();
        ctx.SelectionRect = (p.X, p.Y, 0, 0);
        ctx.SelectionLasso = null;
        ctx.Invalidate?.Invoke();
    }

    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure)
    {
        if (!_down) return;
        var x = Math.Min(_start.X, p.X);
        var y = Math.Min(_start.Y, p.Y);
        var w = Math.Abs(_start.X - p.X);
        var h = Math.Abs(_start.Y - p.Y);
        ctx.SelectionRect = (x, y, w, h);
        ctx.Invalidate?.Invoke();
    }

    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = false;
        if (ctx.SelectionRect is { } r && r.W > 2 && r.H > 2)
        {
            ctx.LastDrawnRectSelection = r;
            var sel = new Bbox(r.X, r.Y, r.W, r.H);
            foreach (var s in ctx.CurrentPage.Strokes)
                if (sel.Overlaps(Bbox.Of(s))) ctx.SelectedStrokeIds.Add(s.Id);
            foreach (var sh in ctx.CurrentPage.Shapes)
                if (sel.Overlaps(Bbox.Of(sh))) ctx.SelectedShapeIds.Add(sh.Id);
            foreach (var t in ctx.CurrentPage.Texts)
                if (sel.Overlaps(Bbox.Of(t))) ctx.SelectedTextIds.Add(t.Id);
            foreach (var i in ctx.CurrentPage.Images)
                if (sel.Overlaps(Bbox.Of(i))) ctx.SelectedImageIds.Add(i.Id);
        }
        else
        {
            ctx.LastDrawnRectSelection = null;
        }
        ctx.SelectionRect = null;     // clear marquee so the committed selection can render
        ctx.Invalidate?.Invoke();
    }
}

public sealed class LassoTool : ITool
{
    public ToolKind Kind => ToolKind.Lasso;
    private bool _down;

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = true;
        ctx.SelectedStrokeIds.Clear();
        ctx.SelectedShapeIds.Clear();
        ctx.SelectedTextIds.Clear();
        ctx.SelectedImageIds.Clear();
        ctx.SelectionLasso = new List<Vector2> { p };
        ctx.SelectionRect = null;
        ctx.Invalidate?.Invoke();
    }

    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure)
    {
        if (!_down || ctx.SelectionLasso is null) return;
        ctx.SelectionLasso.Add(p);
        ctx.Invalidate?.Invoke();
    }

    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = false;
        if (ctx.SelectionLasso is { Count: >= 3 } lasso)
        {
            foreach (var s in ctx.CurrentPage.Strokes)
            {
                var c = Bbox.Of(s).Center;
                if (Bbox.PointInPolygon(c.X, c.Y, lasso)) ctx.SelectedStrokeIds.Add(s.Id);
            }
            foreach (var sh in ctx.CurrentPage.Shapes)
            {
                var c = Bbox.Of(sh).Center;
                if (Bbox.PointInPolygon(c.X, c.Y, lasso)) ctx.SelectedShapeIds.Add(sh.Id);
            }
            foreach (var t in ctx.CurrentPage.Texts)
            {
                var c = Bbox.Of(t).Center;
                if (Bbox.PointInPolygon(c.X, c.Y, lasso)) ctx.SelectedTextIds.Add(t.Id);
            }
            foreach (var i in ctx.CurrentPage.Images)
            {
                var c = Bbox.Of(i).Center;
                if (Bbox.PointInPolygon(c.X, c.Y, lasso)) ctx.SelectedImageIds.Add(i.Id);
            }
        }
        ctx.SelectionLasso = null;    // clear marquee
        ctx.Invalidate?.Invoke();
    }
}

public sealed class PanTool : ITool
{
    public ToolKind Kind => ToolKind.Pan;
    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure) { }
}
