using System;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace YuNotes.Services;

// A YuNotes "container" PDF: flattened page images on disk + the full editable
// state (a SQLite blob) carried alongside as a standard PDF embedded file.
// Reading back the embedded blob is what makes strokes / text / images re-editable.
public sealed class PdfContainerService
{
    private const string EmbeddedName = "yunotes-data.sqlite";
    private const string EmbeddedSourceName = "yunotes-source.pdf";

    static PdfContainerService()
    {
        // PDFsharp 6 needs an explicit encoding registration on .NET 8+ for legacy code pages.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    // Each pagePngs entry: a non-empty PNG draws onto a white page; an empty/null
    // array yields a blank white page (used right after "New note" before the user
    // has drawn anything worth flattening).
    public void Write(byte[][] pagePngs, double[] widths, double[] heights, byte[] embeddedData, string outputPath)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < pagePngs.Length; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(widths[i]);
            page.Height = XUnit.FromPoint(heights[i]);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
            DrawPngIfAny(gfx, pagePngs[i], page.Width.Point, page.Height.Point);
        }
        AttachEmbeddedFile(doc, EmbeddedName, embeddedData);
        doc.Save(outputPath);
    }

    // Vector-preserving write: clone pages from a source PDF (so the original's
    // selectable text and vector graphics survive), then draw a transparent overlay
    // PNG containing the user's strokes / text / images on top of each. Pages that
    // don't map to the source (sourceIndices[i] == null) get a blank white page.
    public void WriteWithSource(byte[][] overlayPngs, double[] widths, double[] heights,
                                int?[] sourceIndices, byte[] sourcePdfBytes,
                                byte[] embeddedData, string outputPath)
    {
        using var source = OpenSource(sourcePdfBytes);
        using var doc = new PdfDocument();

        for (int i = 0; i < overlayPngs.Length; i++)
        {
            PdfPage page;
            var srcIdx = sourceIndices[i];
            if (srcIdx is int idx && idx >= 0 && idx < source.PageCount)
            {
                page = doc.AddPage(source.Pages[idx]);   // clones the source page's vector content
            }
            else
            {
                page = doc.AddPage();
                page.Width = XUnit.FromPoint(widths[i]);
                page.Height = XUnit.FromPoint(heights[i]);
                using var bgGfx = XGraphics.FromPdfPage(page);
                bgGfx.DrawRectangle(XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
            }

            // Append-mode XGraphics so the overlay draws ON TOP of existing page content.
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            DrawPngIfAny(gfx, overlayPngs[i], page.Width.Point, page.Height.Point);
        }

        AttachEmbeddedFile(doc, EmbeddedName, embeddedData);
        AttachEmbeddedFile(doc, EmbeddedSourceName, sourcePdfBytes);
        doc.Save(outputPath);
    }

    private static PdfDocument OpenSource(byte[] bytes)
    {
        // PdfReader.Open from byte[] needs a seekable stream.
        var ms = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
        return PdfReader.Open(ms, PdfDocumentOpenMode.Import);
    }

    private static void DrawPngIfAny(XGraphics gfx, byte[]? png, double width, double height)
    {
        if (png is not { Length: > 0 }) return;
        using var ms = new MemoryStream(png, 0, png.Length, writable: false, publiclyVisible: true);
        var img = XImage.FromStream(ms);
        gfx.DrawImage(img, 0, 0, width, height);
    }

    public byte[]? ExtractSourcePdf(string pdfPath)
    {
        try
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            return FindEmbeddedFile(doc, EmbeddedSourceName);
        }
        catch { return null; }
    }

    public byte[]? ExtractData(string pdfPath)
    {
        try
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            return FindEmbeddedFile(doc, EmbeddedName);
        }
        catch { return null; }
    }

    public bool HasEmbeddedData(string pdfPath) => ExtractData(pdfPath) is not null;

    private static void AttachEmbeddedFile(PdfDocument doc, string name, byte[] data)
    {
        // Stream that holds the actual bytes.
        var fileStream = new PdfDictionary(doc);
        fileStream.CreateStream(data);
        fileStream.Elements["/Type"] = new PdfName("/EmbeddedFile");
        fileStream.Elements["/Subtype"] = new PdfName("/application#2Foctet-stream");
        fileStream.Elements["/Params"] = new PdfDictionary(doc);
        ((PdfDictionary)fileStream.Elements["/Params"]!).Elements["/Size"] = new PdfInteger(data.Length);
        doc.Internals.AddObject(fileStream);

        // File spec referencing the stream.
        var ef = new PdfDictionary(doc);
        ef.Elements["/F"] = fileStream.Reference;
        var spec = new PdfDictionary(doc);
        spec.Elements["/Type"] = new PdfName("/Filespec");
        spec.Elements["/F"] = new PdfString(name);
        spec.Elements["/UF"] = new PdfString(name);
        spec.Elements["/EF"] = ef;
        doc.Internals.AddObject(spec);

        // Names tree: /Catalog → /Names → /EmbeddedFiles → /Names = [ "filename", filespecRef, ... ]
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements["/Names"] is not PdfDictionary names)
        {
            names = new PdfDictionary(doc);
            catalog.Elements["/Names"] = names;
        }
        if (names.Elements["/EmbeddedFiles"] is not PdfDictionary efTree)
        {
            efTree = new PdfDictionary(doc);
            names.Elements["/EmbeddedFiles"] = efTree;
        }
        if (efTree.Elements["/Names"] is not PdfArray namesArray)
        {
            namesArray = new PdfArray(doc);
            efTree.Elements["/Names"] = namesArray;
        }
        // Replace any prior entry for this name; otherwise append.
        var specRef = spec.Reference!;
        for (int i = 0; i + 1 < namesArray.Elements.Count; i += 2)
        {
            if (namesArray.Elements[i] is PdfString s && s.Value == name)
            {
                namesArray.Elements[i + 1] = specRef;
                return;
            }
        }
        namesArray.Elements.Add(new PdfString(name));
        namesArray.Elements.Add(specRef);
    }

    private static byte[]? FindEmbeddedFile(PdfDocument doc, string name)
    {
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements["/Names"] is not PdfDictionary names) return null;
        if (names.Elements["/EmbeddedFiles"] is not PdfDictionary efTree) return null;
        var array = efTree.Elements["/Names"] as PdfArray;
        if (array is null) return null;

        for (int i = 0; i + 1 < array.Elements.Count; i += 2)
        {
            string? entryName = (array.Elements[i] as PdfString)?.Value;
            if (entryName != name) continue;

            // The next element is either the filespec dict or a reference to it.
            var raw = array.Elements[i + 1];
            var spec = raw is PdfReference r ? r.Value as PdfDictionary : raw as PdfDictionary;
            if (spec is null) continue;
            var ef = spec.Elements["/EF"] as PdfDictionary;
            if (ef is null) continue;
            var rawF = ef.Elements["/F"];
            var fileObj = rawF is PdfReference fr ? fr.Value as PdfDictionary : rawF as PdfDictionary;
            if (fileObj?.Stream is null) continue;
            return fileObj.Stream.UnfilteredValue;
        }
        return null;
    }
}
