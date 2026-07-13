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

    // `changedPage`: the page the mutation touched, when the caller knows it.
    // Pages OTHER than it are then shared from the previous snapshot instead
    // of deep-copied — RecordMutation drops from O(document) to O(page), the
    // difference between a per-stroke hitch and nothing on heavily annotated
    // multi-page documents (and the 60-deep undo stack shares page data
    // instead of holding 60 full copies). Pass null when unsure — that's the
    // safe full-copy path.
    public void RecordMutation(NotePage? changedPage = null)
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
        _current = DocSnapshot.From(_doc, _current, changedPage);
        StateChanged?.Invoke();
    }

    // Both return the ids of the pages the step (possibly) changed, so the
    // caller can treat an undo/redo like any other mutation of those pages
    // (dirty the doc, invalidate their flattened images). Empty = no-op.
    public IReadOnlyList<string> Undo()
    {
        if (_doc is null || _current is null || _undo.Count == 0) return Array.Empty<string>();
        _redo.Push(_current);
        var prev = _current;
        _current = _undo.Pop();
        _current.ApplyTo(_doc);
        StateChanged?.Invoke();
        return DocSnapshot.ChangedPageIds(prev, _current);
    }

    public IReadOnlyList<string> Redo()
    {
        if (_doc is null || _current is null || _redo.Count == 0) return Array.Empty<string>();
        _undo.Push(_current);
        var prev = _current;
        _current = _redo.Pop();
        _current.ApplyTo(_doc);
        StateChanged?.Invoke();
        return DocSnapshot.ChangedPageIds(prev, _current);
    }
}

internal sealed class DocSnapshot
{
    private readonly List<PageSnapshot> _pages = new();
    private readonly Dictionary<string, PageSnapshot> _byId = new();

    // Copy-on-write: when `changedPage` is known, every other page's snapshot
    // is shared by reference from `prev`. PageSnapshots are immutable after
    // creation (ApplyTo clones OUT of them), so sharing is safe. A page id
    // missing from `prev` (page added since) is simply built fresh.
    public static DocSnapshot From(Document d, DocSnapshot? prev = null, NotePage? changedPage = null)
    {
        var s = new DocSnapshot();
        foreach (var p in d.Pages)
        {
            if (prev is not null && changedPage is not null && !ReferenceEquals(p, changedPage)
                && prev._byId.TryGetValue(p.Id, out var shared))
            {
                s.Add(shared);
                continue;
            }

            var ps = new PageSnapshot
            {
                PageId = p.Id,
                Width = p.Width,
                BackgroundLeft = p.BackgroundLeft,
                BackgroundContentWidth = p.BackgroundContentWidth,
            };
            foreach (var st in p.Strokes)
            {
                var clone = new Stroke { Id = st.Id, Kind = st.Kind, Color = st.Color, Width = st.Width, PressureMode = st.PressureMode };
                clone.Points.AddRange(st.Points);
                ps.Strokes.Add(clone);
            }
            foreach (var sh in p.Shapes)
                ps.Shapes.Add(new ShapeElement
                {
                    Id = sh.Id, Kind = sh.Kind,
                    X1 = sh.X1, Y1 = sh.Y1, X2 = sh.X2, Y2 = sh.Y2, X3 = sh.X3, Y3 = sh.Y3,
                    Rotation = sh.Rotation,
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
            s.Add(ps);
        }
        return s;
    }

    private void Add(PageSnapshot ps)
    {
        _pages.Add(ps);
        _byId[ps.PageId] = ps;
    }

    // Pages whose snapshot differs between two snapshots. Copy-on-write sharing
    // makes reference identity the test: an untouched page shares the same
    // PageSnapshot object across snapshots, so a mismatch means the page was
    // (possibly) changed. Full-copy snapshots (null-hint mutations) just report
    // every page — conservative, never misses a change.
    internal static List<string> ChangedPageIds(DocSnapshot a, DocSnapshot b)
    {
        var ids = new List<string>();
        foreach (var kv in a._byId)
            if (!b._byId.TryGetValue(kv.Key, out var other) || !ReferenceEquals(kv.Value, other))
                ids.Add(kv.Key);
        foreach (var key in b._byId.Keys)
            if (!a._byId.ContainsKey(key)) ids.Add(key);
        return ids;
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
                var clone = new Stroke { Id = st.Id, Kind = st.Kind, Color = st.Color, Width = st.Width, PressureMode = st.PressureMode };
                clone.Points.AddRange(st.Points);
                p.Strokes.Add(clone);
            }
            p.Shapes.Clear();
            foreach (var sh in ps.Shapes)
                p.Shapes.Add(new ShapeElement
                {
                    Id = sh.Id, Kind = sh.Kind,
                    X1 = sh.X1, Y1 = sh.Y1, X2 = sh.X2, Y2 = sh.Y2, X3 = sh.X3, Y3 = sh.Y3,
                    Rotation = sh.Rotation,
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
