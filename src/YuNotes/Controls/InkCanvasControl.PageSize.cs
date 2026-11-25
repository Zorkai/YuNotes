using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using YuNotes.Input;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Controls;

public sealed partial class InkCanvasControl
{

    // ── Page extension ─────────────────────────────────────────────────────────

    private void OnExtensionDragCompleted(object? sender, (ExtendSide Side, double Amount) args)
    {
        // Amount is the total desired extension (may be 0 = fully unextended).
        if (sender is not PageCanvas pc) return;
        CommitPageExtension(pc.Page, args.Side, args.Amount, pc);
    }

    // A Page size drag inside a page: open space below the row it started at
    // (Delta > 0) or close inserted space above it (Delta < 0). One undo step.
    private void OnSpaceDragCompleted(object? sender, (double Y, double Delta) args)
    {
        if (sender is not PageCanvas pc) return;
        var page = pc.Page;
        if (args.Delta > 0) PageGaps.Insert(page, args.Y, args.Delta);
        else if (PageGaps.Remove(page, args.Y + args.Delta, args.Y) <= 0) return;
        pc.ResizeCanvas();
        UpdateSeparators();
        DocumentMutated?.Invoke(this, page);
    }

    // Called by EditorPage when the dialog is used for precise control.
    public void CommitPageExtension(NotePage page, ExtendSide side, double amount)
    {
        var pc = _pageCanvases.FirstOrDefault(c => ReferenceEquals(c.Page, page));
        CommitPageExtension(page, side, amount, pc);
    }

    public void ResetPageExtension(NotePage page)
    {
        var pc = _pageCanvases.FirstOrDefault(c => ReferenceEquals(c.Page, page));
        ResetExtension(page);
        pc?.ResizeCanvas();
        UpdateSeparators();
        DocumentMutated?.Invoke(this, page);
    }

    private void CommitPageExtension(NotePage page, ExtendSide side, double amount, PageCanvas? pc)
    {
        // amount = total desired extension; SetExtensionAbsolute handles both
        // increase and reduction (amount may be 0 to fully unextend a side).
        SetExtensionAbsolute(page, side, amount);
        pc?.ResizeCanvas();
        UpdateSeparators();
        DocumentMutated?.Invoke(this, page);
    }

    // Migrates all element coordinates and updates the page dimensions.
    /// <summary>
    /// Sets the total extension for one side to an absolute amount.
    /// Handles both increasing and decreasing (including fully removing) extension.
    /// </summary>
    public static void SetExtensionAbsolute(NotePage page, ExtendSide side, double totalExtension)
    {
        totalExtension = Math.Max(0, totalExtension);

        // Ensure original width is captured for any page type (PDF or blank).
        if (page.BackgroundContentWidth == 0 && totalExtension > 0)
            page.BackgroundContentWidth = page.Width - page.BackgroundLeft;

        if (side == ExtendSide.Right)
        {
            double currentRightExt = page.BackgroundContentWidth > 0
                ? Math.Max(0, page.Width - page.BackgroundLeft - page.BackgroundContentWidth)
                : 0;
            page.Width += totalExtension - currentRightExt; // may shrink
        }
        else // Left
        {
            double currentLeftExt = page.BackgroundLeft;
            double delta = totalExtension - currentLeftExt;
            if (delta == 0) return;

            float shift = (float)delta; // positive = more left ext; negative = less
            foreach (var s in page.Strokes)
                for (int i = 0; i < s.Points.Count; i++)
                    s.Points[i] = s.Points[i] with { X = s.Points[i].X + shift };
            foreach (var sh in page.Shapes)
            { sh.X1 += shift; sh.X2 += shift; sh.X3 += shift; }
            foreach (var t  in page.Texts)  t.X  += delta;
            foreach (var im in page.Images) im.X += delta;
            foreach (var tr in page.TextRuns) tr.X += delta;
            page.BackgroundLeft += delta;
            page.Width += delta;
        }
    }

    // Left extension shifts every element X forward so the PDF stays in place.
    // Right extension only grows the page width.
    // Kept for internal use; prefer SetExtensionAbsolute for new callers.
    public static void ApplyExtension(NotePage page, double leftAmt, double rightAmt)
    {
        if (leftAmt <= 0 && rightAmt <= 0) return;

        // Capture original width on first ever extension (any page type).
        if (page.BackgroundContentWidth == 0)
            page.BackgroundContentWidth = page.Width - page.BackgroundLeft;

        if (leftAmt > 0)
        {
            float shift = (float)leftAmt;
            foreach (var s in page.Strokes)
                for (int i = 0; i < s.Points.Count; i++)
                    s.Points[i] = s.Points[i] with { X = s.Points[i].X + shift };
            foreach (var sh in page.Shapes)
            { sh.X1 += shift; sh.X2 += shift; sh.X3 += shift; }
            foreach (var t  in page.Texts)  t.X  += leftAmt;
            foreach (var im in page.Images) im.X += leftAmt;
            foreach (var tr in page.TextRuns) tr.X += leftAmt;
            page.BackgroundLeft += leftAmt;
            page.Width += leftAmt;
        }

        if (rightAmt > 0)
            page.Width += rightAmt;
    }

    // Reverses all extensions: shifts elements back by BackgroundLeft, restores Width.
    public static void ResetExtension(NotePage page)
    {
        if (page.BackgroundLeft == 0 && page.BackgroundContentWidth == 0) return;

        double leftExt = page.BackgroundLeft;
        double originalW = page.BackgroundContentWidth > 0 ? page.BackgroundContentWidth : page.Width;

        if (leftExt > 0)
        {
            float shift = -(float)leftExt;
            foreach (var s in page.Strokes)
                for (int i = 0; i < s.Points.Count; i++)
                    s.Points[i] = s.Points[i] with { X = s.Points[i].X + shift };
            foreach (var sh in page.Shapes)
            { sh.X1 += shift; sh.X2 += shift; sh.X3 += shift; }
            foreach (var t  in page.Texts)  t.X  -= leftExt;
            foreach (var im in page.Images) im.X -= leftExt;
            foreach (var tr in page.TextRuns) tr.X -= leftExt;
        }

        page.BackgroundLeft = 0;
        page.BackgroundContentWidth = 0;
        page.Width = originalW;
    }
}
