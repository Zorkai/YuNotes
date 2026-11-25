using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.Text;
using Colors = Microsoft.UI.Colors;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Controls;

public sealed partial class PageCanvas
{

    private bool _panning;
    private bool _inMomentaryPan;
    private Windows.Foundation.Point _panLastScreen;
    private int _penEventsSinceDown;

    private float PressureOf(PointerRoutedEventArgs e)
        => PressureOf(e.GetCurrentPoint(this).Properties.Pressure, e.Pointer.PointerDeviceType);

    private float PressureOf(float rawPressure, Microsoft.UI.Input.PointerDeviceType device)
    {
        if (rawPressure <= 0) return 0.5f;
        var s = App.Services.Settings.Current;
        if (!s.PressureEnabled) return 0.5f;
        if (device == Microsoft.UI.Input.PointerDeviceType.Pen)
        {
            var v = (rawPressure - (float)s.MinPressure) * (float)s.PressureMultiplier;
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            return (float)YuNotes.Input.PressureCurve.Evaluate(v, s);
        }
        return rawPressure;
    }

    private bool ShouldDropPenSample(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen) return false;
        var s = App.Services.Settings.Current;
        if (s.PressureEnabled)
        {
            var raw = e.GetCurrentPoint(this).Properties.Pressure;
            if (raw > 0 && raw < (float)s.MinPressure) return true;
        }
        if (s.IgnoreFirstEventsEnabled && _penEventsSinceDown < s.IgnoreFirstEventsCount) return true;
        return false;
    }

    private static readonly PanTool s_middleMousePan = new();

    private ITool? PickTool(PointerRoutedEventArgs e)
    {
        // Middle mouse button → momentary pan, overriding whatever tool is active.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsMiddleButtonPressed) return s_middleMousePan;
        }
        var act = ButtonRouter.Resolve(e);
        if (act != PenButtonAction.None && ButtonTools.TryGetValue(act, out var t)) return t;
        return ToolProvider();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try { OnPointerPressedCore(sender, e); }
        catch (Exception ex) { App.LogError(ex, "OnPointerPressed"); }
    }
    private void OnPointerPressedCore(object sender, PointerRoutedEventArgs e)
    {
        if (!PalmRejection.Accept(e)) { e.Handled = true; return; }
        // Fingers never trigger our tools — the outer ScrollViewer's DirectManipulation
        // handles touch pan + pinch-zoom; we just refuse to draw.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            // A finger tap outside a committed selection dismisses it, matching the
            // pen "click outside to clear" behavior below. Touch is owned by the
            // ScrollViewer (we never capture it), so we act on press: only clear
            // when the point is outside the box and off the handles, leaving a
            // finger pan/pinch that starts inside the selection alone.
            if (!Context.EditingSuspended && HasCommittedSelection())
            {
                var tp = ToPageSpace(e);
                if (!PointInSelectionBounds(tp) && HitTestHandles(tp, out _) == DragMode.None)
                    ClearSelection();
            }
            return;
        }
        // Editing suspended (e.g. during a save): reject pen/mouse drawing so the
        // document can't be mutated, but scroll/zoom (handled by the ScrollViewer)
        // still works.
        if (Context.EditingSuspended) return;
        // A new press while a previous gesture is still marked in progress means
        // that gesture was interrupted without a release — finish it first.
        if (_panning || _selectingText || _lasering || _spacing || _tapeGesture || _drag != DragMode.None || _activeTool is not null)
            ResetInputState();
        Focus(FocusState.Pointer);
        var p = ToPageSpace(e);
        _lastToolPoint = p;
        Context.CurrentPage = Page;
        _penEventsSinceDown = 0;

        var tool = PickTool(e);

        // Tape: a press on a strip is a tap that reveals or covers it (see
        // TapeTapTarget for which tools this applies to). Decided on release,
        // so a drag that starts on tape can still pan with the hand tool.
        if (TapeTapTarget(tool, p, e) is { } tape)
        {
            _tapeGesture = true;
            _tapeTap = tape;
            _tapeTapStart = p;
            _tapeTapPans = tool is PanTool;
            CapturePointer(e.Pointer);
            _capturedPointerId = e.Pointer.PointerId;
            e.Handled = true;
            return;
        }

        // Page size tool: a drag inside the page opens (or closes) space.
        if (tool is ExtendPageTool)
        {
            if (HasCommittedSelection()) ClearSelection();
            BeginSpace(p);
            CapturePointer(e.Pointer);
            _capturedPointerId = e.Pointer.PointerId;
            e.Handled = true;
            return;
        }

        // Laser pointer: points, never edits — so it skips selection handling
        // and leaves any committed selection alone.
        if (tool is LaserTool)
        {
            BeginLaser(p);
            CapturePointer(e.Pointer);
            _capturedPointerId = e.Pointer.PointerId;
            e.Handled = true;
            return;
        }

        // Hand tool: left-mouse drag selects PDF text (browser-style); everything else
        // (touch / pen / middle-click) pans the parent ScrollViewer.
        if (tool is PanTool)
        {
            var ttype = e.Pointer.PointerDeviceType;
            var props = e.GetCurrentPoint(this).Properties;
            bool isLeftMouseDrag = ttype == Microsoft.UI.Input.PointerDeviceType.Mouse
                                   && props.IsLeftButtonPressed
                                   && !props.IsMiddleButtonPressed;
            if (isLeftMouseDrag)
            {
                // Fresh left-click clears any prior text selection (like a browser does).
                ClearTextSelection();
                if (Page.TextRuns.Count > 0)
                {
                    _selectAnchor = FindNearestTextRun(p);
                    _selectCursor = _selectAnchor;
                    _selectingText = true;
                    RecomputeTextSelection();
                    CapturePointer(e.Pointer);
                    _capturedPointerId = e.Pointer.PointerId;
                    e.Handled = true;
                    SyncTextSelectionOverlay();
                    return;
                }
                // No selectable text on this page — fall through to pan.
            }

            // Distinguish momentary pan (middle-mouse override) from the user having
            // Pan already chosen — only the former needs a toolbar UI toggle.
            bool isMomentary = ToolProvider() is not PanTool
                               && ttype == Microsoft.UI.Input.PointerDeviceType.Mouse
                               && props.IsMiddleButtonPressed;

            _panning = true;
            _panLastScreen = e.GetCurrentPoint(null).Position;
            CapturePointer(e.Pointer);
            _capturedPointerId = e.Pointer.PointerId;
            if (isMomentary && !_inMomentaryPan)
            {
                _inMomentaryPan = true;
                MomentaryToolStart?.Invoke(this, ToolKind.Pan);
            }
            e.Handled = true;
            return;
        }

        // Selection interactions take priority regardless of the active tool.
        if (HasCommittedSelection())
        {
            var handle = HitTestHandles(p, out _);
            if (handle != DragMode.None)
            {
                StartDrag(handle, p);
                CapturePointer(e.Pointer);
                _capturedPointerId = e.Pointer.PointerId;
                e.Handled = true;
                return;
            }
            if (PointInSelectionBounds(p))
            {
                // Pressed inside the existing selection: drag to move, or — if it
                // turns out to be a tap — dismiss the selection on release.
                StartDrag(DragMode.Move, p, tapDismisses: true);
                CapturePointer(e.Pointer);
                _capturedPointerId = e.Pointer.PointerId;
                e.Handled = true;
                return;
            }
            ClearSelection();
        }

        // With Select tool active, a single tap on an element selects it
        // (so the user can then drag/scale/rotate it via the handles).
        if (tool is LassoTool or RectSelectTool)
        {
            if (TrySelectElementAt(p))
            {
                // StartDrag's ghost setup repaints the selection region itself;
                // chrome appears on release via EndDrag.
                StartDrag(DragMode.Move, p);
                CapturePointer(e.Pointer);
                _capturedPointerId = e.Pointer.PointerId;
                e.Handled = true;
                return;
            }
        }

        _activeTool = tool;
        if (_activeTool is null) return;
        CapturePointer(e.Pointer);
        _capturedPointerId = e.Pointer.PointerId;
        _activeTool.OnPointerDown(Context, p, PressureOf(e));
        // No _canvas.Invalidate() here — drawing tools (PenTool / HighlighterTool)
        // already call ctx.InvalidateLive() to refresh just the active-stroke overlay.
        // Eraser/Text/etc. invalidate the main canvas through ctx.Mutated when they
        // actually mutate committed content. Forcing a full main-canvas redraw on
        // every pen-down would re-rasterise the high-DPI PDF background needlessly
        // and shows up as a visible delay before the first ink appears.
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            UpdateEraserCursor(e);
            UpdateLaserDot(e);
            OnPointerMovedCore(sender, e);
        }
        catch (Exception ex) { App.LogError(ex, "OnPointerMoved"); }
    }
    private void OnPointerMovedCore(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId is null || e.Pointer.PointerId != _capturedPointerId) return;
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen) _penEventsSinceDown++;
        if (ShouldDropPenSample(e)) return;
        if (_panning)
        {
            var sp = e.GetCurrentPoint(null).Position;
            var dx = _panLastScreen.X - sp.X;
            var dy = _panLastScreen.Y - sp.Y;
            Context.RequestPan?.Invoke((float)dx, (float)dy);
            _panLastScreen = sp;
            return;
        }
        var p = ToPageSpace(e);
        if (_selectingText)
        {
            _selectCursor = FindNearestTextRun(p);
            RecomputeTextSelection();
            SyncTextSelectionOverlay();   // overlay-only; no main-canvas redraw
            return;
        }
        if (_drag != DragMode.None)
        {
            // Ghost-only update — no main-canvas invalidate until release.
            ContinueDrag(p);
            return;
        }
        if (_tapeGesture)
        {
            // Moved too far for a tap: the hand tool turns it into a pan;
            // any other tool just lets the tap lapse.
            if (_tapeTap is not null && Vector2.DistanceSquared(p, _tapeTapStart) > TapeTapSlop * TapeTapSlop)
            {
                _tapeTap = null;
                if (_tapeTapPans)
                {
                    _tapeGesture = false;
                    _panning = true;
                    _panLastScreen = e.GetCurrentPoint(null).Position;
                }
            }
            return;
        }
        if (_spacing)
        {
            ContinueSpace(p);
            return;
        }
        if (_lasering)
        {
            var pts = e.GetIntermediatePoints(this);
            if (pts is { Count: > 1 })
                for (int i = pts.Count - 1; i >= 0; i--)
                    AddLaserPoint(new Vector2((float)pts[i].Position.X, (float)pts[i].Position.Y));
            else AddLaserPoint(p);
            return;
        }
        if (_activeTool is null) return;

        // Recover any pointer samples Windows coalesced between this event and the
        // last one we saw, but throw away samples that are visually redundant
        // (closer than a quarter of the stroke width from the previous one). High
        // sample-rate pens can otherwise produce hundreds of points per second,
        // and rendering Catmull-Rom across all of them re-rasterises the entire
        // stroke each frame — costs that compound badly at high zoom.
        var device = e.Pointer.PointerDeviceType;
        var inter = e.GetIntermediatePoints(this);
        _suppressLiveSync = true;
        try
        {
            if (inter is { Count: > 1 })
            {
                for (int i = inter.Count - 1; i >= 0; i--)
                {
                    var pp = inter[i];
                    if (!pp.IsInContact) continue;
                    var raw = pp.Properties.Pressure;
                    if (device == Microsoft.UI.Input.PointerDeviceType.Pen
                        && App.Services.Settings.Current.PressureEnabled
                        && raw > 0 && raw < (float)App.Services.Settings.Current.MinPressure)
                        continue;
                    var pt = new Vector2((float)pp.Position.X, (float)pp.Position.Y);
                    if (TooCloseToLast(pt)) continue;
                    _activeTool.OnPointerMove(Context, pt, PressureOf(raw, device));
                }
            }
            else if (!TooCloseToLast(p))
            {
                _activeTool.OnPointerMove(Context, p, PressureOf(e));
            }
            _lastToolPoint = p;
        }
        finally
        {
            _suppressLiveSync = false;
        }
        // One overlay sync for all samples this event delivered. Tools handle
        // the main canvas themselves (ctx.Invalidate/InvalidateRect on commit),
        // so no full main-canvas invalidate here.
        Context.InvalidateLive?.Invoke();
    }

    private bool TooCloseToLast(Vector2 p)
    {
        var s = Context.ActiveStroke;
        if (s is null || s.Points.Count == 0) return false;
        var last = s.Points[^1];
        var dx = p.X - last.X;
        var dy = p.Y - last.Y;
        var threshold = s.Width * 0.25f;          // quarter the stroke width
        return dx * dx + dy * dy < threshold * threshold;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        try { OnPointerReleasedCore(sender, e); }
        catch (Exception ex) { App.LogError(ex, "OnPointerReleased"); }
    }
    private void OnPointerReleasedCore(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId is null || e.Pointer.PointerId != _capturedPointerId) return;

        // While panning via middle mouse, ignore button-release events that fire because
        // the user clicked-and-released a different button (e.g., a left click during a
        // middle-mouse pan). Only end the pan when the middle button itself is released.
        if (_panning && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsMiddleButtonPressed) return;
        }

        bool skipFullRedraw = false;
        if (_panning)
        {
            _panning = false;
            if (_inMomentaryPan)
            {
                _inMomentaryPan = false;
                MomentaryToolEnd?.Invoke(this, EventArgs.Empty);
            }
            // Panning only moves the viewport; page content is unchanged.
            skipFullRedraw = true;
        }
        else if (_selectingText)
        {
            // Text selection lives entirely on the overlay — the main canvas
            // never changed during the gesture.
            _selectingText = false;
            skipFullRedraw = true;
        }
        else if (_lasering)
        {
            // Overlay-only: the document never changed.
            EndLaser();
            skipFullRedraw = true;
        }
        else if (_spacing)
        {
            // The page is resized and repainted by InkCanvasControl if this
            // drag changed anything.
            EndSpace(commit: true);
            skipFullRedraw = true;
        }
        else if (_tapeGesture)
        {
            if (_tapeTap is { } t) ToggleTape(t);
            _tapeGesture = false;
            _tapeTap = null;
            skipFullRedraw = true;   // ToggleTape repaints just the strip
        }
        else if (_drag != DragMode.None)
        {
            // Applies the transform to the model once and repaints only the
            // affected region (plus Mutated for dirty/history).
            EndDrag();
            skipFullRedraw = true;
        }
        else
        {
            var wasSelectTool = _activeTool is LassoTool or RectSelectTool;
            var wasRectSelect = _activeTool is RectSelectTool;
            // Pen/highlighter repaint themselves on lift (bbox-only CommitStrokeAt)
            // — the full-page invalidate below would re-tessellate every stroke
            // on the page after every stroke. The eraser invalidates each erased
            // region as it goes; the select tools invalidate the selection's own
            // region on commit; the shape tool its committed shape's bbox.
            skipFullRedraw = _activeTool is PenTool or HighlighterTool or EraserTool
                             or RectSelectTool or LassoTool or ShapeTool;
            _activeTool?.OnPointerUp(Context, ToPageSpace(e), PressureOf(e));
            TearDownLiveShapePreview();   // no-op for non-shape tools
            if (wasSelectTool) Context.SelectionChanged?.Invoke();
            if (wasRectSelect && Context.LastDrawnRectSelection.HasValue)
                RectSelectionCompleted?.Invoke(this, EventArgs.Empty);
        }
        // Clear first: ReleasePointerCapture raises PointerCaptureLost, whose
        // handler would otherwise treat this finished gesture as interrupted.
        _capturedPointerId = null;
        _activeTool = null;
        ReleasePointerCapture(e.Pointer);
        if (!skipFullRedraw) _canvas.Invalidate();
    }

    // ── Interrupted-gesture recovery ──────────────────────────────────────────
    //
    // When capture is lost mid-gesture (Alt+Tab, a touchpad swipe, the window
    // being deactivated) no PointerReleased arrives, so the gesture never ended:
    // _panning / _drag / _selectingText / _activeTool stayed set and the next
    // pen stroke was routed into that dead gesture instead of drawing ink —
    // "the pen stops working until I switch tools". This finishes whatever was
    // in flight (committing ink drawn so far) and returns to a clean idle state.
    // Safe to call any time; a no-op when nothing is in progress.
    private Vector2 _lastToolPoint;

    public void ResetInputState()
    {
        try
        {
            if (_panning)
            {
                _panning = false;
                if (_inMomentaryPan)
                {
                    _inMomentaryPan = false;
                    MomentaryToolEnd?.Invoke(this, EventArgs.Empty);
                }
            }
            _selectingText = false;
            _tapeGesture = false;
            _tapeTap = null;
            if (_lasering) EndLaser();
            EndSpace(commit: false);
            if (_drag != DragMode.None) EndDrag();
            if (_activeTool is { } tool)
            {
                _activeTool = null;
                var wasSelect = tool is LassoTool or RectSelectTool;
                tool.OnPointerUp(Context, _lastToolPoint, 1f);
                TearDownLiveShapePreview();
                if (wasSelect) Context.SelectionChanged?.Invoke();
            }
            _capturedPointerId = null;
            HideEraserCursor();
            HideLaserDot();
        }
        catch (Exception ex) { App.LogError(ex, "ResetInputState"); }
    }
}
