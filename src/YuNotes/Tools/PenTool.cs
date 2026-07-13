using System;
using System.Numerics;
using Microsoft.UI.Dispatching;
using YuNotes.Models;

namespace YuNotes.Tools;

public sealed class PenTool : ITool
{
    public ToolKind Kind => ToolKind.Pen;
    public bool UsePressure { get; set; } = false;
    public bool HoldToSnapEnabled { get; set; } = true;

    // Hold-to-snap state
    private DispatcherQueueTimer? _holdTimer;
    private Stroke? _holdStroke;
    private EditorContext? _holdCtx;
    private ShapeElement? _pendingSnap;
    private Stroke? _snapSourceStroke;   // raw ink the snap replaced — kept so undo can restore it
    private Vector2 _holdAnchorPos;
    // Movement below this threshold is treated as "pen still" (stylus jitter is
    // typically < 3 px; 10 px gives a comfortable margin without being too sluggish).
    private const float HoldMoveThreshold = 10f;

    // Post-snap adjustment state: after snap fires the user can still drag to
    // resize/reposition the shape before lifting the pen.
    private bool _inSnapAdjust;
    // Adjustment must not touch the shape until the pen deliberately moves:
    // resting-jitter (or the tiny drift of lifting the pen) used to feed the
    // adjust path immediately, which resized every snap by a few px — and
    // mangled triangles outright (see AdjustSnapShape).
    private bool _snapAdjustEngaged;
    private Vector2 _snapFixedPoint; // corner/endpoint that stays fixed during drag
    private int _snapDragVertex;     // Triangle only: which vertex (0/1/2) follows the pen

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        StopHoldTimer();
        _pendingSnap = null;
        _snapSourceStroke = null;

