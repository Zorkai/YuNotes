using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using YuNotes.Models;

namespace YuNotes.Services;

public sealed class DocumentService
{
    public void EnsureFolder(string folder) => Directory.CreateDirectory(folder);

    public IEnumerable<DocumentInfo> ListRecent(string folder, int limit = 30)
    {
        if (!Directory.Exists(folder)) yield break;
        var di = new DirectoryInfo(folder);
        var files = new List<FileInfo>();
        files.AddRange(di.GetFiles("*.pdf"));
        files.AddRange(di.GetFiles("*.yunote"));
        files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        int n = Math.Min(limit, files.Count);
        for (int i = 0; i < n; i++)
        {
            DocumentInfo? info = null;
            try { info = ReadInfo(files[i].FullName); } catch { }
            if (info is not null) yield return info;
        }
    }

    public DocumentInfo ReadInfo(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            return ReadPdfInfo(path);
        return ReadSqliteInfo(path);
    }

    private DocumentInfo ReadSqliteInfo(string path)
    {
        using var conn = OpenConn(path);
        EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,title,created_at,modified_at,page_count,source_pdf_path FROM document LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException("Document row missing");
        return new DocumentInfo
        {
            Id = r.GetString(0),
            Title = r.GetString(1),
            CreatedAt = DateTime.Parse(r.GetString(2)),
            ModifiedAt = DateTime.Parse(r.GetString(3)),
            PageCount = r.GetInt32(4),
            SourcePdfPath = r.IsDBNull(5) ? null : r.GetString(5),
            FilePath = path
        };
    }

