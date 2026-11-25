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

    // ── Tape (hide and reveal) ─────────────────────────────────────────────────
    //
    // Tape strips are ShapeElements of kind Tape. Tapping one flips it between
    // covered and revealed; that state lives in Context.RevealedTapeIds only,
    // so it never dirties the note or adds an undo step. Finger taps work with
    // any tool (touch never draws); pen and mouse taps are routed here by
    // TapeTapTarget.

    private bool _tapeGesture;           // a pen/mouse press that started on tape
    private ShapeElement? _tapeTap;      // the strip it will toggle (null = lapsed)
    private Vector2 _tapeTapStart;
    private bool _tapeTapPans;           // hand tool: a drag turns into a pan
    private const float TapeTapSlop = 8f;

    // Topmost tape strip under p (rotation-aware), or null.
    private ShapeElement? TapeAt(Vector2 p)
    {
        for (int i = Page.Shapes.Count - 1; i >= 0; i--)
        {
            var s = Page.Shapes[i];
            if (s.Kind != ShapeKind.Tape) continue;
            float x = MathF.Min(s.X1, s.X2), y = MathF.Min(s.Y1, s.Y2);
            float hw = MathF.Abs(s.X2 - s.X1) * 0.5f, hh = MathF.Abs(s.Y2 - s.Y1) * 0.5f;
            var q = p - new Vector2(x + hw, y + hh);
            if (s.Rotation != 0f && float.IsFinite(s.Rotation))
            {
                float a = -s.Rotation * MathF.PI / 180f, cos = MathF.Cos(a), sin = MathF.Sin(a);
                q = new Vector2(q.X * cos - q.Y * sin, q.X * sin + q.Y * cos);
            }
            if (MathF.Abs(q.X) <= hw && MathF.Abs(q.Y) <= hh) return s;
        }
        return null;
    }

    // Which pen/mouse presses on tape count as a reveal/cover tap:
    //  - select tools and the eraser never (so tape can be selected, moved,
    //    resized, recolored and erased like any shape);
    //  - the tape tool and the hand tool: any strip, covered or revealed;
    //  - every other tool (pen, highlighter, text, shapes, laser…): only a
    //    covered strip, so you can still write on top of revealed content.
    // Presses on a live selection's box or handles keep their usual meaning.
    private ShapeElement? TapeTapTarget(ITool? tool, Vector2 p, PointerRoutedEventArgs e)
    {
        if (tool is LassoTool or RectSelectTool or EraserTool or null) return null;
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            && e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return null;
        if (HasCommittedSelection() && (PointInSelectionBounds(p) || HitTestHandles(p, out _) != DragMode.None)) return null;
        var tape = TapeAt(p);
        if (tape is null) return null;
        bool anyStrip = tool is PanTool || tool is ShapeTool { ShapeKind: ShapeKind.Tape };
        return anyStrip || !Context.RevealedTapeIds.Contains(tape.Id) ? tape : null;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            if (e.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch) return;   // pen/mouse: press path
            var pos = e.GetPosition(this);
            if (TapeAt(new Vector2((float)pos.X, (float)pos.Y)) is { } tape)
            {
                ToggleTape(tape);
                e.Handled = true;
            }
        }
        catch (Exception ex) { App.LogError(ex, "OnTapped"); }
    }

    public void ToggleTape(ShapeElement tape)
    {
        if (!Context.RevealedTapeIds.Remove(tape.Id)) Context.RevealedTapeIds.Add(tape.Id);
        RedrawTape(tape);
        TapeToggled?.Invoke(tape);
    }

    // Raised when the user taps a strip open or shut (study mode listens, so
    // tapping the asked strip counts as Show answer).
    public event Action<ShapeElement>? TapeToggled;

    // Redraws a strip including its study outline and missed dot.
    public void RedrawTape(ShapeElement tape)
    {
        var b = Bbox.Of(tape);
        float m = PageRenderer.StudyMarkMargin + 4;
        CommitStrokeRedraw(new Bbox(b.X - m, b.Y - m, b.W + m * 2, b.H + m * 2));
    }

    /// <summary>
    /// A flashcard picture: the strip with the lines around it, the strip
    /// covered (front) or showing what's under it (back). Every other strip in
    /// the picture stays covered, and study marks are left out. Background
    /// bitmaps are decoded here when the page isn't on screen.
    /// </summary>
    public byte[] RenderStudyCardPng(ShapeElement tape, bool revealed, float scale = 2f)
    {
        var b = Bbox.Of(tape);
        float padX = MathF.Max(170f, b.W * 1.2f), padY = MathF.Max(80f, b.H * 1.6f);
        float x0 = MathF.Max(0, b.X - padX), y0 = MathF.Max(0, b.Y - padY);
        float x1 = MathF.Min((float)Page.Width, b.X + b.W + padX), y1 = MathF.Min((float)Page.Height, b.Y + b.H + padY);
        float w = MathF.Max(1, x1 - x0), h = MathF.Max(1, y1 - y0);

        var device = CanvasDevice.GetSharedDevice();
        var temps = new List<CanvasBitmap>();
        var bg = _bgBitmap;
        if (bg is null && Page.BackgroundPng is { Length: > 0 })
        {
            try
            {
                using var bgMs = new MemoryStream(Page.BackgroundPng);
                bg = CanvasBitmap.LoadAsync(device, bgMs.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
                temps.Add(bg);
            }
            catch { bg = null; }
        }
        IDictionary<string, CanvasBitmap> images = _imageCache;
        if (Page.Images.Any(i => !_imageCache.ContainsKey(i.Id)))
        {
            var all = new Dictionary<string, CanvasBitmap>(_imageCache);
            foreach (var img in Page.Images.Where(i => !all.ContainsKey(i.Id)))
            {
                try
                {
                    using var imgMs = new MemoryStream(img.PngData);
                    var bmp = CanvasBitmap.LoadAsync(device, imgMs.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
                    all[img.Id] = bmp;
                    temps.Add(bmp);
                }
                catch { }
            }
            images = all;
        }

        // The renderer is shared by every page: swap its tape callbacks for
        // this one picture and put them back straight after (UI thread only).
        var (rev, focus, missed) = (Renderer.IsTapeRevealed, Renderer.IsTapeFocused, Renderer.IsTapeMissed);
        Renderer.IsTapeRevealed = id => revealed && id == tape.Id;
        Renderer.IsTapeFocused = null;
        Renderer.IsTapeMissed = null;
        try
        {
            using var target = new CanvasRenderTarget(device, w * scale, h * scale, 96f);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Colors.White);
                ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(scale);
                Renderer.DrawPage(ds, device, Page, PageTemplate, bg, images);
            }
            using var ms = new MemoryStream();
            target.SaveAsync(ms.AsRandomAccessStream(), CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
            return ms.ToArray();
        }
        finally
        {
            (Renderer.IsTapeRevealed, Renderer.IsTapeFocused, Renderer.IsTapeMissed) = (rev, focus, missed);
            foreach (var t in temps) t.Dispose();
        }
    }
}