        ctx.ActiveStroke = new Stroke
        {
            Kind = StrokeKind.Pen,
            Color = ctx.CurrentColor,
            Width = ctx.CurrentWidth,
            PressureMode = UsePressure
        };
        ctx.ActiveStroke.Points.Add(new InkPoint(p.X, p.Y, UsePressure ? pressure : 1f));
        ctx.InvalidateLive?.Invoke();
        StartHoldTimer(ctx, p);
    }

    // Pen snaps to the ruler edge when it comes within this many document pixels.
    private const float RulerSnapThreshold = 40f;

    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure)
    {
        // Post-snap adjustment: shape recognised, pen still down — let the user
        // drag to resize before lifting. The recognized shape must survive an
        // unmoving pen, so nothing is touched until the pen leaves the hold
        // jitter radius.
        if (_inSnapAdjust && _pendingSnap is { } snap)
        {
            if (!_snapAdjustEngaged
                && (p - _holdAnchorPos).Length() <= HoldMoveThreshold)
                return;
            _snapAdjustEngaged = true;
            AdjustSnapShape(snap, p);
            ctx.ActiveShape = snap;
            ctx.InvalidateLive?.Invoke();
            return;
        }

        // Ruler snap: snap to the nearer EDGE of the ruler, not its centre line.
        //
        // The ruler body has a half-height of ctx.RulerHalfHeight in document px.
        // We decompose the pen position into components along the ruler (proj) and
        // perpendicular to it (across).  The two edges sit at ±halfH along the
        // normal, so the nearest-edge snap point is:
        //   center  +  along * dir  +  sign(across) * halfH * nrm
        //
        // distFromEdge = | |across| - halfH |, which is the distance from the pen
        // to the nearer edge regardless of whether the pen is inside or outside the
        // ruler body.  Only snap when that distance is below the threshold.
        if (ctx.RulerVisible)
        {
            float rad   = ctx.RulerAngle * MathF.PI / 180f;
            var dir     = new Vector2( MathF.Cos(rad), MathF.Sin(rad));
            var nrm     = new Vector2(-MathF.Sin(rad), MathF.Cos(rad));
            var center  = new Vector2(ctx.RulerX, ctx.RulerY);
            var toP     = p - center;
            float along  = Vector2.Dot(toP, dir);
            float across = Vector2.Dot(toP, nrm);        // signed dist from centre line
            float halfH  = ctx.RulerHalfHeight;
            float distFromEdge = MathF.Abs(MathF.Abs(across) - halfH);
            if (distFromEdge < RulerSnapThreshold)
            {
                float edgeSign = across >= 0f ? 1f : -1f; // which side is the pen on?
                p = center + along * dir + edgeSign * halfH * nrm;
            }
        }

        if (ctx.ActiveStroke is null) return;
        ctx.ActiveStroke.Points.Add(new InkPoint(p.X, p.Y, UsePressure ? pressure : 1f));
        ctx.InvalidateLive?.Invoke();
        // Only restart the timer when the pen has moved beyond jitter range.
        // Micro-tremor from a resting stylus is typically < 3 px and must not
        // keep deferring the snap.
        if ((p - _holdAnchorPos).Length() > HoldMoveThreshold)
            StartHoldTimer(ctx, p);
    }

    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure)
    {
        StopHoldTimer();

        if (_pendingSnap is not null)
        {
            // Hold-to-snap: commit the raw stroke first, then replace it with
            // the recognized shape as a SECOND history step. Nothing is drawn
            // in between, so the user only ever sees the shape — but one undo
            // now turns a mis-recognized shape back into the original
            // handwriting instead of destroying it.
            var snap = _pendingSnap;
            ctx.ActiveStroke = null;
            if (_snapSourceStroke is { } raw)
            {
                ctx.CurrentPage.Strokes.Add(raw);
                ctx.Mutated?.Invoke();
                ctx.CurrentPage.Strokes.Remove(raw);
            }
            ctx.CurrentPage.Shapes.Add(snap);
            ctx.ActiveShape = null;
            _pendingSnap = null;
            _snapSourceStroke = null;
            ctx.Mutated?.Invoke();
            // The raw stroke only ever existed on the live overlay, so just
            // the snapped shape's area needs repainting.
            var b = Bbox.Of(snap);
            float bpad = snap.StrokeWidth + 4f;
            if (ctx.InvalidateRect is { } inv)
                inv(new Bbox(b.X - bpad, b.Y - bpad, b.W + bpad * 2, b.H + bpad * 2));
            else
                ctx.Invalidate?.Invoke();
            return;
        }

        if (ctx.ActiveStroke is null) return;
        ctx.ActiveStroke.Points.Add(new InkPoint(p.X, p.Y, UsePressure ? pressure : 1f));
        var committed = ctx.ActiveStroke;
        ctx.CurrentPage.Strokes.Add(committed);
        ctx.ActiveStroke = null;
        ctx.Mutated?.Invoke();
        // Tear down the live overlay AND repaint only the just-committed stroke's
        // bbox on the main canvas (CommitStrokeAt). Falls back to full invalidate
        // if no commit hook is wired (e.g. unit tests).
        ctx.InvalidateLive?.Invoke();
        if (ctx.CommitStrokeAt is { } commit) commit(Bbox.OfRendered(committed));
        else ctx.Invalidate?.Invoke();
    }

    // ── Hold timer ────────────────────────────────────────────────────────────

    private void StartHoldTimer(EditorContext ctx, Vector2 anchorPos)
    {
        if (!HoldToSnapEnabled) return;

        if (_holdTimer is null)
        {
            if (ctx.DispatcherQueue is not { } dq) return;
            _holdTimer = dq.CreateTimer();
            _holdTimer.Interval = TimeSpan.FromMilliseconds(750);
            _holdTimer.IsRepeating = false;
            _holdTimer.Tick += OnHoldTick;
        }
        _holdAnchorPos = anchorPos;
        _holdTimer.Stop();
        _holdStroke = ctx.ActiveStroke;
        _holdCtx = ctx;
        _holdTimer.Start();
    }

    private void StopHoldTimer()
    {
        _holdTimer?.Stop();
        _holdStroke = null;
        _holdCtx = null;
        _inSnapAdjust = false;
        _snapAdjustEngaged = false;
    }

    // ── Post-snap adjustment helpers ──────────────────────────────────────────

    // Returns the corner/endpoint of `shape` that is diagonally opposite the one
    // nearest to `penPos`, so that corner stays fixed while the pen drags its pair.
    private static Vector2 OppositeCorner(ShapeElement shape, Vector2 penPos)
    {
        var p1 = new Vector2(shape.X1, shape.Y1);
        var p2 = new Vector2(shape.X2, shape.Y2);

        if (shape.Kind is ShapeKind.Line)
            // Fix whichever endpoint is farther from the pen.
            return Vector2.Distance(penPos, p1) > Vector2.Distance(penPos, p2) ? p1 : p2;

        // For rect/ellipse, test all 4 corners and return the diagonally opposite one.
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            p1,
            new Vector2(shape.X2, shape.Y1),
            new Vector2(shape.X1, shape.Y2),
            p2,
        };
        int closest = 0;
        float minDist = float.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            float d = Vector2.Distance(penPos, corners[i]);
            if (d < minDist) { minDist = d; closest = i; }
        }
        return corners[3 - closest]; // 0↔3, 1↔2
    }

    // Moves the adjustable corner/endpoint/vertex to `penPos`.
    private void AdjustSnapShape(ShapeElement shape, Vector2 penPos)
    {
        if (shape.Kind is ShapeKind.Line)
        {
            // Determine which endpoint is the fixed one and update the other.
            if (MathF.Abs(shape.X1 - _snapFixedPoint.X) < 0.5f &&
                MathF.Abs(shape.Y1 - _snapFixedPoint.Y) < 0.5f)
            { shape.X2 = penPos.X; shape.Y2 = penPos.Y; }
            else
            { shape.X1 = penPos.X; shape.Y1 = penPos.Y; }
        }
        else if (shape.Kind is ShapeKind.Triangle)
        {
            // A triangle's X1..Y3 are three FREE VERTICES — the bbox rebuild
            // below must never run for it (it used to, bulldozing two vertices
            // into axis-aligned bbox corners the moment the pen jittered after
            // the snap: triangles collapsed into lines or sprouted right
            // angles). Drag only the vertex nearest to where the pen held.
            switch (_snapDragVertex)
            {
                case 0:  shape.X1 = penPos.X; shape.Y1 = penPos.Y; break;
                case 1:  shape.X2 = penPos.X; shape.Y2 = penPos.Y; break;
                default: shape.X3 = penPos.X; shape.Y3 = penPos.Y; break;
            }
        }
        else
        {
            // Rebuild bbox from fixed corner + pen, keeping coords ordered.
            shape.X1 = MathF.Min(_snapFixedPoint.X, penPos.X);
            shape.Y1 = MathF.Min(_snapFixedPoint.Y, penPos.Y);
            shape.X2 = MathF.Max(_snapFixedPoint.X, penPos.X);
            shape.Y2 = MathF.Max(_snapFixedPoint.Y, penPos.Y);
        }
    }

    private void OnHoldTick(DispatcherQueueTimer sender, object args)
    {
        if (_holdCtx is not { } ctx || _holdStroke is not { } stroke) return;
        if (!ReferenceEquals(ctx.ActiveStroke, stroke)) return;

        var snap = ShapeRecognizer.Recognize(stroke.Points, stroke.Color, stroke.Width);
        ShapeDebugLog.Dump(stroke.Points, snap);
        if (snap is null) return;

        // Replace the live freehand overlay with a clean shape preview. The raw
        // stroke is retained so the commit on lift can offer it as an undo step.
        _pendingSnap = snap;
        _snapSourceStroke = stroke;
        ctx.ActiveStroke = null;
        ctx.ActiveShape = snap;
        ctx.InvalidateLive?.Invoke();

        // Enable drag-to-adjust: the corner/endpoint/vertex closest to the pen
        // becomes the draggable handle.
        if (snap.Kind == ShapeKind.Triangle)
            _snapDragVertex = NearestTriangleVertex(snap, _holdAnchorPos);
        else
            _snapFixedPoint = OppositeCorner(snap, _holdAnchorPos);
        _snapAdjustEngaged = false;
        _inSnapAdjust = true;
    }

    private static int NearestTriangleVertex(ShapeElement s, Vector2 penPos)
    {
        float d1 = Vector2.DistanceSquared(penPos, new Vector2(s.X1, s.Y1));
        float d2 = Vector2.DistanceSquared(penPos, new Vector2(s.X2, s.Y2));
        float d3 = Vector2.DistanceSquared(penPos, new Vector2(s.X3, s.Y3));
        return d1 <= d2 && d1 <= d3 ? 0 : d2 <= d3 ? 1 : 2;
    }
}

