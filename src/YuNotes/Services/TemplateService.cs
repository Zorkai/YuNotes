using Microsoft.Graphics.Canvas;
using Windows.UI;
using YuNotes.Models;

namespace YuNotes.Services;

public sealed class TemplateService
{
    public void DrawTemplate(CanvasDrawingSession ds, double width, double height, TemplateSettings t)
    {
        if (t.Kind == TemplateKind.Blank) return;
        var color = ParseHex(t.LineColorHex);
        var spacing = (float)t.Spacing;
        var thickness = (float)t.LineThickness;

        switch (t.Kind)
        {
            case TemplateKind.Grid:
                for (float x = spacing; x < width; x += spacing)
                    ds.DrawLine(x, 0, x, (float)height, color, thickness);
                for (float y = spacing; y < height; y += spacing)
                    ds.DrawLine(0, y, (float)width, y, color, thickness);
                break;
            case TemplateKind.Dots:
                for (float x = spacing; x < width; x += spacing)
                    for (float y = spacing; y < height; y += spacing)
                        ds.FillCircle(x, y, thickness * 1.2f, color);
                break;
            case TemplateKind.Lined:
                for (float y = spacing; y < height; y += spacing)
                    ds.DrawLine(0, y, (float)width, y, color, thickness);
                break;
            case TemplateKind.Cornell:
                ds.DrawLine((float)width * 0.25f, 0, (float)width * 0.25f, (float)height, color, thickness);
                ds.DrawLine(0, (float)height * 0.85f, (float)width, (float)height * 0.85f, color, thickness);
                for (float y = spacing; y < height; y += spacing)
                    ds.DrawLine((float)width * 0.25f, y, (float)width, y, color, thickness * 0.6f);
                break;
        }
    }

    private static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 0xFF, r, g, b;
        if (hex.Length == 8)
        {
            a = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
            r = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
            g = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
            b = byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber);
        }
        else
        {
            r = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
            g = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
            b = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
        }
        return Color.FromArgb(a, r, g, b);
    }
}
