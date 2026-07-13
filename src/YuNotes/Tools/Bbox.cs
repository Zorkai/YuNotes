using System;
using System.Collections.Generic;
using System.Numerics;
using YuNotes.Models;

namespace YuNotes.Tools;

public readonly record struct Bbox(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public Vector2 Center => new(X + W * 0.5f, Y + H * 0.5f);

    public bool Overlaps(Bbox other)
        => !(Right < other.X || other.Right < X || Bottom < other.Y || other.Bottom < Y);

    public bool Contains(float px, float py)
        => px >= X && px <= Right && py >= Y && py <= Bottom;

    public static Bbox Of(Stroke s)
    {
        if (s.Points.Count == 0) return new Bbox(0, 0, 0, 0);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in s.Points)
        {
            if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
        }
        // Inflate slightly for stroke thickness so the bbox feels right around the ink.
        var pad = s.Width * 0.5f;
        return new Bbox(minX - pad, minY - pad, (maxX - minX) + pad * 2, (maxY - minY) + pad * 2);
    }

    // Bounds of the stroke as RENDERED: like Of(), but for smoothed (non-
    // pressure) strokes also pads by the Catmull-Rom overshoot bound — the
    // curve's control points sit up to max|P[i+1]-P[i-1]|/6 outside the sample
    // hull. Used for commit/redraw regions, where clipping even a pixel of ink
    // leaves a permanent sliver; selection keeps the tighter Of().
    public static Bbox OfRendered(Stroke s)
    {
        var b = Of(s);
        if (s.PressureMode || s.Points.Count < 3) return b;
        var pts = s.Points;
        float maxSpanSq = 0f;
        for (int i = 2; i < pts.Count; i++)
        {
            float dx = pts[i].X - pts[i - 2].X;
            float dy = pts[i].Y - pts[i - 2].Y;
            float d2 = dx * dx + dy * dy;
            if (d2 > maxSpanSq) maxSpanSq = d2;
        }
        float m = MathF.Sqrt(maxSpanSq) / 6f + 1f;
        return new Bbox(b.X - m, b.Y - m, b.W + m * 2, b.H + m * 2);
    }

    public static Bbox Of(ShapeElement s)
    {
        float x = Math.Min(s.X1, s.X2);
        float y = Math.Min(s.Y1, s.Y2);
        float maxX = Math.Max(s.X1, s.X2);
        float maxY = Math.Max(s.Y1, s.Y2);
        // Triangles carry a third vertex; ignoring it left their bbox roughly
        // half-width (X1 is the mid-top apex, X3 the bottom-right corner), so
        // erase/select hit-testing and partial repaints missed the right half.
        if (s.Kind == ShapeKind.Triangle)
        {
            x = Math.Min(x, s.X3);
            y = Math.Min(y, s.Y3);
            maxX = Math.Max(maxX, s.X3);
            maxY = Math.Max(maxY, s.Y3);
        }
        else if (s.Rotation != 0f)
        {
            // Rect/ellipse rotation is about the bbox center; hit-testing and
            // repaints need the bounds of the shape as drawn.
            var rb = RotatedAabb(x, y, maxX, maxY, s.Rotation);
            x = rb.X; y = rb.Y; maxX = rb.Right; maxY = rb.Bottom;
        }
        float w = Math.Max(1f, maxX - x);
        float h = Math.Max(1f, maxY - y);
        var pad = s.StrokeWidth * 0.5f;
        return new Bbox(x - pad, y - pad, w + pad * 2, h + pad * 2);
    }

    // Axis-aligned bounds of the rect (minX,minY)-(maxX,maxY) rotated by
    // rotationDeg about its own center.
    public static Bbox RotatedAabb(float minX, float minY, float maxX, float maxY, float rotationDeg)
    {
        float rad = rotationDeg * MathF.PI / 180f;
        float c = MathF.Abs(MathF.Cos(rad));
        float s = MathF.Abs(MathF.Sin(rad));
        float hw = (maxX - minX) * 0.5f, hh = (maxY - minY) * 0.5f;
        float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f;
        float rw = hw * c + hh * s;   // rotated half-extents
        float rh = hw * s + hh * c;
        return new Bbox(cx - rw, cy - rh, rw * 2, rh * 2);
    }

    public static Bbox Of(TextElement t) => new((float)t.X, (float)t.Y, (float)t.Width, (float)t.Height);
    public static Bbox Of(ImageElement i) => new((float)i.X, (float)i.Y, (float)i.Width, (float)i.Height);

    public static Bbox Union(Bbox a, Bbox b)
    {
        var x = System.Math.Min(a.X, b.X);
        var y = System.Math.Min(a.Y, b.Y);
        var r = System.Math.Max(a.Right, b.Right);
        var btm = System.Math.Max(a.Bottom, b.Bottom);
        return new Bbox(x, y, r - x, btm - y);
    }

    public static bool PointInPolygon(float x, float y, IList<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if (((poly[i].Y > y) != (poly[j].Y > y)) &&
                (x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y + 1e-6f) + poly[i].X))
                inside = !inside;
        }
        return inside;
    }
}
