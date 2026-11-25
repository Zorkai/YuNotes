using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using YuNotes.Services;
using TextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace YuNotes.Views;

// Study mode, built on tape:
//  - Quiz: walks through the note's tape strips in reading order, in place.
//    The asked strip is outlined and scrolled into view; Show answer (Space,
//    or tapping the strip) reveals it, then Again (1) / Got it (2).
//  - Flashcards: each strip as a card, a picture of the strip and the lines
//    around it; flip to see what's under the tape. Again puts a card back.
// The first answer for each strip in a session is kept (StudyDeck): misses
// get a red dot on the page and come first next time.
public sealed partial class EditorPage
{
    private bool _studying;
    private List<StudyItem> _studyQueue = new();
    private int _studyIndex;
    private bool _studyRevealed;
    private int _studyGot;
    private readonly List<StudyItem> _studyMissedNow = new();
    private readonly List<KeyboardAccelerator> _studyAccels = new();

    private string NotePath => _doc?.Info.FilePath ?? "";

    private void InitStudy()
    {
        StudyQuizItem.Click   += (_, __) => StartQuiz();
        StudyCardsItem.Click  += async (_, __) => await ShowFlashcardsAsync(null);
        StudyMissedItem.Click += (_, __) => StartQuiz(missedOnly: true);
        StudyResetItem.Click  += (_, __) =>
        {
            StudyDeck.Clear(NotePath);
            App.Services.Settings.Save();
            Canvas.InvalidateAll();
            ShowToast("Missed marks cleared");
        };
        StudyMenu.Opening += (_, __) =>
        {
            int missed = StudyDeck.Missed(NotePath).Count;
            StudyMissedItem.Text = missed > 0 ? $"Quiz only the missed ({missed})" : "Quiz only the missed";
            StudyMissedItem.IsEnabled = StudyResetItem.IsEnabled = missed > 0;
        };

        TapeQuizBtn.Click   += (_, __) => { TapeFlyout.Hide(); StartQuiz(); };
        TapeCardsBtn.Click  += async (_, __) => { TapeFlyout.Hide(); await ShowFlashcardsAsync(null); };

        StudyShowBtn.Click  += (_, __) => ShowStudyAnswer();
        StudyAgainBtn.Click += (_, __) => AnswerStudy(false);
        StudyGotBtn.Click   += (_, __) => AnswerStudy(true);
        StudyExitBtn.Click  += (_, __) => ExitStudy();
        StudyCardsBtn.Click += async (_, __) =>
        {
            // Carry on with the strips not asked yet.
            var rest = _studyQueue.Skip(_studyIndex).ToList();
            ExitStudy();
            await ShowFlashcardsAsync(rest.Count > 0 ? rest : null);
        };

        // Tapping the asked strip open counts as Show answer.
        Canvas.TapeToggled += (_, tape) =>
        {
            if (_studying && !_studyRevealed && CurrentStudyItem?.Id == tape.Id
                && Canvas.Context.RevealedTapeIds.Contains(tape.Id))
                ShowStudyAnswer();
        };

        var f6 = new KeyboardAccelerator { Key = VirtualKey.F6 };
        f6.Invoked += (_, e) =>
        {
            if (_presenting || ScreenshotOverlay.Visibility == Visibility.Visible) return;
            if (_studying) ExitStudy(); else StartQuiz();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(f6);
    }

    // The Study button shows once the note has tape.
    private void UpdateStudyButton()
    {
        if (StudyShell is null) return;
        StudyShell.Visibility = _doc is not null && Canvas.TapeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private StudyItem? CurrentStudyItem =>
        _studying && _studyIndex >= 0 && _studyIndex < _studyQueue.Count ? _studyQueue[_studyIndex] : null;

    // ── Quiz ─────────────────────────────────────────────────────────────────

    private void StartQuiz(bool missedOnly = false, List<StudyItem>? only = null)
    {
        if (_doc is null || _presenting) return;
        var items = Canvas.StudyItems();
        if (items.Count == 0)
        {
            ShowToast("Put tape over answers first, then study them here", error: true);
            return;
        }
        StudyDeck.Prune(NotePath, items.Select(i => i.Id));
        if (missedOnly)
        {
            var missed = StudyDeck.Missed(NotePath);
            items = items.Where(i => missed.Contains(i.Id)).ToList();
            if (items.Count == 0) { ShowToast("Nothing missed in this note"); return; }
        }
        if (only is not null)
        {
            var ids = new HashSet<string>(only.Select(i => i.Id));
            items = items.Where(i => ids.Contains(i.Id)).ToList();
        }

        if (_studying) ExitStudy();
        if (SearchPanel.Visibility == Visibility.Visible) CloseSearch();
        Canvas.ClearSelection();
        UpdateSelectionUi();
        Canvas.SetAllTapeRevealed(false);

        _studyQueue = items;
        _studyIndex = 0;
        _studyGot = 0;
        _studyMissedNow.Clear();
        _studying = true;
        StudyBar.Visibility = Visibility.Visible;

        void Key(VirtualKey key, Action action)
        {
            var a = new KeyboardAccelerator { Key = key };
            a.Invoked += (_, e) =>
            {
                // Typing in a text box keeps its keys.
                if (Canvas.Context.EditingTextId is not null) return;
                if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
                action();
                e.Handled = true;
            };
            _studyAccels.Add(a);
            KeyboardAccelerators.Add(a);
        }
        Key(VirtualKey.Space, ShowStudyAnswer);
        Key(VirtualKey.Enter, ShowStudyAnswer);
        Key(VirtualKey.Number1, () => AnswerStudy(false));
        Key(VirtualKey.NumberPad1, () => AnswerStudy(false));
        Key(VirtualKey.Number2, () => AnswerStudy(true));
        Key(VirtualKey.NumberPad2, () => AnswerStudy(true));

        ShowStudyItem();
    }

    private void ShowStudyItem()
    {
        if (_studyIndex >= _studyQueue.Count) { _ = FinishQuizAsync(); return; }
        var item = _studyQueue[_studyIndex];
        _studyRevealed = false;
        Canvas.SetTapeRevealed(item, false);
        Canvas.SetStudyFocus(item);
        Canvas.ScrollToRect(item.PageIndex, new Windows.Foundation.Rect(item.X, item.Y, item.W, item.H));

        StudyProgressText.Text = $"{_studyIndex + 1} of {_studyQueue.Count}";
        StudyProgress.Value = 100.0 * _studyIndex / _studyQueue.Count;
        StudyShowBtn.Visibility = Visibility.Visible;
        StudyAgainBtn.Visibility = StudyGotBtn.Visibility = Visibility.Collapsed;
        KeepFocusOffStudyBar();
    }

    private void ShowStudyAnswer()
    {
        if (CurrentStudyItem is not { } item || _studyRevealed) return;
        _studyRevealed = true;
        Canvas.SetTapeRevealed(item, true);
        StudyShowBtn.Visibility = Visibility.Collapsed;
        StudyAgainBtn.Visibility = StudyGotBtn.Visibility = Visibility.Visible;
        KeepFocusOffStudyBar();
    }

    // Space, Enter, 1 and 2 are the quiz's keys. A focused bar button would
    // take Space/Enter itself (and a button that hides passes focus to the
    // next one, e.g. Switch to flashcards), so focus stays on the page.
    private void KeepFocusOffStudyBar()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInside(focused, StudyBar))
            Canvas.Focus(FocusState.Programmatic);
    }

