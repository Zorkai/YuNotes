using System;
using System.Collections.Generic;
using System.Linq;

namespace YuNotes.Models;

public enum SmartNoteKind { Any, Handwritten, ImportedPdf }

// A saved search shown in the Home sidebar: every note (in any folder) that
// matches all of its conditions. Empty conditions match everything.
public sealed class SmartFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    // Tags (lower case, no '#'). MatchAllTags: the note needs every tag;
    // otherwise any one of them is enough.
    public List<string> Tags { get; set; } = new();
    public bool MatchAllTags { get; set; }
    // Edited in the last N days; 0 = any time.
    public int EditedWithinDays { get; set; }
    public SmartNoteKind Kind { get; set; } = SmartNoteKind.Any;
    // Words that must all appear in the title (case-insensitive); "" = any.
    public string TitleContains { get; set; } = "";
    public bool PinnedOnly { get; set; }

    public bool Matches(string title, DateTime modifiedUtc, bool isPdf, bool isPinned, IReadOnlyCollection<string> tags)
    {
        if (Tags.Count > 0)
        {
            bool Has(string t) => tags.Contains(t, StringComparer.OrdinalIgnoreCase);
            if (MatchAllTags ? !Tags.All(Has) : !Tags.Any(Has)) return false;
        }
        // "Last N days" counts calendar days, so "Today" (1) means since local midnight.
        if (EditedWithinDays > 0)
        {
            // Same convention as the card dates: ModifiedAt is UTC, shown local.
            if (modifiedUtc.ToLocalTime() < DateTime.Today.AddDays(-(EditedWithinDays - 1))) return false;
        }
        if (Kind == SmartNoteKind.ImportedPdf && !isPdf) return false;
        if (Kind == SmartNoteKind.Handwritten && isPdf) return false;
        if (PinnedOnly && !isPinned) return false;
        foreach (var word in (TitleContains ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!title.Contains(word, StringComparison.CurrentCultureIgnoreCase)) return false;
        return true;
    }

    // "#exam · edited this week · PDFs" — shown under the name in the editor.
    public string Describe()
    {
        var parts = new List<string>();
        if (Tags.Count > 0)
            parts.Add(string.Join(MatchAllTags ? " and " : " or ", Tags.Select(t => "#" + t)));
        if (EditedWithinDays > 0)
            parts.Add(EditedWithinDays == 1 ? "edited today" : $"edited in the last {EditedWithinDays} days");
        if (Kind == SmartNoteKind.ImportedPdf) parts.Add("imported PDFs");
        if (Kind == SmartNoteKind.Handwritten) parts.Add("handwritten notes");
        if (PinnedOnly) parts.Add("pinned");
        if (!string.IsNullOrWhiteSpace(TitleContains)) parts.Add($"title has “{TitleContains.Trim()}”");
        return parts.Count == 0 ? "All notes" : string.Join(" · ", parts);
    }
}
