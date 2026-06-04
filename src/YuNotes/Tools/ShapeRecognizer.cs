using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;
using YuNotes.Models;

namespace YuNotes.Tools;

// Attempts to recognize a freehand pen stroke as a geometric primitive.
// Called by PenTool after the stylus has been held still for ~750 ms.
// Returns a ShapeElement ready to commit, or null if no shape matches.
internal static class ShapeRecognizer
{
    private const float MinPathLength = 30f;
    private const int ResampleCount  = 128;

    public static ShapeElement? Recognize(
        IReadOnlyList<InkPoint> points, Color color, float strokeWidth)
    {
        if (points.Count < 6) return null;

        float pathLen = PathLength(points);
        if (pathLen < MinPathLength) return null;

        // Normalize to uniform point density before any analysis.
        // Raw stylus input has variable density (many points when drawing slowly,
        // few when drawing quickly) which makes window-based algorithms inconsistent.
        var pts = Resample(points, ResampleCount);

        // ── Line ─────────────────────────────────────────────────────────────
        if (IsLine(pts, out float lx1, out float ly1, out float lx2, out float ly2))
            return Make(ShapeKind.Line, lx1, ly1, lx2, ly2, color, strokeWidth);

        // Closed-shape tests
        if (!IsClosed(pts, pathLen)) return null;

        BoundingBox(pts, out float bx1, out float by1, out float bx2, out float by2);

        // ── Ellipse / Circle ─────────────────────────────────────────────────
        // Tested before rectangle: a round shape can trigger the corner detector
        // if it has small bumps, so we must rule it out first.
        if (IsEllipse(pts))
            return Make(ShapeKind.Ellipse, bx1, by1, bx2, by2, color, strokeWidth);

        // ── Triangle ─────────────────────────────────────────────────────────
        // Exactly 3 corners; tested before rectangle (which now requires 4-6).
        if (IsTriangle(pts, out Vector2 tv1, out Vector2 tv2, out Vector2 tv3))
            return MakeTriangle(tv1, tv2, tv3, color, strokeWidth);

        // ── Rectangle / Square ───────────────────────────────────────────────
        if (IsRectangle(pts))
            return Make(ShapeKind.Rectangle, bx1, by1, bx2, by2, color, strokeWidth);

        return null;
    }

    private static ShapeElement Make(
        ShapeKind k, float x1, float y1, float x2, float y2, Color c, float w)
        => new() { Kind = k, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Color = c, StrokeWidth = w };

    private static ShapeElement MakeTriangle(
        Vector2 v1, Vector2 v2, Vector2 v3, Color c, float w)
        => new() { Kind = ShapeKind.Triangle,
                   X1 = v1.X, Y1 = v1.Y,
                   X2 = v2.X, Y2 = v2.Y,
                   X3 = v3.X, Y3 = v3.Y,
                   Color = c, StrokeWidth = w };

    // ── Resampling ($1-Unistroke style) ───────────────────────────────────────

    // Inserts interpolated points until the result has exactly n equally-spaced
    // samples. Preserves the original start/end endpoints exactly.
    private static List<Vector2> Resample(IReadOnlyList<InkPoint> input, int n)
    {
        var pts = new List<Vector2>(input.Count + n);
        foreach (var p in input) pts.Add(new Vector2(p.X, p.Y));

        float total = 0;
        for (int i = 1; i < pts.Count; i++) total += Vector2.Distance(pts[i - 1], pts[i]);
        if (total <= 0) return pts;

        float interval = total / (n - 1);
        float d = 0;
        var result = new List<Vector2>(n) { pts[0] };

        for (int i = 1; i < pts.Count && result.Count < n; i++)
        {
            float seg = Vector2.Distance(pts[i - 1], pts[i]);
            if (d + seg >= interval)
            {
                float t = (interval - d) / seg;
                var q = pts[i - 1] + t * (pts[i] - pts[i - 1]);
                result.Add(q);
                pts.Insert(i, q); // continue from the interpolated point
                d = 0;
            }
            else { d += seg; }
        }

        while (result.Count < n) result.Add(pts[^1]);
        return result;
    }

