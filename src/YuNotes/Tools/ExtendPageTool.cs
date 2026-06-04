using System.Numerics;

namespace YuNotes.Tools;

/// <summary>
/// Marker tool for the "Page Width" toolbar button.  Actual drag logic lives in
/// PageCanvas — this class just gives the tool a ToolKind so the toolbar can
/// check-mark it and so PageCanvas can test ToolProvider()?.Kind == ExtendPage.
/// </summary>
public sealed class ExtendPageTool : ITool
{
    public ToolKind Kind => ToolKind.ExtendPage;
    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure) { }
    public void OnPointerUp  (EditorContext ctx, Vector2 p, float pressure) { }
}
