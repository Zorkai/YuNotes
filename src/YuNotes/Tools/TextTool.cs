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
        const double defaultW = 320, defaultH = 240;
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
}
