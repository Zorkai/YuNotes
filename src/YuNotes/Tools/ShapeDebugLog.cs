using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using YuNotes.Models;

namespace YuNotes.Tools;

// DEBUG-only dump of every hold-to-snap recognition attempt, so misbehaving
// real strokes can be replayed through the recognizer offline. One CSV per
// attempt in %LOCALAPPDATA%\YuNotes\ShapeDebug (newest 200 kept): a header
// line with the recognition result, then one x,y,pressure row per ink point.
internal static class ShapeDebugLog
{
    private static int s_counter;

    [Conditional("DEBUG")]
    public static void Dump(IReadOnlyList<InkPoint> points, ShapeElement? result)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YuNotes", "ShapeDebug");
            Directory.CreateDirectory(dir);

            var files = Directory.GetFiles(dir, "*.csv");
            if (files.Length > 200)
                foreach (var f in files.OrderBy(File.GetCreationTimeUtc).Take(files.Length - 200))
                    File.Delete(f);

            string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_" + (s_counter++ % 1000) + ".csv";
            using var w = new StreamWriter(Path.Combine(dir, name));
            w.WriteLine(result is null
                ? "# result=null"
                : string.Create(CultureInfo.InvariantCulture,
                    $"# result={result.Kind} X1={result.X1:F1} Y1={result.Y1:F1} X2={result.X2:F1} Y2={result.Y2:F1} X3={result.X3:F1} Y3={result.Y3:F1} Rot={result.Rotation:F1}"));
            foreach (var p in points)
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{p.X:F2},{p.Y:F2},{p.Pressure:F3}"));
        }
        catch { /* never let logging break inking */ }
    }
}
