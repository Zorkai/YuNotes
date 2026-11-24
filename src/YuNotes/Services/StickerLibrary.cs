using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.UI;
using YuNotes.Models;

namespace YuNotes.Services;

// The elements of one sticker, positioned so their bounding box starts at (0,0).
public sealed class StickerContent
{
    public List<Stroke> Strokes { get; } = new();
    public List<ShapeElement> Shapes { get; } = new();
    public List<TextElement> Texts { get; } = new();
    public List<ImageElement> Images { get; } = new();
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsEmpty => Strokes.Count + Shapes.Count + Texts.Count + Images.Count == 0;
}

public sealed class StickerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string ThumbnailPath => Path.Combine(StickerLibrary.Folder, Id + ".png");
}

// Saved clips ("stickers") shared by every note: a diagram, a stamp, a
// signature… Stored outside the notes in %LOCALAPPDATA%\YuNotes\Stickers as
//   index.json      the list (id, name, created), newest first
//   <id>.sticker    the elements (JSON; strokes in their compact binary form)
//   <id>.png        a thumbnail for the picker
// Colors are written as ARGB numbers: Windows.UI.Color is a struct of fields,
// which System.Text.Json would silently drop.
public static class StickerLibrary
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuNotes", "Stickers");

    private static string IndexPath => Path.Combine(Folder, "index.json");

    public static List<StickerInfo> List()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new();
            var list = JsonSerializer.Deserialize<List<StickerInfo>>(File.ReadAllText(IndexPath)) ?? new();
            // Drop entries whose element file went missing.
            return list.Where(s => File.Exists(Path.Combine(Folder, s.Id + ".sticker"))).ToList();
        }
        catch (Exception ex) { App.LogError(ex, "StickerLibrary.List"); return new(); }
    }

    private static void WriteIndex(List<StickerInfo> list)
    {
        Directory.CreateDirectory(Folder);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    public static StickerInfo Add(string name, StickerContent content, byte[]? thumbnailPng)
    {
        Directory.CreateDirectory(Folder);
        var info = new StickerInfo { Id = Guid.NewGuid().ToString("N"), Name = name, CreatedUtc = DateTime.UtcNow };
        File.WriteAllText(Path.Combine(Folder, info.Id + ".sticker"), JsonSerializer.Serialize(ToFile(content)));
        if (thumbnailPng is { Length: > 0 }) File.WriteAllBytes(info.ThumbnailPath, thumbnailPng);
        var list = List();
        list.Insert(0, info);
        WriteIndex(list);
        return info;
    }

    public static void Rename(string id, string name)
    {
        var list = List();
        if (list.FirstOrDefault(s => s.Id == id) is not { } s) return;
        s.Name = name;
        WriteIndex(list);
    }

    public static void Delete(string id)
    {
        var list = List();
        list.RemoveAll(s => s.Id == id);
        WriteIndex(list);
        foreach (var ext in new[] { ".sticker", ".png" })
            try { File.Delete(Path.Combine(Folder, id + ext)); } catch { }
    }

    public static StickerContent? Load(string id)
    {
        try
        {
            var path = Path.Combine(Folder, id + ".sticker");
            if (!File.Exists(path)) return null;
            var f = JsonSerializer.Deserialize<StickerFile>(File.ReadAllText(path));
            return f is null ? null : FromFile(f);
        }
        catch (Exception ex) { App.LogError(ex, $"StickerLibrary.Load {id}"); return null; }
    }

    // ── File format ───────────────────────────────────────────────────────────

    private sealed class StickerFile
    {
        public int Version { get; set; } = 1;
        public double Width { get; set; }
        public double Height { get; set; }
        public List<byte[]> Strokes { get; set; } = new();
        public List<ShapeDto> Shapes { get; set; } = new();
        public List<TextDto> Texts { get; set; } = new();
        public List<ImageDto> Images { get; set; } = new();
    }
    private sealed class ShapeDto
    {
        public ShapeKind Kind { get; set; }
        public float X1 { get; set; } public float Y1 { get; set; }
        public float X2 { get; set; } public float Y2 { get; set; }
        public float X3 { get; set; } public float Y3 { get; set; }
        public float Rotation { get; set; }
        public uint Color { get; set; }
        public float StrokeWidth { get; set; }
        public bool Filled { get; set; }
    }
    private sealed class TextDto
    {
        public double X { get; set; } public double Y { get; set; }
        public double Width { get; set; } public double Height { get; set; }
        public double Rotation { get; set; }
        public string Text { get; set; } = "";
        public float FontSize { get; set; }
        public uint Color { get; set; }
        public string FontFamily { get; set; } = "Segoe UI";
        public bool Bold { get; set; }
        public bool Italic { get; set; }
    }
    private sealed class ImageDto
    {
        public double X { get; set; } public double Y { get; set; }
        public double Width { get; set; } public double Height { get; set; }
        public double Rotation { get; set; }
        public byte[] Png { get; set; } = Array.Empty<byte>();
    }

    private static uint Pack(Color c) => (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
    private static Color Unpack(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    private static StickerFile ToFile(StickerContent c) => new()
    {
        Width = c.Width,
        Height = c.Height,
        Strokes = c.Strokes.Select(s => s.Serialize()).ToList(),
        Shapes = c.Shapes.Select(s => new ShapeDto
        {
            Kind = s.Kind, X1 = s.X1, Y1 = s.Y1, X2 = s.X2, Y2 = s.Y2, X3 = s.X3, Y3 = s.Y3,
            Rotation = s.Rotation, Color = Pack(s.Color), StrokeWidth = s.StrokeWidth, Filled = s.Filled,
        }).ToList(),
        Texts = c.Texts.Select(t => new TextDto
        {
            X = t.X, Y = t.Y, Width = t.Width, Height = t.Height, Rotation = t.Rotation, Text = t.Text,
            FontSize = t.FontSize, Color = Pack(t.Color), FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic,
        }).ToList(),
        Images = c.Images.Select(i => new ImageDto
        {
            X = i.X, Y = i.Y, Width = i.Width, Height = i.Height, Rotation = i.Rotation, Png = i.PngData,
        }).ToList(),
    };

    private static StickerContent FromFile(StickerFile f)
    {
        var c = new StickerContent { Width = f.Width, Height = f.Height };
        foreach (var b in f.Strokes)
            try { c.Strokes.Add(Stroke.Deserialize(b, Guid.NewGuid().ToString("N"))); } catch { }
        foreach (var s in f.Shapes)
            c.Shapes.Add(new ShapeElement
            {
                Kind = s.Kind, X1 = s.X1, Y1 = s.Y1, X2 = s.X2, Y2 = s.Y2, X3 = s.X3, Y3 = s.Y3,
                Rotation = s.Rotation, Color = Unpack(s.Color), StrokeWidth = s.StrokeWidth, Filled = s.Filled,
            });
        foreach (var t in f.Texts)
            c.Texts.Add(new TextElement
            {
                X = t.X, Y = t.Y, Width = t.Width, Height = t.Height, Rotation = t.Rotation, Text = t.Text,
                FontSize = t.FontSize, Color = Unpack(t.Color), FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic,
            });
        foreach (var i in f.Images)
            if (i.Png.Length > 0)
                c.Images.Add(new ImageElement { X = i.X, Y = i.Y, Width = i.Width, Height = i.Height, Rotation = i.Rotation, PngData = i.Png });
        return c;
    }
}
