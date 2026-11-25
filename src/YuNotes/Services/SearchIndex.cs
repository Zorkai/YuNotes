using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.Input.Inking;
using YuNotes.Models;

namespace YuNotes.Services;

// ─── Searchable text on a page ────────────────────────────────────────────────

public enum TextSource { Handwriting = 0, Pdf = 1, Typed = 2 }

// One searchable word (or typed text box) with its position in page coordinates.
// For handwriting, T is the recognizer's best guess and A holds the runner-up
// readings, so "c0ntinuous" still finds "continuous" if it was a close second.
public sealed class TextBox
{
    public string T { get; set; } = "";
    public string[]? A { get; set; }
    public TextSource K { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }

    public Rect Rect => new(X, Y, W, H);
}

public sealed class PageText
{
    public int Index { get; set; }
    // Which note page this is and what its ink looked like when it was read
    // (SearchIndex.InkSignature): a page whose ink hasn't changed reuses its
    // handwriting words instead of being read again.
    public string PageId { get; set; } = "";
    public string InkSig { get; set; } = "";
    public List<TextBox> Boxes { get; set; } = new();
}

public sealed record PageMatch(int PageIndex, List<TextBox> Boxes, string Snippet, TextSource Source);

// ─── Handwriting recognition ──────────────────────────────────────────────────

// Wraps Windows' built-in handwriting recognizer (the same one the touch
// keyboard's handwriting panel uses). Works offline; needs a handwriting
// language installed, which English Windows has by default.
//
// The recognizer is native code that can bring the whole app down if its
// objects are used or released from different threads, so every ink object
// lives on one dedicated thread: callers queue work and await the result.
// Pages go in batches of at most 120 strokes (huge containers make it fail),
// and a recognizer that failed is replaced rather than reused. Native objects
// are released explicitly on that same thread, never left to the finalizer.
public static class HandwritingRecognizer
{
    private const int MaxStrokesPerBatch = 120;

    // Two lanes: work the user is waiting on (find in note, convert to text)
    // is taken before background indexing, which goes one page at a time, so
    // it never waits behind more than a single page of the index.
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> s_work = new();
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> s_background = new();
    private static readonly System.Collections.Concurrent.BlockingCollection<Action>[] s_lanes = { s_work, s_background };
    private static InkRecognizerContainer? s_container;
    private static readonly Task<bool> s_availableTask;
    private static int s_skipped;
    private static Exception? s_lastError;

    static HandwritingRecognizer()
    {
        var t = new Thread(() =>
        {
            // TakeFromAny checks the lanes in order: s_work first.
            while (true)
            {
                System.Collections.Concurrent.BlockingCollection<Action>.TakeFromAny(s_lanes, out var job);
                job();
            }
        })
        {
            IsBackground = true,
            Name = "Handwriting recognizer",
        };
        t.Start();
        // First job on the thread, so asking IsAvailable never waits behind a page.
        s_availableTask = Run(() =>
        {
            try
            {
                s_container = new InkRecognizerContainer();
                return s_container.GetRecognizers().Count > 0;
            }
            catch { return false; }
        });
    }

    private static Task<T> Run<T>(Func<T> work, bool background = false)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        (background ? s_background : s_work).Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public static bool IsAvailable => s_availableTask.GetAwaiter().GetResult();

    // Installed handwriting languages, by recognizer name
    // ("Microsoft English (US) Handwriting Recognizer").
    public static Task<List<string>> GetLanguagesAsync() => Run(() =>
    {
        var names = new List<string>();
        try
        {
            s_container ??= new InkRecognizerContainer();
            foreach (var r in s_container.GetRecognizers()) { names.Add(r.Name); Release(r); }
        }
        catch { }
        return names;
    });

    // "Microsoft English (US) Handwriting Recognizer" → "English (US)".
    public static string ShortName(string recognizer) =>
        recognizer.Replace("Microsoft ", "").Replace(" Handwriting Recognizer", "").Trim();

