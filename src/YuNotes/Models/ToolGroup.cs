using System;
using System.Collections.Generic;

namespace YuNotes.Models;

// A user-made group of drawing tools shown as one toolbar button that opens a
// menu of its tools — for tools that are used rarely. Tool keys are the same
// as in ToolbarDrawingOrder ("Pen", "Laser", …). A tool belongs to at most one
// group; the group sits where its first member would be in the toolbar order.
public sealed class ToolGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = "";
    public List<string> Tools { get; set; } = new();

    // Icons offered for a group (Material Symbols in the app's icon font).
    public static readonly (string Glyph, string Name)[] Icons =
    {
        ("", "Shapes"),
        ("", "Widgets"),
        ("", "Apps"),
        ("", "More"),
        ("", "Star"),
        ("", "Bolt"),
        ("", "Palette"),
        ("", "Brush"),
        ("", "Tools"),
        ("", "Tune"),
        ("", "Study"),
        ("", "Sparkle"),
        ("", "Box"),
        ("", "Folder"),
        ("", "Puzzle"),
        ("", "Chart"),
    };
}
