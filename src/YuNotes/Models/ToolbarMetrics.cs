using System;

namespace YuNotes.Models;

/// <summary>
/// Sizes of the floating drawing toolbar. Shared by the editor and the live
/// preview in Settings so the preview is exactly what the editor will show.
///
/// Two things set the size: the window width (four tiers, so a small window
/// gets a smaller bar) and the user's toolbar size, a scale shown in Settings
/// as a percentage where 100 % is the default ("Normal").
/// </summary>
public static class ToolbarMetrics
{
    public const double DefaultScale = 0.78;   // the old "Normal"
    public const int MinPercent = 60, MaxPercent = 160, StepPercent = 5;

    public readonly record struct Sizes(double Button, double Icon, double ColorButton, double Chip, double ChipDot);

    /// <summary>The saved scale; settings from before the slider fall back
    /// to their Small / Normal / Large choice.</summary>
    public static double ScaleOf(AppSettings s) => Math.Clamp(
        s.ToolbarScale ?? s.ToolbarSize switch
        {
            ToolbarSize.Small => 0.65,
            ToolbarSize.Large => 1.00,
            _ => DefaultScale,
        },
        FromPercent(MinPercent), FromPercent(MaxPercent));

    public static int PercentOf(double scale) => (int)Math.Round(scale / DefaultScale * 100);
    public static double FromPercent(double percent) => percent / 100.0 * DefaultScale;

    public static Sizes For(double windowWidth, double scale)
    {
        double btn, icon, colorBtn, chip, chipDot;
        if (windowWidth >= 1200)      { btn = 44; icon = 24; colorBtn = 30; chip = 36; chipDot = 22; }
        else if (windowWidth >= 1000) { btn = 38; icon = 21; colorBtn = 26; chip = 32; chipDot = 18; }
        else if (windowWidth >= 800)  { btn = 32; icon = 18; colorBtn = 22; chip = 28; chipDot = 14; }
        else                          { btn = 28; icon = 16; colorBtn = 20; chip = 24; chipDot = 12; }
        return new Sizes(
            Math.Round(btn * scale), Math.Round(icon * scale), Math.Round(colorBtn * scale),
            Math.Round(chip * scale), Math.Max(4, Math.Round(chipDot * scale)));
    }
}
