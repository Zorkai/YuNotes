using System.Linq;
using Windows.UI;
using Xunit;
using YuNotes.Models;

namespace YuNotes.Tests;

// Inserted page space (PageGaps): the mapping between page rows and the
// imported background, and how elements move when space opens or closes.
public class PageGapsTests
{
    private static NotePage Page(double h = 1000) => new() { Width = 800, Height = h };

    private static Stroke StrokeAt(float y) => new()
    {
        Color = Color.FromArgb(255, 0, 0, 0),
        Points = { new InkPoint(10, y, 0.5f), new InkPoint(50, y + 10, 0.5f) },
    };

    [Fact]
    public void NoGaps_MeansOneSegmentCoveringThePage()
    {
        var p = Page();
        var seg = Assert.Single(PageGaps.Segments(p));
        Assert.Equal((0.0, 1000.0, 0.0), seg);
        Assert.Equal(400, PageGaps.ToPage(p, 400));
        Assert.Equal(400, PageGaps.ToBackground(p, 400));
    }

    [Fact]
    public void Insert_GrowsPageAndSplitsBackground()
    {
        var p = Page();
        PageGaps.Insert(p, 300, 200);

        Assert.Equal(1200, p.Height);
        Assert.Equal(1000, PageGaps.BackgroundHeight(p));
        Assert.Equal(new[] { (0.0, 300.0, 0.0), (300.0, 700.0, 500.0) }, PageGaps.Segments(p));
        Assert.Equal(new[] { (300.0, 200.0) }, PageGaps.GapRows(p));
    }

    [Fact]
    public void Insert_MovesOnlyElementsBelowTheLine()
    {
        var p = Page();
        var above = StrokeAt(100);
        var below = StrokeAt(500);
        var text = new TextElement { Y = 600, Height = 40 };
        var shape = new ShapeElement { Y1 = 700, Y2 = 760 };
        p.Strokes.Add(above); p.Strokes.Add(below); p.Texts.Add(text); p.Shapes.Add(shape);

        PageGaps.Insert(p, 300, 150);

        Assert.Equal(100, above.Points[0].Y);
        Assert.Equal(650, below.Points[0].Y);
        Assert.Equal(750, text.Y);
        Assert.Equal(850, shape.Y1);
        Assert.Equal(910, shape.Y2);
    }

    [Fact]
    public void Mapping_RoundTripsOutsideGaps_AndSnapsInsideThem()
    {
        var p = Page();
        PageGaps.Insert(p, 300, 200);
        foreach (var y in new[] { 0.0, 299.0, 300.0, 650.0, 999.0 })
            Assert.Equal(y, PageGaps.ToBackground(p, PageGaps.ToPage(p, y)), 6);
        // Any row inside the space maps to the line it was opened at.
        Assert.Equal(300, PageGaps.ToBackground(p, 420));
    }

    [Fact]
    public void Insert_InsideExistingSpace_WidensIt()
    {
        var p = Page();
        PageGaps.Insert(p, 300, 200);
        PageGaps.Insert(p, 400, 50);   // inside the space at 300..500
        var gap = Assert.Single(p.Gaps);
        Assert.Equal(250, gap.Size);
        Assert.Equal(1250, p.Height);
    }

    [Fact]
    public void Remove_ClosesOnlyInsertedSpace()
    {
        var p = Page();
        PageGaps.Insert(p, 300, 200);
        var below = StrokeAt(800);
        p.Strokes.Add(below);

        // Band covers background 200..300, the whole space and 50 rows below it.
        double removed = PageGaps.Remove(p, 200, 550);

        Assert.Equal(200, removed);
        Assert.Empty(p.Gaps);
        Assert.Equal(1000, p.Height);
        Assert.Equal(600, below.Points[0].Y);
    }

    [Fact]
    public void Remove_WhereThereIsNoSpace_DoesNothing()
    {
        var p = Page();
        Assert.Equal(0, PageGaps.Remove(p, 100, 400));
        Assert.Equal(1000, p.Height);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var p = Page();
        PageGaps.Insert(p, 300, 200);
        PageGaps.Insert(p, 900, 80);
        var json = PageGaps.ToJson(p);

        var q = Page();
        PageGaps.FromJson(q, json);
        Assert.Equal(p.Gaps.Select(g => (g.At, g.Size)), q.Gaps.Select(g => (g.At, g.Size)));
        Assert.Null(PageGaps.ToJson(Page()));
    }

    [Fact]
    public void FromJson_IgnoresGarbage()
    {
        var p = Page();
        PageGaps.FromJson(p, "not json");
        Assert.Empty(p.Gaps);
    }
}
