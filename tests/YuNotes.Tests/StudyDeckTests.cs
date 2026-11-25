using System.Linq;
using Xunit;
using YuNotes.Models;
using YuNotes.Services;

namespace YuNotes.Tests;

public class StudyDeckTests
{
    private static ShapeElement Tape(string id, float x, float y, float w = 120, float h = 30) =>
        new() { Id = id, Kind = ShapeKind.Tape, X1 = x, Y1 = y, X2 = x + w, Y2 = y + h, Filled = true };

    [Fact]
    public void Order_ReadsTopToBottom_ThenLeftToRightOnALine()
    {
        var page = new NotePage();
        // Second line, right then left; first line slightly crooked.
        page.Shapes.Add(Tape("line2-right", 600, 300));
        page.Shapes.Add(Tape("line1-right", 500, 108));
        page.Shapes.Add(Tape("line2-left", 100, 296));
        page.Shapes.Add(Tape("line1-left", 80, 100));
        page.Shapes.Add(new ShapeElement { Id = "rect", Kind = ShapeKind.Rectangle, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 });

        var ids = StudyDeck.Order(new[] { page }).Select(i => i.Id).ToArray();

        Assert.Equal(new[] { "line1-left", "line1-right", "line2-left", "line2-right" }, ids);
    }

    [Fact]
    public void Order_GoesPageByPage_AndKeepsThePageIndex()
    {
        var p1 = new NotePage();
        var p2 = new NotePage();
        p2.Shapes.Add(Tape("on-page-2", 50, 50));
        p1.Shapes.Add(Tape("on-page-1", 50, 900));

        var items = StudyDeck.Order(new[] { p1, p2 });

        Assert.Equal(new[] { "on-page-1", "on-page-2" }, items.Select(i => i.Id));
        Assert.Equal(new[] { 0, 1 }, items.Select(i => i.PageIndex));
    }

    [Fact]
    public void Order_HandlesStripsDrawnRightToLeftOrBottomToTop()
    {
        var page = new NotePage();
        page.Shapes.Add(new ShapeElement { Id = "backwards", Kind = ShapeKind.Tape, X1 = 300, Y1 = 230, X2 = 100, Y2 = 200 });
        page.Shapes.Add(Tape("above", 100, 50));

        var items = StudyDeck.Order(new[] { page });

        Assert.Equal("above", items[0].Id);
        var b = items[1];
        Assert.Equal((100.0, 200.0, 200.0, 30.0), (b.X, b.Y, b.W, b.H));
    }

    [Fact]
    public void Order_EmptyWithoutTape() =>
        Assert.Empty(StudyDeck.Order(new[] { new NotePage() }));
}
