using System;
using System.Collections.Generic;
using System.Numerics;

namespace YuNotes.Models;

// Geometry for the shapes that are made of straight segments — arrow,
// coordinate axes and grid. The screen renderer, PDF export, eraser and live
// preview all draw from Segments(), so they always agree.
public static class ShapeGeometry
{
    public static bool IsSegmentKind(ShapeKind k) => k is ShapeKind.Arrow or ShapeKind.Axes or ShapeKind.Grid;

    // Length of an arrowhead's sides.
    public static float HeadSize(ShapeElement s) => MathF.Max(12f, s.StrokeWidth * 4.5f);

    // How far the drawing can reach outside the X1Y1–X2Y2 box (arrowheads).
    public static float Overhang(ShapeElement s) => s.Kind is ShapeKind.Arrow or ShapeKind.Axes ? HeadSize(s) : 0f;

    public static List<(Vector2 A, Vector2 B)> Segments(ShapeElement s)
    {
        var segs = new List<(Vector2, Vector2)>();
        float x1 = MathF.Min(s.X1, s.X2), x2 = MathF.Max(s.X1, s.X2);
        float y1 = MathF.Min(s.Y1, s.Y2), y2 = MathF.Max(s.Y1, s.Y2);
        float w = x2 - x1, h = y2 - y1;

        switch (s.Kind)
        {
            case ShapeKind.Arrow:
            {
                // From where the drag started to where it ended; head at the end.
                var a = new Vector2(s.X1, s.Y1);
                var b = new Vector2(s.X2, s.Y2);
                segs.Add((a, b));
                AddHead(segs, a, b, HeadSize(s));
                break;
            }

            case ShapeKind.Axes:
            {
                // x and y axes crossing in the middle of the box, arrowheads on
                // the positive ends (right and up), small ticks along both.
                float cx = x1 + w / 2, cy = y1 + h / 2;
                var left = new Vector2(x1, cy); var right = new Vector2(x2, cy);
                var bottom = new Vector2(cx, y2); var top = new Vector2(cx, y1);
                segs.Add((left, right));
                segs.Add((bottom, top));
                float head = HeadSize(s);
                AddHead(segs, left, right, head);
                AddHead(segs, bottom, top, head);

                float half = MathF.Min(w, h) / 2;
                if (half > 30)
                {
                    float tick = MathF.Max(4f, s.StrokeWidth * 2f);
                    float step = half / 5;
                    for (int i = -4; i <= 4; i++)
                    {
                        if (i == 0) continue;
                        float tx = cx + i * step, ty = cy + i * step;
                        if (tx > x1 + 2 && tx < x2 - head) segs.Add((new(tx, cy - tick), new(tx, cy + tick)));
                        if (ty > y1 + head && ty < y2 - 2) segs.Add((new(cx - tick, ty), new(cx + tick, ty)));
                    }
                }
                break;
            }

            case ShapeKind.Grid:
            {
                // Square-ish cells about 40 units across, at least one per side.
                int cols = Math.Clamp((int)MathF.Round(w / 40f), 1, 60);
                int rows = Math.Clamp((int)MathF.Round(h / 40f), 1, 60);
                for (int c = 0; c <= cols; c++)
                {
                    float x = x1 + w * c / cols;
                    segs.Add((new(x, y1), new(x, y2)));
                }
                for (int r = 0; r <= rows; r++)
                {
                    float y = y1 + h * r / rows;
                    segs.Add((new(x1, y), new(x2, y)));
                }
                break;
            }
        }
        return segs;
    }

    // Two short lines back from the tip at ±28°.
    private static void AddHead(List<(Vector2, Vector2)> segs, Vector2 from, Vector2 tip, float size)
    {
        var dir = tip - from;
        float len = dir.Length();
        if (len < 1f) return;
        dir /= len;
        size = MathF.Min(size, len * 0.6f);
        const float angle = 28f * MathF.PI / 180f;
        foreach (var sign in new[] { 1f, -1f })
        {
            float c = MathF.Cos(angle), sn = MathF.Sin(angle) * sign;
            var back = new Vector2(-dir.X * c + dir.Y * sn, -dir.Y * c - dir.X * sn);
            segs.Add((tip, tip + back * size));
        }
    }
}
