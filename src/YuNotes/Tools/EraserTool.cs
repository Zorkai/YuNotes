using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YuNotes.Models;

namespace YuNotes.Tools;

public sealed class EraserTool : ITool
{
    public ToolKind Kind => ToolKind.Eraser;
    private bool _down;
    private bool _mutatedDuringGesture;

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = true;
        _mutatedDuringGesture = false;
        EraseAt(ctx, p);
    }

    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure)
    {
        if (_down) EraseAt(ctx, p);
    }

    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure)
    {
        _down = false;
        // Mutated fires once per erase GESTURE, not per sample: every Mutated
        // triggers a history snapshot that deep-copies the whole document, and
        // at pen sample rate that both froze the eraser on large documents and
        // shredded one drag into dozens of undo steps. Erased regions repaint
        // per sample via InvalidateRect regardless.
        if (_mutatedDuringGesture)
        {
            _mutatedDuringGesture = false;
            ctx.Mutated?.Invoke();
        }
    }

    // Cached per-stroke bboxes gate the point scans below: hit-testing
    // otherwise reads every point of every stroke on the page for every
    // eraser sample. Stamped by count + endpoints — every in-place mutation
    // (drag, resize) moves an endpoint, and pixel-erase splits produce new
    // stroke ids — same invalidation scheme as the renderer's geometry cache.
    private readonly Dictionary<string, ((int Count, float X0, float Y0, float X1, float Y1) Stamp, Bbox Box)> _bboxCache = new();

    private Bbox BoxOf(Stroke s)
    {
        var pts = s.Points;
        var stamp = (pts.Count, pts[0].X, pts[0].Y, pts[^1].X, pts[^1].Y);
        if (_bboxCache.TryGetValue(s.Id, out var hit) && hit.Stamp == stamp) return hit.Box;
        if (_bboxCache.Count >= 4096) _bboxCache.Clear();
        var b = Bbox.Of(s);
        _bboxCache[s.Id] = (stamp, b);
        return b;
    }

    private static bool AnyPointInRadius(Stroke s, Vector2 p, float r2)
    {
        var pts = s.Points;
        for (int i = 0; i < pts.Count; i++)
        {
            var dx = pts[i].X - p.X;
            var dy = pts[i].Y - p.Y;
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    private void EraseAt(EditorContext ctx, Vector2 p)
    {
        var r = ctx.EraserWidth * 0.5f;
        var r2 = r * r;
        bool changed = false;
        Bbox? dirty = null;
        var eraserBox = new Bbox(p.X - r, p.Y - r, r * 2, r * 2);

        if (!ctx.EraserPixelMode)
        {
            // Stroke eraser: remove any stroke whose any point is within r.
            // Dirty region is the union of removed-stroke bboxes — they vanish
            // entirely, so wherever they drew has to be repainted.
            List<Stroke>? toRemove = null;
            foreach (var s in ctx.CurrentPage.Strokes)
            {
                if (s.Points.Count == 0) continue;
                if (!eraserBox.Overlaps(BoxOf(s))) continue;
                if (AnyPointInRadius(s, p, r2)) (toRemove ??= new()).Add(s);
            }
            if (toRemove is not null)
                foreach (var s in toRemove)
                {
                    ctx.CurrentPage.Strokes.Remove(s);
                    var b = Bbox.Of(s);
                    dirty = dirty is null ? b : Bbox.Union(dirty.Value, b);
                    changed = true;
                }

            // Shapes: remove any shape whose bbox overlaps the eraser circle's bounding square.
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
                if (s.Points.Count == 0) continue;
                if (!eraserBox.Overlaps(BoxOf(s))) continue;
                if (!AnyPointInRadius(s, p, r2)) continue;

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
            var shapesToRemovePx = ctx.CurrentPage.Shapes
                .Where(sh => eraserBox.Overlaps(Bbox.Of(sh))).ToList();
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
            _mutatedDuringGesture = true;   // Mutated deferred to pointer-up
            if (dirty is { } d && ctx.InvalidateRect is { } inv) inv(d);
            else ctx.Invalidate?.Invoke();
        }
    }
}