    // ── Geometric tests ───────────────────────────────────────────────────────

    private static float PathLength(IReadOnlyList<InkPoint> pts)
    {
        float len = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            len += MathF.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }

    // Chord-to-path > 0.92 AND max perpendicular deviation < 10% of chord.
    private static bool IsLine(List<Vector2> pts,
        out float x1, out float y1, out float x2, out float y2)
    {
        x1 = pts[0].X;  y1 = pts[0].Y;
        x2 = pts[^1].X; y2 = pts[^1].Y;
        float dx = x2 - x1, dy = y2 - y1;
        float chord = MathF.Sqrt(dx * dx + dy * dy);
        if (chord < 15f) return false;

        // For uniformly-resampled points every segment has the same length.
        float segLen = Vector2.Distance(pts[0], pts[1]);
        float pathLen = (pts.Count - 1) * segLen;
        if (chord / pathLen < 0.92f) return false;

        float maxPerp = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            float perp = MathF.Abs((pts[i].X - x1) * dy - (pts[i].Y - y1) * dx) / chord;
            if (perp > maxPerp) maxPerp = perp;
        }
        return maxPerp / chord < 0.10f;
    }

    // Closed = end within 50 px of start (absolute), or gap < 25% of path length.
    // The absolute threshold handles small shapes where the relative check is
    // too strict (e.g. a 120 px circle closed to within 40 px → 33%, fails).
    private static bool IsClosed(List<Vector2> pts, float originalPathLen)
    {
        float gap = Vector2.Distance(pts[0], pts[^1]);
        return gap < 50f || gap / (originalPathLen + 0.001f) < 0.25f;
    }

    private static void BoundingBox(List<Vector2> pts,
        out float x1, out float y1, out float x2, out float y2)
    {
        x1 = float.MaxValue; y1 = float.MaxValue;
        x2 = float.MinValue; y2 = float.MinValue;
        foreach (var p in pts)
        {
            if (p.X < x1) x1 = p.X; if (p.X > x2) x2 = p.X;
            if (p.Y < y1) y1 = p.Y; if (p.Y > y2) y2 = p.Y;
        }
    }

    // Ellipse: standard deviation of radii from centroid / mean < 0.22.
    // Using stddev instead of (max-min) makes the test robust to isolated bumps
    // or the slight spike a stylus produces when lifting at stroke end.
    private static bool IsEllipse(List<Vector2> pts)
    {
        // A shape with 3+ sharp corners is a polygon, not a round shape.
        // Necessary because a square's radius stddev/mean is only ~0.11, which
        // would otherwise pass the 0.22 threshold below.
        if (CountCorners(pts, minTurnDeg: 40f) >= 3) return false;

        float cx = 0, cy = 0;
        foreach (var p in pts) { cx += p.X; cy += p.Y; }
        cx /= pts.Count; cy /= pts.Count;

        float sumR = 0, sumR2 = 0;
        foreach (var p in pts)
        {
            float r = Vector2.Distance(p, new Vector2(cx, cy));
            sumR += r; sumR2 += r * r;
        }
        float mean   = sumR / pts.Count;
        float stddev = MathF.Sqrt(MathF.Max(0f, sumR2 / pts.Count - mean * mean));
        return mean > 8f && stddev / mean < 0.22f;
    }

    // Triangle: closed path with exactly 3 detected corners.
    // Returns the three approximate corner positions (used as vertices).
    private static bool IsTriangle(List<Vector2> pts,
        out Vector2 v1, out Vector2 v2, out Vector2 v3)
    {
        v1 = v2 = v3 = default;
        var corners = FindCornerPositions(pts, minTurnDeg: 40f);
        if (corners.Count != 3) return false;
        v1 = corners[0]; v2 = corners[1]; v3 = corners[2];
        return true;
    }

    // Rectangle: closed path with 4–6 distinct direction-change corners.
    // (3 corners → triangle, handled above.)
    private static bool IsRectangle(List<Vector2> pts)
    {
        int corners = CountCorners(pts, minTurnDeg: 40f);
        return corners is >= 4 and <= 6;
    }

    // ── Corner utilities ──────────────────────────────────────────────────────
    //
    // The main scan runs from index w to n-w, which means any corner the user
    // drew RIGHT AT the start/end of the stroke (index ~0) falls in the dead zone
    // and is missed.  A seam check tests that one position explicitly:
    //
    //   before = direction arriving at index 0  (pts[0] − pts[n-w])
    //   after  = direction leaving  index 0  (pts[w] − pts[0])
    //
    // This is appended only when the main scan ended cleanly (inCorner=false),
    // which avoids double-counting a corner that was already partially detected
    // near the end of the scan range.

    // Counts distinct direction-change corners where the path turns > minTurnDeg.
    private static int CountCorners(List<Vector2> pts, float minTurnDeg)
    {
        int n = pts.Count;
        int w = Math.Max(3, n / 15);
        float threshold = minTurnDeg * MathF.PI / 180f;
        int corners = 0;
        bool inCorner = false;

        for (int i = w; i < n - w; i++)
        {
            var before = pts[i] - pts[i - w];
            var after  = pts[i + w] - pts[i];
            float bLen = before.Length(), aLen = after.Length();
            if (bLen < 0.5f || aLen < 0.5f) continue;

            float dot  = MathF.Max(-1f, MathF.Min(1f,
                Vector2.Dot(before / bLen, after / aLen)));
            float turn = MathF.Acos(dot);

            if (turn > threshold) { if (!inCorner) { corners++; inCorner = true; } }
            else inCorner = false;
        }

        // Seam check: test the corner at index 0 that the main scan cannot reach.
        if (!inCorner)
        {
            var sb = pts[0] - pts[n - w];
            var sa = pts[w]   - pts[0];
            float bl = sb.Length(), al = sa.Length();
            if (bl > 0.5f && al > 0.5f)
            {
                float dot = MathF.Max(-1f, MathF.Min(1f,
                    Vector2.Dot(sb / bl, sa / al)));
                if (MathF.Acos(dot) > threshold) corners++;
            }
        }

        return corners;
    }

    // Returns the approximate positions of corners (one per distinct turn event).
    // Uses the midpoint of each contiguous above-threshold region as the vertex.
    private static List<Vector2> FindCornerPositions(List<Vector2> pts, float minTurnDeg)
    {
        int n = pts.Count;
        int w = Math.Max(3, n / 15);
        float threshold = minTurnDeg * MathF.PI / 180f;
        var positions = new List<Vector2>();
        bool inCorner = false;
        int cornerStart = -1;

        for (int i = w; i < n - w; i++)
        {
            var before = pts[i] - pts[i - w];
            var after  = pts[i + w] - pts[i];
            float bLen = before.Length(), aLen = after.Length();
            if (bLen < 0.5f || aLen < 0.5f) continue;

            float dot  = MathF.Max(-1f, MathF.Min(1f,
                Vector2.Dot(before / bLen, after / aLen)));
            float turn = MathF.Acos(dot);

            if (turn > threshold)
            {
                if (!inCorner) { inCorner = true; cornerStart = i; }
            }
            else
            {
                if (inCorner)
                {
                    // Midpoint of the contiguous above-threshold region.
                    positions.Add(pts[(cornerStart + i) / 2]);
                    inCorner = false;
                }
            }
        }
        if (inCorner && cornerStart >= 0)
            positions.Add(pts[(cornerStart + n - w) / 2]);

        // Seam check: detect a corner near index 0 that the main scan skips.
        if (!inCorner)
        {
            var sb = pts[0] - pts[n - w];
            var sa = pts[w]   - pts[0];
            float bl = sb.Length(), al = sa.Length();
            if (bl > 0.5f && al > 0.5f)
            {
                float dot = MathF.Max(-1f, MathF.Min(1f,
                    Vector2.Dot(sb / bl, sa / al)));
                if (MathF.Acos(dot) > threshold)
                    positions.Insert(0, pts[0]); // prepend: seam corner is the stroke's start point
            }
        }

        return positions;
    }
}
