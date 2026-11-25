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

public enum ExtendSide { Left, Right }

// Payload for PageCanvas.SelectionDragDropped — a selection Move drag released.
public sealed class SelectionDropEventArgs : EventArgs
{
    // Pointer release position in the SOURCE page's local coordinates.
    public Vector2 ReleaseLocal { get; init; }
    // Move delta (release − grab) in source-local coordinates.
    public Vector2 Delta { get; init; }
    // Set by the handler when it moved the selection to a different page.
    public bool Transferred { get; set; }
}

public sealed partial class PageCanvas : ContentControl
{
    public NotePage Page { get; }
    public TemplateSettings PageTemplate { get; set; }
    public EditorContext Context { get; }
    public Func<ITool?> ToolProvider { get; }
    public PalmRejection PalmRejection { get; }
    public PenButtonRouter ButtonRouter { get; }
    public Dictionary<PenButtonAction, ITool> ButtonTools { get; }
    public PageRenderer Renderer { get; }

    // Main canvas — also tile-based so stroke commit can invalidate just the
    // bounding box of the new stroke rather than the whole page (which at high
    // zoom means re-blitting the multi-MB PDF background bitmap).
    private readonly CanvasVirtualControl _canvas;
    // XAML layer hosting the live-stroke shapes, shape previews, selection
    // handles and extension handles. All in-progress ink is drawn here as
    // retained-mode XAML shapes (Polyline et al.) — DComp redraws them
    // natively, no Win2D surface is touched until the stroke commits.
    private readonly Canvas _overlay;

    private CanvasBitmap? _bgBitmap;

    private readonly Dictionary<string, CanvasBitmap> _imageCache = new();
    private uint? _capturedPointerId;
    private ITool? _activeTool;
    // True while a pointer gesture (stroke, drag, pan…) is in flight on this
    // page. EditorPage's autosave defers while any page is being interacted
    // with so a save can't stall the UI thread mid-stroke.
    public bool HasActivePointer => _capturedPointerId is not null;

    // Cached vertical content extent, so a neighbour can cheaply tell how far
    // this page's content overflows its edges without rescanning every frame.
    private bool _contentBoundsDirty = true;
    private float _contentMinY, _contentMaxY;
    // How far content pokes above y=0 / below Height (0 when nothing overflows).
    public float TopOverflow    { get { EnsureContentBounds(); return Math.Max(0f, -_contentMinY); } }
    public float BottomOverflow { get { EnsureContentBounds(); return Math.Max(0f, _contentMaxY - (float)Page.Height); } }

