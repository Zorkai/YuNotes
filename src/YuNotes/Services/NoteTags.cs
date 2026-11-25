using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YuNotes.Models;

namespace YuNotes.Services;

// Tags live in settings keyed by file path, like pinned notes, so they work
// for .yunote and imported .pdf notes alike without touching the files. Every
// place that moves or renames a note calls MovePath / MoveFolder so its tags
// (and pin) follow it.
public static class NoteTags
{
    private static AppSettings S => App.Services.Settings.Current;

    // "#Exam prep " → "exam-prep". Empty when nothing usable is left.
    public static string Normalize(string raw)
    {
        var t = raw.Trim().TrimStart('#').Trim().ToLowerInvariant();
        t = string.Join('-', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > 40 ? t[..40] : t;
    }

    // Splits typed input ("exam, #todo  week-3") into normalized tags.
    public static IEnumerable<string> Parse(string input) =>
        input.Split(new[] { ',', ';', ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
             .Select(Normalize)
             .Where(t => t.Length > 0)
             .Distinct();

    private static string? KeyFor(string path) =>
        S.NoteTags.Keys.FirstOrDefault(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Get(string path) =>
        KeyFor(path) is { } k ? S.NoteTags[k] : Array.Empty<string>();

    public static bool Has(string path, string tag) =>
        Get(path).Contains(tag, StringComparer.OrdinalIgnoreCase);

    // Replaces a note's tags. Does not save; callers batch then Save().
    public static void Set(string path, IEnumerable<string> tags)
    {
        var list = tags.Select(Normalize).Where(t => t.Length > 0)
                       .Distinct().OrderBy(t => t, StringComparer.CurrentCulture).ToList();
        if (KeyFor(path) is { } k) S.NoteTags.Remove(k);
        if (list.Count > 0) S.NoteTags[path] = list;
    }

    public static void Remove(string path)
    {
        if (KeyFor(path) is { } k) S.NoteTags.Remove(k);
    }

    // Tag → number of notes carrying it, limited to notes that still exist.
    public static SortedDictionary<string, int> Counts(Func<string, bool>? include = null)
    {
        var result = new SortedDictionary<string, int>(StringComparer.CurrentCulture);
        foreach (var (path, tags) in S.NoteTags)
        {
            if (include is not null && !include(path)) continue;
            foreach (var t in tags) result[t] = result.TryGetValue(t, out var n) ? n + 1 : 1;
        }
        return result;
    }

    // A note moved or was renamed: carry its tags and pin over. Does not save.
    public static void MovePath(string oldPath, string newPath)
    {
        if (KeyFor(oldPath) is { } k)
        {
            var tags = S.NoteTags[k];
            S.NoteTags.Remove(k);
            S.NoteTags[newPath] = tags;
        }
        for (int i = 0; i < S.PinnedNotes.Count; i++)
            if (string.Equals(S.PinnedNotes[i], oldPath, StringComparison.OrdinalIgnoreCase))
                S.PinnedNotes[i] = newPath;
        foreach (var vk in S.NoteViews.Keys.Where(k => string.Equals(k, oldPath, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var view = S.NoteViews[vk];
            S.NoteViews.Remove(vk);
            S.NoteViews[newPath] = view;
        }
        foreach (var sk in S.StudyMissed.Keys.Where(k => string.Equals(k, oldPath, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var ids = S.StudyMissed[sk];
            S.StudyMissed.Remove(sk);
            S.StudyMissed[newPath] = ids;
        }
    }

    // A folder moved or was renamed: re-key every note under it. Does not save.
    public static void MoveFolder(string oldDir, string newDir)
    {
        string prefix = oldDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string Rebase(string p) => Path.Combine(newDir, p[prefix.Length..]);
        foreach (var k in S.NoteTags.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var tags = S.NoteTags[k];
            S.NoteTags.Remove(k);
            S.NoteTags[Rebase(k)] = tags;
        }
        for (int i = 0; i < S.PinnedNotes.Count; i++)
            if (S.PinnedNotes[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                S.PinnedNotes[i] = Rebase(S.PinnedNotes[i]);
        foreach (var k in S.NoteViews.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var view = S.NoteViews[k];
            S.NoteViews.Remove(k);
            S.NoteViews[Rebase(k)] = view;
        }
        foreach (var k in S.StudyMissed.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var ids = S.StudyMissed[k];
            S.StudyMissed.Remove(k);
            S.StudyMissed[Rebase(k)] = ids;
        }
    }
}
