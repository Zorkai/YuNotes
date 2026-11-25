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
    // PDF text-selection highlight — one overlay Path (a rectangle per selected
    // line). The old Win2D fills forced a full-page redraw per pointer move
    // while dragging across text.
    private XamlPath? _textSelHighlight;

    // PDF-text selection state (Hand tool + left-mouse drag, browser-style).
    private bool _selectingText;
    private TextRun? _selectAnchor;
    private TextRun? _selectCursor;
    private readonly List<TextRun> _selectedTextRuns = new();

    // ─── PDF text selection (Hand tool, browser-style) ───────────────────────────

    private TextRun? FindNearestTextRun(Vector2 p)
    {
        TextRun? best = null;
        double bestDist = double.MaxValue;
        foreach (var r in Page.TextRuns)
        {
            var cx = r.X + r.Width * 0.5;
            var cy = r.Y + r.Height * 0.5;
            var dx = p.X - cx;
            var dy = p.Y - cy;
            var d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = r; }
        }
        return best;
    }

    // Encode (line, order) into one comparable integer so we can pick the runs
    // between anchor and cursor in reading order with a single linear scan.
    private static long ReadingKey(TextRun r) => ((long)r.LineIndex << 32) | (uint)r.OrderInLine;

    private void RecomputeTextSelection()
    {
        _selectedTextRuns.Clear();
        if (_selectAnchor is null || _selectCursor is null) return;
        var aKey = ReadingKey(_selectAnchor);
        var cKey = ReadingKey(_selectCursor);
        var lo = Math.Min(aKey, cKey);
        var hi = Math.Max(aKey, cKey);
        foreach (var r in Page.TextRuns)
        {
            var k = ReadingKey(r);
            if (k >= lo && k <= hi) _selectedTextRuns.Add(r);
        }
    }

    public void ClearTextSelection()
    {
        if (_selectedTextRuns.Count == 0 && _selectAnchor is null && _selectCursor is null) return;
        _selectAnchor = null;
        _selectCursor = null;
        _selectedTextRuns.Clear();
        SyncTextSelectionOverlay();
    }

    // Rebuilds the overlay highlight from _selectedTextRuns, coalescing the
    // runs of each line into one rectangle (fewer geometry nodes AND a
    // continuous highlight instead of per-word gaps).
    private void SyncTextSelectionOverlay()
    {
        if (_selectedTextRuns.Count == 0)
        {
            if (_textSelHighlight is not null)
            {
                _overlay.Children.Remove(_textSelHighlight);
                _textSelHighlight = null;
            }
            return;
        }

        // Nonzero so slightly overlapping line boxes union instead of
        // even-odd cancelling at their intersections.
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        int i = 0;
        while (i < _selectedTextRuns.Count)
        {
            var first = _selectedTextRuns[i];
            int line = first.LineIndex;
            double x1 = first.X, y1 = first.Y;
            double x2 = first.X + first.Width, y2 = first.Y + first.Height;
            i++;
            while (i < _selectedTextRuns.Count && _selectedTextRuns[i].LineIndex == line)
            {
                var r = _selectedTextRuns[i];
                x1 = Math.Min(x1, r.X);
                y1 = Math.Min(y1, r.Y);
                x2 = Math.Max(x2, r.X + r.Width);
                y2 = Math.Max(y2, r.Y + r.Height);
                i++;
            }
            group.Children.Add(new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(x1, y1, x2 - x1, y2 - y1)
            });
        }

        if (_textSelHighlight is null)
        {
            _textSelHighlight = new XamlPath
            {
                Fill = new SolidColorBrush(Color.FromArgb(110, 91, 107, 255)),
                IsHitTestVisible = false
            };
            _overlay.Children.Add(_textSelHighlight);
        }
        _textSelHighlight.Data = group;
    }

    // ─── Search highlights ───────────────────────────────────────────────────────
    // Soft yellow marks on every match on this page; the current match gets a
    // stronger fill and an outline. Rects are in page coordinates.
    private XamlPath? _searchMarks, _searchCurrent;

    public void SetSearchHighlights(IReadOnlyList<Windows.Foundation.Rect>? rects, Windows.Foundation.Rect? current)
    {
        static GeometryGroup Group(IEnumerable<Windows.Foundation.Rect> rs)
        {
            var g = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var r in rs)
                g.Children.Add(new RectangleGeometry { Rect = new Windows.Foundation.Rect(r.X - 4, r.Y - 3, r.Width + 8, r.Height + 6) });
            return g;
        }

        if (rects is null || rects.Count == 0)
        {
            if (_searchMarks is not null) { _overlay.Children.Remove(_searchMarks); _searchMarks = null; }
        }
        else
        {
            _searchMarks ??= AddOverlayPath(Color.FromArgb(80, 255, 213, 59), null);
            _searchMarks.Data = Group(rects);
        }

        if (current is not { } c)
        {
            if (_searchCurrent is not null) { _overlay.Children.Remove(_searchCurrent); _searchCurrent = null; }
        }
        else
        {
            _searchCurrent ??= AddOverlayPath(Color.FromArgb(120, 255, 193, 7), Color.FromArgb(255, 230, 150, 0));
            _searchCurrent.Data = Group(new[] { c });
        }
    }

    private XamlPath AddOverlayPath(Color fill, Color? stroke)
    {
        var p = new XamlPath { Fill = new SolidColorBrush(fill), IsHitTestVisible = false };
        if (stroke is { } s) { p.Stroke = new SolidColorBrush(s); p.StrokeThickness = 2; }
        _overlay.Children.Add(p);
        return p;
    }

    public string? GetSelectedText()
    {
        if (_selectedTextRuns.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        int? lastLine = null;
        foreach (var r in _selectedTextRuns)
        {
            if (lastLine.HasValue)
            {
                if (r.LineIndex != lastLine.Value) sb.Append('\n');
                else sb.Append(' ');
            }
            sb.Append(r.Text);
            lastLine = r.LineIndex;
        }
        return sb.ToString();
    }
}
