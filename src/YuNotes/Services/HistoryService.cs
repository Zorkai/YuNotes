using System;
using System.Collections.Generic;
using System.Linq;
using YuNotes.Models;

namespace YuNotes.Services;

// Document-level undo/redo via snapshots of the editable content
// (strokes, texts, images). Backgrounds + template are not snapshotted —
// they don't change frequently and would blow up memory.
public sealed class HistoryService
{
    private const int MaxStack = 60;
    private readonly Stack<DocSnapshot> _undo = new();
    private readonly Stack<DocSnapshot> _redo = new();
    private DocSnapshot? _current;
    private Document? _doc;

    public event Action? StateChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Bind(Document doc)
    {
        _doc = doc;
        _undo.Clear();
        _redo.Clear();
        _current = DocSnapshot.From(doc);
        StateChanged?.Invoke();
    }

    public void RecordMutation()
    {
        if (_doc is null || _current is null) return;
        _undo.Push(_current);
        if (_undo.Count > MaxStack)
        {
            var tmp = _undo.ToArray();
            _undo.Clear();
            for (int i = tmp.Length - 2; i >= 0; i--) _undo.Push(tmp[i]);
        }
        _redo.Clear();
        _current = DocSnapshot.From(_doc);
        StateChanged?.Invoke();
    }

    public void Undo()
    {
        if (_doc is null || _current is null || _undo.Count == 0) return;
        _redo.Push(_current);
        _current = _undo.Pop();
        _current.ApplyTo(_doc);
        StateChanged?.Invoke();
    }

    public void Redo()
    {
        if (_doc is null || _current is null || _redo.Count == 0) return;
        _undo.Push(_current);
        _current = _redo.Pop();
        _current.ApplyTo(_doc);
        StateChanged?.Invoke();
    }
}

internal sealed class DocSnapshot
{
    private readonly List<PageSnapshot> _pages = new();

    public static DocSnapshot From(Document d)
    {
        var s = new DocSnapshot();
        foreach (var p in d.Pages)
        {
            var ps = new PageSnapshot
            {
                PageId = p.Id,
                Width = p.Width,
                BackgroundLeft = p.BackgroundLeft,
                BackgroundContentWidth = p.BackgroundContentWidth,
            };
            foreach (var st in p.Strokes)
            {
                var clone = new Stroke { Id = st.Id, Kind = st.Kind, Color = st.Color, Width = st.Width };
                clone.Points.AddRange(st.Points);
                ps.Strokes.Add(clone);
            }
            foreach (var sh in p.Shapes)
                ps.Shapes.Add(new ShapeElement
                {
                    Kind = sh.Kind,
                    X1 = sh.X1, Y1 = sh.Y1, X2 = sh.X2, Y2 = sh.Y2, X3 = sh.X3, Y3 = sh.Y3,
                    Color = sh.Color, StrokeWidth = sh.StrokeWidth, Filled = sh.Filled
                });
            foreach (var t in p.Texts)
                ps.Texts.Add(new TextElement
                {
                    Id = t.Id, X = t.X, Y = t.Y, Width = t.Width, Height = t.Height,
                    Rotation = t.Rotation, Text = t.Text, FontSize = t.FontSize, Color = t.Color,
                    FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic
                });
            foreach (var im in p.Images)
                ps.Images.Add(new ImageElement
                {
                    Id = im.Id, X = im.X, Y = im.Y, Width = im.Width, Height = im.Height,
                    Rotation = im.Rotation, PngData = im.PngData
                });
            s._pages.Add(ps);
        }
        return s;
    }

    public void ApplyTo(Document d)
    {
        // Restore by page id; ignores any newly added pages (rare; user can save first).
        foreach (var ps in _pages)
        {
            var p = d.Pages.FirstOrDefault(x => x.Id == ps.PageId);
            if (p is null) continue;

            // Restore page dimensions (changed by extend/reset operations).
            p.Width = ps.Width;
            p.BackgroundLeft = ps.BackgroundLeft;
            p.BackgroundContentWidth = ps.BackgroundContentWidth;

            p.Strokes.Clear();
            foreach (var st in ps.Strokes)
            {
                var clone = new Stroke { Id = st.Id, Kind = st.Kind, Color = st.Color, Width = st.Width };
                clone.Points.AddRange(st.Points);
                p.Strokes.Add(clone);
            }
            p.Shapes.Clear();
            foreach (var sh in ps.Shapes)
                p.Shapes.Add(new ShapeElement
                {
                    Kind = sh.Kind,
                    X1 = sh.X1, Y1 = sh.Y1, X2 = sh.X2, Y2 = sh.Y2, X3 = sh.X3, Y3 = sh.Y3,
                    Color = sh.Color, StrokeWidth = sh.StrokeWidth, Filled = sh.Filled
                });
            p.Texts.Clear();
            foreach (var t in ps.Texts)
                p.Texts.Add(new TextElement
                {
                    Id = t.Id, X = t.X, Y = t.Y, Width = t.Width, Height = t.Height,
                    Rotation = t.Rotation, Text = t.Text, FontSize = t.FontSize, Color = t.Color,
                    FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic
                });
            p.Images.Clear();
            foreach (var im in ps.Images)
                p.Images.Add(new ImageElement
                {
                    Id = im.Id, X = im.X, Y = im.Y, Width = im.Width, Height = im.Height,
                    Rotation = im.Rotation, PngData = im.PngData
                });

            // TextRuns are lazily re-populated from the PDF source; clear them so
            // they are re-extracted with the correct BackgroundLeft offset.
            p.TextRuns.Clear();
        }
    }
}

internal sealed class PageSnapshot
{
    public string PageId = "";
    public double Width;
    public double BackgroundLeft;
    public double BackgroundContentWidth;
    public List<Stroke> Strokes = new();
    public List<ShapeElement> Shapes = new();
    public List<TextElement> Texts = new();
    public List<ImageElement> Images = new();
}
