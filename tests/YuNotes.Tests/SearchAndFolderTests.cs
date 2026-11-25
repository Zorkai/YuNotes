using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YuNotes.Models;
using YuNotes.Services;

namespace YuNotes.Tests;

public class TextMatcherTests
{
    private static TextBox Box(string t, float x, float y, params string[] alts) =>
        new() { T = t, A = alts.Length > 0 ? alts : null, X = x, Y = y, W = 40, H = 16 };

    [Theory]
    [InlineData("Lösung", "losung")]
    [InlineData("Straße", "strasse")]
    [InlineData("ÉTÉ", "ete")]
    public void Normalize_IgnoresCaseAndAccents(string input, string expected) =>
        Assert.Equal(expected, TextMatcher.Normalize(input));

    [Fact]
    public void Match_NeedsEveryTerm()
    {
        var page = new PageText { Index = 2, Boxes = { Box("Exercise", 0, 0), Box("Sheet", 50, 0), Box("ten", 100, 0) } };
        Assert.NotNull(TextMatcher.Match(page, TextMatcher.Terms("sheet exercise")));
        Assert.Null(TextMatcher.Match(page, TextMatcher.Terms("sheet eleven")));
    }

    [Fact]
    public void Match_ReturnsHitsInReadingOrder_WithPageAndSnippet()
    {
        var page = new PageText
        {
            Index = 4,
            Boxes = { Box("second", 0, 40), Box("first", 0, 0), Box("line", 60, 0) },
        };
        var m = TextMatcher.Match(page, TextMatcher.Terms("second first"));
        Assert.NotNull(m);
        Assert.Equal(4, m!.PageIndex);
        Assert.Equal(new[] { "first", "second" }, m.Boxes.Select(b => b.T));
        Assert.StartsWith("first line second", m.Snippet);
    }

    [Fact]
    public void Match_UsesHandwritingAlternatives()
    {
        var page = new PageText { Boxes = { Box("c0ntinuous", 0, 0, "continuous") } };
        Assert.NotNull(TextMatcher.Match(page, TextMatcher.Terms("continuous")));
    }

    [Fact]
    public void PdfBoxes_FollowInsertedSpaceAndWidening()
    {
        var page = new NotePage { Width = 1240, Height = 1754 };
        PageGaps.Insert(page, 500, 100);
        var runs = new[]
        {
            new TextRun { Text = "above", X = 10, Y = 100, Width = 50, Height = 12 },
            new TextRun { Text = "below", X = 10, Y = 900, Width = 50, Height = 12 },
        };
        var boxes = SearchIndex.PdfBoxes(runs, shiftX: 30, page);
        Assert.Equal((40f, 100f), (boxes[0].X, boxes[0].Y));
        Assert.Equal((40f, 1000f), (boxes[1].X, boxes[1].Y));
    }
}

public class SmartFolderTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static bool M(SmartFolder f, string title = "Note", DateTime? mod = null, bool pdf = false,
                          bool pinned = false, params string[] tags) =>
        f.Matches(title, mod ?? Now, pdf, pinned, tags);

    [Fact]
    public void EmptyFolder_MatchesEverything()
    {
        var f = new SmartFolder();
        Assert.True(M(f, "Anything", Now.AddYears(-3), pdf: true));
        Assert.Equal("All notes", f.Describe());
    }

    [Fact]
    public void Tags_AnyOrAll()
    {
        var any = new SmartFolder { Tags = { "exam", "todo" } };
        var all = new SmartFolder { Tags = { "exam", "todo" }, MatchAllTags = true };
        Assert.True(M(any, tags: "EXAM"));
        Assert.False(M(all, tags: "exam"));
        Assert.True(M(all, tags: new[] { "exam", "todo", "x" }));
        Assert.Equal("#exam and #todo", all.Describe());
    }

    [Fact]
    public void EditedWithinDays_CountsCalendarDays()
    {
        var today = new SmartFolder { EditedWithinDays = 1 };
        Assert.True(M(today, mod: Now));
        Assert.False(M(today, mod: DateTime.Today.AddDays(-1).ToUniversalTime()));
        var week = new SmartFolder { EditedWithinDays = 7 };
        Assert.True(M(week, mod: Now.AddDays(-5)));
        Assert.False(M(week, mod: Now.AddDays(-9)));
    }

    [Fact]
    public void KindPinnedAndTitle()
    {
        Assert.False(M(new SmartFolder { Kind = SmartNoteKind.ImportedPdf }, pdf: false));
        Assert.False(M(new SmartFolder { Kind = SmartNoteKind.Handwritten }, pdf: true));
        Assert.False(M(new SmartFolder { PinnedOnly = true }, pinned: false));
        var title = new SmartFolder { TitleContains = "sheet  ten" };
        Assert.True(M(title, "Exercise Sheet TEN"));
        Assert.False(M(title, "Exercise Sheet 9"));
    }
}

