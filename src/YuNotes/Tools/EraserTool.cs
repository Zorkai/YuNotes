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

    // True when the eraser circle touches the stroke's path — its points AND
    // the lines between them, so a fast stroke with widely spaced points can't
    // slip through the gaps.
    private static bool AnyPointInRadius(Stroke s, Vector2 p, float r2)
    {
        var pts = s.Points;
        for (int i = 0; i < pts.Count; i++)
        {
            var dx = pts[i].X - p.X;
            var dy = pts[i].Y - p.Y;
            if (dx * dx + dy * dy <= r2) return true;
            if (i > 0 && DistSqToSegment(p, new Vector2(pts[i - 1].X, pts[i - 1].Y), new Vector2(pts[i].X, pts[i].Y)) <= r2)
                return true;
        }
        return false;
    }

    // True when the eraser circle (center p, radius r) touches what the shape
    // actually draws: its outline, or its interior when Filled. A bbox test
    // alone erased a whole circle/rectangle when erasing writing inside it.
    // Geometry mirrors PageRenderer.DrawShape (rect/ellipse rotate about the
    // X1Y1-X2Y2 bbox center; line/triangle use raw vertices).
    private static bool ShapeHit(ShapeElement s, Vector2 p, float r)
    {
        float reach = r + MathF.Max(0f, s.StrokeWidth) * 0.5f;
        float reach2 = reach * reach;

        switch (s.Kind)
        {
            case ShapeKind.Line:
                return DistSqToSegment(p, new(s.X1, s.Y1), new(s.X2, s.Y2)) <= reach2;

            case ShapeKind.Arrow:
            case ShapeKind.Axes:
            case ShapeKind.Grid:
                foreach (var (a, b) in ShapeGeometry.Segments(s))
                    if (DistSqToSegment(p, a, b) <= reach2) return true;
                return false;

            case ShapeKind.Triangle:
            {
                Vector2 a = new(s.X1, s.Y1), b = new(s.X2, s.Y2), c = new(s.X3, s.Y3);
                if (DistSqToSegment(p, a, b) <= reach2 ||
                    DistSqToSegment(p, b, c) <= reach2 ||
                    DistSqToSegment(p, c, a) <= reach2) return true;
                return s.Filled && InTriangle(p, a, b, c);
            }

            case ShapeKind.Rectangle:
            case ShapeKind.Ellipse:
            case ShapeKind.Tape:
            {
                float x = MathF.Min(s.X1, s.X2), y = MathF.Min(s.Y1, s.Y2);
                float hw = MathF.Abs(s.X2 - s.X1) * 0.5f, hh = MathF.Abs(s.Y2 - s.Y1) * 0.5f;
                var center = new Vector2(x + hw, y + hh);

                // Undo the shape's rotation so we can test in its local frame.
                var q = p - center;
                if (s.Rotation != 0f && float.IsFinite(s.Rotation))
                {
                    float ang = -s.Rotation * MathF.PI / 180f;
                    float cos = MathF.Cos(ang), sin = MathF.Sin(ang);
                    q = new Vector2(q.X * cos - q.Y * sin, q.X * sin + q.Y * cos);
                }

                if (s.Kind is ShapeKind.Rectangle or ShapeKind.Tape)
                {
                    float ax = MathF.Abs(q.X), ay = MathF.Abs(q.Y);
                    bool inside = ax <= hw && ay <= hh;
                    if (inside)   // tape is always solid: erasing anywhere on it removes it
                        return s.Filled || s.Kind == ShapeKind.Tape || MathF.Min(hw - ax, hh - ay) <= reach;
                    float dx = MathF.Max(ax - hw, 0f), dy = MathF.Max(ay - hh, 0f);
                    return dx * dx + dy * dy <= reach2;
                }

                // Ellipse: distance to a fine polyline approximation of the outline.
                if (s.Filled && hw > 0 && hh > 0 &&
                    (q.X * q.X) / (hw * hw) + (q.Y * q.Y) / (hh * hh) <= 1f) return true;
                const int N = 72;
                var prev = new Vector2(hw, 0f);
                for (int i = 1; i <= N; i++)
                {
                    float t = i * (MathF.PI * 2f / N);
                    var cur = new Vector2(hw * MathF.Cos(t), hh * MathF.Sin(t));
                    if (DistSqToSegment(q, prev, cur) <= reach2) return true;
                    prev = cur;
                }
                return false;
            }
        }
        return false;
    }

    private static float DistSqToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 > 0f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Cross(Vector2 o, Vector2 u, Vector2 v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    // Ids of the strokes and shapes a stroke-mode erase at p would remove —
    // the same tests EraseAt uses — for the hover preview that fades them.
    public void CollectStrokeModeHits(NotePage page, Vector2 p, float width, ISet<string> into)
    {
        into.Clear();
        var r = width * 0.5f;
        var eraserBox = new Bbox(p.X - r, p.Y - r, r * 2, r * 2);
        foreach (var s in page.Strokes)
        {
            if (s.Points.Count == 0) continue;
            if (!eraserBox.Overlaps(BoxOf(s))) continue;
            if (AnyPointInRadius(s, p, r * r)) into.Add(s.Id);
        }
        foreach (var sh in page.Shapes)
            if (eraserBox.Overlaps(Bbox.Of(sh)) && ShapeHit(sh, p, r)) into.Add(sh.Id);
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

            // Shapes: remove any shape whose drawn outline (or fill) the eraser touches.
            var shapesToRemove = ctx.CurrentPage.Shapes
                .Where(sh => eraserBox.Overlaps(Bbox.Of(sh)) && ShapeHit(sh, p, r)).ToList();
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
                    var ns = new Stroke { Kind = s.Kind, Color = s.Color, Width = s.Width, PressureMode = s.PressureMode };
                    ns.Points.AddRange(seg);
                    ctx.CurrentPage.Strokes.Insert(i, ns);
                }
                changed = true;
            }
            // Pixel eraser also removes shapes whose drawn outline (or fill) it touches.
            var shapesToRemovePx = ctx.CurrentPage.Shapes
                .Where(sh => eraserBox.Overlaps(Bbox.Of(sh)) && ShapeHit(sh, p, r)).ToList();
            Bbox? shapeDirty = null;
            foreach (var sh in shapesToRemovePx)
            {
                ctx.CurrentPage.Shapes.Remove(sh);
                var b = Bbox.Of(sh);
                shapeDirty = shapeDirty is null ? b : Bbox.Union(shapeDirty.Value, b);
                changed = true;
            }

            if (changed)
            {
                float pad = maxAffectedWidth + 4f;
                dirty = new Bbox(p.X - r - pad, p.Y - r - pad, (r + pad) * 2, (r + pad) * 2);
                // A removed shape vanishes entirely, not just under the eraser.
                if (shapeDirty is { } sd) dirty = Bbox.Union(dirty.Value, sd);
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
