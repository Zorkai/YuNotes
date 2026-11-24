using System;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using YuNotes.Rendering;

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

    // Marks how the page content was written, in the document info dictionary.
    // Pages are only cloned for reuse from a container written the same way —
    // an older file's full-page raster flattens get re-written as vectors once.
    private const string FlattenFormatKey = "/YuNotesFlatten";
    // vector-2: widened imported pages are written at their full width (they
    // were squeezed into the original page size before), so pages saved by
    // vector-1 are re-rendered once instead of reused.
    private const string FlattenFormat = "vector-2";

    // Each pages entry is drawn as vectors onto a white page (PdfVectorWriter); a
    // null entry yields a blank white page.
    // reuseIndices/previousContainer: when reuseIndices[i] is set, page i is cloned
    // as-is from the previous container output (its flatten is unchanged) and
    // pages[i] is ignored — the caller skipped preparing it.
    // embeddedData null: a plain export, no editable state attached.
    public void Write(PageVectorExport?[] pages, double[] widths, double[] heights, byte[]? embeddedData, string outputPath,
                      int?[]? reuseIndices = null, byte[]? previousContainer = null)
    {
        using var prev = OpenPrevious(previousContainer, reuseIndices);
        using var doc = new PdfDocument();
        for (int i = 0; i < pages.Length; i++)
        {
            if (TryReusePage(doc, prev, reuseIndices, i)) continue;
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(widths[i]);
            page.Height = XUnit.FromPoint(heights[i]);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
            if (pages[i] is { } p) PdfVectorWriter.Draw(gfx, p, page.Width.Point, page.Height.Point);
        }
        Finish(doc, embeddedData, null, outputPath);
    }

    // Vector-preserving write: clone pages from a source PDF (so the original's
    // selectable text and vector graphics survive), then draw the user's strokes /
    // shapes / images as vectors on top of each. Pages that don't map to the
    // source (sourceIndices[i] == null) get a blank white page.
    // Unchanged pages (reuseIndices[i] set) are cloned from the previous container
    // output instead — they already carry source content + overlay.
    public void WriteWithSource(PageVectorExport?[] overlays, double[] widths, double[] heights,
                                int?[] sourceIndices, byte[] sourcePdfBytes,
                                byte[]? embeddedData, string outputPath,
                                int?[]? reuseIndices = null, byte[]? previousContainer = null,
                                System.Collections.Generic.IReadOnlyList<YuNotes.Models.NotePage>? notePages = null)
    {
        using var prev = OpenPrevious(previousContainer, reuseIndices);
        using var source = OpenSource(sourcePdfBytes);
        using var doc = new PdfDocument();
        // Opened only if a page was widened or had space inserted (see AddRebuiltSourcePage).
        MemoryStream? formStream = null;
        XPdfForm? form = null;

        for (int i = 0; i < overlays.Length; i++)
        {
            if (TryReusePage(doc, prev, reuseIndices, i)) continue;
            PdfPage page;
            var srcIdx = sourceIndices[i];
            if (srcIdx is int idx && idx >= 0 && idx < source.PageCount
                && notePages is not null && i < notePages.Count && NeedsRebuiltPage(notePages[i]))
            {
                formStream ??= new MemoryStream(sourcePdfBytes, 0, sourcePdfBytes.Length, writable: false, publiclyVisible: true);
                form ??= XPdfForm.FromStream(formStream);
                page = AddRebuiltSourcePage(doc, source.Pages[idx], form, idx, notePages[i]);
                if (overlays[i] is { } withTemplate)
                {
                    using var tg = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    PdfVectorWriter.DrawOwnedTemplate(tg, notePages[i], withTemplate.Template,
                                                      page.Width.Point, page.Height.Point);
                }
            }
            else if (srcIdx is int idx2 && idx2 >= 0 && idx2 < source.PageCount)
            {
                page = doc.AddPage(source.Pages[idx2]);   // clones the source page's vector content
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
            if (overlays[i] is not { } overlay) continue;
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            PdfVectorWriter.Draw(gfx, overlay, page.Width.Point, page.Height.Point);
        }

        Finish(doc, embeddedData, sourcePdfBytes, outputPath);
        form?.Dispose();
        formStream?.Dispose();
    }

    // A source page that no longer has the original's size: widened to the
    // left/right, or with blank space inserted. Cloning the original page
    // would keep its old size and squeeze the notes into it, so these get a
    // new page at the same scale as the original — wider and/or taller.
    private static bool NeedsRebuiltPage(YuNotes.Models.NotePage note)
    {
        if (YuNotes.Models.PageGaps.Any(note)) return true;
        if (note.BackgroundLeft > 0.5) return true;
        return note.BackgroundContentWidth > 0
            && note.Width - note.BackgroundLeft - note.BackgroundContentWidth > 0.5;
    }

    // The new page for NeedsRebuiltPage: the original page is drawn onto it
    // (still as vectors, so its text stays selectable) at the PDF's place in
    // the widened page, once per piece when space was inserted — each piece
    // shifted down past the space above it and clipped to its own rows.
    private static PdfPage AddRebuiltSourcePage(PdfDocument doc, PdfPage src, XPdfForm form, int srcIndex,
                                              YuNotes.Models.NotePage note)
    {
        double srcW = src.Width.Point, srcH = src.Height.Point;
        double bgLeft = note.BackgroundLeft;
        double bgW = note.BackgroundContentWidth > 0 ? note.BackgroundContentWidth : note.Width - bgLeft;
        double bgH = YuNotes.Models.PageGaps.BackgroundHeight(note);
        if (!(bgW > 0) || !(bgH > 0)) return doc.AddPage(src);
        double sx = srcW / bgW, sy = srcH / bgH;   // points per page unit

        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(note.Width * sx);
        page.Height = XUnit.FromPoint(note.Height * sy);
        form.PageIndex = srcIndex;
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawRectangle(XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
        foreach (var (segY, segH, dstY) in YuNotes.Models.PageGaps.Segments(note))
        {
            var st = gfx.Save();
            gfx.IntersectClip(new XRect(bgLeft * sx, dstY * sy, bgW * sx, segH * sy));
            gfx.DrawImage(form, bgLeft * sx, (dstY - segY) * sy, srcW, srcH);
            gfx.Restore(st);
        }
        return page;
    }

    private static void Finish(PdfDocument doc, byte[]? embeddedData, byte[]? sourcePdfBytes, string outputPath)
    {
        if (embeddedData is not null)
        {
            doc.Info.Elements.SetString(FlattenFormatKey, FlattenFormat);
            AttachEmbeddedFile(doc, EmbeddedName, embeddedData);
            if (sourcePdfBytes is not null) AttachEmbeddedFile(doc, EmbeddedSourceName, sourcePdfBytes);
        }
        doc.Save(outputPath);
    }

    // Reads the current container file and reports its page count, so the save
    // can plan which flattened pages to clone instead of re-render. The bytes
    // are read up front — the write later replaces the same path. Null when
    // missing/unreadable: the caller then renders everything, as before.
    public (byte[] Bytes, int PageCount)? TryLoadForReuse(string pdfPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(pdfPath);
            using var ms = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            if (doc.Info.Elements.GetString(FlattenFormatKey) != FlattenFormat) return null;
            return (bytes, doc.PageCount);
        }
        catch { return null; }
    }

    // No try/catch: when reuse was planned, the caller skipped rendering those
    // pages — silently writing without the previous container would emit blank
    // pages. Let the exception fail the whole save instead (nothing is written).
    private static PdfDocument? OpenPrevious(byte[]? previousContainer, int?[]? reuseIndices)
    {
        if (previousContainer is not { Length: > 0 } || reuseIndices is null) return null;
        return OpenSource(previousContainer);
    }

    private static bool TryReusePage(PdfDocument doc, PdfDocument? prev, int?[]? reuseIndices, int i)
    {
        if (prev is null || reuseIndices?[i] is not int rIdx) return false;
        if (rIdx < 0 || rIdx >= prev.PageCount)   // planned reuse gone wrong — fail loud, not blank
            throw new InvalidDataException($"Reuse index {rIdx} out of range for previous container ({prev.PageCount} pages).");
        doc.AddPage(prev.Pages[rIdx]);
        return true;
    }

    private static PdfDocument OpenSource(byte[] bytes)
    {
        // PdfReader.Open from byte[] needs a seekable stream.
        var ms = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
        return PdfReader.Open(ms, PdfDocumentOpenMode.Import);
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

    public (byte[]? Data, byte[]? SourcePdf) ExtractDataAndSource(string pdfPath)
    {
        try
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            return (FindEmbeddedFile(doc, EmbeddedName), FindEmbeddedFile(doc, EmbeddedSourceName));
        }
        catch { return (null, null); }
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

    // Replaces just the embedded editable-state blob inside an existing
    // container PDF, leaving the (already flattened) page content untouched.
    // Involves no rendering and no model access, so it's safe to run on a
    // background thread — autosave uses it because re-flattening every page
    // froze the UI for seconds on large documents. Returns false when the PDF
    // has no embedded blob yet (fresh import never fully saved); the caller
    // must fall back to a full container write.
    public bool TryUpdateEmbeddedData(string pdfPath, byte[] data)
    {
        try
        {
            // Load fully into memory so the file isn't locked during the swap.
            var bytes = File.ReadAllBytes(pdfPath);
            using var ms = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);

            var fileObj = FindEmbeddedFileDict(doc, EmbeddedName);
            if (fileObj?.Stream is null) return false;
            fileObj.Stream.Value = data;
            if (fileObj.Elements["/Params"] is PdfDictionary p)
                p.Elements["/Size"] = new PdfInteger(data.Length);

            // Write-to-temp + atomic replace so a crash mid-save can't corrupt
            // the container.
            var tmp = pdfPath + ".yunotes-tmp";
            doc.Save(tmp);
            try { File.Replace(tmp, pdfPath, null); }
            catch (IOException)
            {
                File.Copy(tmp, pdfPath, overwrite: true);
                try { File.Delete(tmp); } catch { }
            }
            return true;
        }
        catch { return false; }
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
        => FindEmbeddedFileDict(doc, name)?.Stream?.UnfilteredValue;

    // Returns the embedded-file STREAM dictionary (the object whose stream
    // holds the bytes) for `name`, or null if absent.
    private static PdfDictionary? FindEmbeddedFileDict(PdfDocument doc, string name)
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
            return fileObj;
        }
        return null;
    }
}
