using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using YuNotes.Models;

namespace YuNotes.Tools;

public sealed class EditorContext
{
    public NotePage CurrentPage { get; set; } = new();
    public Stroke? ActiveStroke { get; set; }
    public ShapeElement? ActiveShape { get; set; }
    public Color CurrentColor { get; set; } = Colors.Black;
    public float CurrentWidth { get; set; } = 2.5f;
    public float EraserWidth { get; set; } = 20f;
    public bool EraserPixelMode { get; set; } = false;

    public List<string> SelectedStrokeIds { get; } = new();
    public List<string> SelectedShapeIds { get; } = new();
    public List<string> SelectedTextIds { get; } = new();
    public List<string> SelectedImageIds { get; } = new();
    public (float X, float Y, float W, float H)? SelectionRect { get; set; }
    public (float X, float Y, float W, float H)? LastDrawnRectSelection { get; set; }
    public List<System.Numerics.Vector2>? SelectionLasso { get; set; }

    public DispatcherQueue? DispatcherQueue { get; set; }

    public Action? Invalidate { get; set; }
    // Lightweight invalidate that only redraws the active-stroke overlay, not the
    // whole page. Used during pen / highlighter drawing so the renderer doesn't
    // re-rasterize all committed strokes (or the high-DPI PDF background) on every
    // pointer event.
    public Action? InvalidateLive { get; set; }
    // Stroke-commit invalidate: tears down the live overlay AND repaints only the
    // bounding box of the just-committed stroke on the main canvas. Avoids the
    // full-page redraw on pen-up that otherwise blocks the UI thread and delays
    // the next stroke's pointer events.
    public Action<Bbox>? CommitStrokeAt { get; set; }
    // General partial-invalidate for the main canvas. Used by tools (eraser,
    // future drag/resize) that know exactly which region changed.
    public Action<Bbox>? InvalidateRect { get; set; }
    public Action? Mutated { get; set; }   // mark document dirty
    public Action? SelectionChanged { get; set; }
    public Action<string>? EditTextRequested { get; set; }
    public Action<ToolKind>? ToolRequested { get; set; }
    public Action<float, float>? RequestPan { get; set; }
    public string? EditingTextId { get; set; }

    // ── Ruler overlay ─────────────────────────────────────────────────────────
    // RulerX/Y are the ruler's center position in document coordinates.
    // RulerAngle is in degrees (0 = horizontal, clockwise positive).
    public bool  RulerVisible    { get; set; } = false;
    public float RulerX          { get; set; } = 0f;
    public float RulerY          { get; set; } = 0f;
    public float RulerAngle      { get; set; } = 0f;
    // Half-height of the ruler body in document pixels (= RulerBodyHeight / 2
    // from PageCanvas). PenTool reads this so snap targets the edge, not the
    // centre line.
    public float RulerHalfHeight { get; set; } = 18f;  // = PageCanvas.RulerBodyHeight / 2
    // Fired whenever ruler position or angle changes so every PageCanvas can
    // repaint its own ruler overlay from the shared state.
    public Action? RulerChanged { get; set; }
}