    private static bool IsInside(DependencyObject el, DependencyObject container)
    {
        for (var d = el; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d == container) return true;
        return false;
    }

    private void AnswerStudy(bool gotIt)
    {
        if (CurrentStudyItem is not { } item) return;
        // Pressing 1 / 2 before revealing shows the answer first.
        if (!_studyRevealed) { ShowStudyAnswer(); return; }
        StudyDeck.Record(NotePath, item.Id, gotIt);
        if (gotIt) _studyGot++; else _studyMissedNow.Add(item);
        Canvas.SetStudyFocus(null);   // also redraws the strip with its new dot
        _studyIndex++;
        ShowStudyItem();
    }

    private async Task FinishQuizAsync()
    {
        int total = _studyQueue.Count, got = _studyGot;
        var missed = _studyMissedNow.ToList();
        ExitStudy();

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = missed.Count == 0
                ? "You knew every strip in this round."
                : missed.Count == 1
                    ? "The strip you missed now has a red dot on the page and comes first next time."
                    : $"The {missed.Count} strips you missed now have a red dot on the page and come first next time.",
        });
        var dlg = new ContentDialog
        {
            Title = $"{got} of {total} right",
            Content = body,
            PrimaryButtonText = missed.Count > 0 ? $"Review the {missed.Count} missed" : "",
            SecondaryButtonText = "Flashcards",
            CloseButtonText = "Done",
            DefaultButton = missed.Count > 0 ? ContentDialogButton.Primary : ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary) StartQuiz(only: missed);
        else if (res == ContentDialogResult.Secondary) await ShowFlashcardsAsync(missed.Count > 0 ? missed : null);
    }

    /// <summary>Leaves the quiz (Esc, the X, switching notes, presenting).
    /// Answers so far are kept; strips stay as they are on the page.</summary>
    private void ExitStudy()
    {
        if (!_studying) return;
        _studying = false;
        foreach (var a in _studyAccels) KeyboardAccelerators.Remove(a);
        _studyAccels.Clear();
        StudyBar.Visibility = Visibility.Collapsed;
        Canvas.SetStudyFocus(null);
        App.Services.Settings.Save();
        Canvas.InvalidateAll();
    }

    // ── Flashcards ───────────────────────────────────────────────────────────

    private async Task ShowFlashcardsAsync(List<StudyItem>? items)
    {
        if (_doc is null) return;
        ExitStudy();
        var all = Canvas.StudyItems();
        if (all.Count == 0)
        {
            ShowToast("Put tape over answers first, then study them here", error: true);
            return;
        }
        StudyDeck.Prune(NotePath, all.Select(i => i.Id));
        var deck = items is null ? StudyDeck.MissedFirst(all, NotePath) : items.ToList();
        var answered = new HashSet<string>();   // first answer per card is the one kept
        int index = 0, gotFirstTime = 0, total = deck.Count;
        bool flipped = false;
        var cache = new Dictionary<(string, bool), BitmapImage>();

        var res = Application.Current.Resources;
        var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 340, MaxWidth = 600 };
        var cardFrame = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = (Brush)res["AppBorderBrush"], BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(10),
            MinHeight = 180, MinWidth = 420, Child = image,
        };
        var card = new Button
        {
            Content = cardFrame, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, "Flip card");
        var sideText = new TextBlock { FontSize = 13, Foreground = (Brush)res["AppTextSecondaryBrush"], HorizontalAlignment = HorizontalAlignment.Center };
        var progressText = new TextBlock { FontSize = 13, Foreground = (Brush)res["AppTextSecondaryBrush"] };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4, Width = 160, VerticalAlignment = VerticalAlignment.Center };

        Button Pill(string text, string glyph, bool accent)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18 });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = sp, Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(19) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, text);
            if (accent)
            {
                b.Background = (Brush)res["AppAccentSoftBrush"];
                b.Foreground = (Brush)res["AppAccentBrush"];
            }
            return b;
        }
        var flipBtn = Pill("Flip", "", false);
        var againBtn = Pill("Again", "", false);
        var gotBtn = Pill("Got it", "", true);
        ToolTipService.SetToolTip(flipBtn, "Space");
        ToolTipService.SetToolTip(againBtn, "Ask again at the end of the deck (1)");
        ToolTipService.SetToolTip(gotBtn, "2");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(flipBtn);
        actions.Children.Add(againBtn);
        actions.Children.Add(gotBtn);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var prog = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        prog.Children.Add(progressText);
        prog.Children.Add(progress);
        footer.Children.Add(prog);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var summary = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed, Padding = new Thickness(0, 24, 0, 24) };
        var summaryTitle = new TextBlock { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var summaryText = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = (Brush)res["AppTextSecondaryBrush"] };
        var restartBtn = Pill("Study the missed again", "", true);
        restartBtn.HorizontalAlignment = HorizontalAlignment.Center;
        summary.Children.Add(summaryTitle);
        summary.Children.Add(summaryText);
        summary.Children.Add(restartBtn);

        var cardArea = new StackPanel { Spacing = 10 };
        cardArea.Children.Add(sideText);
        cardArea.Children.Add(card);
        var root = new StackPanel { Spacing = 16, MinWidth = 460 };
        root.Children.Add(cardArea);
        root.Children.Add(summary);
        root.Children.Add(footer);

        var dlg = new ContentDialog
        {
            Title = $"Flashcards · {_doc.Info.Title}",
            Content = root,
            CloseButtonText = "Done",
            XamlRoot = XamlRoot,
        };
        // Wide enough for a line of handwriting.
        dlg.Resources["ContentDialogMaxWidth"] = 720.0;

        async Task<BitmapImage?> Picture(StudyItem item, bool back)
        {
            if (cache.TryGetValue((item.Id, back), out var hit)) return hit;
            var png = Canvas.RenderStudyCard(item, back);
            if (png is null) return null;
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(png);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            cache[(item.Id, back)] = bmp;
            return bmp;
        }

        List<StudyItem> missedThisRound = new();

        async Task Render()
        {
            if (index >= deck.Count)
            {
                cardArea.Visibility = Visibility.Collapsed;
                actions.Visibility = Visibility.Collapsed;
                summary.Visibility = Visibility.Visible;
                progress.Value = 100;
                progressText.Text = "Deck finished";
                summaryTitle.Text = $"{gotFirstTime} of {total} right first time";
                summaryText.Text = missedThisRound.Count == 0
                    ? "You knew every card."
                    : "Cards you missed have a red dot on the page and come first next time.";
                restartBtn.Visibility = missedThisRound.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            var item = deck[index];
            cardArea.Visibility = Visibility.Visible;
            actions.Visibility = Visibility.Visible;
            summary.Visibility = Visibility.Collapsed;
            progressText.Text = $"{Math.Min(index + 1, deck.Count)} of {deck.Count}";
            progress.Value = 100.0 * index / deck.Count;
            sideText.Text = flipped ? $"Answer · page {item.PageIndex + 1}" : $"Page {item.PageIndex + 1} · tap the card to flip";
            flipBtn.Visibility = flipped ? Visibility.Collapsed : Visibility.Visible;
            againBtn.Visibility = gotBtn.Visibility = flipped ? Visibility.Visible : Visibility.Collapsed;
            image.Source = await Picture(item, flipped);
            // Warm the back so flipping is instant.
            if (!flipped) _ = Picture(item, true);
        }

        async Task Flip() { if (index < deck.Count) { flipped = !flipped; await Render(); } }
        async Task Answer(bool gotIt)
        {
            if (index >= deck.Count) return;
            if (!flipped) { await Flip(); return; }
            var item = deck[index];
            if (answered.Add(item.Id))
            {
                StudyDeck.Record(NotePath, item.Id, gotIt);
                if (gotIt) gotFirstTime++; else missedThisRound.Add(item);
            }
            if (!gotIt) deck.Add(item);   // back of the deck
            index++;
            flipped = false;
            await Render();
        }

        card.Click += async (_, __) => await Flip();
        flipBtn.Click += async (_, __) => await Flip();
        againBtn.Click += async (_, __) => await Answer(false);
        gotBtn.Click += async (_, __) => await Answer(true);
        restartBtn.Click += async (_, __) =>
        {
            deck = missedThisRound.ToList();
            missedThisRound = new();
            answered.Clear();
            index = 0; gotFirstTime = 0; total = deck.Count; flipped = false;
            await Render();
        };
        // A focused button would also act on Space when it's released.
        dlg.PreviewKeyUp += (_, e) => { if (e.Key == VirtualKey.Space) e.Handled = true; };
        dlg.PreviewKeyDown += async (_, e) =>
        {
            switch (e.Key)
            {
                case VirtualKey.Space: e.Handled = true; await Flip(); break;
                case VirtualKey.Number1 or VirtualKey.NumberPad1: e.Handled = true; await Answer(false); break;
                case VirtualKey.Number2 or VirtualKey.NumberPad2: e.Handled = true; await Answer(true); break;
            }
        };

        await Render();
        await dlg.ShowAsync();
        App.Services.Settings.Save();
        Canvas.InvalidateAll();   // missed dots
    }
}
