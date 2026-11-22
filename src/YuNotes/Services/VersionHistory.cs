using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace YuNotes.Services;

// Earlier versions of each note. Before a save overwrites a note, the file
// as it is on disk is copied to %LOCALAPPDATA%\YuNotes\versions\<note>\ —
// at most once every 10 minutes per note, and only if it changed since the
// last copy. The newest 25 copies per note are kept.
public sealed class VersionHistory
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    public const int MaxVersions = 25;

    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuNotes", "versions");

    public sealed record Version(string FilePath, DateTime SavedAt, long Size);

    private string DirFor(string notePath)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(notePath.ToLowerInvariant())))[..16];
        return Path.Combine(_root, hash);
    }

    public List<Version> List(string notePath)
    {
        var dir = DirFor(notePath);
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir)
            .Where(f => !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new Version(f.FullName, f.LastWriteTime, f.Length))
            .ToList();
    }

    // Called before a save. force: copy even if the last copy is recent (used
    // right before a restore, so the restore itself can be undone).
    public void SnapshotIfDue(string notePath, bool force = false)
    {
        try
        {
            var src = new FileInfo(notePath);
            if (!src.Exists || src.Length == 0) return;
            var latest = List(notePath).FirstOrDefault();
            if (!force && latest is not null)
            {
                // Unchanged since the last copy, or the last copy is recent.
                if (src.LastWriteTime <= latest.SavedAt) return;
                if (DateTime.Now - latest.SavedAt < MinInterval) return;
            }
            var dir = DirFor(notePath);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "note-path.txt"), notePath);
            var dest = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}{src.Extension}");
            // Copy with sharing so a note that is open elsewhere still copies.
            using (var input = new FileStream(notePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = File.Create(dest))
                input.CopyTo(output);
            File.SetLastWriteTime(dest, DateTime.Now);

            foreach (var old in List(notePath).Skip(MaxVersions))
                try { File.Delete(old.FilePath); } catch { }
        }
        catch (Exception ex) { App.LogError(ex, $"Couldn't keep a version of {notePath}"); }
    }

    // Puts a version back in place of the note (after keeping the current one).
    public void Restore(string notePath, Version version)
    {
        SnapshotIfDue(notePath, force: true);
        File.Copy(version.FilePath, notePath, overwrite: true);
        File.SetLastWriteTime(notePath, DateTime.Now);
    }
}
