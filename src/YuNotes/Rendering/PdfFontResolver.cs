using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace YuNotes.Rendering;

// Lets PDFsharp embed the exact font face DirectWrite used to lay out a text
// element. Faces are looked up by PostScript name (unique per face) in an
// index of the installed font files, built once by reading each file's
// 'name' table. Faces are addressed in PDFsharp by synthetic family keys
// ("ynf:<face id>:<simulate bold><simulate italic>") so resolution never
// depends on PDFsharp's own family-name matching.
public sealed class PdfFontResolver : IFontResolver
{
    // Embeddable: the font's license (OS/2 fsType) allows embedding subset
    // outlines in a document.
    private sealed record Face(string Path, int Index, uint Offset, bool IsCff, bool Embeddable);

    private static readonly object s_lock = new();
    private static List<Face>? s_faces;
    private static Dictionary<string, int>? s_byName;   // PostScript / full name → face id
    private static readonly Dictionary<int, byte[]> s_bytes = new();
    private static readonly Dictionary<(string, float), XFont> s_fonts = new();
    private static bool s_installed;

    private PdfFontResolver() { }

    public static void Install()
    {
        lock (s_lock)
        {
            if (s_installed) return;
            GlobalFontSettings.FontResolver = new PdfFontResolver();
            s_installed = true;
        }
    }

