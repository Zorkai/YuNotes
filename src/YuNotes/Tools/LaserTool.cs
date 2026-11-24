using System.Numerics;

namespace YuNotes.Tools;

// Laser pointer: a glowing trail that fades shortly after the pen lifts and
// never touches the document. PageCanvas draws and fades the trail itself in
// its XAML overlay, so these handlers are deliberately empty — the tool only
// marks "a press should point, not draw".
public sealed class LaserTool : ITool
{
    public ToolKind Kind => ToolKind.Laser;
    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure) { }
}
