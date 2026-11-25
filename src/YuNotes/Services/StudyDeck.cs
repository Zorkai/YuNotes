using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YuNotes.Models;

namespace YuNotes.Services;

/// <summary>One tape strip to study: the page it is on and the strip itself.</summary>
public sealed record StudyItem(int PageIndex, ShapeElement Tape)
{
    public string Id => Tape.Id;
    public double X => Math.Min(Tape.X1, Tape.X2);
    public double Y => Math.Min(Tape.Y1, Tape.Y2);
    public double W => Math.Abs(Tape.X2 - Tape.X1);
    public double H => Math.Abs(Tape.Y2 - Tape.Y1);
}

/// <summary>
/// Study mode's deck: a note's tape strips in reading order, and which ones
/// the user last got wrong. Results live in settings keyed by note path
/// (like tags), never in the note, so studying doesn't change the file or
/// add undo steps. Only misses are stored: getting a strip right clears it.
/// </summary>
public static class StudyDeck
{
    private static AppSettings S => App.Services.Settings.Current;

    /// <summary>Every tape strip, page by page, top to bottom; strips on the
    /// same line (their middles overlap vertically) left to right.</summary>
    public static List<StudyItem> Order(IReadOnlyList<NotePage> pages)
    {
        var result = new List<StudyItem>();
        for (int p = 0; p < pages.Count; p++)
        {
            var strips = pages[p].Shapes.Where(s => s.Kind == ShapeKind.Tape)
                .Select(s => new StudyItem(p, s))
                .OrderBy(i => i.Y).ToList();
            var rows = new List<List<StudyItem>>();
            foreach (var item in strips)
            {
                double mid = item.Y + item.H / 2;
                var row = rows.LastOrDefault();
                if (row is not null && mid >= row.Min(r => r.Y) && mid <= row.Max(r => r.Y + r.H))
                    row.Add(item);
                else
                    rows.Add(new List<StudyItem> { item });
            }
            foreach (var row in rows) result.AddRange(row.OrderBy(r => r.X));
        }
        return result;
    }

    /// <summary>Missed strips first (in reading order), then the rest.</summary>
    public static List<StudyItem> MissedFirst(List<StudyItem> items, string notePath)
    {
        var missed = Missed(notePath);
        return items.Where(i => missed.Contains(i.Id)).Concat(items.Where(i => !missed.Contains(i.Id))).ToList();
    }

    private static string? KeyFor(string path) =>
        S.StudyMissed.Keys.FirstOrDefault(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase));

    public static HashSet<string> Missed(string notePath) =>
        notePath.Length > 0 && KeyFor(notePath) is { } k ? new HashSet<string>(S.StudyMissed[k]) : new HashSet<string>();

    public static bool IsMissed(string notePath, string tapeId) =>
        notePath.Length > 0 && KeyFor(notePath) is { } k && S.StudyMissed[k].Contains(tapeId);

    /// <summary>Records one answer. Does not save; callers Save() once per session.</summary>
    public static void Record(string notePath, string tapeId, bool gotIt)
    {
        if (string.IsNullOrEmpty(notePath)) return;
        var key = KeyFor(notePath) ?? notePath;
        if (!S.StudyMissed.TryGetValue(key, out var list)) list = S.StudyMissed[key] = new List<string>();
        list.Remove(tapeId);
        if (!gotIt) list.Add(tapeId);
        if (list.Count == 0) S.StudyMissed.Remove(key);
    }

    public static void Clear(string notePath)
    {
        if (KeyFor(notePath) is { } k) S.StudyMissed.Remove(k);
    }

    /// <summary>Forgets misses for strips that no longer exist (deleted tape).</summary>
    public static void Prune(string notePath, IEnumerable<string> liveTapeIds)
    {
        if (KeyFor(notePath) is not { } k) return;
        var live = new HashSet<string>(liveTapeIds);
        S.StudyMissed[k].RemoveAll(id => !live.Contains(id));
        if (S.StudyMissed[k].Count == 0) S.StudyMissed.Remove(k);
    }
}
