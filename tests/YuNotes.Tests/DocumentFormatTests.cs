using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Windows.UI;
using Xunit;
using YuNotes.Models;
using YuNotes.Services;

namespace YuNotes.Tests;

// The .yunote file format: everything survives a save and reopen, old files
// are upgraded, and files from a newer YuNotes are refused rather than
// overwritten.
public sealed class DocumentFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yunotes-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DocumentService _docs = new();

    public DocumentFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string NewPath(string name = "note.yunote") => Path.Combine(_dir, name);

    private static int UserVersion(string path)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        return DocumentService.ReadSchemaVersion(c);
    }

    private static void Exec(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void NewFile_HasCurrentVersion()
    {
        var path = NewPath();
        _docs.Create(path, "Fresh");
        Assert.Equal(DocumentService.CurrentSchemaVersion, UserVersion(path));
    }

    [Fact]
    public void SaveAndOpen_KeepsEverything()
    {
        var path = NewPath();
        var doc = _docs.Create(path, "Round trip");
        doc.Template = new TemplateSettings { Kind = TemplateKind.Grid, Spacing = 24, SkipImportedPdf = true };
        var page = doc.Pages[0];
        page.Bookmark = "Exam";
        page.TemplateOverride = new TemplateSettings { Kind = TemplateKind.Lined };
        page.Width = 1500; page.BackgroundLeft = 130; page.BackgroundContentWidth = 1240;
        var stroke = new Stroke { Color = Color.FromArgb(255, 200, 10, 10), Width = 3, PressureMode = true };
        stroke.Points.Add(new InkPoint(1, 2, 0.3f));
        stroke.Points.Add(new InkPoint(30, 40, 0.9f));
        page.Strokes.Add(stroke);
        page.Shapes.Add(new ShapeElement { Kind = ShapeKind.Triangle, X1 = 1, Y1 = 2, X2 = 3, Y2 = 4, X3 = 5, Y3 = 6, Color = Color.FromArgb(255, 0, 0, 255) });
        page.Texts.Add(new TextElement { X = 10, Y = 20, Width = 200, Height = 30, Text = "Héllo\nworld", Bold = true, FontSize = 22 });
        page.Images.Add(new ImageElement { X = 5, Y = 6, Width = 70, Height = 80, PngData = new byte[] { 1, 2, 3, 4 } });
        PageGaps.Insert(page, 500, 120);
        doc.Pages.Add(new NotePage { Index = 1 });
        _docs.Save(doc);

        var back = _docs.Open(path);

        Assert.Equal("Round trip", back.Info.Title);
        Assert.Equal(TemplateKind.Grid, back.Template.Kind);
        Assert.True(back.Template.SkipImportedPdf);
        Assert.Equal(2, back.Pages.Count);
        var p = back.Pages[0];
        Assert.Equal("Exam", p.Bookmark);
        Assert.Equal(TemplateKind.Lined, p.TemplateOverride?.Kind);
        Assert.Equal((1500.0, 130.0, 1240.0), (p.Width, p.BackgroundLeft, p.BackgroundContentWidth));
        Assert.Equal(page.Height, p.Height);
        var s = Assert.Single(p.Strokes);
        Assert.Equal(stroke.Points, s.Points);
        Assert.Equal((stroke.Color, stroke.Width, stroke.PressureMode), (s.Color, s.Width, s.PressureMode));
        var sh = Assert.Single(p.Shapes);
        Assert.Equal((ShapeKind.Triangle, 5f, 6f), (sh.Kind, sh.X3, sh.Y3));
        var t = Assert.Single(p.Texts);
        Assert.Equal(("Héllo\nworld", true, 22f), (t.Text, t.Bold, t.FontSize));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Assert.Single(p.Images).PngData);
        var g = Assert.Single(p.Gaps);
        Assert.Equal((500.0, 120.0), (g.At, g.Size));
    }

    [Fact]
    public void FileFromBeforeVersioning_IsUpgraded()
    {
        // The very first schema: no flatten/thumbnail/source columns, no page
        // extension, bookmark or gaps columns, and user_version 0.
        var path = NewPath("old.yunote");
        Exec(path, @"CREATE TABLE document(id TEXT PRIMARY KEY, title TEXT, created_at TEXT, modified_at TEXT, page_count INTEGER, template_json TEXT);
                     CREATE TABLE page(id TEXT PRIMARY KEY, idx INTEGER, width REAL, height REAL, background_png BLOB);
                     CREATE TABLE stroke(id TEXT PRIMARY KEY, page_id TEXT, data BLOB);
                     INSERT INTO document VALUES('d1','Old note','2024-01-01T00:00:00.0000000Z','2024-01-02T00:00:00.0000000Z',1,NULL);
                     INSERT INTO page(id,idx,width,height) VALUES('p1',0,1240,1754);");
        Assert.Equal(0, UserVersion(path));

        var doc = _docs.Open(path);

        Assert.Equal("Old note", doc.Info.Title);
        Assert.Equal(1240, Assert.Single(doc.Pages).Width);
        Assert.Equal(DocumentService.CurrentSchemaVersion, UserVersion(path));
        _docs.Save(doc);   // all the newer columns exist now
        Assert.Equal("Old note", _docs.Open(path).Info.Title);
    }

    [Fact]
    public void UpgradeIsIdempotent_ForPartlyUpgradedFiles()
    {
        // A pre-versioning file that already had some of the later columns.
        var path = NewPath("partial.yunote");
        _docs.Create(path, "Partial");
        Exec(path, "PRAGMA user_version = 0");

        var doc = _docs.Open(path);

        Assert.Equal("Partial", doc.Info.Title);
        Assert.Equal(DocumentService.CurrentSchemaVersion, UserVersion(path));
    }

    [Fact]
    public void FileFromNewerYuNotes_IsRefusedForEditingAndSaving()
    {
        var path = NewPath("future.yunote");
        var doc = _docs.Create(path, "From the future");
        int future = DocumentService.CurrentSchemaVersion + 1;
        Exec(path, $"PRAGMA user_version = {future}");
        var before = File.ReadAllBytes(path);

        var ex = Assert.Throws<NewerFileFormatException>(() => _docs.Open(path));
        Assert.Equal(future, ex.FileVersion);
        Assert.Throws<NewerFileFormatException>(() => _docs.Save(doc));

        Assert.Equal(before, File.ReadAllBytes(path));   // untouched
        Assert.Equal(future, UserVersion(path));
    }

    [Fact]
    public void FileFromNewerYuNotes_StillListsAndIndexes()
    {
        var path = NewPath("future2.yunote");
        _docs.Create(path, "Listed anyway");
        Exec(path, $"PRAGMA user_version = {DocumentService.CurrentSchemaVersion + 5}");

        Assert.Equal("Listed anyway", _docs.ReadInfo(path).Title);
        Assert.Equal("Listed anyway", _docs.Open(path, forIndex: true).Info.Title);
    }
}
