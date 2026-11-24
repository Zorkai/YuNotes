using System;
using System.Collections.Generic;
using System.Linq;
using YuNotes.Models;

namespace YuNotes.Services;

// Document-level undo/redo via snapshots of the editable content
// (strokes, texts, images), page templates (document template and each page's
// override — a few fields, so cheap) and the page list itself (order + enough
// per-page state to recreate a page), so adding pages can be undone/redone.
// Page background bytes are immutable and shared by reference, never copied.
public sealed class HistoryService
{
    private const int MaxStack = 60;
    private Stack<DocSnapshot> _undo = new();
    private Stack<DocSnapshot> _redo = new();
    private DocSnapshot? _current;
    private Document? _doc;

    public event Action? StateChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // True when the last Undo/Redo added, removed or reordered pages — the
    // caller must then rebuild the page views, not just repaint them.
    public bool LastStepChangedPageSet { get; private set; }

    // Undo stacks of notes open in other tabs, parked while another note is
    // being edited so switching tabs keeps each note's own history.
    private sealed record Parked(Stack<DocSnapshot> Undo, Stack<DocSnapshot> Redo, DocSnapshot? Current);
    private readonly Dictionary<Document, Parked> _parked = new(ReferenceEqualityComparer.Instance);

    // Makes `doc` the active document, picking up its parked history if it had
    // one (tab switch), otherwise starting fresh like Bind.
    public void Switch(Document doc)
    {
        if (ReferenceEquals(_doc, doc)) { StateChanged?.Invoke(); return; }
        if (_doc is not null) _parked[_doc] = new Parked(_undo, _redo, _current);
        if (_parked.Remove(doc, out var p))
        {
            _doc = doc;
            _undo = p.Undo;
            _redo = p.Redo;
            _current = p.Current ?? DocSnapshot.From(doc);
            StateChanged?.Invoke();
        }
        else
        {
            _undo = new();
            _redo = new();
            Bind(doc);
        }
    }

    // Drops a closed tab's history (and the active one's, if it is that doc).
    public void Forget(Document doc)
    {
        _parked.Remove(doc);
        if (ReferenceEquals(_doc, doc))
        {
            _doc = null;
            _current = null;
            _undo = new();
            _redo = new();
            StateChanged?.Invoke();
        }
    }

    public void Bind(Document doc)
    {
        _parked.Remove(doc);
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
        Push(DocSnapshot.From(_doc, _current, changedPage));
    }

    // For adding, deleting or reordering pages: no page's CONTENT changed, so
    // every page already in the previous snapshot is shared and only newly
    // added pages are captured — O(new pages), not O(document).
    public void RecordPageListChange()
    {
        if (_doc is null || _current is null) return;
        Push(DocSnapshot.From(_doc, _current, changedPage: null, shareExisting: true));
    }

    private void Push(DocSnapshot next)
    {
        _undo.Push(_current!);
        if (_undo.Count > MaxStack)
        {
            var tmp = _undo.ToArray();
            _undo.Clear();
            for (int i = tmp.Length - 2; i >= 0; i--) _undo.Push(tmp[i]);
        }
        _redo.Clear();
        _current = next;
        StateChanged?.Invoke();
    }

    // Both return the ids of the pages the step (possibly) changed, so the
    // caller can treat an undo/redo like any other mutation of those pages
    // (dirty the doc, invalidate their flattened images). Empty = no-op.
    public IReadOnlyList<string> Undo()
    {
        LastStepChangedPageSet = false;
        if (_doc is null || _current is null || _undo.Count == 0) return Array.Empty<string>();
        _redo.Push(_current);
        var prev = _current;
        _current = _undo.Pop();
        LastStepChangedPageSet = _current.ApplyTo(_doc);
        StateChanged?.Invoke();
        return DocSnapshot.ChangedPageIds(prev, _current);
    }

    public IReadOnlyList<string> Redo()
    {
        LastStepChangedPageSet = false;
        if (_doc is null || _current is null || _redo.Count == 0) return Array.Empty<string>();
        _undo.Push(_current);
        var prev = _current;
        _current = _redo.Pop();
        LastStepChangedPageSet = _current.ApplyTo(_doc);
        StateChanged?.Invoke();
        return DocSnapshot.ChangedPageIds(prev, _current);
    }
}

internal sealed class DocSnapshot
{
    private readonly List<PageSnapshot> _pages = new();
    private readonly Dictionary<string, PageSnapshot> _byId = new();
    // Document-wide template, always captured fresh (it's tiny), so undoing
    // "Apply to all pages" restores it.
    private TemplateSettings _docTemplate = new();

