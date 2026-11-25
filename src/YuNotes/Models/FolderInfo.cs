using System;

namespace YuNotes.Models;

public sealed class FolderInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? ParentId { get; set; }
    // "#RRGGBB" from FolderColors.Palette. Null on folders saved before colors
    // existed; the home page assigns one the next time it syncs the registry.
    public string? Color { get; set; }
    // Template new notes in this folder start with: a TemplateKind name
    // ("Grid") or "custom:<id>" for a template from the TemplateLibrary.
    // Null = Blank, like everywhere else.
    public string? DefaultTemplate { get; set; }
    public double? DefaultTemplateSpacing { get; set; }
}

// Fixed palette of folder colors — mid-tone hues that read on both the light
// and dark themes, used for the sidebar icon, card strip and folder chip.
public static class FolderColors
{
    public static readonly (string Name, string Hex)[] Palette =
    {
        ("Purple", "#7F77DD"),
        ("Teal",   "#1D9E75"),
        ("Coral",  "#D85A30"),
        ("Pink",   "#D4537E"),
        ("Blue",   "#378ADD"),
        ("Amber",  "#BA7517"),
        ("Green",  "#639922"),
        ("Gray",   "#888780"),
    };

    // Next color for a new folder: the palette entry used least so far, so
    // sibling folders get distinct colors until the palette runs out.
    public static string NextFor(System.Collections.Generic.IEnumerable<FolderInfo> existing)
    {
        var counts = new int[Palette.Length];
        foreach (var f in existing)
            for (int i = 0; i < Palette.Length; i++)
                if (string.Equals(f.Color, Palette[i].Hex, StringComparison.OrdinalIgnoreCase)) counts[i]++;
        int best = 0;
        for (int i = 1; i < counts.Length; i++) if (counts[i] < counts[best]) best = i;
        return Palette[best].Hex;
    }

    public static Windows.UI.Color Parse(string? hex, byte alpha = 255)
    {
        if (hex is { Length: 7 } && hex[0] == '#' &&
            uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v))
            return Windows.UI.Color.FromArgb(alpha, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return Windows.UI.Color.FromArgb(alpha, 0x88, 0x87, 0x80);
    }
}
