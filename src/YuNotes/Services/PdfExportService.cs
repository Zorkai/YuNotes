using System.IO;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using YuNotes.Models;

namespace YuNotes.Services;

// Renders each page (composed PNG from the renderer) into a PDF.
public sealed class PdfExportService
{
    public void Export(YuNotes.Models.Document doc, byte[][] composedPagePngs, string outputPath)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Fluent.Document.Create(container =>
        {
            for (int i = 0; i < composedPagePngs.Length; i++)
            {
                var png = composedPagePngs[i];
                var page = doc.Pages[i];
                container.Page(p =>
                {
                    p.Size(new PageSize((float)page.Width, (float)page.Height));
                    p.Margin(0);
                    p.Content().Image(png).FitArea();
                });
            }
        }).GeneratePdf(outputPath);
    }
}
