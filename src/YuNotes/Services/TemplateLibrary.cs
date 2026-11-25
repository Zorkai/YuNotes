using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PDFtoImage;
using SkiaSharp;
using YuNotes.Models;

namespace YuNotes.Services;

public sealed class CustomTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    // Page size in page units (150 per inch, like imported PDFs).
    public double Width { get; set; } = 1240;
    public double Height { get; set; } = 1754;
    public DateTime CreatedUtc { get; set; }
    public string ImagePath => Path.Combine(TemplateLibrary.Folder, Id + ".jpg");
}

/// <summary>
/// Page templates the user made from an image or a PDF page (a planner, a
/// music staff, a school's own paper…), shared by every note. A page that
/// uses one carries the image as its background, so the note looks the same
/// on any PC and in exports, whether or not the template still exists.
/// Stored in %LOCALAPPDATA%\YuNotes\Templates as
///   index.json    the list (id, name, page size), newest first
///   &lt;id&gt;.jpg      the page image
/// </summary>
public static class TemplateLibrary
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuNotes", "Templates");

    public const string KeyPrefix = "custom:";
    private const double CoordDpi = 150;       // page units per inch
    private const int RenderDpi = 300;         // PDF pages render at 2× for sharp zoom
    private const double A4Width = 1240;       // width given to images (A4 at 150 dpi)
    private const int MaxImageSide = 4096;

    private static string IndexPath => Path.Combine(Folder, "index.json");

    // Decoded bytes of each template's image, so finding which template a page
    // uses (to continue it on a new page) doesn't re-read files every time.
    private static readonly Dictionary<string, byte[]> _bytesCache = new();

    public static List<CustomTemplate> List()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new();
            var list = JsonSerializer.Deserialize<List<CustomTemplate>>(File.ReadAllText(IndexPath)) ?? new();
            return list.Where(t => File.Exists(t.ImagePath)).ToList();
        }
        catch (Exception ex) { App.LogError(ex, "TemplateLibrary.List"); return new(); }
    }

    public static CustomTemplate? Get(string id) => List().FirstOrDefault(t => t.Id == id);

    // "custom:<id>" → the template; anything else → null.
    public static CustomTemplate? FromKey(string? key) =>
        key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? Get(key[KeyPrefix.Length..]) : null;

    public static string KeyFor(CustomTemplate t) => KeyPrefix + t.Id;

    private static void WriteIndex(List<CustomTemplate> list)
    {
        Directory.CreateDirectory(Folder);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    /// <summary>Makes a template from an image (PNG, JPEG, …) or the first
    /// page of a PDF. Images are sized to an A4-wide page; PDF pages keep
    /// their own size.</summary>
    public static CustomTemplate Add(string sourcePath, string name)
    {
        bool isPdf = Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        SKBitmap bmp;
        double width, height;
        if (isPdf)
        {
            var bytes = File.ReadAllBytes(sourcePath);
            bmp = Conversion.ToImage(bytes, page: (Index)0, options: new RenderOptions { Dpi = RenderDpi });
            double scale = RenderDpi / CoordDpi;
            width = bmp.Width / scale;
            height = bmp.Height / scale;
        }
        else
        {
            bmp = SKBitmap.Decode(sourcePath)
                  ?? throw new InvalidDataException("This file isn't an image YuNotes can read.");
            if (Math.Max(bmp.Width, bmp.Height) > MaxImageSide)
            {
                double s = (double)MaxImageSide / Math.Max(bmp.Width, bmp.Height);
                var resized = bmp.Resize(new SKImageInfo((int)(bmp.Width * s), (int)(bmp.Height * s)), SKFilterQuality.High);
                bmp.Dispose();
                bmp = resized;
            }
            width = A4Width;
            height = Math.Round(A4Width * bmp.Height / bmp.Width);
        }

        using (bmp)
        {
            var info = new CustomTemplate
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "Template" : name.Trim(),
                Width = width,
                Height = height,
                CreatedUtc = DateTime.UtcNow,
            };
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(info.ImagePath, EncodeOnWhite(bmp));
            var list = List();
            list.Insert(0, info);
            WriteIndex(list);
            return info;
        }
    }

    // JPEG has no transparency: flatten onto white paper first.
    private static byte[] EncodeOnWhite(SKBitmap bmp)
    {
        using var surface = SKSurface.Create(new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawBitmap(bmp, 0, 0);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    public static void Delete(string id)
    {
        var list = List();
        list.RemoveAll(t => t.Id == id);
        WriteIndex(list);
        lock (_bytesCache) _bytesCache.Remove(id);
        try { File.Delete(Path.Combine(Folder, id + ".jpg")); } catch { }
    }

    public static void Rename(string id, string name)
    {
        var list = List();
        var t = list.FirstOrDefault(x => x.Id == id);
        if (t is null || string.IsNullOrWhiteSpace(name)) return;
        t.Name = name.Trim();
        WriteIndex(list);
    }

    public static byte[] ReadImage(CustomTemplate t)
    {
        lock (_bytesCache)
        {
            if (_bytesCache.TryGetValue(t.Id, out var cached)) return cached;
            var bytes = File.ReadAllBytes(t.ImagePath);
            _bytesCache[t.Id] = bytes;
            return bytes;
        }
    }

    /// <summary>Puts the template on a page as its background. The page takes
    /// the template's size so the paper isn't stretched.</summary>
    public static void ApplyTo(NotePage page, CustomTemplate t)
    {
        page.BackgroundPng = ReadImage(t);
        page.BackgroundLeft = 0;
        page.BackgroundContentWidth = 0;
        page.SourcePageIndex = null;
        page.Width = t.Width;
        page.Height = t.Height;
    }

    public static NotePage NewPage(CustomTemplate t, int index)
    {
        var page = new NotePage { Index = index };
        ApplyTo(page, t);
        return page;
    }

    /// <summary>The template a page's background came from, if any — used so
    /// a new page added after a templated one continues the same paper.</summary>
    public static CustomTemplate? Find(NotePage? page)
    {
        if (page?.BackgroundPng is not { Length: > 0 } bg || page.SourcePageIndex is not null) return null;
        foreach (var t in List())
        {
            try
            {
                if (new FileInfo(t.ImagePath).Length != bg.Length) continue;
                if (ReadImage(t).AsSpan().SequenceEqual(bg)) return t;
            }
            catch { }
        }
        return null;
    }

    // True when a page has no paper of its own yet (no imported PDF page and
    // no template image), so a custom template can go on it.
    public static bool CanTakeTemplate(NotePage page) =>
        page.SourcePageIndex is null && (page.BackgroundPng is not { Length: > 0 } || Find(page) is not null);
}
