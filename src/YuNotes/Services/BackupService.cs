using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using YuNotes.Models;

namespace YuNotes.Services;

/// <summary>
/// One-file backup of everything YuNotes keeps: the notes folder (archive
/// included, Trash left out), the library data that lives in settings keyed
/// by file path (tags, pins, folder colors, smart folders, last page and
/// view), stickers, custom page templates and, optionally, preferences.
///
/// A backup is a .zip:
///   manifest.json      format version, when, from where
///   notes/...          the notes folder, paths relative to it
///   library.json       tags, pins, folders, smart folders, views (relative paths)
///   preferences.json   every other setting (pens, toolbar, theme, …)
///   stickers/...       %LOCALAPPDATA%\YuNotes\Stickers
///   templates/...      %LOCALAPPDATA%\YuNotes\Templates
///
/// Restoring merges into what is already there and never overwrites a file:
/// a note whose path is taken by a different file comes back next to it as
/// "Name (restored).pdf"; one that is byte-for-byte the same is skipped.
/// </summary>
public static class BackupService
{
    public const int FormatVersion = 1;
    public const string FileExtension = ".zip";
    private const string TrashName = "_trash";

    public sealed class Manifest
    {
        public int Format { get; set; } = FormatVersion;
        public string App { get; set; } = "YuNotes";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string NotesFolder { get; set; } = "";
        public int NoteCount { get; set; }
    }

    // Settings data keyed by note path, stored relative to the notes folder
    // so it follows the notes to a different folder or PC.
    public sealed class Library
    {
        public List<string> PinnedNotes { get; set; } = new();
        public Dictionary<string, List<string>> NoteTags { get; set; } = new();
        public Dictionary<string, int> LastPage { get; set; } = new();
        public Dictionary<string, NoteView> Views { get; set; } = new();
        public Dictionary<string, List<string>> StudyMissed { get; set; } = new();
        public List<FolderEntry> Folders { get; set; } = new();
        public List<SmartFolder> SmartFolders { get; set; } = new();
    }

    public sealed class FolderEntry
    {
        public string Path { get; set; } = "";
        public string? Color { get; set; }
        public string? DefaultTemplate { get; set; }
        public double? DefaultTemplateSpacing { get; set; }
    }

    public sealed record BackupResult(int Notes, int Stickers, int Templates, long Bytes);

    public sealed record RestoreResult(int NotesAdded, int NotesSkipped, int NotesRenamed,
                                       int Stickers, int Templates, bool PreferencesRestored);