    // Applies the language chosen in Settings (empty = Windows' default) to
    // the current recognizer. Runs on the recognizer thread.
    private static object? s_langContainer;
    private static string? s_langApplied;
    private static void ApplyLanguage()
    {
        var want = App.Services.Settings.Current.HandwritingLanguage ?? "";
        if (s_container is null || (ReferenceEquals(s_langContainer, s_container) && s_langApplied == want)) return;
        s_langContainer = s_container;
        s_langApplied = want;
        if (want.Length == 0) return;
        try
        {
            foreach (var r in s_container.GetRecognizers())
            {
                if (r.Name == want) { s_container.SetDefaultRecognizer(r); }
                else Release(r);
            }
        }
        catch { }
    }

    // Recognizes the pen strokes of one page. Highlighter strokes and dots are
    // skipped. Returns one box per recognized word.
    // background: queue behind anything the user is waiting on (indexing).
    public static Task<List<TextBox>> RecognizeAsync(IReadOnlyList<Stroke> strokes, bool background = false)
    {
        if (!IsAvailable) return Task.FromResult(new List<TextBox>());
        // Copy now: the caller's page can change while the work waits its turn.
        var pen = strokes.Where(s => s.Kind == StrokeKind.Pen && s.Points.Count >= 2)
                         .Select(s => s.Points.ToArray()).ToList();
        if (pen.Count == 0) return Task.FromResult(new List<TextBox>());

        // Strokes stay in the order they were written: the recognizer relies on
        // it, and batches/splits then still keep lines of writing together.
        // Writing done while zoomed in can be tiny in page units (a line a dozen
        // units tall), which the recognizer rejects. Scale it so a typical
        // stroke is about 28 units tall, like normal handwriting.
        var heights = pen.Select(s => s.Max(p => p.Y) - s.Min(p => p.Y))
                         .Where(h => h > 0.5f).OrderBy(h => h).ToList();
        float median = heights.Count > 0 ? heights[heights.Count / 2] : 28f;
        float scale = Math.Clamp(28f / Math.Max(median, 0.5f), 1f, 12f);

        return Run(() =>
        {
            var result = new List<TextBox>();
            s_skipped = 0; s_lastError = null;
            for (int i = 0; i < pen.Count; i += MaxStrokesPerBatch)
                RecognizeChunk(pen.GetRange(i, Math.Min(MaxStrokesPerBatch, pen.Count - i)), scale, result);
            if (s_lastError is not null)
                App.LogError(s_lastError, $"Handwriting recognition skipped {s_skipped} of {pen.Count} strokes on a page");
            return result;
        }, background);
    }

    // Drops our reference to a native object right here, on the recognizer
    // thread, instead of later from the garbage collector's thread.
    private static void Release(object? o)
    {
        if (o is WinRT.IWinRTObject w)
            try { w.NativeObject.Dispose(); } catch { }
    }

