using System.Numerics;
using YuNotes.Models;

namespace YuNotes.Tools;

public enum ToolKind { Pen, Highlighter, Eraser, Text, Image, Lasso, RectSelect, Pan, Shape, ExtendPage }

public interface ITool
{
    ToolKind Kind { get; }
    void OnPointerDown(EditorContext ctx, Vector2 p, float pressure);
    void OnPointerMove(EditorContext ctx, Vector2 p, float pressure);
    void OnPointerUp(EditorContext ctx, Vector2 p, float pressure);
}
