using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;
using YuNotes.Models;
using YuNotes.Rendering;
using YuNotes.Tools;

namespace YuNotes.Views;

public sealed partial class EditorPage
{
    private void AddBlankPage()
    {
        if (_doc is null) return;
        var last = _doc.Pages.LastOrDefault();
        // Use the pre-extension width so extended PDF pages don't dictate new-page size.
        double newW = last?.BackgroundContentWidth > 0 ? last.BackgroundContentWidth : last?.Width ?? 1240;
        var newPage = new NotePage { Index = _doc.Pages.Count, Width = newW, Height = last?.Height ?? 1754 };
        ContinueTemplate(newPage, last);
        _doc.Pages.Add(newPage);
        Canvas.Rebuild();
        App.Services.History.RecordPageListChange();   // undoable: undo removes the page
        UpdateHistoryUi();
        MarkPageSetChanged();
        if (_pagePanelOpen) _ = RefreshPagePanelAsync();
    }

    private async Task AddPagesFromPdfAsync()
    {
        if (_doc is null) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        InitPicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var temp = new Document();
            App.Services.PdfImport.ImportInto(temp, file.Path);
            foreach (var p in temp.Pages)
            {
                p.Index = _doc.Pages.Count;
                _doc.Pages.Add(p);
            }
            Canvas.Rebuild();
            App.Services.History.RecordPageListChange();   // undoable: undo removes the added pages
            UpdateHistoryUi();
            MarkPageSetChanged();
            if (_pagePanelOpen) _ = RefreshPagePanelAsync();
        }
        catch (Exception ex) { await Notify("Add pages failed", ex.Message); }
    }

    // ─── Page sorter panel ───────────────────────────────────────────────────────

    private bool _pagePanelOpen;
    private bool _pagePanelEditMode;

    // Drag-to-reorder state
    private int   _dragSourceIndex      = -1;
    private int   _dragTargetIndex      = -1;
    private bool  _pageDragging;
    private Windows.Foundation.Point _dragStartPos;
    // Vertical travel (DIPs) before a press turns into a reorder drag. Kept generously
    // large so a tap with a little hand/pen wobble reads as a click, not a drag.
    private const double PageDragThreshold = 16.0;
    private Border? _dropLine;
    private Button? _dragSourceCard;
    private bool  _suppressNextCardClick;

    // Multi-select state (Shift/Ctrl-click a range or set of page cards).
    private readonly HashSet<int> _selectedPages = new();
    private int  _selectionAnchor   = -1;   // anchor for Shift-range selection
    private bool _dragWholeSelection;        // true while dragging the selected group

    private void TogglePagePanel()
    {
        _pagePanelOpen = !_pagePanelOpen;
        PagePanel.Visibility = _pagePanelOpen ? Visibility.Visible : Visibility.Collapsed;
        ClearPageSelection();
        if (_pagePanelOpen) _ = RefreshPagePanelAsync();
    }

    private async Task RefreshPagePanelAsync()
    {
        if (_doc is null) return;
        _panelBookmarkSig = BookmarkSignature();
        PageListPanel.Children.Clear();
        UpdatePageIndicator();

        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            byte[]? thumbBytes = null;
            try { thumbBytes = Canvas.RenderCurrentPagePng(i, scale: 0.12f); } catch { }

            Microsoft.UI.Xaml.Media.Imaging.BitmapImage? bmp = null;
            if (thumbBytes is { Length: > 0 })
            {
                try
                {
                    bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using var ms = new System.IO.MemoryStream(thumbBytes);
                    await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                }
                catch { bmp = null; }
            }
            PageListPanel.Children.Add(BuildPageCard(i, bmp));
        }

        // "+" add-page card at the bottom
        var addCard = new Button
        {
            Width = 120, Height = 160,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)Application.Current.Resources["AppSurfaceVariantBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
            BorderThickness = new Thickness(1.5),
            Content = new FontIcon { Glyph = "", FontSize = 24 }
        };
        ToolTipService.SetToolTip(addCard, "Add page");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(addCard, "Add page");
        addCard.Click += (_, __) => AddBlankPage();
        PageListPanel.Children.Add(addCard);
    }

    private UIElement BuildPageCard(int index, Microsoft.UI.Xaml.Media.Imaging.BitmapImage? thumbnail)
    {
        bool isActive   = _doc!.Pages.Count > 0 && ReferenceEquals(_doc.Pages[index], Canvas.ActivePage);
        bool isSelected = _selectedPages.Contains(index);
        bool accentEdge = isActive || isSelected;

        var cardBorder = new Border
        {
            Width = 120, Height = 160,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(accentEdge ? 2.5 : 1.5),
            BorderBrush = accentEdge
                ? (Brush)Application.Current.Resources["AppAccentBrush"]
                : (Brush)Application.Current.Resources["AppBorderBrush"],
            Background = (Brush)Application.Current.Resources["AppSurfaceVariantBrush"],
        };

        var cardGrid = new Grid();

        if (thumbnail is not null)
        {
            cardGrid.Children.Add(new Image
            {
                Source = thumbnail,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            });
        }
        else
        {
            cardGrid.Children.Add(new FontIcon
            {
                Glyph = "", FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"]
            });
        }

        // Selection tint overlay (below the buttons so they stay clickable).
        var selOverlay = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["AppAccentSoftBrush"],
            Opacity = isSelected ? 0.35 : 0,
            IsHitTestVisible = false,
            Tag = "selOverlay"
        };
        cardGrid.Children.Add(selOverlay);

        // Three-dot context menu (top-right)
        var menuBtn = new Button
        {
            Width = 24, Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 11, Foreground = new SolidColorBrush(Colors.White) },
            Flyout = BuildPageMenuFlyout(index)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(menuBtn, $"Page {index + 1} options");
        cardGrid.Children.Add(menuBtn);

        // Delete button (top-left, visible in edit mode only)
        if (_pagePanelEditMode)
        {
            var delBtn = new Button
            {
                Width = 24, Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 4, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(220, 224, 58, 58)),
                BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Colors.White) },
                Tag = index
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delBtn, $"Delete page {index + 1}");
            delBtn.Click += async (_, __) =>
            {
                int i = (int)((Button)delBtn).Tag;
                if (_selectedPages.Count > 1 && _selectedPages.Contains(i))
                    await DeleteSelectedPagesAsync();
                else
                    await DeletePageAsync(i);
            };
            cardGrid.Children.Add(delBtn);
        }

        // Selection check badge (bottom-right).
        var selBadge = new Border
        {
            Width = 22, Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = (Brush)Application.Current.Resources["AppAccentBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 4),
            Opacity = isSelected ? 1 : 0,
            IsHitTestVisible = false,
            Tag = "selBadge",
            Child = new FontIcon
            {
                Glyph = "",   // CheckMark (Segoe MDL2)
                FontSize = 12,
                Foreground = new SolidColorBrush(Colors.White)
            }
        };
        cardGrid.Children.Add(selBadge);

        // Bookmark label (bottom-left).
        if (_doc.Pages[index].Bookmark is { } bookmark)
        {
            cardGrid.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 30, 4),
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.Resources["AppAccentBrush"],
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = bookmark, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis,
                },
            });
        }

        cardBorder.Child = cardGrid;

        // Page number label below card
        var pageNumLabel = new TextBlock
        {
            Text = $"{index + 1}",
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 12,
            Foreground = accentEdge
                ? (Brush)Application.Current.Resources["AppAccentBrush"]
                : (Brush)Application.Current.Resources["AppTextSecondaryBrush"]
        };

        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(cardBorder);
        stack.Children.Add(pageNumLabel);

        // Wrap the card + label in a tap button for navigation
        var clickBtn = new Button
        {
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Center,
            Tag = index
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clickBtn, $"Go to page {index + 1}");
        clickBtn.Content = stack;

        // Navigation click — suppressed when a drag or selection gesture just finished.
        int capturedIndex = index;
        clickBtn.Click += (_, __) =>
        {
            if (_suppressNextCardClick) { _suppressNextCardClick = false; return; }
            // A plain click clears any multi-selection and navigates.
            if (_selectedPages.Count > 0) ClearPageSelection();
            Canvas.ScrollToPage(capturedIndex);
        };

        // ── Drag-to-reorder + Shift/Ctrl multi-select (mouse / pen only) ──
        // NOTE: a Button marks pointer events as Handled in its own class handler,
        // so `+=` handlers never fire. We must use AddHandler(..., handledEventsToo: true).
        clickBtn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;

            // Decide afresh each press whether the upcoming Click should be swallowed.
            // (Resetting here keeps the flag from getting stuck true when a previous
            // gesture's Click never fired — e.g. the pointer drifted off the card, or
            // the click followed a captured drag — which would otherwise eat this click.)
            _suppressNextCardClick = false;

            // e.KeyModifiers is unreliable for mouse pointer events in WinUI 3 desktop,
            // so fall back to the live keyboard state. GetKeyStateForCurrentThread is itself
            // unreliable for the *aggregate* VK_CONTROL/VK_SHIFT — it commonly reports only
            // the physical Left/Right keys — so probe all variants.
            var mods   = e.KeyModifiers;
            bool ctrl  = (mods & VirtualKeyModifiers.Control) != 0
                         || IsModifierDown(VirtualKey.Control, VirtualKey.LeftControl, VirtualKey.RightControl);
            bool shift = (mods & VirtualKeyModifiers.Shift)   != 0
                         || IsModifierDown(VirtualKey.Shift, VirtualKey.LeftShift, VirtualKey.RightShift);

            if (ctrl || shift)
            {
                // Selection gesture — no navigation, no drag.
                if (shift) SelectPageRangeTo(capturedIndex);
                else       TogglePageSelection(capturedIndex);
                _suppressNextCardClick = true;
                _dragSourceCard  = null;
                _dragSourceIndex = -1;
                return;
            }

            _dragSourceIndex = capturedIndex;
            _dragSourceCard  = clickBtn;
            _dragStartPos    = e.GetCurrentPoint(PageListPanel).Position;
            _pageDragging    = false;

            // Drag the whole group only when grabbing a card that's part of a
            // multi-selection; otherwise a plain interaction resets the selection.
            if (_selectedPages.Contains(capturedIndex) && _selectedPages.Count > 1)
            {
                _dragWholeSelection = true;
            }
            else
            {
                _dragWholeSelection = false;
                if (_selectedPages.Count > 0) ClearPageSelection();
                // Remember this card as the anchor for a following Shift-click range.
                _selectionAnchor = capturedIndex;
            }
        }), handledEventsToo: true);

        clickBtn.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((s, e) =>
        {
            if (_dragSourceCard != clickBtn) return;
            var pos = e.GetCurrentPoint(PageListPanel).Position;

            if (!_pageDragging)
            {
                if (Math.Abs(pos.Y - _dragStartPos.Y) < PageDragThreshold) return;
                // Threshold exceeded → start drag.
                _pageDragging = true;
                clickBtn.CapturePointer(e.Pointer);
                SetDragOpacity(0.45);
                EnsureDropLine();
            }

            UpdateDropIndicator(pos.Y);
        }), handledEventsToo: true);

        // The drag ends on whichever pointer event fires first. On a normal drop WinUI raises
        // PointerCaptureLost *before* PointerReleased, so the commit must be able to run from
        // capture-loss — committing only in PointerReleased misses the drop entirely.
        clickBtn.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((s, e) => { _ = EndPageDragAsync(commit: true); }), handledEventsToo: true);
        clickBtn.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler((s, e) => { _ = EndPageDragAsync(commit: true); }), handledEventsToo: true);
        clickBtn.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler((s, e) => { _ = EndPageDragAsync(commit: false); }), handledEventsToo: true);

        return clickBtn;
    }

    private MenuFlyout BuildPageMenuFlyout(int pageIndex)
    {
        // Constrained so the in-app acrylic menu background can blur the app
        // content beneath (see AppPopupAcrylicBrush in Colors.xaml).
        var flyout = new MenuFlyout { ShouldConstrainToRootBounds = true };

        var addAbove = new MenuFlyoutItem { Text = "Add page above" };
        addAbove.Click += (_, __) => InsertPageAt(pageIndex);

        var addBelow = new MenuFlyoutItem { Text = "Add page below" };
        addBelow.Click += (_, __) => InsertPageAt(pageIndex + 1);

        var duplicate = new MenuFlyoutItem { Text = "Duplicate page" };
        duplicate.Click += async (_, __) => await DuplicatePageAsync(pageIndex);

        var moveUp = new MenuFlyoutItem { Text = "Move up", IsEnabled = pageIndex > 0 };
        moveUp.Click += async (_, __) => await MovePageAsync(pageIndex, pageIndex - 1);

        var moveDown = new MenuFlyoutItem { Text = "Move down", IsEnabled = _doc != null && pageIndex < _doc.Pages.Count - 1 };
        moveDown.Click += async (_, __) => await MovePageAsync(pageIndex, pageIndex + 1);

        var delete = new MenuFlyoutItem { Text = "Delete page" };
        delete.Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 58, 58));
        delete.Click += async (_, __) => await DeletePageAsync(pageIndex);

        var extendPage = new MenuFlyoutItem { Text = "Extend page width…" };
        extendPage.Click += async (_, __) =>
        {
            if (_doc is not null && pageIndex < _doc.Pages.Count)
                await ShowExtendPageDialogAsync(_doc.Pages[pageIndex]);
        };

        flyout.Items.Add(BuildBookmarkSubmenu(pageIndex));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(addAbove);
        flyout.Items.Add(addBelow);
        flyout.Items.Add(duplicate);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(moveUp);
        flyout.Items.Add(moveDown);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(extendPage);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(delete);
        return flyout;
    }

    // ─── Page bookmarks ──────────────────────────────────────────────────────────
    // A page can carry one short label. Bookmarked pages show it on their
    // thumbnail, are listed under the bookmark button in the page panel, and
    // the label is searchable like any text on the page.

    private static readonly string[] BookmarkLabels = { "Definition", "Theorem", "Example", "Exercise", "Exam", "Important" };

    private MenuFlyoutSubItem BuildBookmarkSubmenu(int pageIndex)
    {
        var current = _doc is not null && pageIndex < _doc.Pages.Count ? _doc.Pages[pageIndex].Bookmark : null;
        var sub = new MenuFlyoutSubItem { Text = current is null ? "Bookmark" : $"Bookmark: {current}" };
        foreach (var label in BookmarkLabels)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = label == current };
            item.Click += (_, __) => SetBookmark(pageIndex, label);
            sub.Items.Add(item);
        }
        var custom = new MenuFlyoutItem { Text = "Custom label…" };
        custom.Click += async (_, __) =>
        {
            var box = new TextBox { PlaceholderText = "Lagrange multipliers", Text = current ?? "", MaxLength = 40 };
            var dlg = new ContentDialog
            {
                Title = $"Bookmark page {pageIndex + 1}", Content = box,
                PrimaryButtonText = "Save", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
            };
            box.Loaded += (_, __) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0)
                SetBookmark(pageIndex, box.Text.Trim());
        };
        sub.Items.Add(new MenuFlyoutSeparator());
        sub.Items.Add(custom);
        if (current is not null)
        {
            var remove = new MenuFlyoutItem { Text = "Remove bookmark" };
            remove.Click += (_, __) => SetBookmark(pageIndex, null);
            sub.Items.Add(remove);
        }
        return sub;
    }

    private void SetBookmark(int pageIndex, string? label)
    {
        if (_doc is null || pageIndex < 0 || pageIndex >= _doc.Pages.Count) return;
        var page = _doc.Pages[pageIndex];
        if (page.Bookmark == label) return;
        page.Bookmark = label;
        _doc.IsDirty = true;
        App.Services.History.RecordMutation(page);
        UpdateHistoryUi();
        UpdateSaveStateUi();
        ScheduleAutosave();
        if (_pagePanelOpen) _ = RefreshPagePanelAsync();
    }

    private void RebuildBookmarksFlyout()
    {
        BookmarksFlyout.Items.Clear();
        if (_doc is null) return;
        bool any = false;
        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            if (_doc.Pages[i].Bookmark is not { } label) continue;
            int idx = i;
            var item = new MenuFlyoutItem { Text = $"Page {i + 1} · {label}" };
            item.Click += (_, __) => Canvas.ScrollToPage(idx);
            BookmarksFlyout.Items.Add(item);
            any = true;
        }
        if (!any)
            BookmarksFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "No bookmarks yet. Use a page's ⋯ menu.", IsEnabled = false,
            });
    }

    private void UpdatePageIndicator()
    {
        if (_doc is null) return;
        var activePage = Canvas.ActivePage;
        int idx = activePage is not null ? _doc.Pages.IndexOf(activePage) + 1 : 1;
        PageIndicator.Text = $"{idx} / {_doc.Pages.Count}";
    }

    /// <summary>Updates card highlight borders, selection tints and page number colors in-place (no thumbnail re-render).</summary>
    private void UpdatePagePanelHighlight()
    {
        if (!_pagePanelOpen || _doc is null) return;
        UpdatePageIndicator();

        var activePage = Canvas.ActivePage;
        int activeIdx = activePage is not null ? _doc.Pages.IndexOf(activePage) : -1;
        var accentBrush = (Brush)Application.Current.Resources["AppAccentBrush"];
        var borderBrush = (Brush)Application.Current.Resources["AppBorderBrush"];
        var mutedBrush  = (Brush)Application.Current.Resources["AppTextSecondaryBrush"];

        // PageListPanel children: page cards (Buttons with int Tag), plus the add card and maybe the drop line.
        foreach (var child in PageListPanel.Children)
        {
            if (child is not Button clickBtn || clickBtn.Tag is not int i) continue;
            if (clickBtn.Content is not StackPanel sp || sp.Children.Count < 2) continue;

            bool isSelected = _selectedPages.Contains(i);
            bool accentEdge = i == activeIdx || isSelected;

            if (sp.Children[0] is Border cardBorder)
            {
                cardBorder.BorderThickness = new Thickness(accentEdge ? 2.5 : 1.5);
                cardBorder.BorderBrush = accentEdge ? accentBrush : borderBrush;

                var (overlay, badge) = GetCardSelectionDecor(cardBorder);
                if (overlay is not null) overlay.Opacity = isSelected ? 0.35 : 0;
                if (badge   is not null) badge.Opacity   = isSelected ? 1 : 0;
            }
            if (sp.Children[1] is TextBlock tb)
                tb.Foreground = accentEdge ? accentBrush : mutedBrush;
        }

        UpdateSelectionBar();
    }

    // ─── Multi-select helpers ──────────────────────────────────────────────────

    private static bool IsModifierDown(params VirtualKey[] keys)
    {
        foreach (var key in keys)
            if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                 & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down)
                return true;
        return false;
    }

    private static (Border? overlay, Border? badge) GetCardSelectionDecor(Border cardBorder)
    {
        Border? overlay = null, badge = null;
        if (cardBorder.Child is Grid g)
            foreach (var c in g.Children)
                if (c is Border b)
                {
                    if ((b.Tag as string) == "selOverlay") overlay = b;
                    else if ((b.Tag as string) == "selBadge") badge = b;
                }
        return (overlay, badge);
    }

    private void TogglePageSelection(int index)
    {
        if (!_selectedPages.Add(index)) _selectedPages.Remove(index);
        _selectionAnchor = index;
        UpdatePagePanelHighlight();
    }

    private void SelectPageRangeTo(int index)
    {
        if (_doc is null) return;

        // Anchor = last non-shift click. Fall back to the active page, then to this card.
        int anchor = _selectionAnchor;
        if (anchor < 0 || anchor >= _doc.Pages.Count)
        {
            var active = Canvas.ActivePage;
            anchor = active is not null ? _doc.Pages.IndexOf(active) : index;
            if (anchor < 0) anchor = index;
        }

        _selectedPages.Clear();
        int lo = Math.Min(anchor, index), hi = Math.Max(anchor, index);
        for (int i = lo; i <= hi && i < _doc.Pages.Count; i++) _selectedPages.Add(i);
        _selectionAnchor = anchor;   // keep stable so further Shift-clicks re-range from here
        UpdatePagePanelHighlight();
    }

    private void ClearPageSelection()
    {
        bool had = _selectedPages.Count > 0;
        _selectedPages.Clear();
        _selectionAnchor = -1;
        if (had) UpdatePagePanelHighlight();
        else UpdateSelectionBar();
    }

    private void UpdateSelectionBar()
    {
        int n = _selectedPages.Count;
        PageSelectionBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        PageSelCount.Text = $"{n} selected";
    }

    private void SetDragOpacity(double opacity)
    {
        if (_dragWholeSelection)
        {
            foreach (var child in PageListPanel.Children)
                if (child is Button b && b.Tag is int i && _selectedPages.Contains(i))
                    b.Opacity = opacity;
        }
        else if (_dragSourceCard is { } c) c.Opacity = opacity;
    }

    private void RestoreDragOpacity()
    {
        foreach (var child in PageListPanel.Children)
            if (child is Button b) b.Opacity = 1.0;
    }

    // ─── Page drag helpers ────────────────────────────────────────────────────

    private void EnsureDropLine()
    {
        if (_dropLine is not null) return;
        _dropLine = new Border
        {
            Height = 2.5,
            Margin = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(2),
            Background = (Brush)Application.Current.Resources["AppAccentBrush"]
        };
    }

    /// <summary>
    /// Repositions the drop-indicator line inside PageListPanel based on the
    /// cursor's Y coordinate (in PageListPanel space).  Computes which slot
    /// the cursor is closest to and inserts the line there.
    /// </summary>
    private void UpdateDropIndicator(double yInPanel)
    {
        if (_doc is null || _dropLine is null) return;

        // Remove existing line so child indices reflect only real page cards.
        if (PageListPanel.Children.Contains(_dropLine))
            PageListPanel.Children.Remove(_dropLine);

        // Walk accumulated heights (Padding.Top + per-child height + Spacing).
        double topPadding = 10; // PageListPanel Padding="8,10,8,10"
        double spacing    = 10; // PageListPanel Spacing
        double accumulated = topPadding;
        int pageCount  = _doc.Pages.Count;
        int insertSlot = pageCount; // default: after all cards

        for (int i = 0; i < pageCount && i < PageListPanel.Children.Count; i++)
        {
            if (PageListPanel.Children[i] is not FrameworkElement child) continue;
            double h = child.ActualHeight > 0 ? child.ActualHeight : 186;
            if (yInPanel < accumulated + h / 2) { insertSlot = i; break; }
            accumulated += h + spacing;
        }

        _dragTargetIndex = insertSlot;

        // Clamp insertion to valid range and add line.
        int clampedSlot = Math.Clamp(insertSlot, 0, PageListPanel.Children.Count);
        PageListPanel.Children.Insert(clampedSlot, _dropLine);
    }

    /// <summary>
    /// Ends an in-progress page drag. Wired to PointerReleased, PointerCaptureLost and
    /// PointerCanceled — whichever fires first wins. (WinUI raises CaptureLost before Released
    /// on a normal drop, so the commit must work from there.) Drag state is cleared up front,
    /// so the second of the two events becomes a harmless no-op.
    /// </summary>
    private async Task EndPageDragAsync(bool commit)
    {
        if (!_pageDragging)
        {
            // Not an active drag (plain press/click): drop any stale source ref so a later
            // hover-move can't be mistaken for a drag, and let the Click handler navigate.
            _dragSourceCard  = null;
            _dragSourceIndex = -1;
            return;
        }

        int from = _dragSourceIndex;
        int insertSlot = _dragTargetIndex;
        bool group = _dragWholeSelection;

        // Snapshot taken — now clear all state so a re-entrant CaptureLost/Released is a no-op.
        _pageDragging       = false;
        _dragSourceCard     = null;
        _dragSourceIndex    = -1;
        _dragTargetIndex    = -1;
        _dragWholeSelection = false;

        if (_dropLine is not null)
        {
            if (PageListPanel.Children.Contains(_dropLine))
                PageListPanel.Children.Remove(_dropLine);
            _dropLine = null;
        }
        RestoreDragOpacity();
        _suppressNextCardClick = true;

        if (commit) await CommitPageMoveAsync(from, insertSlot, group);
    }

    private async Task CommitPageMoveAsync(int from, int insertSlot, bool group)
    {
        if (_doc is null || insertSlot < 0) return;

        // ── Group move: relocate every selected page to the drop slot. ──
        if (group && _selectedPages.Count > 0)
        {
            var indices = _selectedPages.OrderBy(i => i).ToList();
            // No-op when the selection is contiguous and the drop lands inside it.
            bool contiguous = indices[^1] - indices[0] == indices.Count - 1;
            if (contiguous && insertSlot >= indices[0] && insertSlot <= indices[^1] + 1) return;
            await MovePagesAsync(indices, insertSlot);
            return;
        }

        // ── Single move (no-op if the page wouldn't actually move). ──
        if (from < 0 || insertSlot == from || insertSlot == from + 1) return;
        int to = insertSlot > from ? insertSlot - 1 : insertSlot;
        if (to < 0 || to >= _doc.Pages.Count || to == from) return;
        await MovePageAsync(from, to);
    }

    /// <summary>Moves a set of pages (by current index) so they sit contiguously at the drop slot, preserving their order.</summary>
    private async Task MovePagesAsync(List<int> indices, int insertSlot)
    {
        if (_doc is null) return;
        indices = indices.Where(i => i >= 0 && i < _doc.Pages.Count).Distinct().OrderBy(i => i).ToList();
        if (indices.Count == 0) return;

        var moving = indices.Select(i => _doc.Pages[i]).ToList();
        int removedBefore = indices.Count(i => i < insertSlot);
        int target = insertSlot - removedBefore;

        foreach (var i in indices.OrderByDescending(x => x)) _doc.Pages.RemoveAt(i);
        target = Math.Clamp(target, 0, _doc.Pages.Count);
        for (int k = 0; k < moving.Count; k++) _doc.Pages.Insert(target + k, moving[k]);

        for (int i = 0; i < _doc.Pages.Count; i++) _doc.Pages[i].Index = i;
        Canvas.Rebuild();
        App.Services.History.RecordPageListChange();   // undoable
        UpdateHistoryUi();
        MarkPageSetChanged();

        // Keep the moved pages selected at their new positions.
        _selectedPages.Clear();
        for (int k = 0; k < moving.Count; k++) _selectedPages.Add(target + k);
        _selectionAnchor = target;
        await RefreshPagePanelAsync();
    }

    private async Task DeleteSelectedPagesAsync()
    {
        if (_doc is null || _selectedPages.Count == 0) return;
        var indices = _selectedPages.OrderBy(i => i).ToList();
        if (indices.Count >= _doc.Pages.Count)
        {
            await Notify("Cannot delete", "A document must keep at least one page.");
            return;
        }

        var dlg = new ContentDialog
        {
            Title = "Delete pages",
            Content = $"Delete {indices.Count} selected pages? You can bring them back with Undo (Ctrl+Z).",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var i in indices.OrderByDescending(x => x))
            if (i >= 0 && i < _doc.Pages.Count) _doc.Pages.RemoveAt(i);

        for (int i = 0; i < _doc.Pages.Count; i++) _doc.Pages[i].Index = i;
        Canvas.Rebuild();
        App.Services.History.RecordPageListChange();   // undoable
        UpdateHistoryUi();
        MarkPageSetChanged();

        _selectedPages.Clear();
        _selectionAnchor = -1;
        await RefreshPagePanelAsync();
    }

    private async Task DuplicatePageAsync(int index)
    {
        if (_doc is null || index < 0 || index >= _doc.Pages.Count) return;
        _selectedPages.Clear(); _selectionAnchor = -1;
        var clone = DeepClonePage(_doc.Pages[index], index + 1);
        _doc.Pages.Insert(index + 1, clone);
        for (int i = 0; i < _doc.Pages.Count; i++) _doc.Pages[i].Index = i;
        Canvas.Rebuild();
        App.Services.History.RecordPageListChange();   // undoable
        UpdateHistoryUi();
        MarkPageSetChanged();
        await RefreshPagePanelAsync();
        Canvas.ScrollToPage(index + 1);
    }

    /// <summary>
    /// Deep-clones a page, giving the clone and all its elements new unique IDs.
    /// The background PNG and image pixel data are shared by reference (read-only bytes).
    /// </summary>
    private static NotePage DeepClonePage(NotePage src, int newIndex)
    {
        var clone = new NotePage
        {
            Index           = newIndex,
            Width           = src.Width,
            Height          = src.Height,
            BackgroundPng   = src.BackgroundPng,        // immutable bytes — safe to share
            SourcePageIndex = src.SourcePageIndex,
            TemplateOverride = src.TemplateOverride is { } t
                ? new TemplateSettings
                    { Kind = t.Kind, Spacing = t.Spacing,
                      LineColorHex = t.LineColorHex, LineThickness = t.LineThickness,
                      SkipImportedPdf = t.SkipImportedPdf }
                : null
        };
        clone.Gaps.AddRange(PageGaps.Clone(src.Gaps));

        foreach (var s in src.Strokes)
        {
            var cs = new Stroke { Kind = s.Kind, Color = s.Color, Width = s.Width, PressureMode = s.PressureMode };
            cs.Points.AddRange(s.Points); // InkPoint is a readonly struct
            clone.Strokes.Add(cs);
        }

        foreach (var sh in src.Shapes)
            clone.Shapes.Add(new ShapeElement
            {
                Kind = sh.Kind,
                X1 = sh.X1, Y1 = sh.Y1, X2 = sh.X2, Y2 = sh.Y2, X3 = sh.X3, Y3 = sh.Y3,
                Rotation = sh.Rotation,
                Color = sh.Color, StrokeWidth = sh.StrokeWidth, Filled = sh.Filled
            });

        foreach (var te in src.Texts)
            clone.Texts.Add(new TextElement
            {
                X = te.X, Y = te.Y, Width = te.Width, Height = te.Height,
                Rotation = te.Rotation, Text = te.Text, FontSize = te.FontSize,
                Color = te.Color, FontFamily = te.FontFamily, Bold = te.Bold, Italic = te.Italic
            });

        foreach (var img in src.Images)
            clone.Images.Add(new ImageElement
            {
                X = img.X, Y = img.Y, Width = img.Width, Height = img.Height,
                Rotation = img.Rotation, PngData = img.PngData  // immutable bytes
            });

        return clone;
    }

    private void InsertPageAt(int index)
    {
        if (_doc is null) return;
        _selectedPages.Clear(); _selectionAnchor = -1;
        var last = _doc.Pages.LastOrDefault();
        double newW = last?.BackgroundContentWidth > 0 ? last.BackgroundContentWidth : last?.Width ?? 1240;
        var newPage = new NotePage { Index = index, Width = newW, Height = last?.Height ?? 1754 };
        // Same paper as the page it goes after (or before, at the very top).
        ContinueTemplate(newPage, index > 0 ? _doc.Pages[Math.Min(index, _doc.Pages.Count) - 1]
                                            : _doc.Pages.FirstOrDefault());
        _doc.Pages.Insert(index, newPage);
        for (int i = 0; i < _doc.Pages.Count; i++) _doc.Pages[i].Index = i;
        Canvas.Rebuild();
        App.Services.History.RecordPageListChange();   // undoable
        UpdateHistoryUi();
        MarkPageSetChanged();
        if (_pagePanelOpen) _ = RefreshPagePanelAsync();
    }

    private async Task MovePageAsync(int from, int to)
    {
        if (_doc is null || to < 0 || to >= _doc.Pages.Count) return;
        _selectedPages.Clear(); _selectionAnchor = -1;
        var page = _doc.Pages[from];
        _doc.Pages.RemoveAt(from);
        _doc.Pages.Insert(to, page);
        for (int i = 0; i < _doc.Pages.Count; i++) _doc.Pages[i].Index = i;
        Canvas.Rebuild();
        App.Services.History.RecordPageListChange();   // undoable
        UpdateHistoryUi();
        MarkPageSetChanged();
        await RefreshPagePanelAsync();
    }

    private async Task DeletePageAsync(int index)
    {
        if (_doc is null) return;
        if (_doc.Pages.Count <= 1)
        {
            await Notify("Cannot delete", "A document must have at least one page.");
            return;
        }
        var dlg = new ContentDialog
        {
            Title = "Delete page",
            Content = $"Delete page {index + 1}? You can bring it back with Undo (Ctrl+Z).",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _selectedPages.Clear(); _selectionAnchor = -1;
        _doc.Pages.RemoveAt(index);
        for (int i = 0; i < _doc.Pages.Count; i++) _doc.Pages[i].Index = i;
        Canvas.Rebuild();
        App.Services.History.RecordPageListChange();   // undoable
        UpdateHistoryUi();
        MarkPageSetChanged();
        await RefreshPagePanelAsync();
    }
}
