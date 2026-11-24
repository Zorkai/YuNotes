using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HandwritingRecognizer = YuNotes.Services.HandwritingRecognizer;
using RecognizedWord = YuNotes.Services.TextBox;
using DataPackage = Windows.ApplicationModel.DataTransfer.DataPackage;
using Clipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace YuNotes.Views;

public sealed partial class EditorPage
{
    // ─── Ink to text ────────────────────────────────────────────────────────────
    // Lasso some handwriting and choose "Convert to text" in the selection bar.
    // Windows' handwriting recognizer (the one search already uses) reads it;
    // the result opens in a dialog to fix up, then either replaces the ink with
    // a text box in the same place (undoable) or goes to the clipboard.

    private bool _convertingInk;

    private void InitInkToText()
    {
        SelToTextBtn.Click += async (_, __) => await ConvertSelectionToTextAsync();
    }

    private async Task ConvertSelectionToTextAsync()
    {
        if (_doc is null || _convertingInk || !Canvas.SelectionHasPenInk) return;
        var strokes = Canvas.SelectedPenStrokes();
        if (strokes.Count == 0) return;

        _convertingInk = true;
        SelToTextBtn.IsEnabled = false;
        List<RecognizedWord> words;
        try
        {
            if (!await Task.Run(() => HandwritingRecognizer.IsAvailable))
            {
                await Notify("Handwriting recognition isn't available",
                    "Add a language with handwriting support in Windows Settings → Time & language → Language & region, then try again.");
                return;
            }
            words = await HandwritingRecognizer.RecognizeAsync(strokes);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Converting handwriting to text");
            await Notify("Couldn't read the handwriting", ex.Message);
            return;
        }
        finally
        {
            _convertingInk = false;
            SelToTextBtn.IsEnabled = true;
        }

        if (words.Count == 0)
        {
            await Notify("No words found", "The handwriting recognizer couldn't read any words in the selection.");
            return;
        }

        var text = JoinIntoLines(words);
        var edited = await AskConvertedTextAsync(text);
        if (edited is null) return;

        if (edited.Value.replace)
        {
            var color = strokes[0].Color;
            float fontSize = EstimateFontSize(words);
            if (Canvas.ReplaceSelectedPenInkWithText(edited.Value.text, color, fontSize))
                UpdateSelectionUi();
        }
        else
        {
            var dp = new DataPackage();
            dp.SetText(edited.Value.text);
            Clipboard.SetContent(dp);
            ShowToast("Text copied");
        }
    }

    // Returns the (possibly edited) text and whether to replace the ink with it
    // (true) or copy it (false); null = cancelled or emptied.
    private async Task<(string text, bool replace)?> AskConvertedTextAsync(string text)
    {
        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            MaxHeight = 320,
            IsSpellCheckEnabled = true,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = "Fix anything the recognizer misread. Replacing swaps the ink for a text box; Undo brings the ink back.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
        });
        var dlg = new ContentDialog
        {
            Title = "Convert to text",
            Content = panel,
            PrimaryButtonText = "Replace ink",
            SecondaryButtonText = "Copy text",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        box.TextChanged += (_, __) =>
        {
            bool any = !string.IsNullOrWhiteSpace(box.Text);
            dlg.IsPrimaryButtonEnabled = any;
            dlg.IsSecondaryButtonEnabled = any;
        };
        box.Loaded += (_, __) => box.Focus(FocusState.Programmatic);

        var result = await dlg.ShowAsync();
        // The TextBox uses \r for new lines; keep \n so other apps and PDF
        // export see ordinary line breaks.
        var final = (box.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (final.Length == 0) return null;
        return result switch
        {
            ContentDialogResult.Primary => (final, true),
            ContentDialogResult.Secondary => (final, false),
            _ => null,
        };
    }

    // Puts recognized words back into lines of writing: words whose vertical
    // centers fall inside a line's band join it, lines read top to bottom and
    // words left to right.
    internal static string JoinIntoLines(IReadOnlyList<RecognizedWord> words)
    {
        var lines = new List<(float top, float bottom, List<RecognizedWord> words)>();
        foreach (var w in words.OrderBy(w => w.Y + w.H / 2))
        {
            float cy = w.Y + w.H / 2;
            int hit = lines.FindIndex(l => cy >= l.top && cy <= l.bottom);
            if (hit < 0) lines.Add((w.Y, w.Y + w.H, new List<RecognizedWord> { w }));
            else
            {
                var l = lines[hit];
                l.words.Add(w);
                // Only stretch the band a little, so a tall word doesn't swallow
                // the next line.
                lines[hit] = (Math.Min(l.top, w.Y + w.H * 0.25f), Math.Max(l.bottom, w.Y + w.H * 0.75f), l.words);
            }
        }
        var sb = new StringBuilder();
        foreach (var l in lines.OrderBy(l => l.top))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(string.Join(" ", l.words.OrderBy(w => w.X).Select(w => w.T.Trim())));
        }
        return sb.ToString();
    }

    // A word's ink box spans ascenders to descenders, roughly one em, so the
    // typed text comes out about as big as the writing was.
    private static float EstimateFontSize(IReadOnlyList<RecognizedWord> words)
    {
        var heights = words.Select(w => w.H).Where(h => h > 1).OrderBy(h => h).ToList();
        if (heights.Count == 0) return 18f;
        return Math.Clamp(heights[heights.Count / 2] * 0.8f, 12f, 72f);
    }
}