    private void EnsureContentBounds()
    {
        if (!_contentBoundsDirty) return;
        _contentBoundsDirty = false;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var s in Page.Strokes)  { var b = Bbox.Of(s);  if (b.Y < minY) minY = b.Y; if (b.Bottom > maxY) maxY = b.Bottom; }
        foreach (var sh in Page.Shapes)  { var b = Bbox.Of(sh); if (b.Y < minY) minY = b.Y; if (b.Bottom > maxY) maxY = b.Bottom; }
        foreach (var t in Page.Texts)    { var b = Bbox.Of(t);  if (b.Y < minY) minY = b.Y; if (b.Bottom > maxY) maxY = b.Bottom; }
        foreach (var im in Page.Images)  { var b = Bbox.Of(im); if (b.Y < minY) minY = b.Y; if (b.Bottom > maxY) maxY = b.Bottom; }
        if (minY == float.MaxValue) { minY = 0f; maxY = 0f; }
        _contentMinY = minY; _contentMaxY = maxY;
    }

    // Neighbours read this page's overflow to decide whether to bleed; recompute
    // it after any content change on this page.
    public void InvalidateContentBounds() => _contentBoundsDirty = true;

    public PageCanvas(
        NotePage page,
        TemplateSettings template,
        EditorContext ctx,
        Func<ITool?> toolProvider,
        PalmRejection palmRejection,
        PenButtonRouter buttonRouter,
        Dictionary<PenButtonAction, ITool> buttonTools,
        PageRenderer renderer)
    {
        Page = page;
        PageTemplate = template;
        Context = ctx;
        ToolProvider = toolProvider;
        PalmRejection = palmRejection;
        ButtonRouter = buttonRouter;
        ButtonTools = buttonTools;
        Renderer = renderer;

        _canvas = new CanvasVirtualControl
        {
            Width = page.Width,
            Height = page.Height,
            ClearColor = Colors.White
        };
        _canvas.RegionsInvalidated += OnMainRegionsInvalidated;
        _canvas.CreateResources += OnCreateResources;

        _overlay = new Canvas
        {
            Width = page.Width,
            Height = page.Height,
            IsHitTestVisible = true,
            Background = null
        };

        var root = new Grid { Width = page.Width, Height = page.Height };
        root.Children.Add(_canvas);
        root.Children.Add(_overlay);

        Width = page.Width;
        Height = page.Height;
        Content = root;
        Background = new SolidColorBrush(Colors.White);
        CornerRadius = new CornerRadius(6);
        IsTabStop = true;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerCaptureLost += (_, __) =>
        {
            // Lost without a release (see ResetInputState) → end the gesture cleanly.
            if (_capturedPointerId is not null) ResetInputState();
            _capturedPointerId = null;
            Canvas.SetZIndex(this, 0);
        };
        // Eraser range preview: leave the page → hide; press/lift → re-evaluate
        // (e.g. the eraser button was released mid-hover).
        PointerExited += (_, e) => { if (_capturedPointerId != e.Pointer.PointerId) { HideEraserCursor(); HideLaserDot(); } };
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) => UpdateEraserCursor(e)), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) => UpdateEraserCursor(e)), handledEventsToo: true);
        DoubleTapped += OnDoubleTapped;
        Tapped += OnTapped;
        SetupExtensionHandles();
    }

    // Raised when a finger touch or middle-mouse drag momentarily overrides the
    // active tool with the Hand/Pan tool. EditorPage listens to keep the toolbar
    // visually in sync — Pan button lights up while held, previous tool returns
    // on release.
    public event EventHandler<ToolKind>? MomentaryToolStart;
    public event EventHandler? MomentaryToolEnd;
    public event EventHandler? RectSelectionCompleted;

    // Raised when a Move drag is released. InkCanvasControl uses it to hand the
    // selection to whichever page the pointer ended over (a cross-page move);
    // the handler sets Transferred=true if it did, so this page skips its own
    // in-page translate (which would push the elements off the bottom edge).
    public event EventHandler<SelectionDropEventArgs>? SelectionDragDropped;

    // ── Input-handler containment ──────────────────────────────────────────────
    // Pointer/gesture handlers run on the Windows App SDK native input dispatch
    // path. If a managed exception escapes one, the input manager
    // (Microsoft.InputStateManager.dll) raises a __fastfail (0xc0000409) that NO
    // managed handler can catch — a hard crash with no dialog and no crash.log
    // (the reported "opens and silently crashes"). Every handler below is wrapped
    // so a tool / hit-test / stale-pointer bug is logged and survived instead of
    // taking the process — and the user's unsaved work — down with it.
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        try { OnDoubleTappedCore(sender, e); }
        catch (Exception ex) { App.LogError(ex, "OnDoubleTapped"); }
    }
    private void OnDoubleTappedCore(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Context.EditingSuspended) return;
        var pos = e.GetPosition(this);
        var p = new Vector2((float)pos.X, (float)pos.Y);
        foreach (var t in Page.Texts.AsEnumerable().Reverse())
        {
            if (Bbox.Of(t).Contains(p.X, p.Y))
            {
                BeginInlineTextEdit(t.Id);
                e.Handled = true;
                return;
            }
        }
    }

    public void RequestRedraw()
    {
        _contentBoundsDirty = true;
        _canvas.Invalidate();
    }

    // Called by tools on every pen sample. Live strokes render through
    // LiveInkSurface: a virtualized DComp surface updated with dirty-rect
    // draws — pixels persist between samples, so only the small changed
    // region is redrawn and recomposited (see LiveInkSurface).
    public void RequestLiveRedraw()
    {
        if (_suppressLiveSync) return;   // OnPointerMovedCore syncs once per event
        SyncMarqueeOverlay();
        var s = Context.ActiveStroke;

        // Shape tool: show a rubber-band XAML preview while dragging
        if (Context.ActiveShape is { } shape && ReferenceEquals(Context.CurrentPage, Page))
        {
            TearDownLiveInk();
            EnsureLiveShapePreview(shape);
            SyncLiveShapePreview(shape);
            return;
        }
        TearDownLiveShapePreview();

        if (s is null || !ReferenceEquals(Context.CurrentPage, Page))
        {
            TearDownLiveInk();
            return;
        }

        ScheduleLiveInkSync();
    }

    public void SetTemplate(TemplateSettings template)
    {
        PageTemplate = template;
        _canvas.Invalidate();
    }

    // Render the main Win2D backing store at a higher pixel density so it stays
    // crisp when the parent ScrollViewer zooms in. (The live stroke is XAML
    // shapes — DComp renders those at native resolution at any zoom.)
    public void SetDpiScale(float scale)
    {
        if (scale < 1f) scale = 1f;
        if (Math.Abs(_canvas.DpiScale - scale) > 0.01f) _canvas.DpiScale = scale;
    }

    private void OnCreateResources(CanvasVirtualControl sender, CanvasCreateResourcesEventArgs args)
    {
        // Device (re)created — any hi-res crop and decoded image belongs to the
        // old device.
        ClearHiResBackground();
        _bgBitmap = null;
        _imageCache.Clear();
        // Pages away from the screen decode nothing until they come near it.
        if (_resident) args.TrackAsyncAction(LoadResourcesAsync(sender, _residencyGen).AsAsyncAction());
    }

    // ── Page images only near the screen ─────────────────────────────────────
    // Every page's canvas is created when a note opens, but its images — the
    // imported PDF background (~35 MB of GPU memory for an A4 page at 300 DPI)
    // and any pictures — are decoded only while the page is near the screen.
    // InkCanvasControl.UpdatePageResidency decides which pages those are and
    // calls SetResident as the view moves. A page without its images still
    // draws its ink, text and template; its background appears once decoded.
    private bool _resident;
    private int _residencyGen;
    private readonly HashSet<string> _imagesLoading = new();
    private bool _bgLoading;

    public bool IsResident => _resident;

    public void SetResident(bool resident)
    {
        if (resident == _resident) return;
        _resident = resident;
        int gen = ++_residencyGen;
        if (!resident)
        {
            ReleaseImages();
            return;
        }
        // Without a device yet, OnCreateResources loads them once it has one.
        if (TryGetCanvasDevice() is { } device) _ = LoadResidentAsync(device, gen);
    }

    private async Task LoadResidentAsync(ICanvasResourceCreator dev, int gen)
    {
        try { await LoadResourcesAsync(dev, gen); }
        catch (Exception ex) { App.LogError(ex, "Loading page images"); }
        if (gen == _residencyGen && _resident) _canvas.Invalidate();
    }

    private void ReleaseImages()
    {
        ClearHiResBackground();
        _bgBitmap?.Dispose();
        _bgBitmap = null;
        foreach (var b in _imageCache.Values) b.Dispose();
        _imageCache.Clear();
    }

    // Runs on the UI thread (each await resumes there); a result that arrives
    // after the page left the screen (generation moved on) is thrown away.
    private async Task LoadResourcesAsync(ICanvasResourceCreator dev, int gen)
    {
        if (Page.BackgroundPng is { Length: > 0 } && _bgBitmap is null && !_bgLoading)
        {
            _bgLoading = true;
            try
            {
                using var ms = new MemoryStream(Page.BackgroundPng);
                var bmp = await CanvasBitmap.LoadAsync(dev, ms.AsRandomAccessStream());
                if (gen != _residencyGen || !_resident) { bmp.Dispose(); return; }
                _bgBitmap?.Dispose();
                _bgBitmap = bmp;
            }
            catch { _bgBitmap = null; }
            finally { _bgLoading = false; }
        }
        foreach (var img in Page.Images.ToList())
        {
            if (gen != _residencyGen || !_resident) return;
            await EnsureImageLoadedAsync(dev, img);
        }
    }

    private async Task EnsureImageLoadedAsync(ICanvasResourceCreator dev, ImageElement img)
    {
        if (_imageCache.ContainsKey(img.Id) || !_imagesLoading.Add(img.Id)) return;
        int gen = _residencyGen;
        try
        {
            using var ms = new MemoryStream(img.PngData);
            var bmp = await CanvasBitmap.LoadAsync(dev, ms.AsRandomAccessStream());
            if (gen != _residencyGen) { bmp.Dispose(); return; }
            if (_imageCache.TryGetValue(img.Id, out var old)) old.Dispose();
            _imageCache[img.Id] = bmp;
        }
        catch { }
        finally { _imagesLoading.Remove(img.Id); }
    }

    private void OnMainRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        // Safety net: a page that became near-screen while its device was being
        // set up (neither SetResident nor OnCreateResources could load then)
        // starts loading here, now that it is drawing and the device is ready.
        if (_resident && _bgBitmap is null && !_bgLoading && Page.BackgroundPng is { Length: > 0 })
            _ = LoadResidentAsync(sender, _residencyGen);

        if (Page.Images.Count > 0)
        {
            var missing = Page.Images.Where(i => !_imageCache.ContainsKey(i.Id) && !_imagesLoading.Contains(i.Id)).ToList();
            if (missing.Count > 0) _ = LoadMissingImagesAsync(sender, missing);
        }

        // One drawing session per dirty region — Win2D clips drawing to that
        // region's tiles, and the region is passed as `clip` so DrawPage also
        // skips geometry building for ink that can't touch it.
        // The hi-res crop's rect doesn't account for the extension-preview
        // shift, so it sits out during a preview drag.
        bool extPreview = _previewExtLeft != 0 || _previewExtRight != 0;
        foreach (var region in args.InvalidatedRegions)
        {
            using var ds = sender.CreateDrawingSession(region);
            // This runs inside a native Win2D callback. An unhandled managed
            // exception here (a bad element that slips past the renderer's guards,
            // a lost device, …) propagates through native code and hard-kills the
            // process with no dialog and no catchable stack — the "opens and
            // silently crashes" symptom. Contain it per-region: log it, draw what
            // we can, and keep the app (and the user's unsaved work) alive.
            try
            {
                Renderer.DrawPage(ds, sender, Page, PageTemplate, _bgBitmap, _imageCache, Context.EditingTextId,
                                  previewExtLeft: _previewExtLeft, previewExtRight: _previewExtRight,
                                  clip: region,
                                  hiResBackground: extPreview ? null : _bgHiRes,
                                  hiResRect: !extPreview && _bgHiRes is not null ? _bgHiResRect : null,
                                  skipElementIds: _hiddenElementIds,
                                  fadedElementIds: _eraseHoverIds);

                // Draw the slice of any neighbouring page's content that spills
                // across the seam into this region, so a straddling element
                // shows on both pages. Skipped during an extension preview
                // (element coords are shifted then).
                if (!extPreview) DrawNeighborBleed(ds, sender, region);

                // The select-tool marquee is a XAML overlay element (see
                // SyncMarqueeOverlay) — nothing to draw here. Selection chrome
                // is skipped while a drag is in flight: the overlay ghost shows
                // the selection, and drawing chrome here would force a main-
                // canvas repaint on every drag move.
                if (ReferenceEquals(Context.CurrentPage, Page)
                    && _drag == DragMode.None && HasCommittedSelection())
                {
                    DrawSelectionVisuals(ds);
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex, $"DrawPage failed (page index {Page.Index})");
            }
        }

        // A DpiScale change blanked the canvas behind the freeze-frame overlay;
        // its regions are now redrawn and committed, so the overlay can go.
        // Low priority lets this batch present before the overlay disappears.
        if (_unfreezeAfterDraw)
        {
            _unfreezeAfterDraw = false;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UnfreezeViewport);
        }
    }

    // ── Freeze-frame across DpiScale changes ────────────────────────────────
    // Changing CanvasVirtualControl.DpiScale recreates its virtual surface,
    // which blanks to ClearColor (white) until RegionsInvalidated re-renders —
    // a visible flash on every backing-tier change while zooming. Freeze draws
    // the visible crop into a CanvasImageSource overlay BEFORE the change at
    // the OLD backing scale (pixel-identical to what was on screen); the
    // overlay is removed right after the first post-change redraw commits.
    private Image? _freezeImage;
    private bool _unfreezeAfterDraw;

    // CanvasVirtualControl.Device THROWS (ArgumentException, "does not currently
    // have a CanvasDevice") rather than returning null until the control has
    // created its device — e.g. canvases just recreated by InkCanvasControl.
    // Rebuild (adding a page) that haven't loaded/drawn yet. Callers treat null
    // as "skip" or fall back to the shared device.
    private async Task LoadMissingImagesAsync(CanvasVirtualControl sender, List<ImageElement> missing)
    {
        foreach (var img in missing) await EnsureImageLoadedAsync(sender, img);
        sender.Invalidate();
    }

    private CanvasDevice? TryGetCanvasDevice()
    {
        try { return _canvas.ReadyToDraw ? _canvas.Device : null; }
        catch { return null; }
    }

    public void FreezeViewportForDpiChange(Windows.Foundation.Rect viewRect, float oldScale)
    {
        UnfreezeViewport();
        var device = TryGetCanvasDevice();
        if (device is null || viewRect.Width < 1 || viewRect.Height < 1) return;
        try
        {
            float dpi = 96f * Math.Max(1f, oldScale);
            var snapshot = new CanvasImageSource(device, (float)viewRect.Width, (float)viewRect.Height, dpi);
            bool extPreview = _previewExtLeft != 0 || _previewExtRight != 0;
            using (var ds = snapshot.CreateDrawingSession(Colors.White))
            {
                ds.Transform = Matrix3x2.CreateTranslation((float)-viewRect.X, (float)-viewRect.Y);
                Renderer.DrawPage(ds, device, Page, PageTemplate, _bgBitmap, _imageCache, Context.EditingTextId,
                                  previewExtLeft: _previewExtLeft, previewExtRight: _previewExtRight,
                                  clip: viewRect,
                                  hiResBackground: extPreview ? null : _bgHiRes,
                                  hiResRect: !extPreview && _bgHiRes is not null ? _bgHiResRect : null,
                                  skipElementIds: _hiddenElementIds,
                                  fadedElementIds: _eraseHoverIds);
            }
            _freezeImage = new Image
            {
                Source = snapshot,
                Width = viewRect.Width,
                Height = viewRect.Height,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_freezeImage, viewRect.X);
            Canvas.SetTop(_freezeImage, viewRect.Y);
            Canvas.SetZIndex(_freezeImage, 15);
            _overlay.Children.Add(_freezeImage);
            _unfreezeAfterDraw = true;

            // Safety net: if no redraw ever lands (page scrolled out mid-change,
            // device lost, …), don't leave a stale snapshot covering the page.
            var current = _freezeImage;
            _ = Task.Delay(1000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_freezeImage, current)) UnfreezeViewport();
            }));
        }
        catch { UnfreezeViewport(); }
    }

    public void UnfreezeViewport()
    {
        _unfreezeAfterDraw = false;
        if (_freezeImage is null) return;
        _overlay.Children.Remove(_freezeImage);
        _freezeImage = null;
    }

    // Called from PenTool/HighlighterTool via ctx.CommitStrokeAt on stroke
    // commit. Repaints only the just-committed stroke's bbox on the main
    // canvas instead of the whole page — that's the difference between a
    // sub-ms commit and one that blocks the UI thread for ~10–20 ms.
    public void CommitStrokeRedraw(Bbox bbox)
    {
        _contentBoundsDirty = true;
        // If the change reaches past an edge, the neighbour the content bleeds
        // into must repaint (it reads this page's now-stale overflow). Do this
        // before clamping the rect to this page's bounds.
        if (bbox.Y < 0) PrevPageCanvas?.RequestRedraw();
        if (bbox.Y + bbox.H > Page.Height) NextPageCanvas?.RequestRedraw();

        // Round outward to whole units so a fractional edge can't leave a
        // half-repainted row of pixels behind.
        double x = Math.Max(0, Math.Floor(bbox.X) - 1);
        double y = Math.Max(0, Math.Floor(bbox.Y) - 1);
        double w = Math.Min(Page.Width,  Math.Ceiling(bbox.X + bbox.W) + 1) - x;
        double h = Math.Min(Page.Height, Math.Ceiling(bbox.Y + bbox.H) + 1) - y;
        if (w <= 0 || h <= 0) return;
        _canvas.Invalidate(new Windows.Foundation.Rect(x, y, w, h));
    }

    private Vector2 ToPageSpace(PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this).Position;
        return new Vector2((float)pt.X, (float)pt.Y);
    }

    // Called by InkCanvasControl after an extension is committed to the model,
    // to resize all internal canvases to the new page dimensions.
    public void ResizeCanvas()
    {
        double newW = Page.Width, newH = Page.Height;
        _canvas.Width      = newW;
        _overlay.Width     = newW;
        _canvas.Height     = newH;
        _overlay.Height    = newH;
        var root = (Grid)Content;
        root.Width = newW;
        root.Height = newH;
        Width = newW;
        Height = newH;

        // Background placement changed — the hi-res crop rect is stale.
        ClearHiResBackground();

        // Reposition right handle for new width; both stay centred vertically.
        if (_rightExtHandle is not null)
            Canvas.SetLeft(_rightExtHandle, newW - ExtHandleWidth);
        double centerY = (newH - ExtHandleHeight) / 2;
        if (_leftExtHandle is not null) Canvas.SetTop(_leftExtHandle, centerY);
        if (_rightExtHandle is not null) Canvas.SetTop(_rightExtHandle, centerY);

        _canvas.Invalidate();
    }
}
