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

    public static Bbox Of(ShapeElement s)
    {
        float x = Math.Min(s.X1, s.X2);
        float y = Math.Min(s.Y1, s.Y2);
        float w = Math.Max(1f, Math.Abs(s.X2 - s.X1));
        float h = Math.Max(1f, Math.Abs(s.Y2 - s.Y1));
        var pad = s.StrokeWidth * 0.5f;
        return new Bbox(x - pad, y - pad, w + pad * 2, h + pad * 2);
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