    /// <summary>Builds the face index (reads font file headers only). Safe to
    /// call from a background thread to warm it up before the first save.</summary>
    public static void EnsureIndexed()
    {
        lock (s_lock)
        {
            if (s_faces is not null) return;
            var faces = new List<Face>();
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "Microsoft", "Windows", "Fonts"),
            };
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); } catch { continue; }
                foreach (var file in files)
                {
                    var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".ttf" or ".otf" or ".ttc" or ".otc")) continue;
                    try { IndexFile(file, faces, byName); }
                    catch { /* unreadable / malformed font: skip */ }
                }
            }
            s_faces = faces;
            s_byName = byName;
        }
    }

    /// <summary>
    /// PDFsharp family key for the installed face with this PostScript (or
    /// full) name, or null when it isn't installed as a file PDFsharp can embed
    /// (CFF-outline OpenType, variable-font named instances, memory fonts) or
    /// whose license forbids embedding it.
    /// </summary>
    public static string? KeyFor(IEnumerable<string> names, bool simulateBold, bool simulateItalic)
    {
        EnsureIndexed();
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (s_byName!.TryGetValue(name, out int id) && !s_faces![id].IsCff && s_faces[id].Embeddable)
                return $"ynf:{id}:{(simulateBold ? 1 : 0)}{(simulateItalic ? 1 : 0)}";
        }
        return null;
    }

    /// <summary>Cached XFont for a key from <see cref="KeyFor"/>. Creating it
    /// loads and parses the face, so a broken font throws here — callers
    /// probe once while preparing a page and fall back to raster text.</summary>
    public static XFont GetFont(string key, float size)
    {
        Install();
        lock (s_lock)
            if (s_fonts.TryGetValue((key, size), out var cached)) return cached;
        // Built outside s_lock: PDFsharp takes its own global lock and calls
        // back into GetFont(faceName) — holding ours across that could
        // deadlock against a writer thread doing the reverse.
        var f = new XFont(key, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        lock (s_lock)
        {
            if (s_fonts.TryGetValue((key, size), out var raced)) return raced;
            s_fonts[(key, size)] = f;
            return f;
        }
    }

    // ── IFontResolver ───────────────────────────────────────────────────────

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Only our own keys are ever requested; flags ride in the key.
        if (!TryParseKey(familyName, out _, out bool simBold, out bool simItalic)) return null;
        return new FontResolverInfo(familyName.ToLowerInvariant(), simBold, simItalic);
    }

    public byte[]? GetFont(string faceName)
    {
        if (!TryParseKey(faceName, out int id, out _, out _)) return null;
        EnsureIndexed();
        lock (s_lock)
        {
            if (s_bytes.TryGetValue(id, out var cached)) return cached;
            if (id < 0 || id >= s_faces!.Count) return null;
            var face = s_faces[id];
            var file = File.ReadAllBytes(face.Path);
            // A collection (.ttc) holds several faces sharing tables; PDF
            // embedding needs a standalone font, so copy this face's tables out.
            var bytes = IsCollection(file) ? ExtractFace(file, face.Offset) : file;
            s_bytes[id] = bytes;
            return bytes;
        }
    }

    private static bool TryParseKey(string key, out int id, out bool simBold, out bool simItalic)
    {
        id = -1; simBold = simItalic = false;
        var parts = key.Split(':');
        if (parts.Length != 3 || !parts[0].Equals("ynf", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(parts[1], out id) || parts[2].Length != 2)
            return false;
        simBold = parts[2][0] == '1';
        simItalic = parts[2][1] == '1';
        return true;
    }

    // ── sfnt parsing ────────────────────────────────────────────────────────

    private const uint TagTtcf = 0x74746366;   // 'ttcf'
    private const uint TagOtto = 0x4F54544F;   // 'OTTO'
    private const uint TagName = 0x6E616D65;   // 'name'

    // OS/2 fsType embedding permissions. Bits 1–3 are the usage level; when
    // several are set the least restrictive applies, so a font is
    // "Restricted License" only when bit 1 is the sole one. PDFsharp embeds a
    // subset of the outlines, which fonts flagged "no subsetting" (bit 8) or
    // "bitmap embedding only" (bit 9) don't allow either.
    private const uint TagOs2 = 0x4F532F32;   // 'OS/2'

    internal static bool IsEmbeddable(int fsType)
    {
        if ((fsType & 0x000E) == 0x0002) return false;   // Restricted License
        if ((fsType & 0x0100) != 0) return false;        // No subsetting
        if ((fsType & 0x0200) != 0) return false;        // Bitmap embedding only
        return true;
    }

    private static bool IsCollection(byte[] file) => file.Length >= 4 && U32(file, 0) == TagTtcf;

    private static void IndexFile(string path, List<Face> faces, Dictionary<string, int> byName)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        var head = ReadAt(fs, 0, 12);
        var offsets = new List<uint>();
        if (U32(head, 0) == TagTtcf)
        {
            uint n = U32(head, 8);
            if (n == 0 || n > 256) return;
            var table = ReadAt(fs, 12, (int)n * 4);
            for (int i = 0; i < n; i++) offsets.Add(U32(table, i * 4));
        }
        else offsets.Add(0);

        for (int i = 0; i < offsets.Count; i++)
        {
            uint off = offsets[i];
            var dir = ReadAt(fs, off, 12);
            uint version = U32(dir, 0);
            int numTables = U16(dir, 4);
            if (numTables <= 0 || numTables > 512) continue;
            var records = ReadAt(fs, off + 12, numTables * 16);
            uint nameOff = 0, nameLen = 0, os2Off = 0, os2Len = 0;
            for (int t = 0; t < numTables; t++)
            {
                uint tag = U32(records, t * 16);
                if (tag == TagName)
                {
                    nameOff = U32(records, t * 16 + 8);
                    nameLen = U32(records, t * 16 + 12);
                }
                else if (tag == TagOs2)
                {
                    os2Off = U32(records, t * 16 + 8);
                    os2Len = U32(records, t * 16 + 12);
                }
            }
            if (nameLen == 0 || nameLen > 4 * 1024 * 1024) continue;
            var names = ReadNames(ReadAt(fs, nameOff, (int)nameLen));
            // No OS/2 table (old Mac-style fonts): no restriction declared.
            int fsType = os2Len >= 10 ? U16(ReadAt(fs, os2Off, 10), 8) : 0;

            int id = faces.Count;
            faces.Add(new Face(path, i, off, version == TagOtto, IsEmbeddable(fsType)));
            // First file wins: per-user fonts are indexed after system ones.
            foreach (var n in names) byName.TryAdd(n, id);
        }
    }

    // PostScript name (6) and full name (4), Windows-platform strings first.
    private static List<string> ReadNames(byte[] t)
    {
        var result = new List<string>();
        if (t.Length < 6) return result;
        int count = U16(t, 2), strOff = U16(t, 4);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < count; i++)
            {
                int r = 6 + i * 12;
                if (r + 12 > t.Length) break;
                int platform = U16(t, r), nameId = U16(t, r + 6);
                int len = U16(t, r + 8), off = strOff + U16(t, r + 10);
                if (nameId is not (4 or 6) || off + len > t.Length) continue;
                string? s = null;
                if (pass == 0 && (platform == 3 || platform == 0))
                    s = Encoding.BigEndianUnicode.GetString(t, off, len);
                else if (pass == 1 && platform == 1)
                    s = Encoding.Latin1.GetString(t, off, len);
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
            }
        }
        return result;
    }

    // Rebuilds one face of a collection as a standalone sfnt: same header,
    // table records re-pointed at a compact copy of its tables.
    private static byte[] ExtractFace(byte[] file, uint faceOffset)
    {
        int o = (int)faceOffset;
        int numTables = U16(file, o + 4);
        int headerLen = 12 + numTables * 16;
        var tables = new List<(int rec, uint off, uint len)>();
        long total = headerLen;
        for (int t = 0; t < numTables; t++)
        {
            int rec = o + 12 + t * 16;
            uint off = U32(file, rec + 8), len = U32(file, rec + 12);
            if (off + (long)len > file.Length) throw new InvalidDataException("Font table out of range.");
            tables.Add((rec, off, len));
            total += (len + 3) & ~3u;
        }
        var outBuf = new byte[total];
        Buffer.BlockCopy(file, o, outBuf, 0, 12);
        uint pos = (uint)headerLen;
        for (int t = 0; t < tables.Count; t++)
        {
            var (rec, off, len) = tables[t];
            int outRec = 12 + t * 16;
            Buffer.BlockCopy(file, rec, outBuf, outRec, 8);   // tag + checksum
            W32(outBuf, outRec + 8, pos);
            W32(outBuf, outRec + 12, len);
            Buffer.BlockCopy(file, (int)off, outBuf, (int)pos, (int)len);
            pos += (len + 3) & ~3u;
        }
        return outBuf;
    }

    private static byte[] ReadAt(FileStream fs, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > fs.Length) throw new InvalidDataException("Read past end of font file.");
        var buf = new byte[count];
        fs.Position = offset;
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(buf, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return buf;
    }

    private static int U16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
    private static uint U32(byte[] b, int i) => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
    private static void W32(byte[] b, int i, uint v)
    {
        b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v;
    }
}
