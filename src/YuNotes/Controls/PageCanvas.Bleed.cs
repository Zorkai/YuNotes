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

    // ── Cross-page bleed ────────────────────────────────────────────────────
    // Adjacent page canvases (set by InkCanvasControl after Rebuild). When an
    // element sits partly past this page's top/bottom edge — e.g. a selection
    // dragged so it straddles the seam — the overflowing part is invisible on
    // its owning page (clipped to the page surface). Each page therefore also
    // renders the portion of its neighbours' content that bleeds into it, so a
    // straddling element shows on BOTH pages.
    public PageCanvas? PrevPageCanvas { get; set; }
    public PageCanvas? NextPageCanvas { get; set; }
    internal System.Collections.Generic.IDictionary<string, CanvasBitmap> ImageCacheView => _imageCache;
    internal System.Collections.Generic.ISet<string>? HiddenElementIdsView => _hiddenElementIds;

    // Draws the portion of each vertical neighbour's content that overflows the
    // shared edge into this page's `region`. Cheap in the common case: a
    // neighbour with no overflow toward this page (cached) is skipped outright,
    // and it only engages when the dirty region actually reaches the seam band.
    private void DrawNeighborBleed(CanvasDrawingSession ds, ICanvasResourceCreator dev, Windows.Foundation.Rect region)
    {
        // Previous page (above) → bleeds down into this page's top band.
        if (PrevPageCanvas is { } prev)
        {
            float overflow = prev.BottomOverflow;
            if (overflow > 0.5f && region.Y < overflow && TryNeighborOffset(prev, out float ox, out float oy))
                DrawOneNeighborBleed(ds, dev, prev, region, ox, oy);
        }
        // Next page (below) → bleeds up into this page's bottom band.
        if (NextPageCanvas is { } next)
        {
            float overflow = next.TopOverflow;
            if (overflow > 0.5f && region.Y + region.Height > (float)Page.Height - overflow
                && TryNeighborOffset(next, out float ox, out float oy))
                DrawOneNeighborBleed(ds, dev, next, region, ox, oy);
        }
    }

    // Bakes the same cross-page bleed into a full-page export raster. Uses the
    // exact page-height offset (NOT the on-screen TransformToVisual, which folds
    // in the ~4px seam separator) so a straddling stroke continues seamlessly
    // across the page boundary in the paginated PDF.
    private void DrawNeighborBleedForExport(CanvasDrawingSession ds, ICanvasResourceCreator dev)
    {
        var region = new Windows.Foundation.Rect(0, 0, Page.Width, Page.Height);
        if (PrevPageCanvas is { } prev && prev.BottomOverflow > 0.5f)
            DrawOneNeighborBleed(ds, dev, prev, region, 0f, -(float)prev.Page.Height);
        if (NextPageCanvas is { } next && next.TopOverflow > 0.5f)
            DrawOneNeighborBleed(ds, dev, next, region, 0f, (float)Page.Height);
    }

    // On-screen offset that maps a neighbour's local coords into this page's
    // coords (pure vertical translation — pages share the left edge and zoom).
    private bool TryNeighborOffset(PageCanvas neighbor, out float ox, out float oy)
    {
        try
        {
            var o = neighbor.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
            ox = (float)o.X; oy = (float)o.Y;
            return true;
        }
        catch { ox = 0; oy = 0; return false; }
    }

    private void DrawOneNeighborBleed(CanvasDrawingSession ds, ICanvasResourceCreator dev, PageCanvas neighbor,
                                      Windows.Foundation.Rect region, float ox, float oy)
    {
        // Cull the neighbour against the same region expressed in ITS coords.
        var neighborClip = new Windows.Foundation.Rect(region.X - ox, region.Y - oy, region.Width, region.Height);
        var prev = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(ox, oy) * prev;
        try
        {
            // overlayOnly: elements only — no white fill / template / background
            // over this page. Win2D clips drawing to `region`, so nothing spills
            // outside the seam band. Honour the neighbour's own hidden set so an
            // element being dragged off it isn't drawn twice.
            Renderer.DrawPage(ds, dev, neighbor.Page, neighbor.PageTemplate, null,
                              neighbor.ImageCacheView, Context.EditingTextId, overlayOnly: true,
                              clip: neighborClip, skipElementIds: neighbor.HiddenElementIdsView);
        }
        finally { ds.Transform = prev; }
    }
}