public sealed class HighlighterTool : ITool
{
    public ToolKind Kind => ToolKind.Highlighter;

    public void OnPointerDown(EditorContext ctx, Vector2 p, float pressure)
    {
        ctx.ActiveStroke = new Stroke
        {
            Kind = StrokeKind.Highlighter,
            Color = ctx.CurrentColor,
            Width = ctx.CurrentWidth
        };
        ctx.ActiveStroke.Points.Add(new InkPoint(p.X, p.Y, 1f));
        ctx.InvalidateLive?.Invoke();
    }

    public void OnPointerMove(EditorContext ctx, Vector2 p, float pressure)
    {
        if (ctx.ActiveStroke is null) return;
        ctx.ActiveStroke.Points.Add(new InkPoint(p.X, p.Y, 1f));
        ctx.InvalidateLive?.Invoke();
    }

    public void OnPointerUp(EditorContext ctx, Vector2 p, float pressure)
    {
        if (ctx.ActiveStroke is null) return;
        ctx.ActiveStroke.Points.Add(new InkPoint(p.X, p.Y, 1f));
        var committed = ctx.ActiveStroke;
        ctx.CurrentPage.Strokes.Add(committed);
        ctx.ActiveStroke = null;
        ctx.Mutated?.Invoke();
        ctx.InvalidateLive?.Invoke();
        if (ctx.CommitStrokeAt is { } commit) commit(Bbox.OfRendered(committed));
        else ctx.Invalidate?.Invoke();
    }
}
