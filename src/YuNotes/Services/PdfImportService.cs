using System;
using System.Collections.Generic;
using System.IO;
using PDFtoImage;
using YuNotes.Models;
using SkiaSharp;

namespace YuNotes.Services;

public sealed class PdfImportService
{
    // Page coordinates live in this DPI space. Strokes, text, etc. are stored against it.
    // The background bitmap is rendered at a higher DPI so zooming in stays crisp without
    // changing the coordinate system of any existing strokes.
    private const double CoordDpi = 150.0;
    private const int    DefaultBitmapDpi = 300;   // 2x oversample of the coord space

    // Render PDF pages and add each as a page background.
    public void ImportInto(Document doc, string pdfPath, int dpi = DefaultBitmapDpi)
    {
        var scale = dpi / CoordDpi;
        var bytes = File.ReadAllBytes(pdfPath);
        int pageCount = Conversion.GetPageCount(bytes);
        doc.Pages.Clear();
        for (int i = 0; i < pageCount; i++)
        {
            using var bmp = Conversion.ToImage(bytes, page: (Index)i, options: new RenderOptions { Dpi = dpi });
            using var ms = new MemoryStream();
            bmp.Encode(ms, SKEncodedImageFormat.Png, 95);
            var page = new NotePage
            {
                Index = i,
                Width  = bmp.Width  / scale,
                Height = bmp.Height / scale,
                BackgroundPng = ms.ToArray(),
                SourcePageIndex = i   // remember which source PDF page this corresponds to
            };
            doc.Pages.Add(page);
        }
        doc.Info.PageCount = doc.Pages.Count;
    }
}
