using System;
using YuNotes.Models;

namespace YuNotes.Input;

// Pen pressure response curve: a cubic Bézier from (0,0) to (1,1) with two
// control points, like a CSS easing curve. X is the pressure after the
// minimum-pressure cutoff and boost; Y is the pressure the stroke uses.
// Control points on the diagonal give a straight line (no change).
public static class PressureCurve
{
    public static readonly (string Name, double X1, double Y1, double X2, double Y2)[] Presets =
    {
        ("Soft",    0.15, 0.45, 0.50, 0.90),
        ("Natural", 1 / 3.0, 1 / 3.0, 2 / 3.0, 2 / 3.0),
        ("Firm",    0.50, 0.10, 0.85, 0.55),
    };

    public static double Evaluate(double x, double x1, double y1, double x2, double y2)
    {
        x = Math.Clamp(x, 0, 1);
        // x(t) is monotone when both control x values are in [0,1], so bisect.
        double lo = 0, hi = 1, t = x;
        for (int i = 0; i < 24; i++)
        {
            t = (lo + hi) / 2;
            if (Bezier(t, x1, x2) < x) lo = t; else hi = t;
        }
        return Math.Clamp(Bezier(t, y1, y2), 0, 1);
    }

    public static double Evaluate(double x, AppSettings s) =>
        Evaluate(x, s.PressureCurveX1, s.PressureCurveY1, s.PressureCurveX2, s.PressureCurveY2);

    // Raw pen pressure → (curve input, stroke pressure). Returns false when the
    // sample falls under the minimum pressure and should be dropped.
    public static bool TryMap(float raw, AppSettings s, out float input, out float output)
    {
        input = output = 0;
        if (raw < (float)s.MinPressure) return false;
        input = Math.Clamp((raw - (float)s.MinPressure) * (float)s.PressureMultiplier, 0f, 1f);
        output = (float)Evaluate(input, s);
        return true;
    }

    private static double Bezier(double t, double p1, double p2)
    {
        double u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }
}
