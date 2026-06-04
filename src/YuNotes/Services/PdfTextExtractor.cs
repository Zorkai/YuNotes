using System;
using System.Collections.Generic;
using System.IO;
using UglyToad.PdfPig;
using YuNotes.Models;

namespace YuNotes.Services;

// Pulls word-level text + bounding boxes out of a PDF and converts them into our
// NotePage coord space (origin top-left, scaled to CoordDpi). Used by the Hand
// tool to allow browser-like text selection over PDF backgrounds.
public sealed class PdfTextExtractor
{
    private const double CoordDpi = 150.0;
    private const double PointToCoord = CoordDpi / 72.0;

    // Returns one list of TextRuns per PDF page. Empty list on failure (encrypted,
    // image-only, parse error). Indices line up with PDF page indices.
    public List<List<TextRun>> Extract(byte[] pdfBytes)
    {
        var result = new List<List<TextRun>>();
        if (pdfBytes is null || pdfBytes.Length == 0) return result;

        try
        {
            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfDocument.Open(ms);
            foreach (var page in doc.GetPages())
            {
                var runs = new List<TextRun>();
                IEnumerable<UglyToad.PdfPig.Content.Word> words;
                try { words = page.GetWords(); }
                catch { words = Array.Empty<UglyToad.PdfPig.Content.Word>(); }

                foreach (var w in words)
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    var bb = w.BoundingBox;
                    var noteX = bb.Left * PointToCoord;
                    var noteY = (page.Height - bb.Top) * PointToCoord;
                    var noteW = (bb.Right - bb.Left) * PointToCoord;
                    var noteH = (bb.Top - bb.Bottom) * PointToCoord;
                    if (noteW <= 0 || noteH <= 0) continue;
                    runs.Add(new TextRun
                    {
                        Text = w.Text,
                        X = noteX,
                        Y = noteY,
                        Width = noteW,
                        Height = noteH,
                    });
                }

                // Reading order: top-to-bottom, left-to-right. Two runs share a line when
                // their vertical centers are within half a line-height of each other.
                runs.Sort((a, b) =>
                {
                    var aCy = a.Y + a.Height * 0.5;
                    var bCy = b.Y + b.Height * 0.5;
                    var tol = Math.Min(a.Height, b.Height) * 0.5;
                    if (Math.Abs(aCy - bCy) > tol) return aCy < bCy ? -1 : 1;
                    return a.X.CompareTo(b.X);
                });

                int line = -1;
                double lineCy = double.NaN;
                double lineH = 0;
                int orderInLine = 0;
                foreach (var r in runs)
                {
                    var cy = r.Y + r.Height * 0.5;
                    if (line < 0 || Math.Abs(cy - lineCy) > lineH * 0.5)
                    {
                        line++;
                        lineCy = cy;
                        lineH = r.Height;
                        orderInLine = 0;
                    }
                    r.LineIndex = line;
                    r.OrderInLine = orderInLine++;
                }

                result.Add(runs);
            }
        }
        catch
        {
            // Encrypted/malformed PDF, or PdfPig hits something it can't parse — skip
            // text selection silently; everything else (rendering, drawing) still works.
        }

        return result;
    }
}