public class NoteTagsTests
{
    [Theory]
    [InlineData("#Exam", "exam")]
    [InlineData("  #  Exam prep ", "exam-prep")]
    [InlineData("#", "")]
    public void Normalize(string raw, string expected) => Assert.Equal(expected, NoteTags.Normalize(raw));

    [Fact]
    public void Parse_SplitsAndDeduplicates() =>
        Assert.Equal(new[] { "exam", "todo", "week-3" }, NoteTags.Parse("exam, #todo  week-3;EXAM").ToArray());
}

// The per-page ink fingerprint the search index stores to skip re-reading
// pages whose handwriting hasn't changed.
public class InkSignatureTests
{
    private static NotePage PageWith(params (float X, float Y)[] pts)
    {
        var p = new NotePage();
        var s = new Stroke { Color = Windows.UI.Color.FromArgb(255, 0, 0, 0) };
        foreach (var (x, y) in pts) s.Points.Add(new InkPoint(x, y, 0.5f));
        p.Strokes.Add(s);
        return p;
    }

    [Fact]
    public void SameInk_SameSignature_EvenForACopy()
    {
        var a = PageWith((1, 2), (3, 4));
        // A different page object with the same stroke (as after reopening the note).
        var b = new NotePage();
        var copy = new Stroke { Id = a.Strokes[0].Id, Color = a.Strokes[0].Color };
        copy.Points.AddRange(a.Strokes[0].Points);
        b.Strokes.Add(copy);
        Assert.Equal(SearchIndex.InkSignature(a), SearchIndex.InkSignature(b));
    }

    [Fact]
    public void MovedAddedOrRemovedInk_ChangesTheSignature()
    {
        var p = PageWith((1, 2), (3, 4));
        var before = SearchIndex.InkSignature(p);

        var moved = p.Strokes[0].Points.Select(q => new InkPoint(q.X + 10, q.Y, q.Pressure)).ToList();
        p.Strokes[0].Points.Clear();
        p.Strokes[0].Points.AddRange(moved);
        var afterMove = SearchIndex.InkSignature(p);
        Assert.NotEqual(before, afterMove);

        p.Strokes.Add(new Stroke { Color = Windows.UI.Color.FromArgb(255, 0, 0, 0), Points = { new InkPoint(0, 0, 1), new InkPoint(5, 5, 1) } });
        Assert.NotEqual(afterMove, SearchIndex.InkSignature(p));

        p.Strokes.RemoveAt(1);
        Assert.Equal(afterMove, SearchIndex.InkSignature(p));
    }

    [Fact]
    public void Signature_IsStableText()
    {
        // Saved to disk, so it must not depend on per-process string hashing:
        // the same stroke id always gives the same text.
        var p = PageWith((1, 2), (3, 4));
        var s = new Stroke { Id = "fixed-id", Color = Windows.UI.Color.FromArgb(255, 0, 0, 0), Points = { new InkPoint(1, 1, 1), new InkPoint(2, 2, 1) } };
        var q = new NotePage { Strokes = { s } };
        Assert.Equal("1:", SearchIndex.InkSignature(q)[..2]);
        Assert.Equal(SearchIndex.InkSignature(q), SearchIndex.InkSignature(new NotePage { Strokes = { s } }));
    }
}
