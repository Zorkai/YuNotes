using System;
using System.Collections.Generic;
using System.Linq;

namespace YuNotes.Models;

// Blank space inserted into a page, e.g. between two questions on an imported
// exercise sheet so there is room to write the answer. The page grows by
// Size; the background (PDF) is split at At and the part below moves down.
//
// At is measured on the background as it was imported (before any space was
// inserted), so a gap stays at the same line of the PDF however many other
// gaps are added above it.
public sealed class PageGap
{
    public double At { get; set; }
    public double Size { get; set; }
}

public static class PageGaps
{
    // Below this a gap is treated as gone.
    private const double MinSize = 0.5;

    public static bool Any(NotePage p) => p.Gaps.Count > 0;

    public static double Total(NotePage p)
    {
        double t = 0;
        foreach (var g in p.Gaps) t += g.Size;
        return t;
    }

    // Height of the background without the inserted space.
    public static double BackgroundHeight(NotePage p) => Math.Max(1, p.Height - Total(p));

    private static IEnumerable<PageGap> Ordered(NotePage p) => p.Gaps.OrderBy(g => g.At);

    // Pieces of the background and where each is drawn: background rows
    // SrcY..SrcY+SrcH go to page rows DstY..DstY+SrcH. One piece when the page
    // has no gaps.
    public static List<(double SrcY, double SrcH, double DstY)> Segments(NotePage p)
    {
        var list = new List<(double, double, double)>();
        double h = BackgroundHeight(p), src = 0, shift = 0;
        foreach (var g in Ordered(p))
        {
            double at = Math.Clamp(g.At, 0, h);
            if (at > src) list.Add((src, at - src, src + shift));
            src = Math.Max(src, at);
            shift += g.Size;
        }
        if (h > src) list.Add((src, h - src, src + shift));
        return list;
    }

    // The inserted space, as page rows.
    public static List<(double Y, double H)> GapRows(NotePage p)
    {
        var list = new List<(double, double)>();
        double shift = 0;
        foreach (var g in Ordered(p))
        {
            list.Add((g.At + shift, g.Size));
            shift += g.Size;
        }
        return list;
    }

    // Background row → page row.
    public static double ToPage(NotePage p, double bgY)
    {
        double shift = 0;
        foreach (var g in Ordered(p))
        {
            if (g.At > bgY) break;
            shift += g.Size;
        }
        return bgY + shift;
    }

    // Page row → background row. A row inside inserted space maps to the line
    // the space was opened at.
    public static double ToBackground(NotePage p, double pageY)
    {
        double shift = 0;
        foreach (var g in Ordered(p))
        {
            double top = g.At + shift;
            if (pageY < top) break;
            if (pageY < top + g.Size) return g.At;
            shift += g.Size;
        }
        return pageY - shift;
    }

    // Opens `amount` of blank space at page row `pageY`: everything below moves
    // down and the page grows. Space opened inside existing space widens it.
    public static void Insert(NotePage p, double pageY, double amount)
    {
        if (!(amount > 0)) return;
        double at = ToBackground(p, pageY);
        var same = p.Gaps.FirstOrDefault(g => Math.Abs(g.At - at) < 0.5);
        if (same is not null) same.Size += amount;
        else p.Gaps.Add(new PageGap { At = at, Size = amount });
        p.Height += amount;
        ShiftElements(p, y => y >= pageY ? amount : 0);
        foreach (var r in p.TextRuns)
            if (r.Y >= pageY) r.Y += amount;
    }

    // Removes inserted space between page rows `top` and `bottom` (only space,
    // never the background). Things below move up; anything that was written
    // inside the removed space moves up to where it started. Returns how much
    // was removed.
    public static double Remove(NotePage p, double top, double bottom)
    {
        if (bottom < top) (top, bottom) = (bottom, top);
        var removed = new List<(double Y, double H)>();
        double shift = 0;
        foreach (var g in Ordered(p).ToList())
        {
            double gTop = g.At + shift, gBottom = gTop + g.Size;
            shift += g.Size;
            double a = Math.Max(top, gTop), b = Math.Min(bottom, gBottom);
            if (b - a <= 0) continue;
            removed.Add((a, b - a));
            g.Size -= b - a;
            if (g.Size < MinSize) p.Gaps.Remove(g);
        }
        double total = removed.Sum(r => r.H);
        if (total <= 0) return 0;

        double RemovedAbove(double y)
        {
            double s = 0;
            foreach (var (ry, rh) in removed)
                if (y > ry) s += Math.Min(rh, y - ry);
            return s;
        }
        p.Height = Math.Max(1, p.Height - total);
        ShiftElements(p, y => -RemovedAbove(y));
        foreach (var r in p.TextRuns) r.Y -= RemovedAbove(r.Y);
        return total;
    }

    // Moves each element down by dy(its vertical centre). Elements move whole,
    // so ink that crosses the line keeps its shape.
    private static void ShiftElements(NotePage p, Func<double, double> dy)
    {
        foreach (var s in p.Strokes)
        {
            if (s.Points.Count == 0) continue;
            float min = float.MaxValue, max = float.MinValue;
            foreach (var pt in s.Points) { if (pt.Y < min) min = pt.Y; if (pt.Y > max) max = pt.Y; }
            float d = (float)dy((min + max) / 2.0);
            if (d == 0) continue;
            for (int i = 0; i < s.Points.Count; i++)
            {
                var pt = s.Points[i];
                s.Points[i] = new InkPoint(pt.X, pt.Y + d, pt.Pressure);
            }
        }
        foreach (var sh in p.Shapes)
        {
            double min = Math.Min(sh.Y1, sh.Y2), max = Math.Max(sh.Y1, sh.Y2);
            if (sh.Kind == ShapeKind.Triangle) { min = Math.Min(min, sh.Y3); max = Math.Max(max, sh.Y3); }
            float d = (float)dy((min + max) / 2.0);
            if (d == 0) continue;
            sh.Y1 += d; sh.Y2 += d;
            if (sh.Kind == ShapeKind.Triangle) sh.Y3 += d;
        }
        foreach (var t in p.Texts)
            t.Y += dy(t.Y + t.Height / 2);
        foreach (var im in p.Images)
            im.Y += dy(im.Y + im.Height / 2);
    }

    public static List<PageGap> Clone(IEnumerable<PageGap> gaps) =>
        gaps.Select(g => new PageGap { At = g.At, Size = g.Size }).ToList();

    public static string? ToJson(NotePage p) =>
        p.Gaps.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(p.Gaps);

    public static void FromJson(NotePage p, string? json)
    {
        p.Gaps.Clear();
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<PageGap>>(json);
            if (list is not null)
                p.Gaps.AddRange(list.Where(g => double.IsFinite(g.At) && double.IsFinite(g.Size) && g.Size >= MinSize));
        }
        catch { }
    }
}
