using System;
using System.Collections.Generic;

namespace YuNotes.Models;

public enum PenButtonAction
{
    None, Eraser, LassoSelect, RectSelect, Highlighter, Pen, Undo, Redo
}

public enum ToolbarPosition { Top, Bottom, Left, Right }
public enum ToolbarSize { Compact, Small, Normal, Large }

public sealed class AppSettings
{
    public List<string> PenPresetColors { get; set; } = new()
    {
        "#FF1B1F2A",
        "#FFE53935",
        "#FF1E88E5",
        "#FF43A047",
        "#FFFB8C00"
    };
    public List<string> HighlighterPresetColors { get; set; } = new()
    {
        "#80FFEB3B",
        "#8033EE33",
        "#80FF6699",
        "#8033CCFF"
    };

    // Pen buttons
    public PenButtonAction BarrelButtonAction { get; set; } = PenButtonAction.Eraser;

    // Palm rejection
    public bool PalmRejectionEnabled { get; set; } = true;
    public bool IgnoreTouchWhilePenActive { get; set; } = true;
    public int PalmRejectionGraceMs { get; set; } = 500;

    // Default tool settings
    public float DefaultPenWidth { get; set; } = 2.5f;
    public float DefaultHighlighterWidth { get; set; } = 18f;
    public float DefaultEraserWidth { get; set; } = 20f;
    public string DefaultPenColorHex { get; set; } = "#FF1B1F2A";
    public string DefaultHighlighterColorHex { get; set; } = "#80FFEB3B";

    // Pressure sensitivity
    public bool PressureEnabled { get; set; } = true;
    public double MinPressure { get; set; } = 0.05;     // drop samples below this raw pressure
    public double PressureMultiplier { get; set; } = 1.0;

    // Artifact workaround — drop the first N pointer-move events after a pen-down
    public bool IgnoreFirstEventsEnabled { get; set; } = false;
    public int IgnoreFirstEventsCount { get; set; } = 1;

    // Compatibility — emulate stylus tip down when a barrel button is pressed
    public bool EmulateTipOnButtonPress { get; set; } = false;

    // Editor tool modes (persisted across sessions)
    public bool PenPressureMode { get; set; } = false;
    public bool EraserPixelMode { get; set; } = false;
    public bool SelectRectMode { get; set; } = false;

    // Layout
    public ToolbarPosition ToolbarPosition { get; set; } = ToolbarPosition.Top;
    public ToolbarSize ToolbarSize { get; set; } = ToolbarSize.Normal;
    public bool HideZoomBar { get; set; } = true;
    public bool SeamlessPages { get; set; } = true;

    // Toolbar customization — tool keys that are hidden from the toolbar
    public List<string> HiddenToolbarTools { get; set; } = new();

    // Toolbar tool order — keys in display order; empty = default order
    public List<string> ToolbarDrawingOrder { get; set; } = new();
    public List<string> ToolbarActionOrder { get; set; } = new();

    // Pen shape recognition
    public bool HoldToSnapEnabled { get; set; } = true;

    // Render
    public bool UseHighRefreshRate { get; set; } = true;

    // Locations
    public string DocumentsFolder { get; set; } = "";
    public List<string> RecentDocuments { get; set; } = new();

    // Folder registry — stable IDs survive renames; synced with filesystem on load
    public List<FolderInfo> Folders { get; set; } = new();
}