    // Settings that describe the library rather than preferences; restored by
    // merging (library.json), never by overwriting.
    private static readonly HashSet<string> LibraryProperties = new()
    {
        nameof(AppSettings.DocumentsFolder), nameof(AppSettings.RecentDocuments),
        nameof(AppSettings.PinnedNotes), nameof(AppSettings.NoteTags),
        nameof(AppSettings.SmartFolders), nameof(AppSettings.Folders),
        nameof(AppSettings.LastPageByDocument), nameof(AppSettings.NoteViews),
        nameof(AppSettings.StudyMissed),
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ── Back up ──────────────────────────────────────────────────────────────

    public static BackupResult Create(string zipPath, AppSettings settings,
                                      string stickersFolder, string templatesFolder)
    {
        var root = Path.GetFullPath(settings.DocumentsFolder);
        var tmp = zipPath + ".partial";
        if (File.Exists(tmp)) File.Delete(tmp);
        int notes = 0, stickers = 0, templates = 0;

        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root, file);
                    if (IsInTrash(rel) || IsTempFile(file)) continue;
                    zip.CreateEntryFromFile(file, "notes/" + ToEntry(rel), CompressionLevel.Fastest);
                    if (IsNote(file)) notes++;
                }
            }
            stickers = AddFolder(zip, stickersFolder, "stickers/", countExt: ".sticker");
            templates = AddFolder(zip, templatesFolder, "templates/", countExt: ".jpg");

            WriteJson(zip, "library.json", BuildLibrary(settings, root));
            WriteJson(zip, "preferences.json", settings);
            WriteJson(zip, "manifest.json", new Manifest { NotesFolder = root, NoteCount = notes });
        }
        File.Move(tmp, zipPath, overwrite: true);
        return new BackupResult(notes, stickers, templates, new FileInfo(zipPath).Length);
    }

    public static Library BuildLibrary(AppSettings s, string root)
    {
        string? Rel(string path) => RelativeUnder(root, path);
        var lib = new Library();
        foreach (var p in s.PinnedNotes)
            if (Rel(p) is { } r) lib.PinnedNotes.Add(r);
        foreach (var (p, tags) in s.NoteTags)
            if (Rel(p) is { } r) lib.NoteTags[r] = tags.ToList();
        foreach (var (p, page) in s.LastPageByDocument)
            if (Rel(p) is { } r) lib.LastPage[r] = page;
        foreach (var (p, view) in s.NoteViews)
            if (Rel(p) is { } r) lib.Views[r] = view;
        foreach (var (p, ids) in s.StudyMissed)
            if (Rel(p) is { } r) lib.StudyMissed[r] = ids.ToList();
        foreach (var f in s.Folders)
            if (Rel(f.Path) is { } r)
                lib.Folders.Add(new FolderEntry
                {
                    Path = r, Color = f.Color,
                    DefaultTemplate = f.DefaultTemplate, DefaultTemplateSpacing = f.DefaultTemplateSpacing,
                });
        lib.SmartFolders = s.SmartFolders.ToList();
        return lib;
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    /// <summary>Reads only the manifest, to show the user what a backup holds.</summary>
    public static Manifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("This file isn't a YuNotes backup.");
        using var s = entry.Open();
        var m = JsonSerializer.Deserialize<Manifest>(s)
            ?? throw new InvalidDataException("This file isn't a YuNotes backup.");
        if (m.Format > FormatVersion)
            throw new InvalidDataException("This backup was made by a newer version of YuNotes. Update YuNotes, then try again.");
        return m;
    }

    public static RestoreResult Restore(string zipPath, AppSettings settings,
                                        string stickersFolder, string templatesFolder,
                                        bool restorePreferences)
    {
        ReadManifest(zipPath);
        var root = Path.GetFullPath(settings.DocumentsFolder);
        Directory.CreateDirectory(root);
        // Backup-relative path → where it actually landed (differs when renamed).
        var placed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, skipped = 0, renamed = 0;

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("notes/", StringComparison.Ordinal) || entry.Name.Length == 0) continue;
            var rel = FromEntry(entry.FullName["notes/".Length..]);
            var dest = SafeCombine(root, rel);
            if (dest is null) continue;   // "..\" or absolute path in the zip: ignore
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            bool note = IsNote(dest);

            if (File.Exists(dest))
            {
                if (SameContent(entry, dest))
                {
                    placed[rel] = dest;
                    if (note) skipped++;
                    continue;
                }
                if (!note) continue;   // keep the user's own non-note files
                dest = UniqueRestoredPath(dest);
                renamed++;
            }
            else if (note) added++;
            entry.ExtractToFile(dest, overwrite: false);
            File.SetLastWriteTimeUtc(dest, entry.LastWriteTime.UtcDateTime);
            placed[rel] = dest;
        }

        if (ReadJson<Library>(zip, "library.json") is { } lib)
            MergeLibrary(settings, lib, root, placed);

        int stickers = MergeIndexedFolder(zip, "stickers/", stickersFolder);
        int templates = MergeIndexedFolder(zip, "templates/", templatesFolder);

        bool prefs = false;
        if (restorePreferences && ReadJson<AppSettings>(zip, "preferences.json") is { } old)
        {
            foreach (var p in typeof(AppSettings).GetProperties())
                if (p.CanRead && p.CanWrite && !LibraryProperties.Contains(p.Name))
                    p.SetValue(settings, p.GetValue(old));
            prefs = true;
        }
        return new RestoreResult(added, skipped, renamed, stickers, templates, prefs);
    }

    public static void MergeLibrary(AppSettings s, Library lib, string root,
                                    IReadOnlyDictionary<string, string> placed)
    {
        string Where(string rel) =>
            placed.TryGetValue(rel, out var p) ? p : Path.Combine(root, rel);
        static bool Has(IEnumerable<string> keys, string path) =>
            keys.Any(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase));

        foreach (var rel in lib.PinnedNotes)
        {
            var path = Where(rel);
            if (File.Exists(path) && !Has(s.PinnedNotes, path)) s.PinnedNotes.Add(path);
        }
        foreach (var (rel, tags) in lib.NoteTags)
        {
            var path = Where(rel);
            if (!File.Exists(path)) continue;
            var key = s.NoteTags.Keys.FirstOrDefault(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase));
            var merged = (key is null ? new List<string>() : s.NoteTags[key])
                .Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.CurrentCulture).ToList();
            if (key is not null) s.NoteTags.Remove(key);
            s.NoteTags[path] = merged;
        }
        foreach (var (rel, page) in lib.LastPage)
        {
            var path = Where(rel);
            if (File.Exists(path) && !Has(s.LastPageByDocument.Keys, path)) s.LastPageByDocument[path] = page;
        }
        foreach (var (rel, view) in lib.Views)
        {
            var path = Where(rel);
            if (File.Exists(path) && !Has(s.NoteViews.Keys, path)) s.NoteViews[path] = view;
        }
        foreach (var (rel, ids) in lib.StudyMissed)
        {
            var path = Where(rel);
            if (File.Exists(path) && !Has(s.StudyMissed.Keys, path)) s.StudyMissed[path] = ids.ToList();
        }
        // Folder colors and default templates. The Home page adds registry
        // entries for new folders on its next sync; seeding them here (without
        // a parent id, which the sync does not need) keeps the backup's colors.
        foreach (var f in lib.Folders)
        {
            var path = SafeCombine(root, f.Path);
            if (path is null || !Directory.Exists(path)) continue;
            var existing = s.Folders.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                s.Folders.Add(new FolderInfo
                {
                    Name = Path.GetFileName(path), Path = path, Color = f.Color,
                    DefaultTemplate = f.DefaultTemplate, DefaultTemplateSpacing = f.DefaultTemplateSpacing,
                });
            }
            else
            {
                existing.DefaultTemplate ??= f.DefaultTemplate;
                existing.DefaultTemplateSpacing ??= f.DefaultTemplateSpacing;
            }
        }
        foreach (var sf in lib.SmartFolders)
            if (!s.SmartFolders.Any(x => x.Id == sf.Id)) s.SmartFolders.Add(sf);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static bool IsNote(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".yunote", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInTrash(string rel) =>
        rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]
           .Equals(TrashName, StringComparison.OrdinalIgnoreCase);

    // Half-written saves and SQLite side files are not worth keeping.
    private static bool IsTempFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    public static string? RelativeUnder(string root, string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string full;
        try { full = Path.GetFullPath(path); } catch { return null; }
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return full[prefix.Length..];
    }

    // root + rel, or null when rel would land outside root.
    public static string? SafeCombine(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return null;
        var full = Path.GetFullPath(Path.Combine(root, rel));
        return RelativeUnder(root, full) is null ? null : full;
    }

    private static string ToEntry(string rel) => rel.Replace(Path.DirectorySeparatorChar, '/');
    private static string FromEntry(string entry) => entry.Replace('/', Path.DirectorySeparatorChar);

    public static string UniqueRestoredPath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var candidate = Path.Combine(dir, $"{name} (restored){ext}");
        for (int i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} (restored {i}){ext}");
        return candidate;
    }

    private static bool SameContent(ZipArchiveEntry entry, string file)
    {
        if (new FileInfo(file).Length != entry.Length) return false;
        using var a = entry.Open();
        using var b = File.OpenRead(file);
        return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }

    private static int AddFolder(ZipArchive zip, string folder, string prefix, string countExt)
    {
        if (!Directory.Exists(folder)) return 0;
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (IsTempFile(file)) continue;
            zip.CreateEntryFromFile(file, prefix + ToEntry(Path.GetRelativePath(folder, file)), CompressionLevel.Fastest);
            if (file.EndsWith(countExt, StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count;
    }

    // Stickers and templates: a folder of <id>.* files plus index.json, a
    // JSON list of objects with an "Id". Copies files that aren't there yet
    // and appends index entries whose Id is new. Returns entries added.
    private static int MergeIndexedFolder(ZipArchive zip, string prefix, string folder)
    {
        var entries = zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
        if (entries.Count == 0) return 0;
        Directory.CreateDirectory(folder);
        foreach (var e in entries)
        {
            if (e.Name.Equals("index.json", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = SafeCombine(folder, FromEntry(e.FullName[prefix.Length..]));
            if (dest is null || File.Exists(dest)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest);
        }

        var indexEntry = zip.GetEntry(prefix + "index.json");
        if (indexEntry is null) return 0;
        JsonArray incoming;
        using (var s = indexEntry.Open())
            incoming = JsonNode.Parse(s) as JsonArray ?? new JsonArray();
        var indexPath = Path.Combine(folder, "index.json");
        var current = File.Exists(indexPath)
            ? JsonNode.Parse(File.ReadAllText(indexPath)) as JsonArray ?? new JsonArray()
            : new JsonArray();
        var ids = new HashSet<string>(current.Select(n => n?["Id"]?.GetValue<string>() ?? ""));
        int added = 0;
        foreach (var node in incoming)
        {
            var id = node?["Id"]?.GetValue<string>();
            if (id is null || !ids.Add(id)) continue;
            current.Add(node!.DeepClone());
            added++;
        }
        if (added > 0)
        {
            var tmp = indexPath + ".tmp";
            File.WriteAllText(tmp, current.ToJsonString(Json));
            File.Move(tmp, indexPath, overwrite: true);
        }
        return added;
    }

    private static void WriteJson<T>(ZipArchive zip, string name, T value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var s = entry.Open();
        JsonSerializer.Serialize(s, value, Json);
    }

    private static T? ReadJson<T>(ZipArchive zip, string name) where T : class
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        try
        {
            using var s = entry.Open();
            return JsonSerializer.Deserialize<T>(s);
        }
        catch (JsonException) { return null; }
    }
}
