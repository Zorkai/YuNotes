using System;
using System.Numerics;
using YuNotes.Models;

namespace YuNotes.Tools;

public sealed class ShapeTool : ITool
{
    public ToolKind Kind => ToolKind.Shape;

    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;

    private ShapeElement? _active;
    // Stores the first pointer-down position so triangle vertex computation
    // always has the original drag anchor — _active.X1/Y1 is overwritten when
    // computing the apex and can no longer be used as the anchor.
    private Vector2 _anchor;

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        _anchor = p;
        _active = new ShapeElement
        {
            Kind = ShapeKind,
            Color = ctx.CurrentColor,
            StrokeWidth = ctx.CurrentWidth,
            X1 = p.X, Y1 = p.Y,
            X2 = p.X, Y2 = p.Y
        };
        if (ShapeKind == ShapeKind.Triangle)
            SetTriangleVertices(_active, _anchor, p);
        ctx.ActiveShape = _active;
        ctx.InvalidateLive?.Invoke();
    }

    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure)
    {
        if (_active is null) return;
        if (ShapeKind == ShapeKind.Triangle)
            SetTriangleVertices(_active, _anchor, p);
        else
        {
            _active.X2 = p.X;
            _active.Y2 = p.Y;
        }
        ctx.InvalidateLive?.Invoke();
    }

    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure)
    {
        if (_active is null) return;

        bool meaningful;
        if (ShapeKind == ShapeKind.Triangle)
        {
            SetTriangleVertices(_active, _anchor, p);
            // A triangle needs a visible area — check against the original drag delta.
            meaningful = Math.Abs(p.X - _anchor.X) > 2 || Math.Abs(p.Y - _anchor.Y) > 2;
        }
        else
        {
            _active.X2 = p.X;
            _active.Y2 = p.Y;
            meaningful = Math.Abs(_active.X2 - _active.X1) > 2 || Math.Abs(_active.Y2 - _active.Y1) > 2;
        }

        if (meaningful)
        {
            ctx.CurrentPage.Shapes.Add(_active);
            var committed = _active;
            ctx.ActiveShape = null;
            _active = null;
            ctx.Mutated?.Invoke();
            // Repaint just the new shape's area — the live preview was a XAML
            // overlay element, so nothing else on the main canvas changed.
            var b = Bbox.Of(committed);
            float pad = committed.StrokeWidth + 4f;
            if (ctx.InvalidateRect is { } inv)
                inv(new Bbox(b.X - pad, b.Y - pad, b.W + pad * 2, b.H + pad * 2));
            else
                ctx.Invalidate?.Invoke();
        }
        else
        {
            // Nothing committed (sub-2px drag): no model change, no dirty doc,
            // no history entry, no repaint. The canvas tears the preview down.
            ctx.ActiveShape = null;
            _active = null;
        }
    }

    // Derives triangle vertices from a bbox drag so the shape is always a
    // sensible upward-pointing triangle:
    //   V1 (X1,Y1) = apex         — center of the top edge
    //   V2 (X2,Y2) = bottom-left  — min-X, max-Y
    //   V3 (X3,Y3) = bottom-right — max-X, max-Y
    private static void SetTriangleVertices(ShapeElement s, Vector2 anchor, Vector2 current)
    {
        float minX = MathF.Min(anchor.X, current.X);
        float maxX = MathF.Max(anchor.X, current.X);
        float minY = MathF.Min(anchor.Y, current.Y);
        float maxY = MathF.Max(anchor.Y, current.Y);
        s.X1 = (minX + maxX) * 0.5f; s.Y1 = minY;  // apex
        s.X2 = minX;                  s.Y2 = maxY;  // bottom-left
        s.X3 = maxX;                  s.Y3 = maxY;  // bottom-right
    }
}
