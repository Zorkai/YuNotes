using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;
using Xunit;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Tools;

namespace YuNotes.Tests;

public class PressureCurveTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void NaturalPreset_IsAStraightLine(double x)
    {
        var (_, x1, y1, x2, y2) = PressureCurve.Presets.Single(p => p.Name == "Natural");
        Assert.Equal(x, PressureCurve.Evaluate(x, x1, y1, x2, y2), 3);
    }

    [Fact]
    public void EveryPreset_RunsFromZeroToOne_AndNeverGoesDown()
    {
        foreach (var (name, x1, y1, x2, y2) in PressureCurve.Presets)
        {
            Assert.Equal(0, PressureCurve.Evaluate(0, x1, y1, x2, y2), 3);
            Assert.Equal(1, PressureCurve.Evaluate(1, x1, y1, x2, y2), 3);
            double prev = -1;
            for (double x = 0; x <= 1.0001; x += 0.05)
            {
                double y = PressureCurve.Evaluate(x, x1, y1, x2, y2);
                Assert.True(y >= prev - 1e-9, $"{name} drops at {x}");
                prev = y;
            }
        }
    }

    [Fact]
    public void SoftIsAboveFirm_InTheMiddle()
    {
        double At(string n)
        {
            var (_, x1, y1, x2, y2) = PressureCurve.Presets.Single(p => p.Name == n);
            return PressureCurve.Evaluate(0.5, x1, y1, x2, y2);
        }
        Assert.True(At("Soft") > At("Natural"));
        Assert.True(At("Natural") > At("Firm"));
    }

    [Fact]
    public void TryMap_DropsSamplesUnderTheMinimum_AndBoosts()
    {
        var s = new AppSettings { MinPressure = 0.1, PressureMultiplier = 2 };
        Assert.False(PressureCurve.TryMap(0.05f, s, out _, out _));
        Assert.True(PressureCurve.TryMap(0.3f, s, out var input, out var output));
        Assert.Equal(0.4f, input, 3);           // (0.3 - 0.1) × 2
        Assert.Equal(0.4f, output, 2);          // default curve is the straight line
        Assert.True(PressureCurve.TryMap(1f, s, out input, out _));
        Assert.Equal(1f, input);                // clamped
    }
}

public class ShapeRecognizerTests
{
    private static readonly Color Ink = Color.FromArgb(255, 0, 0, 0);

    // A hand-drawn-ish closed path through the given corners, densely sampled.
    private static List<InkPoint> Path(params (float X, float Y)[] corners)
    {
        var pts = new List<InkPoint>();
        for (int i = 0; i < corners.Length; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Length];
            for (int k = 0; k < 30; k++)
            {
                float t = k / 30f;
                pts.Add(new InkPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, 0.5f));
            }
        }
        pts.Add(new InkPoint(corners[0].X, corners[0].Y, 0.5f));
        return pts;
    }

    [Fact]
    public void StraightStroke_BecomesALine()
    {
        var pts = Enumerable.Range(0, 60).Select(i => new InkPoint(100 + i * 5, 200 + i * 0.2f, 0.5f)).ToList();
        var shape = ShapeRecognizer.Recognize(pts, Ink, 3);
        Assert.Equal(ShapeKind.Line, shape?.Kind);
    }

    [Fact]
    public void Square_BecomesARectangle()
    {
        var shape = ShapeRecognizer.Recognize(Path((100, 100), (300, 100), (300, 300), (100, 300)), Ink, 3);
        Assert.Equal(ShapeKind.Rectangle, shape?.Kind);
    }

    [Fact]
    public void Triangle_BecomesATriangle()
    {
        var shape = ShapeRecognizer.Recognize(Path((200, 100), (320, 300), (80, 300)), Ink, 3);
        Assert.Equal(ShapeKind.Triangle, shape?.Kind);
    }

    [Fact]
    public void Circle_BecomesAnEllipse()
    {
        var pts = Enumerable.Range(0, 121)
            .Select(i => i * Math.PI * 2 / 120)
            .Select(a => new InkPoint((float)(300 + 120 * Math.Cos(a)), (float)(300 + 120 * Math.Sin(a)), 0.5f))
            .ToList();
        var shape = ShapeRecognizer.Recognize(pts, Ink, 3);
        Assert.Equal(ShapeKind.Ellipse, shape?.Kind);
    }

    [Fact]
    public void LetterSizedLoop_StaysInk()
    {
        var pts = Enumerable.Range(0, 41)
            .Select(i => i * Math.PI * 2 / 40)
            .Select(a => new InkPoint((float)(100 + 8 * Math.Cos(a)), (float)(100 + 8 * Math.Sin(a)), 0.5f))
            .ToList();
        Assert.Null(ShapeRecognizer.Recognize(pts, Ink, 3));
    }

    [Fact]
    public void Zigzag_StaysInk()
    {
        var pts = Enumerable.Range(0, 80)
            .Select(i => new InkPoint(100 + i * 4, i % 2 == 0 ? 100 : 160, 0.5f))
            .ToList();
        Assert.Null(ShapeRecognizer.Recognize(pts, Ink, 3));
    }
}