    // Runs on the recognizer thread. If a batch fails, the recognizer is
    // replaced and the batch is split in two and retried, down to small groups;
    // only a group that still fails is skipped.
    private static void RecognizeChunk(List<YuNotes.Models.InkPoint[]> pen, float scale, List<TextBox> result)
    {
        InkStrokeContainer? ink = null;
        InkStrokeBuilder? builder = null;
        var natives = new List<object>();
        Exception? failed = null;
        try
        {
            // Recognize in a local frame starting near (0,0): ink on an extended
            // page can sit at negative or very large coordinates, which the
            // recognizer rejects. Results are shifted back afterwards.
            float ox = pen.Min(s => s.Min(p => p.X)) - 10;
            float oy = pen.Min(s => s.Min(p => p.Y)) - 10;
            ink = new InkStrokeContainer();
            builder = new InkStrokeBuilder();
            foreach (var s in pen)
            {
                var pts = s.Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y))
                    .Select(p => new Windows.UI.Input.Inking.InkPoint(
                        new Point((p.X - ox) * scale, (p.Y - oy) * scale),
                        Math.Clamp(p.Pressure <= 0 || !float.IsFinite(p.Pressure) ? 0.5f : p.Pressure, 0.01f, 1f)))
                    .ToList();
                if (pts.Count < 2) continue;
                try
                {
                    var stroke = builder.CreateStrokeFromInkPoints(pts, Matrix3x2.Identity);
                    natives.Add(stroke);
                    ink.AddStroke(stroke);
                }
                catch { }
            }
            s_container ??= new InkRecognizerContainer();
            ApplyLanguage();
            var words = s_container.RecognizeAsync(ink, InkRecognitionTarget.All).AsTask().GetAwaiter().GetResult();
            foreach (var w in words)
            {
                natives.Add(w);
                var cands = w.GetTextCandidates();
                if (cands.Count == 0 || string.IsNullOrWhiteSpace(cands[0])) continue;
                var r = w.BoundingRect;
                result.Add(new TextBox
                {
                    T = cands[0],
                    A = cands.Skip(1).Take(4).Where(c => !string.IsNullOrWhiteSpace(c)).ToArray(),
                    K = TextSource.Handwriting,
                    X = (float)r.X / scale + ox, Y = (float)r.Y / scale + oy, W = (float)r.Width / scale, H = (float)r.Height / scale,
                });
            }
        }
        catch (Exception ex)
        {
            failed = ex;
            // A failed call can leave the recognizer unusable for every later
            // call: release it and start over with a fresh one.
            Release(s_container);
            try { s_container = new InkRecognizerContainer(); } catch { s_container = null; }
        }
        finally
        {
            foreach (var n in natives) Release(n);
            Release(ink);
            Release(builder);
        }

        if (failed is not null)
        {
            var ex = failed;
            if (pen.Count < 8)
            {
                s_skipped += pen.Count;
                s_lastError = ex;
                return;
            }
            int half = pen.Count / 2;
            RecognizeChunk(pen.GetRange(0, half), scale, result);
            RecognizeChunk(pen.GetRange(half, pen.Count - half), scale, result);
        }
    }
}

// ─── Matching ─────────────────────────────────────────────────────────────────

public static class TextMatcher
{
    // Lower case, accents removed ("Lösung" → "losung"), so search is forgiving.
    public static string Normalize(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Replace("ß", "ss");
    }

    public static string[] Terms(string query) =>
        Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool BoxHas(TextBox b, string term) =>
        Normalize(b.T).Contains(term) || (b.A?.Any(a => Normalize(a).Contains(term)) ?? false);

    // A page matches when every term appears somewhere on it. Returns the boxes
    // that contain a term, in reading order, plus a snippet around the first.
    public static PageMatch? Match(PageText page, string[] terms)
    {
        if (terms.Length == 0 || page.Boxes.Count == 0) return null;
        var hits = new List<TextBox>();
        foreach (var term in terms)
        {
            bool any = false;
            foreach (var b in page.Boxes)
                if (BoxHas(b, term)) { any = true; if (!hits.Contains(b)) hits.Add(b); }
            if (!any) return null;
        }
        var ordered = ReadingOrder(page.Boxes);
        hits = hits.OrderBy(h => ordered.IndexOf(h)).ToList();
        var first = hits[0];
        return new PageMatch(page.Index, hits, Snippet(ordered, first), first.K);
    }

    // Rows of roughly equal height, top to bottom, then left to right.
    public static List<TextBox> ReadingOrder(List<TextBox> boxes)
    {
        if (boxes.Count == 0) return new();
        float lineH = Math.Max(8f, boxes.Select(b => b.H).OrderBy(h => h).ElementAt(boxes.Count / 2) * 0.7f);
        return boxes.OrderBy(b => (int)((b.Y + b.H / 2) / lineH)).ThenBy(b => b.X).ToList();
    }

    private static string Snippet(List<TextBox> ordered, TextBox hit)
    {
        int i = ordered.IndexOf(hit);
        int from = Math.Max(0, i - 5), to = Math.Min(ordered.Count, i + 7);
        var text = string.Join(" ", ordered.Skip(from).Take(to - from).Select(b => b.T.Trim()));
        return (from > 0 ? "… " : "") + text + (to < ordered.Count ? " …" : "");
    }
}

