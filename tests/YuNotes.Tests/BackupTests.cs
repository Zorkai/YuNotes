using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using YuNotes.Models;
using YuNotes.Services;

namespace YuNotes.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yunotes-backup-tests-" + Guid.NewGuid().ToString("N"));

    public BackupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // A notes folder with a note at the top, one in a subfolder, one in
    // Trash; a tag, a pin, a folder color and a sticker.
    private (AppSettings settings, string stickers, string templates) MakeSource()
    {
        var root = P("src", "notes");
        Write(Path.Combine(root, "Top.pdf"), "top note");
        Write(Path.Combine(root, "Math", "Algebra.pdf"), "algebra note");
        Write(Path.Combine(root, "_trash", "Old.pdf"), "deleted note");
        var s = new AppSettings { DocumentsFolder = root };
        s.NoteTags[Path.Combine(root, "Math", "Algebra.pdf")] = new List<string> { "exam" };
        s.PinnedNotes.Add(Path.Combine(root, "Top.pdf"));
        s.Folders.Add(new FolderInfo { Name = "Math", Path = Path.Combine(root, "Math"), Color = "#1D9E75", DefaultTemplate = "Grid" });
        s.SmartFolders.Add(new SmartFolder { Name = "Exams" });
        s.PenFavorites.Clear();

        var stickers = P("src", "stickers");
        Write(Path.Combine(stickers, "s1.sticker"), "{}");
        Write(Path.Combine(stickers, "index.json"), "[{\"Id\":\"s1\",\"Name\":\"Stamp\"}]");
        return (s, stickers, P("src", "templates"));
    }

    [Fact]
    public void BackupThenRestore_IntoAnEmptyFolder_BringsBackNotesAndLibrary()
    {
        var (src, stickers, templates) = MakeSource();
        var zip = P("backup.zip");
        var made = BackupService.Create(zip, src, stickers, templates);
        Assert.Equal(2, made.Notes);   // Trash is left out
        Assert.Equal(1, made.Stickers);

        var dstRoot = P("dst", "notes");
        var dst = new AppSettings { DocumentsFolder = dstRoot };
        var dstStickers = P("dst", "stickers");
        var r = BackupService.Restore(zip, dst, dstStickers, P("dst", "templates"), restorePreferences: false);

        Assert.Equal(2, r.NotesAdded);
        Assert.Equal("algebra note", File.ReadAllText(Path.Combine(dstRoot, "Math", "Algebra.pdf")));
        Assert.False(File.Exists(Path.Combine(dstRoot, "_trash", "Old.pdf")));
        // Path-keyed data follows the notes to the new folder.
        Assert.Equal(new[] { "exam" }, dst.NoteTags[Path.Combine(dstRoot, "Math", "Algebra.pdf")]);
        Assert.Contains(Path.Combine(dstRoot, "Top.pdf"), dst.PinnedNotes);
        var folder = Assert.Single(dst.Folders);
        Assert.Equal("#1D9E75", folder.Color);
        Assert.Equal("Grid", folder.DefaultTemplate);
        Assert.Single(dst.SmartFolders);
        Assert.True(File.Exists(Path.Combine(dstStickers, "s1.sticker")));
        Assert.Contains("Stamp", File.ReadAllText(Path.Combine(dstStickers, "index.json")));
        // Preferences were not asked for.
        Assert.NotEmpty(dst.PenFavorites);
    }

    [Fact]
    public void Restore_NeverOverwrites_SkipsSameAndKeepsChangedAsCopy()
    {
        var (src, stickers, templates) = MakeSource();
        var zip = P("backup.zip");
        BackupService.Create(zip, src, stickers, templates);

        // Restore into the same notes folder after editing one note.
        var top = Path.Combine(src.DocumentsFolder, "Top.pdf");
        File.WriteAllText(top, "top note, edited since");
        var r = BackupService.Restore(zip, src, stickers, templates, restorePreferences: false);

        Assert.Equal(0, r.NotesAdded);
        Assert.Equal(1, r.NotesSkipped);    // Algebra.pdf is unchanged
        Assert.Equal(1, r.NotesRenamed);    // Top.pdf differs
        Assert.Equal("top note, edited since", File.ReadAllText(top));
        Assert.Equal("top note", File.ReadAllText(Path.Combine(src.DocumentsFolder, "Top (restored).pdf")));
        Assert.Single(src.SmartFolders);   // not duplicated
        Assert.Single(src.NoteTags.Values.SelectMany(t => t));
    }

    [Fact]
    public void Restore_WithPreferences_CopiesSettingsButKeepsLocation()
    {
        var (src, stickers, templates) = MakeSource();
        src.Theme = AppThemeMode.Dark;
        var zip = P("backup.zip");
        BackupService.Create(zip, src, stickers, templates);

        var dst = new AppSettings { DocumentsFolder = P("dst2") };
        BackupService.Restore(zip, dst, P("dst2s"), P("dst2t"), restorePreferences: true);
        Assert.Equal(AppThemeMode.Dark, dst.Theme);
        Assert.Empty(dst.PenFavorites);
        Assert.Equal(P("dst2"), dst.DocumentsFolder);
    }

    [Fact]
    public void ReadManifest_RejectsOtherZips()
    {
        var zip = P("other.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            z.CreateEntry("hello.txt");
        Assert.Throws<InvalidDataException>(() => BackupService.ReadManifest(zip));
    }

    [Theory]
    [InlineData("..\\outside.pdf")]
    [InlineData("a\\..\\..\\outside.pdf")]
    [InlineData("C:\\Windows\\x.pdf")]
    public void SafeCombine_StaysInsideTheRoot(string rel) =>
        Assert.Null(BackupService.SafeCombine(P("root"), rel));

    [Fact]
    public void UniqueRestoredPath_CountsUp()
    {
        var path = P("Note.pdf");
        Write(path, "a");
        Write(P("Note (restored).pdf"), "b");
        Assert.Equal(P("Note (restored 2).pdf"), BackupService.UniqueRestoredPath(path));
    }
}
