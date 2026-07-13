using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;
using YuNotes.Models;

namespace YuNotes.Tools;

// Attempts to recognize a freehand pen stroke as a geometric primitive.
// Called by PenTool after the stylus has been held still for ~750 ms.
// Returns a ShapeElement ready to commit, or null if no shape matches.
//
// Two-stage design:
//  1. Universal gates (closure, size, winding). These have to be strict:
//     the hold timer fires on any writing pause, so the shape tests run
//     constantly on ordinary HANDWRITING, and a false positive silently
//     converts the user's word into a shape (reported 2026-07-10,
//     "surprise rectangles").
//  2. Every representable shape (ellipse / rectangle / triangle) is scored
//     against the ink and the best geometric fit wins. Fixed-order
//     first-match testing was the main source of shape-vs-shape confusion:
//     a rectangle with rounded corners could edge past the ellipse test
//     before the rectangle test ever ran.
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
        {
            SnapLineAngle(ref lx1, ref ly1, ref lx2, ref ly2);
            return Make(ShapeKind.Line, lx1, ly1, lx2, ly2, color, strokeWidth);
        }

        // ── Closed-shape gates ───────────────────────────────────────────────
        if (!IsClosed(pts, pathLen)) return null;

        // A deliberately traced shape winds around its interior exactly once;
        // handwriting zigzags (net winding near 0) or loops repeatedly.
        if (!WindsOnce(pts)) return null;

        // Corner analysis runs on the raw (non-derotated) points: turn angles
        // are rotation-invariant, and the triangle test needs vertex positions
        // in page space.
        var regions = FindCornerRegions(pts, minTurnDeg: 40f);
        int corners = 0;
        foreach (var r in regions) corners += r.TurnCount;

        // Nobody draws perfectly axis-aligned: a 3:1 ellipse drawn 5° off-axis
        // deviates from the bbox-inscribed ellipse by ~9% of its major axis at
        // the tips and was rejected outright. Estimate the tilt from the ink's
        // dominant edge direction and analyze in the derotated frame; the
        // folded estimate never exceeds 45°, so any rect/ellipse orientation is
        // reachable. Small tilts (≤ ~3°) are committed straightened — that's
        // what a beautifier should do — while a deliberate tilt is committed
        // as ShapeElement.Rotation (see MakeRotated), so tilted rectangles,
        // diamonds, and slanted ellipses snap instead of staying ink.
        float tilt = EstimateTilt(pts);
        Vector2 centroid = Centroid(pts);
        var ptsD = tilt != 0f ? Derotate(pts, tilt, centroid) : pts;

        BoundingBox(ptsD, out float bx1, out float by1, out float bx2, out float by2);

        // Letter-sized closed loops (o, e, a, ...) are writing, not shapes.
        float minDim = MathF.Min(bx2 - bx1, by2 - by1);
        if (minDim < 25f) return null;

        // ── Shared features ──────────────────────────────────────────────────

        // Scale used to normalize fit tolerances. Normalizing by minDim alone
        // starves elongated shapes: hand wobble grows with the stroke's overall
        // extent, so a 3:1 ellipse whose short axis is 60 px was held to the
        // tolerance of a 60 px circle and rejected ~40% of the time. Averaging
        // in the long dimension (capped at 2×minDim so a wide short word bbox
        // doesn't inflate it) tracks the actual wobble scale.
        float maxDim = MathF.Max(bx2 - bx1, by2 - by1);
        float scale = MathF.Min(0.5f * (minDim + maxDim), 2f * minDim);

        // Fraction of the bounding box the traced loop encloses. Strongly
        // shape-typed and independent of the fit tests: rectangles ≈ 1.0,
        // ellipses ≈ π/4 ≈ 0.785, triangles and diamonds ≈ 0.5. Serves as an
        // eligibility gate so shapes the app cannot represent (diamonds,
        // rotated polygons) are rejected and left as ink instead of being
        // snapped to whichever axis-aligned shape they resemble least badly —
        // a diamond's mean deviation from the bbox-inscribed ellipse is small
        // enough to pass the ellipse fit alone.
        float areaRatio = PolygonArea(ptsD)
            / MathF.Max((bx2 - bx1) * (by2 - by1), 1e-3f);

        // ── Candidates: best fit wins ────────────────────────────────────────
        // All errors are mean px deviation of the ink from the exact outline a
        // snap would commit, so they are directly comparable across shapes.
        // Ellipses and rectangles are centrally symmetric about the bbox
        // center; a D-shape (arc + flat chord), a blob, or handwriting is not.
        // The fit tests alone can't catch a D — its mean deviation from the
        // inscribed ellipse is small — but its flat side reflects onto the
        // bulge and fails this immediately.
        float symErr = SymmetryError(ptsD, (bx1 + bx2) * 0.5f, (by1 + by2) * 0.5f);
        bool symmetric = symErr < MathF.Max(0.06f * scale, 4.5f);

        ShapeElement? best = null;
        float bestErr = float.MaxValue;

        float ellErr  = EllipseFitError(ptsD, bx1, by1, bx2, by2, out float ellMax);
        float rectErr = RectFitError(ptsD, bx1, by1, bx2, by2, out float rectMax);

        // Ellipse: area near π/4, ink hugging the bbox-inscribed ellipse, and
        // fitting the ellipse no worse than the bbox perimeter. The relative
        // comparison (not a corner-count gate — a clean 3:1 ellipse's tips
        // register as corner regions) matters when the pen gap swallowed one
        // rectangle corner: the corner points are exactly the ones the
        // ellipse fit penalizes, so with them missing the ellipse's absolute
        // error looks fine, but the rectangle fit is still clearly better.
        if (symmetric && ellErr <= rectErr && areaRatio is > 0.60f and < 0.92f)
        {
            if (ellErr < MathF.Max(0.08f * scale, 4f)
                && ellMax < MathF.Max(0.30f * scale, 9f))
            {
                best = MakeRotated(ShapeKind.Ellipse, bx1, by1, bx2, by2,
                    tilt, centroid, color, strokeWidth);
                bestErr = ellErr;
            }
        }

        // Rectangle / Square: 3–6 distinct corners (3 happens when the pen gap
        // swallowed a corner), area filling the bbox, and ink hugging the bbox
        // perimeter. The corner count alone is nowhere near sufficient — half
        // a handwritten word has 4–6 direction changes and a nearly-closed
        // path. Absolute floors keep small deliberate rectangles
        // (checkbox-sized) recognizable despite wobble.
        if (symmetric && corners is >= 3 and <= 6 && areaRatio > 0.75f)
        {
            if (rectErr < MathF.Max(0.10f * scale, 4f)
                && rectMax < MathF.Max(0.35f * scale, 10f)
                && rectErr < bestErr)
            {
                best = MakeRotated(ShapeKind.Rectangle, bx1, by1, bx2, by2,
                    tilt, centroid, color, strokeWidth);
                bestErr = rectErr;
            }
        }

        // Triangle: exactly 3 detected corners with all ink near the edges
        // those corners define. This is the original triangle algorithm (a
        // fancier side-line-intersection reconstruction produced distorted
        // triangles on real strokes and was reverted at the user's request,
        // 2026-07-13) with one targeted fix for the corner where the pen
        // started and stopped — see SeamAwareVertex.
        if (regions.Count == 3 && TriangleVertices(pts, regions, out var tverts))
        {
            BoundingBox(pts, out float tx1, out float ty1, out float tx2, out float ty2);
            float tMin = MathF.Min(tx2 - tx1, ty2 - ty1);
            float err = TriangleFitError(pts, tverts[0], tverts[1], tverts[2], out float max);
            if (err < MathF.Max(0.12f * tMin, 4f)
                && max < MathF.Max(0.40f * tMin, 10f)
                && err < bestErr)
            {
                best = MakeTriangle(tverts[0], tverts[1], tverts[2], color, strokeWidth);
                bestErr = err;
            }
        }

        return best;
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

    // Commits a rect/ellipse whose fit ran in the derotated frame. Tilts of a
    // few degrees are committed straightened (beautified); a deliberate tilt
    // is kept via ShapeElement.Rotation. The renderer rotates about the
    // shape's own bbox center, but the analysis derotated about the STROKE
    // CENTROID — so the bbox is translated to the position whose center-
    // rotation reproduces the centroid-rotation placement exactly
    // (m' = c + R·(m − c)).
    private static ShapeElement MakeRotated(
        ShapeKind k, float bx1, float by1, float bx2, float by2,
        float tilt, Vector2 centroid, Color c, float w)
    {
        float deg = tilt * 180f / MathF.PI;
        if (MathF.Abs(deg) <= 3f)
            return Make(k, bx1, by1, bx2, by2, c, w);

        var m = new Vector2((bx1 + bx2) * 0.5f, (by1 + by2) * 0.5f);
        var d = m - centroid;
        float cos = MathF.Cos(tilt), sin = MathF.Sin(tilt);
        var off = centroid + new Vector2(cos * d.X - sin * d.Y,
                                         sin * d.X + cos * d.Y) - m;
        var s = Make(k, bx1 + off.X, by1 + off.Y, bx2 + off.X, by2 + off.Y, c, w);
        s.Rotation = deg;
        return s;
    }

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

    // Chord-to-path > 0.92 AND max perpendicular deviation < 10% of chord,
    // evaluated on hook-trimmed points: real pen strokes start and end with a
    // small tail a few px long, and with raw endpoints defining the chord a
    // hook rotates the whole test line and fails clean lines. The committed
    // endpoints stay the raw ones when they sit near the trimmed chord (so
    // deliberate line length is preserved), and fall back to the trimmed
    // points when they are hook outliers.
    private static bool IsLine(List<Vector2> pts,
        out float x1, out float y1, out float x2, out float y2)
    {
        int n = pts.Count;
        int t = Math.Max(1, n / 50);         // ~2 samples ≈ hook length
        var a = pts[t];
        var b = pts[n - 1 - t];
        x1 = a.X; y1 = a.Y;
        x2 = b.X; y2 = b.Y;
        float dx = x2 - x1, dy = y2 - y1;
        float chord = MathF.Sqrt(dx * dx + dy * dy);
        if (chord < 15f) return false;

        // For uniformly-resampled points every segment has the same length.
        float segLen = Vector2.Distance(pts[0], pts[1]);
        float pathLen = (n - 1 - 2 * t) * segLen;
        if (chord / pathLen < 0.92f) return false;

        float maxPerp = 0;
        for (int i = t + 1; i < n - 1 - t; i++)
        {
            float perp = MathF.Abs((pts[i].X - x1) * dy - (pts[i].Y - y1) * dx) / chord;
            if (perp > maxPerp) maxPerp = perp;
        }
        if (maxPerp / chord >= 0.10f) return false;

        // Keep the raw endpoints unless they are hook outliers.
        float pd0 = MathF.Abs((pts[0].X - x1) * dy - (pts[0].Y - y1) * dx) / chord;
        float pd1 = MathF.Abs((pts[^1].X - x1) * dy - (pts[^1].Y - y1) * dx) / chord;
        if (pd0 < 4f) { x1 = pts[0].X;  y1 = pts[0].Y; }
        if (pd1 < 4f) { x2 = pts[^1].X; y2 = pts[^1].Y; }
        return true;
    }

    // Nudges a recognized line onto the nearest key angle (multiple of 45°:
    // horizontal, vertical, diagonals) when the drawn angle is within a few
    // degrees of one — a nearly-vertical stroke becomes exactly vertical.
    // Deliberately off-axis lines stay exactly as drawn, and the post-snap
    // drag can always pull an endpoint off the snapped angle again. Rotation
    // is about the midpoint with the length preserved, so the line stays
    // where it was drawn.
    private const float LineAngleSnapToleranceDeg = 6f;

    private static void SnapLineAngle(ref float x1, ref float y1, ref float x2, ref float y2)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float ang = MathF.Atan2(dy, dx);
        float step = MathF.PI / 4f;
        float snapped = MathF.Round(ang / step) * step;
        if (MathF.Abs(ang - snapped) > LineAngleSnapToleranceDeg * MathF.PI / 180f)
            return;

        float half = MathF.Sqrt(dx * dx + dy * dy) * 0.5f;
        float cx = (x1 + x2) * 0.5f, cy = (y1 + y2) * 0.5f;
        float ox = MathF.Cos(snapped) * half, oy = MathF.Sin(snapped) * half;
        x1 = cx - ox; y1 = cy - oy;
        x2 = cx + ox; y2 = cy + oy;
    }

    // Closed = start/end gap under 15% of path length, with a floor of 20 px
    // and a ceiling of max(50 px, 6% of path). The floor keeps small shapes
    // recognizable (a small circle rarely closes to the pixel). The ceiling
    // matters because an unbounded relative rule ("gap < 25% of path") let a
    // long handwritten word count as closed with a 200 px gap — but it must
    // scale with path length: a fixed 50 px cap silently un-recognized every
    // LARGE shape (a 1600 px rectangle perimeter with a routine 5% pen gap of
    // 80 px never reached the shape tests at all).
    private static bool IsClosed(List<Vector2> pts, float originalPathLen)
    {
        float gap = Vector2.Distance(pts[0], pts[^1]);
        float cap = MathF.Max(50f, 0.10f * originalPathLen);
        return gap < MathF.Min(cap, MathF.Max(20f, 0.15f * originalPathLen));
    }

    // Dominant tilt of the ink relative to the nearest cardinal axis, in
    // (−45°, 45°]: the circular mean of the windowed segment directions folded
    // modulo 90° (i.e. of 4× their angle). Segment directions are the robust
    // orientation signal here — a rectangle's edges all fold onto the same
    // angle regardless of rounded corners or which part the pen gap swallowed,
    // and an eccentric ellipse's long sides dominate the sum. (Second moments
    // of the point cloud were tried first and are visibly biased by the pen
    // gap: a rounded rectangle got tilt estimates of 6–12° from a 2° draw,
    // and derotating by that bogus angle CREATED ellipse misreads.)
    // Returns 0 when no direction dominates (circles: fold-resultant ≈ 0),
    // where derotating by a noisy estimate would do more harm than good.
    private static float EstimateTilt(List<Vector2> pts)
    {
        int n = pts.Count;
        int w = Math.Max(3, n / 15);
        float sx = 0, sy = 0;
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            var d = pts[(i + w) % n] - pts[i];
            if (d.LengthSquared() < 0.25f) continue;
            float a4 = 4f * MathF.Atan2(d.Y, d.X);
            sx += MathF.Cos(a4);
            sy += MathF.Sin(a4);
            count++;
        }
        if (count == 0) return 0f;
        float mag = MathF.Sqrt(sx * sx + sy * sy) / count;
        if (mag >= 0.2f) return Fold(0.25f * MathF.Atan2(sy, sx));

        // Weak fold-resultant: an ellipse's directions are spread continuously,
        // so at moderate eccentricity the mod-90 sum washes out. Fall back to
        // the principal axis of the second moments — reliable exactly here
        // (smooth symmetric outline, no corners for the pen gap to bias) —
        // and still return 0 for genuinely isotropic ink (circles, squares).
        Vector2 mean = Centroid(pts);
        float sxx = 0, sxy = 0, syy = 0;
        foreach (var p in pts)
        {
            var q = p - mean;
            sxx += q.X * q.X; sxy += q.X * q.Y; syy += q.Y * q.Y;
        }
        float root = MathF.Sqrt((sxx - syy) * (sxx - syy) + 4f * sxy * sxy);
        float lo = (sxx + syy - root) * 0.5f;
        if (lo <= 1e-3f || (sxx + syy + root) * 0.5f / lo < 1.5f) return 0f;
        return Fold(0.5f * MathF.Atan2(2f * sxy, sxx - syy));
    }

    // Folds an angle to the nearest cardinal axis: result in (−45°, 45°].
    private static float Fold(float ang)
    {
        while (ang >  MathF.PI / 4f) ang -= MathF.PI / 2f;
        while (ang <= -MathF.PI / 4f) ang += MathF.PI / 2f;
        return ang;
    }

    private static Vector2 Centroid(List<Vector2> pts)
    {
        Vector2 mean = Vector2.Zero;
        foreach (var p in pts) mean += p;
        return mean / pts.Count;
    }

    // Rotates the points by −angle about the given centroid (into the frame
    // where the estimated tilt is zero). The centroid stays fixed, so the
    // derotated coordinates remain valid page-space positions for the
    // committed shape.
    private static List<Vector2> Derotate(List<Vector2> pts, float angle, Vector2 centroid)
    {
        float c = MathF.Cos(-angle), s = MathF.Sin(-angle);
        var result = new List<Vector2>(pts.Count);
        foreach (var p in pts)
        {
            var q = p - centroid;
            result.Add(centroid + new Vector2(c * q.X - s * q.Y, s * q.X + c * q.Y));
        }
        return result;
    }

    // Net signed winding must be ~one full revolution (±360°, tolerance
    // 300–430°). A traced rectangle/ellipse/triangle turns through exactly
    // 360° in one direction; handwriting alternates turn directions and nets
    // out near 0°, and repeated loops overshoot to 720°+.
    //
    // Two robustness details, both load-bearing:
    //  - The winding is measured around the VIRTUALLY CLOSED loop (indices
    //    wrap past the start/end seam). An open trace with an endpoint gap
    //    only turns ~270–350° — it's missing the corner the pen never drew.
    //    Closing the loop makes the total the curve's turning number, which
    //    is exactly ±360°·k however the pen stopped.
    //  - Directions are window-averaged (same window as the corner scan).
    //    Raw per-segment angles on densely resampled points flip by ~180°
    //    on pixel-level wobble, and the wrap-around corrupts the sum.
    private static bool WindsOnce(List<Vector2> pts)
    {
        int n = pts.Count;
        int w = Math.Max(3, n / 15);
        float total = 0;
        Vector2 prev = default;
        bool hasPrev = false;
        for (int i = 0; i <= n; i++)     // one step past the end closes the angle chain
        {
            int a = i % n;
            var d = pts[(a + w) % n] - pts[a];
            if (d.LengthSquared() < 0.25f) continue;
            if (hasPrev)
                total += MathF.Atan2(prev.X * d.Y - prev.Y * d.X, Vector2.Dot(prev, d));
            prev = d;
            hasPrev = true;
        }
        float deg = MathF.Abs(total) * 180f / MathF.PI;
        return deg is > 300f and < 430f;
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

    // Unsigned shoelace area of the (virtually closed) resampled loop.
    private static float PolygonArea(List<Vector2> pts)
    {
        float sum = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return MathF.Abs(sum) * 0.5f;
    }

    // Mean (and max) distance of the ink from the boundary of the ellipse
    // inscribed in the bounding box — the exact shape a snap would commit.
    // Per point the distance is measured along the ray from the ellipse
    // center: the boundary sits at radius r/s where s² = (dx/a)² + (dy/b)²,
    // so |r − r/s| = r·|s−1|/s. Exact for circles, a close proxy otherwise.
    private static float EllipseFitError(List<Vector2> pts,
        float bx1, float by1, float bx2, float by2, out float max)
    {
        float a = (bx2 - bx1) * 0.5f, b = (by2 - by1) * 0.5f;
        float cx = (bx1 + bx2) * 0.5f, cy = (by1 + by2) * 0.5f;
        max = 0;
        float sum = 0;
        foreach (var p in pts)
        {
            float dx = p.X - cx, dy = p.Y - cy;
            float r = MathF.Sqrt(dx * dx + dy * dy);
            float s = MathF.Sqrt(dx * dx / (a * a) + dy * dy / (b * b));
            float err = s < 1e-3f ? MathF.Min(a, b) : r * MathF.Abs(s - 1f) / s;
            sum += err;
            if (err > max) max = err;
        }
        return sum / pts.Count;
    }

    // Mean distance between each point reflected through the given center and
    // its nearest ink point — near zero for centrally symmetric outlines
    // (ellipses, rectangles), large for D-shapes, blobs, and handwriting.
    // O(n²) on the 128 resampled points; negligible next to the fit tests.
    private static float SymmetryError(List<Vector2> pts, float cx, float cy)
    {
        var c2 = new Vector2(cx * 2f, cy * 2f);
        float sum = 0;
        foreach (var p in pts)
        {
            var q = c2 - p;
            float best = float.MaxValue;
            foreach (var r in pts)
            {
                float d = Vector2.DistanceSquared(q, r);
                if (d < best) best = d;
            }
            sum += MathF.Sqrt(best);
        }
        return sum / pts.Count;
    }

    // Mean (and max) distance of the ink from the bounding-box perimeter.
    // A traced rectangle has essentially no ink in the bbox interior; a
    // handwritten word fills it. Normalizing by the smaller bbox dimension
    // (not the diagonal) matters: word bboxes are wide and short, and their
    // interior is never far from an edge relative to the diagonal.
    private static float RectFitError(List<Vector2> pts,
        float bx1, float by1, float bx2, float by2, out float max)
    {
        max = 0;
        float sum = 0;
        foreach (var p in pts)
        {
            float d = MathF.Min(
                MathF.Min(p.X - bx1, bx2 - p.X),
                MathF.Min(p.Y - by1, by2 - p.Y));
            sum += d;
            if (d > max) max = d;
        }
        return sum / pts.Count;
    }

    // ── Triangle ──────────────────────────────────────────────────────────────

    // Picks the three triangle vertices. Normally each vertex is the
    // sharpest-turn ink point of its corner region (the region's turn peak —
    // the index-MIDPOINT used before it drifted down an edge whenever the
    // endpoint gap made the region lopsided).
    //
    // The one exception is the corner containing the stroke's start/end seam
    // when the pen left a real gap there. People start AND stop a triangle on
    // a corner, and with a gap that corner's apex is often never inked at all
    // — the stroke begins a little past it and ends a little short of it, so
    // EVERY on-ink point is wrong by up to the gap size (reported as "one
    // triangle point wrong depending on draw direction"). For that corner
    // only, the vertex is extrapolated as the intersection of the arriving
    // and departing edge directions, bounded to the seam's neighborhood.
    //
    // Strictly AT MOST ONE region is ever extrapolated — the one that
    // actually contains the seam. If two regions touch the seam (a pen-lift
    // hook can add its own tiny turn region right next to the apex region),
    // the seam is ambiguous and nothing is extrapolated: two extrapolated
    // regions both landed near the seam and collapsed the triangle into a
    // line (reported 2026-07-13). A final degeneracy check re-tries with pure
    // on-ink peaks and otherwise refuses the triangle — no snap beats a
    // collapsed one.
    private static bool TriangleVertices(
        List<Vector2> pts, List<CornerRegion> regions, out Vector2[] v)
    {
        int n = pts.Count;
        v = new Vector2[3];
        for (int k = 0; k < 3; k++) v[k] = pts[regions[k].Peak];

        float gap = Vector2.Distance(pts[0], pts[^1]);
        int seamRegion = -1;
        if (gap > 12f)
        {
            for (int k = 0; k < 3; k++)
            {
                var r = regions[k];
                if (r.First > r.Last || r.First == 0 || r.Last == n - 1)
                {
                    seamRegion = seamRegion < 0 ? k : -2;   // -2 = ambiguous
                }
            }
        }
        if (seamRegion >= 0
            && ExtrapolateSeamVertex(pts, regions[seamRegion], gap) is { } sv)
            v[seamRegion] = sv;

        if (!Degenerate(v)) return true;
        for (int k = 0; k < 3; k++) v[k] = pts[regions[k].Peak];
        return !Degenerate(v);
    }

    private static bool Degenerate(Vector2[] v)
    {
        float longest = MathF.Max(Vector2.Distance(v[0], v[1]),
                        MathF.Max(Vector2.Distance(v[1], v[2]),
                                  Vector2.Distance(v[2], v[0])));
        float shortest = MathF.Min(Vector2.Distance(v[0], v[1]),
                         MathF.Min(Vector2.Distance(v[1], v[2]),
                                   Vector2.Distance(v[2], v[0])));
        return shortest < MathF.Max(15f, 0.2f * longest);
    }

    // Intersection of the seam region's arriving and departing edge lines,
    // or null when degenerate or too far from the seam to be the drawn apex.
    private static Vector2? ExtrapolateSeamVertex(
        List<Vector2> pts, CornerRegion r, float gap)
    {
        var e = r.Entry;
        var x = r.Exit;
        float el = e.Length(), xl = x.Length();
        if (el < 0.5f || xl < 0.5f) return null;
        e /= el; x /= xl;
        float denom = e.X * x.Y - e.Y * x.X;
        if (MathF.Abs(denom) < 0.26f) return null;      // near-parallel (< ~15°)

        var a = pts[r.First];
        var d = pts[r.Last] - a;
        var v = a + (d.X * x.Y - d.Y * x.X) / denom * e;

        // The reconstructed apex must stay in the seam's neighborhood.
        var seamMid = (pts[0] + pts[^1]) * 0.5f;
        return Vector2.Distance(v, seamMid) <= MathF.Max(24f, 1.2f * gap) ? v : null;
    }

    // Mean (and max) distance of the ink from the triangle's three edges.
    private static float TriangleFitError(
        List<Vector2> pts, Vector2 v1, Vector2 v2, Vector2 v3, out float max)
    {
        max = 0;
        float sum = 0;
        foreach (var p in pts)
        {
            float d = MathF.Min(DistToSegment(p, v1, v2),
                      MathF.Min(DistToSegment(p, v2, v3),
                                DistToSegment(p, v3, v1)));
            sum += d;
            if (d > max) max = d;
        }
        return sum / pts.Count;
    }

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = MathF.Max(0f, MathF.Min(1f, Vector2.Dot(p - a, ab) / len2));
        return Vector2.Distance(p, a + t * ab);
    }

    // ── Corner utilities ──────────────────────────────────────────────────────
    //
    // Corners are contiguous regions where the angle between the windowed
    // arriving and departing directions exceeds the threshold. The scan is
    // CYCLIC — the windowed vectors wrap across the start/end seam — so a
    // corner drawn right at the pen's start point is found like any other.
    // (The old linear scan had a dead zone at the seam, papered over with a
    // special case that went blind whenever a different corner's region was
    // still open at the end of the scan range: a wide rectangle whose last
    // corner sat near the stroke end counted only 3 corners.)

    // One contiguous above-threshold turn region of the cyclic path.
    // TurnCount is the number of drawn corners the region represents: two
    // corners separated by a side shorter than the scan window (e.g. the
    // short side of a very elongated rectangle) blur into one region turning
    // ~180°, which must count as two.
    private readonly record struct CornerRegion(
        int First, int Last, int Peak, int TurnCount, Vector2 Entry, Vector2 Exit);

    private static List<CornerRegion> FindCornerRegions(
        List<Vector2> pts, float minTurnDeg)
    {
        var list = new List<CornerRegion>();
        WalkCornerRegions(pts, minTurnDeg, (first, last, peak, entry, exit) =>
            list.Add(new CornerRegion(first, last, peak,
                RegionCornerCount(entry, exit), entry, exit)));
        return list;
    }

    // Walks the contiguous above-threshold turn regions around the cyclic
    // path, invoking onRegion(firstIdx, lastIdx, peakIdx, arrivingDir,
    // departingDir) once per region. peakIdx is the index with the sharpest
    // windowed turn inside the region — the actual corner apex.
    private static void WalkCornerRegions(
        List<Vector2> pts, float minTurnDeg,
        Action<int, int, int, Vector2, Vector2> onRegion)
    {
        int n = pts.Count;
        int w = Math.Max(3, n / 15);
        float threshold = minTurnDeg * MathF.PI / 180f;

        var hot = new bool[n];
        var ang = new float[n];
        var bef = new Vector2[n];
        var aft = new Vector2[n];
        int anchor = -1;                    // any index outside a turn region
        for (int i = 0; i < n; i++)
        {
            var before = pts[i] - pts[(i - w + n) % n];
            var after  = pts[(i + w) % n] - pts[i];
            bef[i] = before;
            aft[i] = after;
            float bLen = before.Length(), aLen = after.Length();
            if (bLen > 0.5f && aLen > 0.5f)
            {
                float dot = MathF.Max(-1f, MathF.Min(1f,
                    Vector2.Dot(before / bLen, after / aLen)));
                ang[i] = MathF.Acos(dot);
                hot[i] = ang[i] > threshold;
            }
            if (!hot[i] && anchor < 0) anchor = i;
        }
        if (anchor < 0) return;             // turning everywhere — no distinct corners

        // Starting just past the anchor guarantees no region straddles the
        // scan's own wrap point.
        bool inRegion = false;
        int first = -1, last = -1, peak = -1;
        for (int k = 1; k <= n; k++)
        {
            int i = (anchor + k) % n;
            if (hot[i])
            {
                if (!inRegion) { inRegion = true; first = i; peak = i; }
                last = i;
                if (ang[i] > ang[peak]) peak = i;
            }
            else if (inRegion)
            {
                onRegion(first, last, peak, bef[first], aft[last]);
                inRegion = false;
            }
        }
    }

    // Net direction change across one contiguous above-threshold region.
    // A single drawn corner turns ≤ ~135°; a region turning close to 180° is
    // two corners whose separating side fell inside the scan window.
    private static int RegionCornerCount(Vector2 entry, Vector2 exit)
    {
        float el = entry.Length(), xl = exit.Length();
        if (el < 0.5f || xl < 0.5f) return 1;
        float dot = MathF.Max(-1f, MathF.Min(1f, Vector2.Dot(entry / el, exit / xl)));
        return MathF.Acos(dot) * 180f / MathF.PI > 150f ? 2 : 1;
    }
}