// ─── Index of every note's text ───────────────────────────────────────────────

// Per-note record of searchable text, cached on disk and rebuilt when the file
// changes (size or write time).
public sealed class NoteIndexEntry
{
    public int Version { get; set; }
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public DateTime WriteUtc { get; set; }
    public string Title { get; set; } = "";
    // Handwriting language the note was read with ("" = Windows default).
    public string Language { get; set; } = "";
    // False while a note is still being read: saved every few pages so a big
    // note's progress survives closing YuNotes, but not yet up to date.
    public bool Complete { get; set; }
    public List<PageText> Pages { get; set; } = new();
}

public sealed record NoteSearchHit(string Path, string Title, PageMatch Match);

public sealed class SearchIndex
{
    // Bump when indexing changes so existing caches are rebuilt.
    private const int FormatVersion = 9;   // 9: per-page ink signatures, partial progress
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, NoteIndexEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private bool _loaded;
    // Crash guard: the note whose handwriting is being read is written to
    // MarkerFile first. If the app dies there, the next start finds the marker
    // and adds that note to SkipFile, so it isn't read again (no crash loop).
    private readonly HashSet<string> _skipInk = new(StringComparer.OrdinalIgnoreCase);
    private string MarkerFile => System.IO.Path.Combine(_dir, "reading-handwriting.txt");
    private string SkipFile => System.IO.Path.Combine(_dir, "skip-handwriting.txt");

    // Raised (on a background thread) after each note is (re)indexed.
    public event Action? Updated;

    public int Pending { get; private set; }
    public bool HandwritingAvailable => HandwritingRecognizer.IsAvailable;