    internal static TemplateSettings? CloneTemplate(TemplateSettings? t) =>
        t is null ? null : new TemplateSettings
        {
            Kind = t.Kind, Spacing = t.Spacing,
            LineColorHex = t.LineColorHex, LineThickness = t.LineThickness,
            SkipImportedPdf = t.SkipImportedPdf
        };

    // Copy-on-write: when `changedPage` is known, every other page's snapshot
    // is shared by reference from `prev`. PageSnapshots are immutable after
    // creation (ApplyTo clones OUT of them), so sharing is safe. A page id
    // missing from `prev` (page added since) is simply built fresh.
    // `shareExisting`: share EVERY page present in `prev` (page-list changes,
    // where no page's content changed).
    public static DocSnapshot From(Document d, DocSnapshot? prev = null, NotePage? changedPage = null,
                                   bool shareExisting = false)
    {
        var s = new DocSnapshot { _docTemplate = CloneTemplate(d.Template)! };
        foreach (var p in d.Pages)
        {
            if (prev is not null
                && (shareExisting || (changedPage is not null && !ReferenceEquals(p, changedPage)))
                && prev._byId.TryGetValue(p.Id, out var shared))
            {
                s.Add(shared);
                continue;
            }

            var ps = new PageSnapshot
            {
                PageId = p.Id,
                Width = p.Width,
                Height = p.Height,
                BackgroundPng = p.BackgroundPng,       // immutable bytes — shared
                SourcePageIndex = p.SourcePageIndex,
                BackgroundLeft = p.BackgroundLeft,
                BackgroundContentWidth = p.BackgroundContentWidth,
                TemplateOverride = CloneTemplate(p.TemplateOverride),
                Bookmark = p.Bookmark,
                Gaps = PageGaps.Clone(p.Gaps),
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

    // Restores the snapshot onto the live document, including its page list:
    // pages missing from the document (e.g. a page whose "add" was undone, now
    // being redone) are recreated, pages not in the snapshot (an added page
    // being undone) are dropped, and the order is restored. Live NotePage
    // objects are reused by id so views and references to them stay valid.
    // Returns true when the page list changed (pages added/removed/reordered).
    public bool ApplyTo(Document d)
    {
        // Copy fields into the live object rather than replacing it, so anything
        // holding a reference to Document.Template stays valid.
        d.Template.Kind = _docTemplate.Kind;
        d.Template.Spacing = _docTemplate.Spacing;
        d.Template.LineColorHex = _docTemplate.LineColorHex;
        d.Template.LineThickness = _docTemplate.LineThickness;
        d.Template.SkipImportedPdf = _docTemplate.SkipImportedPdf;

        var live = new Dictionary<string, NotePage>();
        foreach (var lp in d.Pages) live[lp.Id] = lp;
        var ordered = new List<NotePage>(_pages.Count);

        foreach (var ps in _pages)
        {
            if (!live.TryGetValue(ps.PageId, out var p))
                p = new NotePage { Id = ps.PageId };
            ordered.Add(p);

            // Restore page dimensions (changed by extend/reset operations).
            p.Width = ps.Width;
            p.Height = ps.Height;
            p.BackgroundPng = ps.BackgroundPng;
            p.SourcePageIndex = ps.SourcePageIndex;
            p.BackgroundLeft = ps.BackgroundLeft;
            p.BackgroundContentWidth = ps.BackgroundContentWidth;
            p.TemplateOverride = CloneTemplate(ps.TemplateOverride);
            p.Bookmark = ps.Bookmark;
            p.Gaps.Clear();
            p.Gaps.AddRange(PageGaps.Clone(ps.Gaps));

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

        bool pageSetChanged = ordered.Count != d.Pages.Count;
        for (int i = 0; !pageSetChanged && i < ordered.Count; i++)
            pageSetChanged = !ReferenceEquals(ordered[i], d.Pages[i]);
        if (pageSetChanged)
        {
            d.Pages.Clear();
            foreach (var p in ordered) d.Pages.Add(p);
        }
        for (int i = 0; i < d.Pages.Count; i++) d.Pages[i].Index = i;
        return pageSetChanged;
    }
}

internal sealed class PageSnapshot
{
    public string PageId = "";
    public double Width;
    public double Height;
    public byte[]? BackgroundPng;
    public int? SourcePageIndex;
    public double BackgroundLeft;
    public double BackgroundContentWidth;
    public TemplateSettings? TemplateOverride;
    public string? Bookmark;
    public List<PageGap> Gaps = new();
    public List<Stroke> Strokes = new();
    public List<ShapeElement> Shapes = new();
    public List<TextElement> Texts = new();
    public List<ImageElement> Images = new();
}
