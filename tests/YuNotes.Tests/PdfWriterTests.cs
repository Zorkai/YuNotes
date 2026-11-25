using System;
using System.IO;
using System.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Windows.UI;
using Xunit;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Services;

namespace YuNotes.Tests;

// Saving notes into a PDF: imported pages keep their original content (as
// vectors, text selectable), widened pages and pages with inserted space get
// their real size, and the editable data embedded in the file comes back out.
public sealed class PdfWriterTests : IDisposable
{
    private const double CoordPerPt = 150.0 / 72.0;   // PdfImportService's page coordinates
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yunotes-pdf-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PdfContainerService _container = new();

    public PdfWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // A one-page A4 source PDF with a few words of real text.
    private static byte[] SourcePdf()
    {
        // Fonts go through the app's own resolver, by its font keys.
        var key = PdfFontResolver.KeyFor(new[] { "Arial", "ArialMT", "Segoe UI", "SegoeUI" }, false, false)
                  ?? throw new InvalidOperationException("No Arial or Segoe UI font installed.");
        var font = PdfFontResolver.GetFont(key, 20);
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var g = XGraphics.FromPdfPage(page))
            g.DrawString("Alpha Beta Gamma", font, XBrushes.Black, 72, 100);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static NotePage ImportedPage() => new()
    {
        Width = 595 * CoordPerPt, Height = 842 * CoordPerPt, SourcePageIndex = 0,
    };

    private static PageVectorExport Overlay(NotePage p) => new()
    {
        Page = p, Template = new TemplateSettings { Kind = TemplateKind.Grid, SkipImportedPdf = true }, OverlayOnly = true,
    };

    private (double W, double H, int Words) WriteOne(NotePage p)
    {
        var src = SourcePdf();
        var outPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        _container.WriteWithSource(new PageVectorExport?[] { Overlay(p) }, new[] { p.Width }, new[] { p.Height },
            new int?[] { 0 }, src, null, outPath, notePages: new[] { p });
        using var pig = UglyToad.PdfPig.PdfDocument.Open(File.ReadAllBytes(outPath));
        var pg = pig.GetPage(1);
        return (pg.Width, pg.Height, pg.GetWords().Count());
    }

    [Fact]
    public void UnchangedImportedPage_KeepsOriginalSizeAndText()
    {
        var (w, h, words) = WriteOne(ImportedPage());
        Assert.Equal(595, w, 0);
        Assert.Equal(842, h, 0);
        Assert.Equal(3, words);
    }

    [Fact]
    public void WidenedPage_IsWrittenAtFullWidth()
    {
        var p = ImportedPage();
        double original = p.Width;
        p.BackgroundContentWidth = original;
        p.Width = original * 1.5;   // widened to the right by half its width

        var (w, h, words) = WriteOne(p);

        Assert.Equal(595 * 1.5, w, 0);
        Assert.Equal(842, h, 0);
        Assert.Equal(3, words);    // still the original page, once, as text
    }

    [Fact]
    public void LeftWidenedPage_IsWrittenAtFullWidth()
    {
        var p = ImportedPage();
        double original = p.Width;
        p.BackgroundContentWidth = original;
        p.BackgroundLeft = original / 4;
        p.Width = original * 1.25;

        var (w, _, words) = WriteOne(p);

        Assert.Equal(595 * 1.25, w, 0);
        Assert.Equal(3, words);
    }

    [Fact]
    public void PageWithInsertedSpace_IsTaller()
    {
        var p = ImportedPage();
        double original = p.Height;
        PageGaps.Insert(p, original / 2, original / 4);

        var (w, h, _) = WriteOne(p);

        Assert.Equal(595, w, 0);
        Assert.Equal(842 * 1.25, h, 0);
    }

    [Fact]
    public void Ink_IsWrittenAsVectors()
    {
        var p = ImportedPage();
        var s = new Stroke { Color = Color.FromArgb(255, 255, 0, 0), Width = 4 };
        s.Points.Add(new InkPoint(100, 100, 0.5f));
        s.Points.Add(new InkPoint(400, 300, 0.5f));
        p.Strokes.Add(s);
        var outPath = Path.Combine(_dir, "ink.pdf");

        _container.WriteWithSource(new PageVectorExport?[] { Overlay(p) }, new[] { p.Width }, new[] { p.Height },
            new int?[] { 0 }, SourcePdf(), null, outPath, notePages: new[] { p });

        using var pig = UglyToad.PdfPig.PdfDocument.Open(File.ReadAllBytes(outPath));
        Assert.NotEmpty(pig.GetPage(1).ExperimentalAccess.Paths);
    }

    [Fact]
    public void EmbeddedNoteData_RoundTrips()
    {
        var data = new byte[] { 9, 8, 7, 6, 5 };
        var src = SourcePdf();
        var p = ImportedPage();
        var outPath = Path.Combine(_dir, "container.pdf");

        _container.WriteWithSource(new PageVectorExport?[] { Overlay(p) }, new[] { p.Width }, new[] { p.Height },
            new int?[] { 0 }, src, data, outPath, notePages: new[] { p });

        var (back, source) = _container.ExtractDataAndSource(outPath);
        Assert.Equal(data, back);
        Assert.Equal(src, source);
        // A container written by this build can have its pages reused next save.
        Assert.Equal(1, _container.TryLoadForReuse(outPath)?.PageCount);
    }

    [Fact]
    public void PlainExport_HasNoEmbeddedData_AndIsNotReused()
    {
        var p = ImportedPage();
        var outPath = Path.Combine(_dir, "export.pdf");
        _container.WriteWithSource(new PageVectorExport?[] { Overlay(p) }, new[] { p.Width }, new[] { p.Height },
            new int?[] { 0 }, SourcePdf(), null, outPath, notePages: new[] { p });

        Assert.Null(_container.ExtractData(outPath));
        Assert.Null(_container.TryLoadForReuse(outPath));
    }
}
