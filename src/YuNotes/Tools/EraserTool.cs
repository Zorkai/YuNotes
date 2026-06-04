using System;
using System.Linq;
using System.Numerics;
using YuNotes.Models;

namespace YuNotes.Tools;

public sealed class EraserTool : ITool
{
    public ToolKind Kind => ToolKind.Eraser;
    private bool _down;

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = true;
        EraseAt(ctx, p);
    }

    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure)
    {
        if (_down) EraseAt(ctx, p);
    }

    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = false;
    }

    private void EraseAt(EditorContext ctx, Vector2 p)
    {
        var r = ctx.EraserWidth * 0.5f;
        var r2 = r * r;
        bool changed = false;
        Bbox? dirty = null;

        if (!ctx.EraserPixelMode)
        {
            // Stroke eraser: remove any stroke whose any point is within r.
            // Dirty region is the union of removed-stroke bboxes — they vanish
            // entirely, so wherever they drew has to be repainted.
            var toRemove = ctx.CurrentPage.Strokes
                .Where(s => s.Points.Any(pt =>
                {
                    var dx = pt.X - p.X; var dy = pt.Y - p.Y;
                    return dx * dx + dy * dy <= r2;
                })).ToList();
            foreach (var s in toRemove)
            {
                ctx.CurrentPage.Strokes.Remove(s);
                var b = Bbox.Of(s);
                dirty = dirty is null ? b : Bbox.Union(dirty.Value, b);
                changed = true;
            }

            // Shapes: remove any shape whose bbox overlaps the eraser circle's bounding square.
            var eraserBox = new Bbox(p.X - r, p.Y - r, r * 2, r * 2);
            var shapesToRemove = ctx.CurrentPage.Shapes
                .Where(sh => eraserBox.Overlaps(Bbox.Of(sh))).ToList();
            foreach (var sh in shapesToRemove)
            {
                ctx.CurrentPage.Shapes.Remove(sh);
                var b = Bbox.Of(sh);
                dirty = dirty is null ? b : Bbox.Union(dirty.Value, b);
                changed = true;
            }
        }
        else
        {
            // Pixel eraser: split strokes by dropping points within radius.
            // Dirty region is the eraser stamp itself, padded by the widest
            // affected stroke (Catmull-Rom rebuild at the cut boundary can shift
            // the curve by ~one stroke-width).
            float maxAffectedWidth = 0f;
            for (int i = ctx.CurrentPage.Strokes.Count - 1; i >= 0; i--)
            {
                var s = ctx.CurrentPage.Strokes[i];
                if (!s.Points.Any(pt =>
                {
                    var dx = pt.X - p.X; var dy = pt.Y - p.Y;
                    return dx * dx + dy * dy <= r2;
                })) continue;

                if (s.Width > maxAffectedWidth) maxAffectedWidth = s.Width;

                var segments = new System.Collections.Generic.List<System.Collections.Generic.List<InkPoint>>();
                var current = new System.Collections.Generic.List<InkPoint>();
                foreach (var pt in s.Points)
                {
                    var dx = pt.X - p.X; var dy = pt.Y - p.Y;
                    if (dx * dx + dy * dy <= r2)
                    {
                        if (current.Count > 1) segments.Add(current);
                        current = new();
                    }
                    else current.Add(pt);
                }
                if (current.Count > 1) segments.Add(current);

                ctx.CurrentPage.Strokes.RemoveAt(i);
                foreach (var seg in segments)
                {
                    var ns = new Stroke { Kind = s.Kind, Color = s.Color, Width = s.Width };
                    ns.Points.AddRange(seg);
                    ctx.CurrentPage.Strokes.Insert(i, ns);
                }
                changed = true;
            }
            // Pixel eraser also removes shapes whose bbox the eraser circle hits.
            var eraserBoxPx = new Bbox(p.X - r, p.Y - r, r * 2, r * 2);
            var shapesToRemovePx = ctx.CurrentPage.Shapes
                .Where(sh => eraserBoxPx.Overlaps(Bbox.Of(sh))).ToList();
            foreach (var sh in shapesToRemovePx)
            {
                ctx.CurrentPage.Shapes.Remove(sh);
                changed = true;
            }

            if (changed)
            {
                float pad = maxAffectedWidth + 4f;
                dirty = new Bbox(p.X - r - pad, p.Y - r - pad, (r + pad) * 2, (r + pad) * 2);
            }
        }

        if (changed)
        {
            ctx.Mutated?.Invoke();
            if (dirty is { } d && ctx.InvalidateRect is { } inv) inv(d);
            else ctx.Invalidate?.Invoke();
        }
    }
}
