using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace YuNotes.Models;

public enum TemplateKind { Blank, Grid, Dots, Lined, Cornell }

public sealed class TemplateSettings
{
    public TemplateKind Kind { get; set; } = TemplateKind.Blank;
    public double Spacing { get; set; } = 32;
    public string LineColorHex { get; set; } = "#FFE0E3EA";
    public double LineThickness { get; set; } = 1;
}

public sealed class DocumentInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled";
    public string FilePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public int PageCount { get; set; } = 1;
    public string? ThumbnailPath { get; set; }
    // When set, this doc is the editable sidecar for an external PDF. Saving flattens
    // pages back to this path so changes show up when the PDF is opened in any viewer.
    public string? SourcePdfPath { get; set; }
}

public sealed class Document
{
    public DocumentInfo Info { get; set; } = new();
    public ObservableCollection<NotePage> Pages { get; } = new();
    public TemplateSettings Template { get; set; } = new();
    public bool IsDirty { get; set; }

    // Original PDF bytes (vector). Kept around in memory after open so that save
    // can copy the original vector pages and only overlay user content on top —
    // preserves selectable text in the output PDF.
    public byte[]? SourcePdfBytes { get; set; }

    // Page ids in the order they were flattened into the container .pdf at the
    // last full save (container page i ↔ FlattenPageOrder[i]). Null = the file
    // holds no reusable flatten (legacy file, .yunote, or never fully saved).
    public List<string>? FlattenPageOrder { get; set; }

    // Pages edited since that flatten — their images in the container are stale
    // and must be re-rendered on the next full save; every other page's
    // flattened image can be cloned from the existing file. Persisted, so a
    // blob-only autosave followed by an app close still re-renders only these.
    public HashSet<string> FlattenDirtyPageIds { get; } = new();

    // null page = unknown scope → conservatively mark every page stale.
    public void MarkFlattenDirty(NotePage? page)
    {
        if (page is not null) { FlattenDirtyPageIds.Add(page.Id); return; }
        foreach (var p in Pages) FlattenDirtyPageIds.Add(p.Id);
    }
}

public sealed class NotePage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Index { get; set; }
    public double Width { get; set; } = 1240;   // A4 @ ~150dpi
    public double Height { get; set; } = 1754;
    // X offset of the PDF background within the (possibly extended) canvas. 0 = no left extension.
    public double BackgroundLeft { get; set; } = 0;
    // Natural width of the PDF background. 0 means "use full page width" (legacy/no extension).
    public double BackgroundContentWidth { get; set; } = 0;
    public byte[]? BackgroundPng { get; set; }  // rendered PDF page, if imported
    public TemplateSettings? TemplateOverride { get; set; }   // null = inherit Document.Template
    // If this page corresponds to a page in Document.SourcePdfBytes, this is its index there.
    // Lets save copy the original vector content instead of flattening to raster.
    public int? SourcePageIndex { get; set; }
    public List<Stroke> Strokes { get; } = new();
    public List<ShapeElement> Shapes { get; } = new();
    public List<TextElement> Texts { get; } = new();
    public List<ImageElement> Images { get; } = new();
    // PDF source text with positions in this page's coord space. Populated lazily after
    // load when Document.SourcePdfBytes is available. Used for "select text like a
    // browser" while the navigator/hand tool is active.
    public List<TextRun> TextRuns { get; } = new();
}

// One word (or contiguous text fragment) extracted from the source PDF, with its
// bounding box in the page's coord space.
public sealed class TextRun
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    // For grouping into reading order. Same value ⇒ same visual line on the page.
    public int LineIndex { get; set; }
    // Index within the line, left-to-right.
    public int OrderInLine { get; set; }
}
