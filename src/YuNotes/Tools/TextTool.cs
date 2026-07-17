using System;
using System.Numerics;
using YuNotes.Models;

namespace YuNotes.Tools;

public sealed class TextTool : ITool
{
    public ToolKind Kind => ToolKind.Text;

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        // Tap on an existing text element → re-edit it instead of creating a new one.
        if (ctx.CurrentPage is not null)
        {
            var texts = ctx.CurrentPage.Texts;
            for (int i = texts.Count - 1; i >= 0; i--)
            {
                var existing = texts[i];
                if (Bbox.Of(existing).Contains(p.X, p.Y))
                {
                    ctx.EditTextRequested?.Invoke(existing.Id);
                    return;
                }
            }
        }

        var t = new TextElement
        {
            X = p.X, Y = p.Y, Width = 280, Height = 48,
            Text = "", Color = ctx.CurrentColor, FontSize = System.Math.Max(14, ctx.CurrentWidth * 6)
        };
        if (ctx.CurrentPage is null) return;
        ctx.CurrentPage.Texts.Add(t);
        ctx.SelectedStrokeIds.Clear();
        ctx.SelectedImageIds.Clear();
        ctx.SelectedTextIds.Clear();
        ctx.SelectedTextIds.Add(t.Id);
        ctx.Mutated?.Invoke();
        ctx.SelectionChanged?.Invoke();
        ctx.EditTextRequested?.Invoke(t.Id);
        ctx.Invalidate?.Invoke();
    }
    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure) { }
}

public sealed class ImageTool : ITool
{
    public ToolKind Kind => ToolKind.Image;
    // Image placement is driven from the toolbar button (file picker), so this tool
    // only marks the next click location.
    public Vector2? PendingDropAt;
    public byte[]? PendingPng;

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        if (PendingPng is null) { PendingDropAt = p; return; }
        // Base the drop size on the image's real aspect ratio (longest side ~360 in
        // page units) so a wide/tall capture isn't squished into a fixed box.
        double defaultW = 320, defaultH = 240;
        if (TryGetPngSize(PendingPng, out int pw, out int ph) && pw > 0 && ph > 0)
        {
            double longest = 360.0;
            double scale = longest / Math.Max(pw, ph);
            defaultW = pw * scale;
            defaultH = ph * scale;
        }
        var img = new ImageElement
        {
            X = p.X - defaultW / 2, Y = p.Y - defaultH / 2,
            Width = defaultW, Height = defaultH,
            PngData = PendingPng
        };
        ctx.CurrentPage.Images.Add(img);
        PendingPng = null;
        // Select the new image so transform handles appear immediately.
        ctx.SelectedStrokeIds.Clear();
        ctx.SelectedTextIds.Clear();
        ctx.SelectedImageIds.Clear();
        ctx.SelectedImageIds.Add(img.Id);
        ctx.Mutated?.Invoke();
        ctx.SelectionChanged?.Invoke();
        // Switch to select tool so the user can drag the handles.
        ctx.ToolRequested?.Invoke(ToolKind.RectSelect);
        ctx.Invalidate?.Invoke();
    }
    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure) { }

    // Reads width/height from a PNG's IHDR chunk (big-endian ints at bytes 16–23)
    // without decoding the pixels. Returns false if the bytes aren't a valid PNG.
    public static bool TryGetPngSize(byte[] png, out int w, out int h)
    {
        w = h = 0;
        if (png.Length < 24) return false;
        if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return false;
        w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return w > 0 && h > 0;
    }
}