    public SearchIndex()
    {
        _dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "YuNotes", "search-index");
    }

    private string CacheFile(string path)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));
        return System.IO.Path.Combine(_dir, hash + ".json");
    }

    private void LoadCache()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(MarkerFile))
            {
                var crashed = File.ReadAllText(MarkerFile).Trim();
                if (crashed.Length > 0)
                {
                    File.AppendAllText(SkipFile, crashed + Environment.NewLine);
                    App.LogError(new Exception("YuNotes closed while reading this note's handwriting"),
                                 $"Handwriting search now skips {crashed}");
                }
                File.Delete(MarkerFile);
            }
            if (File.Exists(SkipFile))
                foreach (var line in File.ReadAllLines(SkipFile))
                    if (line.Trim().Length > 0) _skipInk.Add(line.Trim());
        }
        catch { }
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var f in Directory.GetFiles(_dir, "*.json"))
            {
                try
                {
                    var e = JsonSerializer.Deserialize<NoteIndexEntry>(File.ReadAllText(f));
                    if (e is { Version: FormatVersion } && e.Path.Length > 0) _entries[e.Path] = e;
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsCurrent(NoteIndexEntry e, FileInfo fi) =>
        e.Complete && e.Version == FormatVersion && e.Length == fi.Length && e.WriteUtc == fi.LastWriteTimeUtc
        && (e.Language ?? "") == (App.Services.Settings.Current.HandwritingLanguage ?? "");

    // Brings the index up to date for these notes, one at a time in the
    // background. Notes that haven't changed since they were indexed are skipped.
    public async Task EnsureIndexedAsync(IEnumerable<string> paths)
    {
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _runGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(LoadCache).ConfigureAwait(false);
            var todo = list.Where(p =>
            {
                var fi = new FileInfo(p);
                return fi.Exists && !(_entries.TryGetValue(p, out var e) && IsCurrent(e, fi));
            }).ToList();
            // Forget notes that no longer exist.
            foreach (var gone in _entries.Keys.Where(k => !File.Exists(k)).ToList())
            {
                _entries.TryRemove(gone, out _);
                try { File.Delete(CacheFile(gone)); } catch { }
            }
            Pending = todo.Count;
            foreach (var p in todo)
            {
                try
                {
                    bool readInk = !_skipInk.Contains(p);
                    if (readInk) { Directory.CreateDirectory(_dir); File.WriteAllText(MarkerFile, p); }
                    _entries.TryGetValue(p, out var previous);
                    var entry = await Task.Run(() => BuildAsync(p, readInk, previous, SavePartial)).ConfigureAwait(false);
                    if (readInk) File.Delete(MarkerFile);
                    if (entry is not null)
                    {
                        _entries[p] = entry;
                        Directory.CreateDirectory(_dir);
                        await File.WriteAllTextAsync(CacheFile(p), JsonSerializer.Serialize(entry)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { App.LogError(ex, $"Indexing failed: {p}"); }
                Pending--;
                Updated?.Invoke();
            }
        }
        finally
        {
            Pending = 0;
            _runGate.Release();
        }
    }

    // Every SaveEvery newly read pages, a big note's progress is written out
    // (marked not Complete), so the next run picks up from there.
    private const int SaveEvery = 20;

    private void SavePartial(NoteIndexEntry partial)
    {
        try
        {
            _entries[partial.Path] = partial;
            Directory.CreateDirectory(_dir);
            File.WriteAllText(CacheFile(partial.Path), JsonSerializer.Serialize(partial));
        }
        catch { }
    }

    // A short fingerprint of a page's ink: changes when a stroke is added,
    // removed, moved, reshaped or recolored to/from highlighter.
    public static string InkSignature(NotePage p)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            void Mix(long v) { h ^= (ulong)v; h *= 1099511628211UL; }
            foreach (var s in p.Strokes)
            {
                // Stable across runs (string.GetHashCode is randomized per process,
                // and these signatures are saved to disk).
                foreach (char c in s.Id) Mix(c);
                Mix((int)s.Kind);
                Mix(s.Points.Count);
                if (s.Points.Count == 0) continue;
                var a = s.Points[0];
                var b = s.Points[^1];
                Mix(BitConverter.SingleToInt32Bits(a.X)); Mix(BitConverter.SingleToInt32Bits(a.Y));
                Mix(BitConverter.SingleToInt32Bits(b.X)); Mix(BitConverter.SingleToInt32Bits(b.Y));
            }
            return $"{p.Strokes.Count}:{h:x16}";
        }
    }

    // Handwriting words already read for a note's pages, by page id, with the
    // ink signature they were read for. Lets the editor's find-in-note start
    // from what the index has instead of reading every page again.
    public Dictionary<string, (string Sig, List<TextBox> Words)> HandwritingFor(string path)
    {
        var result = new Dictionary<string, (string, List<TextBox>)>();
        if (!_entries.TryGetValue(path, out var e)) return result;
        foreach (var page in e.Pages)
            if (page.PageId.Length > 0 && page.InkSig.Length > 0)
                result[page.PageId] = (page.InkSig, page.Boxes.Where(b => b.K == TextSource.Handwriting).ToList());
        return result;
    }

    private static async Task<NoteIndexEntry?> BuildAsync(string path, bool readInk, NoteIndexEntry? previous,
                                                          Action<NoteIndexEntry> savePartial)
    {
        var fi = new FileInfo(path);
        var (doc, srcPdf) = App.Services.Documents.OpenForIndex(path, App.Services.PdfContainer);
        var pdfPages = srcPdf is null ? new List<List<TextRun>>() : App.Services.PdfText.Extract(srcPdf);
        var entry = new NoteIndexEntry
        {
            Version = FormatVersion,
            Path = path,
            Length = fi.Length,
            WriteUtc = fi.LastWriteTimeUtc,
            Language = App.Services.Settings.Current.HandwritingLanguage ?? "",
            Title = doc?.Info.Title is { Length: > 0 } t ? t : System.IO.Path.GetFileNameWithoutExtension(path),
            Complete = true,
        };

        if (doc is null)
        {
            // Plain PDF with no YuNotes data: one page per PDF page, text only.
            for (int i = 0; i < pdfPages.Count; i++)
                entry.Pages.Add(new PageText { Index = i, Boxes = PdfBoxes(pdfPages[i], 0) });
            return entry;
        }

        // Handwriting already read for pages whose ink hasn't changed since.
        var known = new Dictionary<string, PageText>();
        if (previous?.Version == FormatVersion && (previous.Language ?? "") == entry.Language)
            foreach (var old in previous.Pages)
                if (old.PageId.Length > 0) known[old.PageId] = old;

        int readSinceSave = 0;
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            var p = doc.Pages[i];
            var page = new PageText { Index = i, PageId = p.Id };
            if (readInk)
            {
                var sig = InkSignature(p);
                if (known.TryGetValue(p.Id, out var old) && old.InkSig == sig)
                    page.Boxes.AddRange(old.Boxes.Where(b => b.K == TextSource.Handwriting));
                else
                {
                    // One page per job, in the background lane.
                    page.Boxes.AddRange(await HandwritingRecognizer.RecognizeAsync(p.Strokes, background: true).ConfigureAwait(false));
                    readSinceSave++;
                }
                page.InkSig = sig;
            }
            if (p.SourcePageIndex is int si && si >= 0 && si < pdfPages.Count)
                page.Boxes.AddRange(PdfBoxes(pdfPages[si], p.BackgroundLeft, p));
            page.Boxes.AddRange(TypedBoxes(p.Texts));
            if (BookmarkBox(p) is { } bm) page.Boxes.Add(bm);
            entry.Pages.Add(page);

            if (readSinceSave >= SaveEvery && i < doc.Pages.Count - 1)
            {
                readSinceSave = 0;
                // Pages read so far plus what is known for the rest; not
                // Complete, so the note is read again (from here) next time.
                var partial = new NoteIndexEntry
                {
                    Version = entry.Version, Path = entry.Path, Length = entry.Length, WriteUtc = entry.WriteUtc,
                    Language = entry.Language, Title = entry.Title, Complete = false,
                    Pages = entry.Pages.Concat(known.Values.Where(k => !entry.Pages.Any(e => e.PageId == k.PageId))).ToList(),
                };
                savePartial(partial);
            }
        }
        return entry;
    }

    // `page`: place the words where the page shows them, below any space
    // inserted above them.
    public static List<TextBox> PdfBoxes(IEnumerable<TextRun> runs, double shiftX, NotePage? page = null) =>
        runs.Select(r => new TextBox
        {
            T = r.Text, K = TextSource.Pdf,
            X = (float)(r.X + shiftX),
            Y = (float)(page is not null && page.Gaps.Count > 0 ? PageGaps.ToPage(page, r.Y) : r.Y),
            W = (float)r.Width, H = (float)r.Height,
        }).ToList();

    // A page's bookmark label is searchable too; its match marks the page's
    // top-left corner.
    public static TextBox? BookmarkBox(NotePage p) =>
        string.IsNullOrWhiteSpace(p.Bookmark) ? null : new TextBox
        {
            T = p.Bookmark!, K = TextSource.Typed,
            X = 8, Y = 8, W = (float)Math.Min(240, p.Width - 16), H = 36,
        };

    public static List<TextBox> TypedBoxes(IEnumerable<TextElement> texts) =>
        texts.Where(t => !string.IsNullOrWhiteSpace(t.Text)).Select(t => new TextBox
        {
            T = t.Text, K = TextSource.Typed,
            X = (float)t.X, Y = (float)t.Y, W = (float)t.Width, H = (float)t.Height,
        }).ToList();

    // Every matching page across indexed notes, best first: notes whose title
    // matches, then by number of hits.
    public List<NoteSearchHit> Search(string query, int max = 60)
    {
        var terms = TextMatcher.Terms(query);
        if (terms.Length == 0) return new();
        var hits = new List<NoteSearchHit>();
        foreach (var e in _entries.Values)
        {
            if (!File.Exists(e.Path)) continue;
            foreach (var page in e.Pages)
                if (TextMatcher.Match(page, terms) is { } m) hits.Add(new NoteSearchHit(e.Path, e.Title, m));
        }
        return hits
            .OrderByDescending(h => terms.All(t => TextMatcher.Normalize(h.Title).Contains(t)))
            .ThenByDescending(h => h.Match.Boxes.Count)
            .Take(max).ToList();
    }
}
