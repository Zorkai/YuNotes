using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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

        var pages = new NotePage[pageCount];

        // Parallelize page rendering across all available CPU cores to drastically
        // speed up PDF importing and avoid single-threaded rasterization/compression bottlenecks.
        Parallel.For(0, pageCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            using var bmp = Conversion.ToImage(bytes, page: (Index)i, options: new RenderOptions { Dpi = dpi });
            using var ms = new MemoryStream();
            bmp.Encode(ms, SKEncodedImageFormat.Jpeg, 90);
            pages[i] = new NotePage
            {
                Index = i,
                Width  = bmp.Width  / scale,
                Height = bmp.Height / scale,
                BackgroundPng = ms.ToArray(),
                SourcePageIndex = i   // remember which source PDF page this corresponds to
            };
        });

        for (int i = 0; i < pageCount; i++)
        {
            doc.Pages.Add(pages[i]);
        }
        doc.Info.PageCount = doc.Pages.Count;
    }
}
