using System;
using System.Collections.Generic;
using System.IO;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace YuNotes.Models;

public enum StrokeKind { Pen, Highlighter }

public enum ShapeKind { Rectangle, Ellipse, Line, Triangle }

public sealed class ShapeElement
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ShapeKind Kind { get; set; }
    // (X1,Y1) = drag-start/tail, (X2,Y2) = drag-end/tip.
    // Triangle also stores a third vertex in (X3,Y3); other kinds leave it at 0.
    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float X2 { get; set; }
    public float Y2 { get; set; }
    public float X3 { get; set; }
    public float Y3 { get; set; }
    // Rotation in degrees (screen-clockwise) about the (X1,Y1)-(X2,Y2) bbox
    // center. Only meaningful for Rectangle and Ellipse — Line and Triangle
    // vertices are free-form already. Default 0 keeps old documents and the
    // shape tool unchanged; JSON round-trips it automatically.
    public float Rotation { get; set; }
    public Color Color { get; set; }
    public float StrokeWidth { get; set; } = 2.5f;
    public bool Filled { get; set; } = false;
}

public readonly record struct InkPoint(float X, float Y, float Pressure);

public sealed class Stroke
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public StrokeKind Kind { get; set; } = StrokeKind.Pen;
    public Color Color { get; set; } = Colors.Black;
    public float Width { get; set; } = 2.5f;
    public bool PressureMode { get; set; } = false;   // render width-modulated by per-point pressure
    public List<InkPoint> Points { get; } = new();

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)2);                          // version
        bw.Write((byte)Kind);
        bw.Write(Color.A); bw.Write(Color.R); bw.Write(Color.G); bw.Write(Color.B);
        bw.Write(Width);
        bw.Write(PressureMode);
        bw.Write(Points.Count);
        foreach (var p in Points)
        {
            bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Pressure);
        }
        return ms.ToArray();
    }

    public static Stroke Deserialize(byte[] data, string id)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        var version = br.ReadByte();
        var s = new Stroke { Id = id };
        s.Kind = (StrokeKind)br.ReadByte();
        var a = br.ReadByte(); var r = br.ReadByte(); var g = br.ReadByte(); var b = br.ReadByte();
        s.Color = Color.FromArgb(a, r, g, b);
        s.Width = br.ReadSingle();
        s.PressureMode = version >= 2 && br.ReadBoolean();
        var n = br.ReadInt32();
        // A corrupt/truncated blob can carry a bogus (huge or negative) count.
        // Each point is 3 floats = 12 bytes; clamp to what the stream can hold so
        // we never pre-fault on a giant allocation or read past the end.
        long remaining = ms.Length - ms.Position;
        if (n < 0 || n > remaining / 12) n = (int)Math.Max(0, remaining / 12);
        for (int i = 0; i < n; i++)
            s.Points.Add(new InkPoint(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
        return s;
    }
}

public sealed class TextElement
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 60;
    public double Rotation { get; set; } = 0;     // degrees
    public string Text { get; set; } = "";
    public float FontSize { get; set; } = 18;
    public Color Color { get; set; } = Colors.Black;
    public string FontFamily { get; set; } = "Segoe UI";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
}

public sealed class ImageElement
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Rotation { get; set; } = 0;     // degrees
    public byte[] PngData { get; set; } = Array.Empty<byte>();
}