    // For .pdf files: peek at the embedded SQLite blob if present, otherwise return
    // minimal info derived from the file itself so external PDFs still list.
    private DocumentInfo ReadPdfInfo(string path)
    {
        var container = new PdfContainerService();
        var blob = container.ExtractData(path);
        if (blob is not null && blob.Length > 0)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"yunotes-{Guid.NewGuid():N}.sqlite");
            try
            {
                File.WriteAllBytes(temp, blob);
                var info = ReadSqliteInfo(temp);
                info.FilePath = path;   // user-visible path is the .pdf
                return info;
            }
            finally { try { File.Delete(temp); } catch { } }
        }
        // External PDF without embedded YuNotes data — show file metadata only.
        var fi = new FileInfo(path);
        return new DocumentInfo
        {
            Title = Path.GetFileNameWithoutExtension(path),
            FilePath = path,
            CreatedAt = fi.CreationTimeUtc,
            ModifiedAt = fi.LastWriteTimeUtc,
            SourcePdfPath = path
        };
    }

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    // Open a .pdf file as a YuNotes document. If the PDF has YuNotes data embedded
    // (a SQLite blob), extract and open from that — full editable state is restored.
    // Otherwise rasterize the PDF pages so they show up as page backgrounds.
    public Document OpenPdfContainer(string pdfPath, PdfContainerService container, PdfImportService pdfImport)
    {
        var blob = container.ExtractData(pdfPath);
        if (blob is not null && blob.Length > 0)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"yunotes-{Guid.NewGuid():N}.sqlite");
            File.WriteAllBytes(temp, blob);
            try
            {
                var doc = Open(temp);
                doc.Info.FilePath = pdfPath;
                if (string.IsNullOrEmpty(doc.Info.SourcePdfPath)) doc.Info.SourcePdfPath = pdfPath;
                // Carry the pristine source PDF (if previously embedded) so the next save
                // can overlay strokes on top of the vector content instead of flattening.
                doc.SourcePdfBytes = container.ExtractSourcePdf(pdfPath);
                return doc;
            }
            finally { try { File.Delete(temp); } catch { } }
        }

        // Plain PDF, no embedded YuNotes data — treat as a fresh import and keep the
        // original bytes around so the first save still preserves vector content.
        var temp2 = Path.Combine(Path.GetTempPath(), $"yunotes-{Guid.NewGuid():N}.sqlite");
        try
        {
            var title = Path.GetFileNameWithoutExtension(pdfPath);
            var fresh = Create(temp2, title);
            pdfImport.ImportInto(fresh, pdfPath);
            fresh.Info.FilePath = pdfPath;
            fresh.Info.SourcePdfPath = pdfPath;
            fresh.SourcePdfBytes = File.ReadAllBytes(pdfPath);
            return fresh;
        }
        finally { try { File.Delete(temp2); } catch { } }
    }

    // Save a doc to its .pdf container: flatten pages as images + embed the
    // full SQLite blob alongside, so the next open restores editable objects.
    // flatPagePngs: when source PDF is present, these are TRANSPARENT overlays (just
    // user content). When there's no source, they are the full flattened page renders.
    // reusePageIndices/previousContainer: pages whose flattened image is
    // unchanged since the last full save are cloned from the existing container
    // (reusePageIndices[i] = that file's page index) instead of getting a fresh
    // render — the caller then skips rendering them entirely, which is what
    // makes saving a 30-page doc with 2 edited pages fast.
    public void SavePdfContainer(Document doc, byte[][] flatPagePngs, byte[]? thumbnail, PdfContainerService container,
                                 int?[]? reusePageIndices = null, byte[]? previousContainer = null)
    {
        var pdfPath = doc.Info.FilePath;
        var blob = SerializeToBlob(doc, thumbnail);
        var widths  = new double[doc.Pages.Count];
        var heights = new double[doc.Pages.Count];
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            widths[i]  = doc.Pages[i].Width;
            heights[i] = doc.Pages[i].Height;
        }

        if (doc.SourcePdfBytes is { Length: > 0 } src)
        {
            var srcIdx = new int?[doc.Pages.Count];
            for (int i = 0; i < doc.Pages.Count; i++) srcIdx[i] = doc.Pages[i].SourcePageIndex;
            container.WriteWithSource(flatPagePngs, widths, heights, srcIdx, src, blob, pdfPath,
                                      reusePageIndices, previousContainer);
        }
        else
        {
            container.Write(flatPagePngs, widths, heights, blob, pdfPath,
                            reusePageIndices, previousContainer);
        }
    }

    // Deep-enough clone for a background save: element data is copied, large
    // immutable blobs (page backgrounds, image PNGs, source PDF bytes) are
    // shared by reference — those are only ever replaced wholesale, never
    // mutated in place. Clone on the UI thread (fast: no blob copies), then
    // Save(clone) from a background thread while the user keeps editing the
    // original. Used by the .yunote autosave.
    public static Document CloneForSave(Document d)
    {
        var c = new Document
        {
            Info = new DocumentInfo
            {
                Id = d.Info.Id, Title = d.Info.Title, FilePath = d.Info.FilePath,
                CreatedAt = d.Info.CreatedAt, ModifiedAt = d.Info.ModifiedAt,
                PageCount = d.Info.PageCount, ThumbnailPath = d.Info.ThumbnailPath,
                SourcePdfPath = d.Info.SourcePdfPath
            },
            Template = CloneTemplate(d.Template)!,
            SourcePdfBytes = d.SourcePdfBytes,
            FlattenPageOrder = d.FlattenPageOrder is null ? null : new List<string>(d.FlattenPageOrder)
        };
        foreach (var id in d.FlattenDirtyPageIds) c.FlattenDirtyPageIds.Add(id);
        foreach (var p in d.Pages)
        {
            var np = new NotePage
            {
                Id = p.Id, Index = p.Index, Width = p.Width, Height = p.Height,
                BackgroundLeft = p.BackgroundLeft,
                BackgroundContentWidth = p.BackgroundContentWidth,
                BackgroundPng = p.BackgroundPng,
                SourcePageIndex = p.SourcePageIndex,
                TemplateOverride = CloneTemplate(p.TemplateOverride)
            };
            foreach (var st in p.Strokes)
            {
                var sc = new Stroke { Id = st.Id, Kind = st.Kind, Color = st.Color, Width = st.Width, PressureMode = st.PressureMode };
                sc.Points.AddRange(st.Points);
                np.Strokes.Add(sc);
            }
            foreach (var sh in p.Shapes)
                np.Shapes.Add(new ShapeElement
                {
                    Id = sh.Id, Kind = sh.Kind,
                    X1 = sh.X1, Y1 = sh.Y1, X2 = sh.X2, Y2 = sh.Y2, X3 = sh.X3, Y3 = sh.Y3,
                    Rotation = sh.Rotation,
                    Color = sh.Color, StrokeWidth = sh.StrokeWidth, Filled = sh.Filled
                });
            foreach (var t in p.Texts)
                np.Texts.Add(new TextElement
                {
                    Id = t.Id, X = t.X, Y = t.Y, Width = t.Width, Height = t.Height,
                    Rotation = t.Rotation, Text = t.Text, FontSize = t.FontSize, Color = t.Color,
                    FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic
                });
            foreach (var im in p.Images)
                np.Images.Add(new ImageElement
                {
                    Id = im.Id, X = im.X, Y = im.Y, Width = im.Width, Height = im.Height,
                    Rotation = im.Rotation, PngData = im.PngData
                });
            // TextRuns are not persisted — re-extracted lazily on open.
            c.Pages.Add(np);
        }
        return c;
    }

    private static TemplateSettings? CloneTemplate(TemplateSettings? t) =>
        t is null ? null : new TemplateSettings
        {
            Kind = t.Kind, Spacing = t.Spacing,
            LineColorHex = t.LineColorHex, LineThickness = t.LineThickness
        };

    // Serializes the editable model to a standalone SQLite blob — the same
    // bytes SavePdfContainer embeds. Touches the live model, so call it on the
    // UI thread; the returned bytes are then safe to persist from a background
    // thread (used by the pdf autosave's blob-only container update).
    public byte[] SerializeToBlob(Document doc, byte[]? thumbnail = null)
    {
        var orig = doc.Info.FilePath;
        var temp = Path.Combine(Path.GetTempPath(), $"yunotes-{Guid.NewGuid():N}.sqlite");
        try
        {
            try
            {
                doc.Info.FilePath = temp;
                Save(doc);
                if (thumbnail is { Length: > 0 }) SaveThumbnail(temp, thumbnail);
            }
            finally { doc.Info.FilePath = orig; }
            return File.ReadAllBytes(temp);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    public byte[]? ReadThumbnail(string path)
    {
        try
        {
            if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var blob = new PdfContainerService().ExtractData(path);
                if (blob is null || blob.Length == 0) return null;
                var temp = Path.Combine(Path.GetTempPath(), $"yunotes-{Guid.NewGuid():N}.sqlite");
                try
                {
                    File.WriteAllBytes(temp, blob);
                    return ReadThumbnailSqlite(temp);
                }
                finally { try { File.Delete(temp); } catch { } }
            }
            return ReadThumbnailSqlite(path);
        }
        catch { return null; }
    }

    private byte[]? ReadThumbnailSqlite(string sqlitePath)
    {
        using var conn = OpenConn(sqlitePath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT thumbnail FROM document LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (r.Read() && !r.IsDBNull(0)) return (byte[])r["thumbnail"];
        return null;
    }

    public void SaveThumbnail(string path, byte[] png)
    {
        try
        {
            using var conn = OpenConn(path);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE document SET thumbnail=$t";
            cmd.Parameters.AddWithValue("$t", png);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // Fallback for documents with no saved thumbnail (plain PDFs that were never
    // annotated, or .yunote files never saved since thumbnails were added —
    // including most of what lands in trash): render a small first-page preview.
    // .yunote renders are backfilled into the file so the cost is paid once;
    // plain PDFs render on demand — we never rewrite a file the user didn't save.
    public byte[]? RenderFallbackThumbnail(string path, TemplateService templates)
    {
        try
        {
            if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = File.ReadAllBytes(path);
                using var bmp = PDFtoImage.Conversion.ToImage(bytes, page: (Index)0,
                    options: new PDFtoImage.RenderOptions { Width = 280, WithAspectRatio = true });
                using var ms = new MemoryStream();
                bmp.Encode(ms, SkiaSharp.SKEncodedImageFormat.Png, 85);
                return ms.ToArray();
            }

            var doc = Open(path);
            if (doc.Pages.Count == 0) return null;
            var page = doc.Pages[0];
            var template = page.TemplateOverride ?? doc.Template;

            var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            var temps = new List<Microsoft.Graphics.Canvas.CanvasBitmap>();
            try
            {
                Microsoft.Graphics.Canvas.CanvasBitmap? bg = null;
                if (page.BackgroundPng is { Length: > 0 })
                {
                    using var bgMs = new MemoryStream(page.BackgroundPng);
                    bg = Microsoft.Graphics.Canvas.CanvasBitmap.LoadAsync(device, bgMs.AsRandomAccessStream())
                            .AsTask().GetAwaiter().GetResult();
                    temps.Add(bg);
                }
                var images = new Dictionary<string, Microsoft.Graphics.Canvas.CanvasBitmap>();
                foreach (var img in page.Images)
                {
                    try
                    {
                        using var imgMs = new MemoryStream(img.PngData);
                        var ib = Microsoft.Graphics.Canvas.CanvasBitmap.LoadAsync(device, imgMs.AsRandomAccessStream())
                                    .AsTask().GetAwaiter().GetResult();
                        images[img.Id] = ib;
                        temps.Add(ib);
                    }
                    catch { }
                }

                const float scale = 0.2f;
                using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(
                    device, (float)(page.Width * scale), (float)(page.Height * scale), 96f);
                using (var ds = target.CreateDrawingSession())
                {
                    ds.Clear(Microsoft.UI.Colors.White);
                    ds.Transform = System.Numerics.Matrix3x2.CreateScale(scale);
                    new Rendering.PageRenderer(templates).DrawPage(ds, device, page, template, bg, images);
                }
                using var outMs = new MemoryStream();
                target.SaveAsync(outMs.AsRandomAccessStream(), Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png)
                      .AsTask().GetAwaiter().GetResult();
                var png = outMs.ToArray();
                SaveThumbnail(path, png);   // backfill — next refresh reads it straight from the file
                return png;
            }
            finally { foreach (var t in temps) t.Dispose(); }
        }
        catch { return null; }
    }

    private static SqliteConnection OpenConn(string path)
    {
        // Pooling=False: without it Microsoft.Data.Sqlite keeps the native handle
        // alive in a pool after Dispose, which blocks the very next File.ReadAllBytes /
        // File.Delete in the PDF container flow with a "used by another process" error.
        var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        return conn;
    }

    public Document Create(string path, string title)
    {
        if (File.Exists(path)) File.Delete(path);
        using var conn = OpenConn(path);
        EnsureSchema(conn);
        var doc = new Document();
        doc.Info.Title = title;
        doc.Info.FilePath = path;
        doc.Pages.Add(new NotePage { Index = 0 });
        WriteAll(conn, doc);
        return doc;
    }

    // Create a new YuNotes .pdf with a single blank page, ready to draw on. The PDF
    // also carries the SQLite editable state embedded inside it.
    public Document CreatePdf(string path, string title, PdfContainerService container, TemplateKind template = TemplateKind.Blank)
    {
        if (File.Exists(path)) File.Delete(path);
        var doc = new Document();
        doc.Info.Title = title;
        doc.Info.FilePath = path;
        doc.Template.Kind = template;
        doc.Pages.Add(new NotePage { Index = 0 });

        var tempSqlite = Path.Combine(Path.GetTempPath(), $"yunotes-{Guid.NewGuid():N}.sqlite");
        try
        {
            try
            {
                doc.Info.FilePath = tempSqlite;
                Save(doc);
            }
            finally { doc.Info.FilePath = path; }

            var blob = File.ReadAllBytes(tempSqlite);
            var p = doc.Pages[0];
            container.Write(
                new byte[][] { Array.Empty<byte>() },
                new[] { p.Width },
                new[] { p.Height },
                blob, path);
        }
        finally { try { File.Delete(tempSqlite); } catch { } }

        return doc;
    }

    public Document Open(string path)
    {
        using var conn = OpenConn(path);
        EnsureSchema(conn);
        var doc = new Document();
        doc.Info.FilePath = path;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id,title,created_at,modified_at,page_count,template_json,source_pdf_path,flatten_json FROM document LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                doc.Info.Id = r.GetString(0);
                doc.Info.Title = r.GetString(1);
                doc.Info.CreatedAt = DateTime.Parse(r.GetString(2));
                doc.Info.ModifiedAt = DateTime.Parse(r.GetString(3));
                doc.Info.PageCount = r.GetInt32(4);
                if (!r.IsDBNull(5))
                {
                    var t = JsonSerializer.Deserialize<TemplateSettings>(r.GetString(5));
                    if (t is not null) doc.Template = t;
                }
                if (!r.IsDBNull(6)) doc.Info.SourcePdfPath = r.GetString(6);
                if (!r.IsDBNull(7))
                {
                    try
                    {
                        var fl = JsonSerializer.Deserialize<FlattenState>(r.GetString(7));
                        if (fl?.Order is { Count: > 0 })
                        {
                            doc.FlattenPageOrder = fl.Order;
                            foreach (var id in fl.Dirty ?? new()) doc.FlattenDirtyPageIds.Add(id);
                        }
                    }
                    catch { }   // corrupt state just disables reuse for the next save
                }
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id,idx,width,height,background_png,template_json,source_page_index,background_left,background_width FROM page ORDER BY idx";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var p = new NotePage
                {
                    Id = r.GetString(0),
                    Index = r.GetInt32(1),
                    Width = r.GetDouble(2),
                    Height = r.GetDouble(3),
                    BackgroundPng = r.IsDBNull(4) ? null : (byte[])r["background_png"]
                };
                if (!r.IsDBNull(5))
                {
                    try { p.TemplateOverride = JsonSerializer.Deserialize<TemplateSettings>(r.GetString(5)); }
                    catch { }
                }
                if (!r.IsDBNull(6)) p.SourcePageIndex = r.GetInt32(6);
                if (!r.IsDBNull(7)) p.BackgroundLeft = r.GetDouble(7);
                if (!r.IsDBNull(8)) p.BackgroundContentWidth = r.GetDouble(8);
                doc.Pages.Add(p);
            }
        }

        foreach (var p in doc.Pages)
        {
            // Per-element try/catch: a single corrupt row (truncated stroke blob,
            // malformed JSON) should cost that one element, not abort the whole
            // open and leave the user unable to reach the rest of their document.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,data FROM stroke WHERE page_id=$p";
                cmd.Parameters.AddWithValue("$p", p.Id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    try { p.Strokes.Add(Stroke.Deserialize((byte[])r["data"], r.GetString(0))); }
                    catch { }
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,json FROM shape_element WHERE page_id=$p";
                cmd.Parameters.AddWithValue("$p", p.Id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    try
                    {
                        var sh = JsonSerializer.Deserialize<ShapeElement>(r.GetString(1));
                        if (sh is not null) p.Shapes.Add(sh);
                    }
                    catch { }
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,json FROM text_element WHERE page_id=$p";
                cmd.Parameters.AddWithValue("$p", p.Id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    try
                    {
                        var t = JsonSerializer.Deserialize<TextElement>(r.GetString(1));
                        if (t is not null) p.Texts.Add(t);
                    }
                    catch { }
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,x,y,w,h,png FROM image_element WHERE page_id=$p";
                cmd.Parameters.AddWithValue("$p", p.Id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    try
                    {
                        p.Images.Add(new ImageElement
                        {
                            Id = r.GetString(0),
                            X = r.GetDouble(1), Y = r.GetDouble(2),
                            Width = r.GetDouble(3), Height = r.GetDouble(4),
                            PngData = (byte[])r["png"]
                        });
                    }
                    catch { }
                }
            }
        }
        return doc;
    }

    public void Save(Document doc)
    {
        doc.Info.ModifiedAt = DateTime.UtcNow;
        doc.Info.PageCount = doc.Pages.Count;
        using var conn = OpenConn(doc.Info.FilePath);
        EnsureSchema(conn);
        using var tx = conn.BeginTransaction();
        WriteAll(conn, doc);
        tx.Commit();
    }

    private static void WriteAll(SqliteConnection conn, Document doc)
    {
        EnsureSchema(conn);
        Exec(conn, "DELETE FROM stroke");
        Exec(conn, "DELETE FROM shape_element");
        Exec(conn, "DELETE FROM text_element");
        Exec(conn, "DELETE FROM image_element");
        Exec(conn, "DELETE FROM page");
        Exec(conn, "DELETE FROM document");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO document(id,title,created_at,modified_at,page_count,template_json,source_pdf_path,flatten_json)
                                VALUES($id,$t,$c,$m,$pc,$tpl,$src,$fl)";
            cmd.Parameters.AddWithValue("$id", doc.Info.Id);
            cmd.Parameters.AddWithValue("$t", doc.Info.Title);
            cmd.Parameters.AddWithValue("$c", doc.Info.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$m", doc.Info.ModifiedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$pc", doc.Pages.Count);
            cmd.Parameters.AddWithValue("$tpl", JsonSerializer.Serialize(doc.Template));
            cmd.Parameters.AddWithValue("$src", (object?)doc.Info.SourcePdfPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fl", doc.FlattenPageOrder is null
                ? (object)DBNull.Value
                : JsonSerializer.Serialize(new FlattenState
                    { Order = doc.FlattenPageOrder, Dirty = new List<string>(doc.FlattenDirtyPageIds) }));
            cmd.ExecuteNonQuery();
        }

        for (int i = 0; i < doc.Pages.Count; i++)
        {
            var p = doc.Pages[i];
            p.Index = i;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO page(id,idx,width,height,background_png,template_json,source_page_index,background_left,background_width)
                                    VALUES($id,$i,$w,$h,$bg,$tpl,$src,$bl,$bw)";
                cmd.Parameters.AddWithValue("$id", p.Id);
                cmd.Parameters.AddWithValue("$i", p.Index);
                cmd.Parameters.AddWithValue("$w", p.Width);
                cmd.Parameters.AddWithValue("$h", p.Height);
                cmd.Parameters.AddWithValue("$bg", (object?)p.BackgroundPng ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tpl",
                    p.TemplateOverride is null ? (object)DBNull.Value : JsonSerializer.Serialize(p.TemplateOverride));
                cmd.Parameters.AddWithValue("$src", (object?)p.SourcePageIndex ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bl", p.BackgroundLeft);
                cmd.Parameters.AddWithValue("$bw", p.BackgroundContentWidth);
                cmd.ExecuteNonQuery();
            }
            foreach (var s in p.Strokes)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO stroke(id,page_id,data) VALUES($id,$p,$d)";
                cmd.Parameters.AddWithValue("$id", s.Id);
                cmd.Parameters.AddWithValue("$p", p.Id);
                cmd.Parameters.AddWithValue("$d", s.Serialize());
                cmd.ExecuteNonQuery();
            }
            foreach (var sh in p.Shapes)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO shape_element(id,page_id,json) VALUES($id,$p,$j)";
                cmd.Parameters.AddWithValue("$id", sh.Id);
                cmd.Parameters.AddWithValue("$p", p.Id);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(sh));
                cmd.ExecuteNonQuery();
            }
            foreach (var t in p.Texts)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO text_element(id,page_id,json) VALUES($id,$p,$j)";
                cmd.Parameters.AddWithValue("$id", t.Id);
                cmd.Parameters.AddWithValue("$p", p.Id);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(t));
                cmd.ExecuteNonQuery();
            }
            foreach (var img in p.Images)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO image_element(id,page_id,x,y,w,h,png) VALUES($id,$p,$x,$y,$w,$h,$png)";
                cmd.Parameters.AddWithValue("$id", img.Id);
                cmd.Parameters.AddWithValue("$p", p.Id);
                cmd.Parameters.AddWithValue("$x", img.X);
                cmd.Parameters.AddWithValue("$y", img.Y);
                cmd.Parameters.AddWithValue("$w", img.Width);
                cmd.Parameters.AddWithValue("$h", img.Height);
                cmd.Parameters.AddWithValue("$png", img.PngData);
                cmd.ExecuteNonQuery();
            }
        }
    }

    // Persisted alongside the document row: which container page each NotePage
    // was flattened to at the last full save, and which pages changed since.
    private sealed class FlattenState
    {
        public List<string>? Order { get; set; }
        public List<string>? Dirty { get; set; }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureSchema(SqliteConnection c)
    {
        Exec(c, @"CREATE TABLE IF NOT EXISTS document(
            id TEXT PRIMARY KEY, title TEXT, created_at TEXT, modified_at TEXT,
            page_count INTEGER, template_json TEXT, thumbnail BLOB, source_pdf_path TEXT)");
        // Add columns for files created with older schemas (idempotent).
        try { Exec(c, "ALTER TABLE document ADD COLUMN thumbnail BLOB"); } catch { }
        try { Exec(c, "ALTER TABLE document ADD COLUMN source_pdf_path TEXT"); } catch { }
        try { Exec(c, "ALTER TABLE document ADD COLUMN flatten_json TEXT"); } catch { }
        Exec(c, @"CREATE TABLE IF NOT EXISTS page(
            id TEXT PRIMARY KEY, idx INTEGER, width REAL, height REAL, background_png BLOB,
            template_json TEXT, source_page_index INTEGER)");
        try { Exec(c, "ALTER TABLE page ADD COLUMN template_json TEXT"); } catch { }
        try { Exec(c, "ALTER TABLE page ADD COLUMN source_page_index INTEGER"); } catch { }
        try { Exec(c, "ALTER TABLE page ADD COLUMN background_left REAL DEFAULT 0"); } catch { }
        try { Exec(c, "ALTER TABLE page ADD COLUMN background_width REAL DEFAULT 0"); } catch { }
        Exec(c, @"CREATE TABLE IF NOT EXISTS stroke(
            id TEXT PRIMARY KEY, page_id TEXT, data BLOB)");
        Exec(c, @"CREATE INDEX IF NOT EXISTS idx_stroke_page ON stroke(page_id)");
        Exec(c, @"CREATE TABLE IF NOT EXISTS shape_element(
            id TEXT PRIMARY KEY, page_id TEXT, json TEXT)");
        Exec(c, @"CREATE INDEX IF NOT EXISTS idx_shape_page ON shape_element(page_id)");
        Exec(c, @"CREATE TABLE IF NOT EXISTS text_element(
            id TEXT PRIMARY KEY, page_id TEXT, json TEXT)");
        Exec(c, @"CREATE INDEX IF NOT EXISTS idx_text_page ON text_element(page_id)");
        Exec(c, @"CREATE TABLE IF NOT EXISTS image_element(
            id TEXT PRIMARY KEY, page_id TEXT, x REAL, y REAL, w REAL, h REAL, png BLOB)");
        Exec(c, @"CREATE INDEX IF NOT EXISTS idx_image_page ON image_element(page_id)");
    }
}
