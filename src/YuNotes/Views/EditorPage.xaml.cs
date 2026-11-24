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

// Opens a note at a search match: the editor runs the same search and lands on
// the first match at or after PageIndex.
public sealed record EditorOpenRequest(string Path, int PageIndex, string Query);

public sealed partial class EditorPage : Page
{
    private Document? _doc;
    private ToolKind _selected = ToolKind.Pen;
    private ToolKind _selectMode = ToolKind.Lasso;
    private ToolKind? _restoreToolKind;
    private bool _eraserPixelMode;
    private bool _penPressureMode;
    private bool _suppressZoomSlider;

    // Autosave: debounced write-back so a crash or an abrupt close can't wipe out
    // more than a couple of seconds of work. Restarted on every edit; fires once
    // the user pauses. _saving guards against overlapping saves (manual + auto).
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autosaveTimer;
    private bool _saving;

    // Ink/overlay is flattened to raster when a .pdf is saved/exported. The
    // resolution is auto-detected per page (InkCanvasControl.RenderPageForExport
    // → PageCanvas.AutoExportScale) to match the densest view the user gets on
    // screen: max zoom tier × display scaling.

    // Per-tool width state (persists across tool switches within a session)
    private float _penWidth;
    private float _highlighterWidth;
    private float _eraserWidth;

    private static readonly double[] PenWidths = { 1.0, 2.0, 3.5, 6.0, 10.0 };
    private static readonly double[] HighlighterWidths = { 8.0, 14.0, 22.0, 32.0, 44.0 };
    private static readonly double[] EraserWidths = { 10.0, 20.0, 36.0, 60.0, 100.0 };

    private readonly PenTool _pen = new();
    private readonly HighlighterTool _highlighter = new();
    private readonly EraserTool _eraser = new();
    private readonly TextTool _text = new();
    private readonly ImageTool _image = new();
    private readonly LassoTool _lasso = new();
    private readonly RectSelectTool _rect = new();
    private readonly PanTool _pan = new();
    private readonly ShapeTool _shape = new();
    private readonly ExtendPageTool _extendPage = new();
    private readonly LaserTool _laser = new();
    private readonly ShapeTool _tape = new() { ShapeKind = ShapeKind.Tape };

    // Tape colors: soft, opaque hues that clearly read as "something is hidden here".
    private static readonly List<string> TapePresetColors = new()
    {
        "#FFF2C94C", "#FF7FB3E6", "#FFF29BB2", "#FF8CD3A8", "#FFB8BDC7",
    };
    private Windows.UI.Color _tapeColor = Controls.ColorPickerControl.ParseHex("#FFF2C94C");
    private ShapeKind _shapeKind = ShapeKind.Rectangle;

    // Laser colors: bright, saturated hues that read as light on paper and PDFs.
    private static readonly List<string> LaserPresetColors = new()
    {
        "#FFFF3B30", "#FF34C759", "#FF0A84FF", "#FFFFCC00", "#FFAF52DE",
    };
    private Windows.UI.Color _laserColor = Controls.ColorPickerControl.ParseHex("#FFFF3B30");
    private Windows.UI.Color _colorBeforeLaser = Microsoft.UI.Colors.Black;

    public EditorPage()
    {
        InitializeComponent();
        // Index installed fonts off-thread so the first PDF save doesn't pay
        // for it (text is written as real PDF text with embedded fonts).
        _ = Task.Run(Rendering.PdfFontResolver.EnsureIndexed);
        // GoBack reverses the drill-in animation the note was opened with.
        BackBtn.Click += async (_, __) => { if (await ConfirmLeaveAsync()) MainWindow.GoBack(); };
        SettingsBtn.Click += (_, __) => MainWindow.Navigate<SettingsPage>(
            null, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
        SaveBtn.Click += async (_, __) => await SaveAsync();

        UndoBtn.Click += (_, __) => DoUndo();
        RedoBtn.Click += (_, __) => DoRedo();

        AddPageBtn.Click += (_, __) => AddBlankPage();
        AddPdfPagesBtn.Click += async (_, __) => await AddPagesFromPdfAsync();
        ExportBtn.Click += async (_, __) => await ExportAsync();
        VersionsBtn.Click += async (_, __) => await ShowVersionsAsync();
        ScreenshotBtn.Click += (_, __) => EnterScreenshotMode();
        ScreenshotOverlay.PointerPressed  += OnShotPointerPressed;
        ScreenshotOverlay.PointerMoved    += OnShotPointerMoved;
        ScreenshotOverlay.PointerReleased += OnShotPointerReleased;
        ScreenshotOverlay.KeyDown         += OnShotKeyDown;
        ShotCopyBtn.Click   += async (_, __) => await CopyShotAsync();
        ShotInsertBtn.Click += async (_, __) => await InsertShotAsync();
        ShotSaveBtn.Click   += async (_, __) => await SaveShotAsync();
        ShotCancelBtn.Click += (_, __) => ExitScreenshotMode();
        TemplateBtn.Click += async (_, __) => await ChooseTemplateAsync();

        ToolHand.Click += (_, __) => SelectTool(ToolKind.Pan);
        ToolShape.Click += (_, __) => SelectTool(ToolKind.Shape);
        ToolPen.Click += (_, __) => SelectTool(ToolKind.Pen);
        ToolHighlighter.Click += (_, __) => SelectTool(ToolKind.Highlighter);
        ToolEraser.Click += (_, __) => SelectTool(ToolKind.Eraser);
        ToolText.Click += (_, __) => SelectTool(ToolKind.Text);
        ToolImage.Click += async (_, __) => { SelectTool(ToolKind.Image); await PickImageAsync(); };
        ToolPaste.Click += async (_, __) => await PasteClipboardImageAsync();
        ToolSelect.Click += (_, __) => SelectTool(_selectMode);
        ToolExtend.Click += (_, __) => SelectTool(ToolKind.ExtendPage);
        ToolLaser.Click += (_, __) => SelectTool(ToolKind.Laser);
        ToolTape.Click += (_, __) => SelectTool(ToolKind.Tape);
        TapeCoverAllItem.Click += (_, __) => { Canvas.SetAllTapeRevealed(false); TapeFlyout.Hide(); };
        TapeRevealAllItem.Click += (_, __) => { Canvas.SetAllTapeRevealed(true); TapeFlyout.Hide(); };
        TapeFlyout.Opening += (_, __) =>
        {
            int n = Canvas.TapeCount;
            TapeCountText.Text = n == 0 ? "No tape in this note yet." : n == 1 ? "1 piece of tape in this note." : $"{n} pieces of tape in this note.";
            TapeCoverAllItem.IsEnabled = TapeRevealAllItem.IsEnabled = n > 0;
        };
        LaserDotItem.Click += (_, __) => { SetLaserMode(LaserMode.Dot); LaserFlyout.Hide(); };
        LaserTrailItem.Click += (_, __) => { SetLaserMode(LaserMode.Trail); LaserFlyout.Hide(); };

        PresentBtn.Click += (_, __) => EnterPresentation();
        PresentExitBtn.Click += (_, __) => ExitPresentation();
        PresentLaserDotBtn.Click += (_, __) => SetLaserMode(LaserMode.Dot);
        PresentLaserTrailBtn.Click += (_, __) => SetLaserMode(LaserMode.Trail);
        PresentPenBtn.Click += (_, __) => SelectTool(ToolKind.Pen);
        PresentHandBtn.Click += (_, __) => SelectTool(ToolKind.Pan);
        PresentPrevBtn.Click += (_, __) => StepPresentationPage(-1);
        PresentNextBtn.Click += (_, __) => StepPresentationPage(+1);
        PresentBar.PointerEntered += (_, __) => PresentBar.Opacity = 1;
        PresentBar.PointerExited += (_, __) => PresentBar.Opacity = 0.35;

        ShapeRectItem.Click     += (_, __) => { _shapeKind = ShapeKind.Rectangle; _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ShapeEllipseItem.Click  += (_, __) => { _shapeKind = ShapeKind.Ellipse;   _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ShapeLineItem.Click     += (_, __) => { _shapeKind = ShapeKind.Line;      _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ShapeTriangleItem.Click += (_, __) => { _shapeKind = ShapeKind.Triangle;  _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ShapeArrowItem.Click    += (_, __) => { _shapeKind = ShapeKind.Arrow;     _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ShapeAxesItem.Click     += (_, __) => { _shapeKind = ShapeKind.Axes;      _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ShapeGridItem.Click     += (_, __) => { _shapeKind = ShapeKind.Grid;      _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ToolRuler.Click += (_, __) => Canvas.SetRulerVisible(ToolRuler.IsChecked == true);
        EraserStrokeItem.Click += (_, __) => { _eraserPixelMode = false; SyncEraserMenu(); Canvas.Context.EraserPixelMode = false; SaveToolModes(); SelectTool(ToolKind.Eraser); EraserFlyout.Hide(); };
        EraserPixelItem.Click += (_, __) => { _eraserPixelMode = true; SyncEraserMenu(); Canvas.Context.EraserPixelMode = true; SaveToolModes(); SelectTool(ToolKind.Eraser); EraserFlyout.Hide(); };
        // The pen flyout stays open on selection so the preview shows the new mode.
        PenSolidItem.Click += (_, __) => { _penPressureMode = false; _pen.UsePressure = false; SyncPenMenu(); SaveToolModes(); SelectTool(ToolKind.Pen); PenPreview.Invalidate(); };
        PenPressureItem.Click += (_, __) => { _penPressureMode = true; _pen.UsePressure = true; SyncPenMenu(); SaveToolModes(); SelectTool(ToolKind.Pen); PenPreview.Invalidate(); };
        PenFlyout.Opened += (_, __) => { PenPreview.Invalidate(); RebuildPenFavorites(); };
        SelectLassoItem.Click += (_, __) => { _selectMode = ToolKind.Lasso; SyncSelectMenu(); SaveToolModes(); SelectTool(_selectMode); SelectFlyout.Hide(); };
        SelectRectItem.Click += (_, __) => { _selectMode = ToolKind.RectSelect; SyncSelectMenu(); SaveToolModes(); SelectTool(_selectMode); SelectFlyout.Hide(); };

        Canvas.ToolProvider = () => _selected switch
        {
            ToolKind.Pen => _pen,
            ToolKind.Highlighter => _highlighter,
            ToolKind.Eraser => _eraser,
            ToolKind.Text => _text,
            ToolKind.Image => _image,
            ToolKind.Lasso => _lasso,
            ToolKind.RectSelect => _rect,
            ToolKind.Pan => _pan,
            ToolKind.Shape => _shape,
            ToolKind.ExtendPage => _extendPage,
            ToolKind.Laser => _laser,
            ToolKind.Tape => _tape,
            _ => _pen
        };
        Canvas.DocumentMutated += (_, page) => { if (_doc is not null) { _doc.IsDirty = true; _doc.MarkFlattenDirty(page); } App.Services.History.RecordMutation(page); UpdateSelectionUi(); UpdateSaveStateUi(); ScheduleAutosave(); };
        Canvas.SelectionChanged += (_, __) => UpdateSelectionUi();
        Canvas.ToolRequested += (_, kind) => { SelectTool(kind); HideImageGhost(); };
        Canvas.AddPageRequested += (_, __) => AddBlankPage();

        // Image ghost: follows the pointer until the user clicks to place
        RootGrid.AddHandler(PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), handledEventsToo: true);

        // Finger touch or middle-mouse temporarily overrides the active tool with the
        // Hand/Pan tool. Only flash the toolbar's checked state — the heavy SelectTool
        // path (ColorPicker / WidthPicker rebuild) was making every gesture feel slow.
        // RectSelectionCompleted is no longer used (copy-as-image is now a toolbar button).

        // Live glass refraction re-captures the whole canvas on every change;
        // freeze it to the plain backdrop while a stroke is being drawn.
        Canvas.LiveInkActiveChanged += (_, active) => ToolbarGlass.RefractionPaused = active;
        Canvas.MomentaryToolStart += (_, kind) =>
        {
            if (_selected == kind) return;
            _restoreToolKind = _selected;
            // Don't light up the hand button during a finger-scroll or middle-mouse
            // pan — the gesture is invisible and the flash is distracting.
            if (kind != ToolKind.Pan) UpdateToolUi(kind);
        };
        Canvas.MomentaryToolEnd += (_, __) =>
        {
            if (_restoreToolKind is ToolKind k)
            {
                _restoreToolKind = null;
                UpdateToolUi(k);
            }
        };

        SelDeleteBtn.Click += (_, __) => { Canvas.DeleteSelection(); UpdateSelectionUi(); };
        SelDuplicateBtn.Click += (_, __) => { Canvas.DuplicateSelection(); UpdateSelectionUi(); };
        SelClearBtn.Click += (_, __) => { Canvas.ClearSelection(); UpdateSelectionUi(); };
        // Selection bar color: recolors the selected ink / shapes / text in place.
        SelColorPicker.ColorChanged += (_, c) => Canvas.RecolorSelection(c);
        SelWidthPicker.WidthChanged += (_, w) => { Canvas.SetSelectionWidth((float)w); PositionSelectionActions(); };

        PagePanelBtn.Click += (_, __) => TogglePagePanel();
        BookmarksFlyout.Opening += (_, __) => RebuildBookmarksFlyout();
        SideBySideFlyout.Opening += (_, __) => RebuildSideBySideFlyout();
        RefCloseBtn.Click += (_, __) => CloseReference();
        RefOpenBtn.Click += async (_, __) => await OpenReferenceAsMainAsync();
        RefSplitter.PointerPressed += (_, e) => { RefSplitter.CapturePointer(e.Pointer); _splitDragging = true; e.Handled = true; };
        RefSplitter.PointerMoved += (_, e) =>
        {
            if (!_splitDragging || CanvasArea.ActualWidth < 100) return;
            double f = 1 - e.GetCurrentPoint(CanvasArea).Position.X / CanvasArea.ActualWidth;
            _refFraction = Math.Clamp(f, 0.2, 0.75);
            ApplyReferenceWidths();
        };
        RefSplitter.PointerReleased += (_, e) => { _splitDragging = false; RefSplitter.ReleasePointerCapture(e.Pointer); };
        RefCanvas.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 4) FitReferenceZoom(); };
        RefSplitter.PointerEntered += (_, __) => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        RefSplitter.PointerExited += (_, __) => { if (!_splitDragging) ProtectedCursor = null; };
        PageEditModeBtn.Click += (_, __) => { _pagePanelEditMode = !_pagePanelEditMode; ClearPageSelection(); if (_pagePanelOpen) _ = RefreshPagePanelAsync(); };
        PageSelDeleteBtn.Click += async (_, __) => await DeleteSelectedPagesAsync();
        PageSelClearBtn.Click  += (_, __) => ClearPageSelection();

        Canvas.ActivePageScrolled += (_, __) => { UpdatePagePanelHighlight(); SaveLastPage(); if (Canvas.HasSelection) PositionSelectionActions(); if (_presenting) UpdatePresentPageLabel(); };

        SearchBtn.Click += (_, __) => OpenSearch();
        SearchCloseBtn.Click += (_, __) => CloseSearch();
        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(220);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, __) => RunSearch();
        SearchBox.TextChanged += (_, __) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        SearchBox.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape) CloseSearch();
            else if (args.Key == Windows.System.VirtualKey.Enter) { StepSearch(IsShiftDown() ? -1 : +1); args.Handled = true; }
        };
        SearchPrevBtn.Click += (_, __) => StepSearch(-1);
        SearchNextBtn.Click += (_, __) => StepSearch(+1);

        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Delete });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Back });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Escape });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.D, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Y, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.C, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.F5 });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.T, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.W, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Tab, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Tab, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift });
        InitTabs();
        InitShare();
        InitStickers();
        for (var k = VirtualKey.Number1; k <= VirtualKey.Number5; k++)
            KeyboardAccelerators.Add(new KeyboardAccelerator { Key = k, Modifiers = VirtualKeyModifiers.Control });
        foreach (var a in KeyboardAccelerators) a.Invoked += OnAccelerator;

        ColorPicker.ColorChanged += (_, c) =>
        {
            Canvas.Context.CurrentColor = c;
            if (_selected == ToolKind.Laser) { _laserColor = c; return; }   // the laser never recolors a selection
            if (_selected == ToolKind.Tape) _tapeColor = c;
            // With a selection active, the toolbar color also recolors the selected
            // items, matching the selection bar's button. Deliberately not gated on
            // _selected being a select tool: a pen-barrel lasso is momentary, so
            // _selected stays Pen while the selection lives. Leaving a select tool
            // clears the selection (SelectTool), so a live one is always intended.
            if (Canvas.HasSelection && Canvas.SelectionHasRecolorable)
            {
                Canvas.RecolorSelection(c);
                SyncSelectionColorPicker();
            }
        };
        WidthPicker.WidthChanged += (_, w) =>
        {
            switch (_selected)
            {
                case ToolKind.Pen:        _penWidth = (float)w; Canvas.Context.CurrentWidth = _penWidth; break;
                case ToolKind.Highlighter: _highlighterWidth = (float)w; Canvas.Context.CurrentWidth = _highlighterWidth; break;
                case ToolKind.Eraser:     _eraserWidth = (float)w; Canvas.Context.EraserWidth = _eraserWidth; break;
            }
            // Like the toolbar color: with ink selected, the pen/highlighter width
            // also resizes the selection.
            if (_selected is ToolKind.Pen or ToolKind.Highlighter
                && Canvas.HasSelection && Canvas.SelectionHasResizableWidth)
            {
                Canvas.SetSelectionWidth((float)w);
                SyncSelectionWidthPicker();
            }
        };
        SyncEraserMenu();
        SyncSelectMenu();
        SyncPenMenu();
        SyncShapeMenu();
        UpdateChevronColors();

        App.Services.History.StateChanged += UpdateHistoryUi;

        ZoomSlider.ValueChanged += (_, e) =>
        {
            if (_suppressZoomSlider) return;
            ZoomLabel.Text = $"{e.NewValue:0}%";
            Canvas.SetZoom(e.NewValue / 100.0);
        };
        Canvas.ZoomChanged += (_, factor) =>
        {
            var pct = factor * 100.0;
            _suppressZoomSlider = true;
            ZoomSlider.Value = Math.Clamp(pct, ZoomSlider.Minimum, ZoomSlider.Maximum);
            _suppressZoomSlider = false;
            ZoomLabel.Text = $"{pct:0}%";
            if (Canvas.HasSelection) PositionSelectionActions();
        };

        SizeChanged += (_, e) => ApplyToolbarScaleForWidth(e.NewSize.Width);
        Loaded += (_, __) => ApplyToolbarPosition(App.Services.Settings.Current.ToolbarPosition);
        Loaded += (_, __) =>
        {
            MainWindow.SetDragRegion(TopBarDragRegion);
            TopBarGrid.Padding = new Thickness(6, 2, 6 + MainWindow.CaptionButtonInset, 2);
            UpdateTitlePassthrough();
        };
        Unloaded += (_, __) => SetTitlePassthrough(null);

        // Switching windows (Alt+Tab, touchpad swipe, clicking elsewhere) can cut
        // a pen/mouse gesture off without a release, leaving the canvas stuck in
        // it so the pen draws nothing. Reset input when the window loses focus.
        Loaded += (_, __) => { if (App.MainWindow is { } w) { w.Activated -= OnWindowActivated; w.Activated += OnWindowActivated; } };
        Unloaded += (_, __) => { if (App.MainWindow is { } w) w.Activated -= OnWindowActivated; };

        // Click-to-rename title.
        // The title moves (not just resizes) when toolbar buttons beside it
        // show/hide, so track every layout pass; UpdateTitlePassthrough only
        // touches the OS region when the bounds actually changed.
        TopBarTitleHost.LayoutUpdated += (_, __) => UpdateTitlePassthrough();
        TopBarTitleBtn.Click += (_, __) => BeginTitleEdit();
        TopBarTitleBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = EndTitleEditAsync(commit: true); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; _ = EndTitleEditAsync(commit: false); }
        };
        TopBarTitleBox.LostFocus += (_, __) => _ = EndTitleEditAsync(commit: true);

        DragHandle.PointerPressed += DragHandle_PointerPressed;
        DragHandle.PointerMoved += DragHandle_PointerMoved;
        DragHandle.PointerReleased += DragHandle_PointerReleased;
        DragHandle.PointerCaptureLost += DragHandle_PointerReleased;

        // Seed the toolbar's persisted drag offset before first layout so it
        // renders in place on the very first frame (no post-load jump).
        var toolbarSettings = App.Services.Settings.Current;
        ToolbarTranslate.X = toolbarSettings.ToolbarOffsetX;
        ToolbarTranslate.Y = toolbarSettings.ToolbarOffsetY;
    }

    private bool _dragActive;
    private Windows.Foundation.Point _dragStart;
    private double _dragStartTx, _dragStartTy;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        OpenFromNavigation(e.Parameter);
        var s = App.Services.Settings.Current;
        _pen.HoldToSnapEnabled = s.HoldToSnapEnabled;
        ApplyToolbarPosition(s.ToolbarPosition);
        ApplyToolbarScaleForWidth(ActualWidth > 0 ? ActualWidth : 1200);
        ApplyToolbarOrder();
        ApplyToolbarVisibility();
        ToolbarGlass.RefractionSource = Canvas;
        ToolbarGlass.IsGlassActive = s.LiquidGlassEnabled;
        if (_doc is not null) ZoomPanel.Visibility = s.HideZoomBar ? Visibility.Collapsed : Visibility.Visible;

        // Show the paste button whenever the clipboard holds an image, and keep it in
        // sync as the clipboard changes (e.g. after copying an image in a browser).
        Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged += OnClipboardChanged;
        RefreshPasteAffordance();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.MainWindow!.Title = "YuNotes";
        if (_presenting) ExitPresentation();
        Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged -= OnClipboardChanged;
        SaveLastPage();
        // Left with "Don't save": the tab must not keep the discarded edits.
        if (_doc is { IsDirty: true }) DropActiveTabDocument();
        // Stop the debounce so a queued tick can't fire a save after the user
        // left — in particular after they picked "Don't save" in ConfirmLeaveAsync
        // (which intentionally leaves the in-memory doc dirty without persisting).
        _autosaveTimer?.Stop();
    }

    // Records the page the user is currently viewing so reopening this document
    // returns there. Called on scroll-settle and on leaving the editor.
    private void SaveLastPage()
    {
        if (_doc is null || string.IsNullOrEmpty(_doc.Info.FilePath)) return;
        var page = Canvas.ActivePage;
        if (page is null) return;
        int idx = _doc.Pages.IndexOf(page);
        if (idx < 0) return;
        var map = App.Services.Settings.Current.LastPageByDocument;
        if (map.TryGetValue(_doc.Info.FilePath, out var cur) && cur == idx) return;   // unchanged
        map[_doc.Info.FilePath] = idx;
        App.Services.Settings.Save();
    }

    // On open, jump to the last page the user was on (once pages are laid out).
    private void RestoreLastPage()
    {
        if (_doc is null || string.IsNullOrEmpty(_doc.Info.FilePath)) return;
        if (!App.Services.Settings.Current.LastPageByDocument.TryGetValue(_doc.Info.FilePath, out var idx)) return;
        if (idx > 0 && idx < _doc.Pages.Count) Canvas.ScrollToPageDeferred(idx);
    }

    // Opens the document off the UI thread so the editor shell (and the page
    // transition into it) renders immediately; the overlay blocks input while
    // _doc is still null.
    private async Task LoadFromPathAsync(string path)
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        try
        {
            var doc = await Task.Run(() =>
                string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
                    ? App.Services.Documents.OpenPdfContainer(path, App.Services.PdfContainer, App.Services.PdfImport)
                    : App.Services.Documents.Open(path));
            // The user may have switched to another tab while this loaded.
            if (ActiveTab is { } tab && !SamePath(tab.Path, path)) return;
            Load(doc);
            RunPendingSearch();
        }
        catch (Exception ex)
        {
            App.LogError(ex, $"Open failed: {path}");
            var dlg = new ContentDialog
            {
                Title = "Open failed", Content = ex.Message,
                CloseButtonText = "OK", XamlRoot = XamlRoot
            };
            await dlg.ShowAsync();
            // Close the tab that failed; go Home only if it was the last one.
            int failed = s_tabs.FindIndex(t => SamePath(t.Path, path));
            if (failed >= 0) s_tabs.RemoveAt(failed);
            if (s_tabs.Count == 0) { s_activeTab = -1; MainWindow.GoBack(); }
            else ActivateTab(Math.Min(Math.Max(failed, 0), s_tabs.Count - 1));
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // Opened from a search result on Home: show the same search here.
    private void RunPendingSearch()
    {
        if (_pendingSearch is not { } ps) return;
        OpenSearch();
        SearchBox.Text = ps.Query;
        // TextChanged doesn't reliably fire for a box that was hidden
        // a moment ago, so start the search directly.
        _searchDebounce.Stop();
        RunSearch();
    }

    // ── Click-to-rename title ─────────────────────────────────────────────────

    // The top bar's center column is the window drag region (SetTitleBar), which
    // swallows all input. Carve the title out as a passthrough rect so it gets
    // clicks; re-run whenever its bounds can change.
    private void UpdateTitlePassthrough()
    {
        if (XamlRoot is null || TopBarTitleHost.ActualWidth <= 0) return;
        try
        {
            var b = TopBarTitleHost.TransformToVisual(null)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, TopBarTitleHost.ActualWidth, TopBarTitleHost.ActualHeight));
            double s = XamlRoot.RasterizationScale;
            var r = new Windows.Graphics.RectInt32(
                (int)Math.Floor(b.X * s), (int)Math.Floor(b.Y * s),
                (int)Math.Ceiling(b.Width * s), (int)Math.Ceiling(b.Height * s));
            if (_titlePassthrough is { } last && last.X == r.X && last.Y == r.Y
                && last.Width == r.Width && last.Height == r.Height) return;
            SetTitlePassthrough(r);
        }
        catch (Exception ex) { App.LogError(ex, "UpdateTitlePassthrough"); }
    }

    private Windows.Graphics.RectInt32? _titlePassthrough;

    // null clears it (page unloaded), so other pages' title bars are unaffected.
    private bool _titlePassthroughErrorLogged;

    private void SetTitlePassthrough(Windows.Graphics.RectInt32? rect)
    {
        try
        {
            MainWindow.SetTitleBarPassthrough(
                rect is { } r ? new[] { r } : Array.Empty<Windows.Graphics.RectInt32>());
            // Cache only on success so a failed attempt is retried on the next
            // layout pass instead of being treated as applied.
            _titlePassthrough = rect;
        }
        catch (Exception ex)
        {
            // Clearing on Unloaded can run after the window is gone (app closing):
            // nothing to clear then, so stay quiet.
            if (rect is null) return;
            // UpdateTitlePassthrough runs on every layout pass — log once, not per frame.
            if (!_titlePassthroughErrorLogged) App.LogError(ex, "SetTitlePassthrough");
            _titlePassthroughErrorLogged = true;
        }
    }

    private bool _editingTitle;

    private void BeginTitleEdit()
    {
        if (_doc is null || _editingTitle) return;
        _editingTitle = true;
        TopBarTitleBox.Text = _doc.Info.Title;
        TopBarTitleBtn.Visibility = Visibility.Collapsed;
        TopBarTitleBox.Visibility = Visibility.Visible;
        TopBarTitleBox.Focus(FocusState.Programmatic);
        TopBarTitleBox.SelectAll();
    }

    private async Task EndTitleEditAsync(bool commit)
    {
        if (!_editingTitle) return;
        _editingTitle = false;   // guards the LostFocus that hiding the box triggers
        var text = TopBarTitleBox.Text;
        TopBarTitleBox.Visibility = Visibility.Collapsed;
        TopBarTitleBtn.Visibility = Visibility.Visible;
        if (commit) await RenameDocumentAsync(text);
    }

    // Renames the note: its title (embedded, shown on the home screen) and its
    // file on disk to match, carrying path-keyed settings (recents, last page)
    // over to the new path. If the file can't be renamed (e.g. open in another
    // app) the title still changes and the user is told.
    private async Task RenameDocumentAsync(string requested)
    {
        if (_doc is null) return;
        var title = (requested ?? "").Trim();
        if (title.Length == 0 || title == _doc.Info.Title) return;

        // Don't move the file out from under an in-flight save.
        while (_saving) await Task.Delay(100);

        _doc.Info.Title = title;
        TopBarTitle.Text = title;

        var oldPath = _doc.Info.FilePath;
        string? fileError = null;
        if (!string.IsNullOrEmpty(oldPath) && File.Exists(oldPath))
        {
            var safeName = string.Concat(title.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (safeName.Length > 0)
            {
                var dir = System.IO.Path.GetDirectoryName(oldPath)!;
                var ext = System.IO.Path.GetExtension(oldPath);
                var newPath = System.IO.Path.Combine(dir, safeName + ext);
                bool sameFile = string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase);
                // Another file already has that name: add " (2)", " (3)", …
                for (int n = 2; !sameFile && File.Exists(newPath); n++)
                    newPath = System.IO.Path.Combine(dir, $"{safeName} ({n}){ext}");

                if (newPath != oldPath)
                {
                    try
                    {
                        if (sameFile)
                        {
                            // Case-only rename: Windows needs a hop through a temp name.
                            var tmp = oldPath + ".rename-" + Guid.NewGuid().ToString("N");
                            File.Move(oldPath, tmp);
                            File.Move(tmp, newPath);
                        }
                        else File.Move(oldPath, newPath);

                        _doc.Info.FilePath = newPath;
                        if (string.Equals(_doc.Info.SourcePdfPath, oldPath, StringComparison.OrdinalIgnoreCase))
                            _doc.Info.SourcePdfPath = newPath;

                        var settings = App.Services.Settings.Current;
                        for (int i = 0; i < settings.RecentDocuments.Count; i++)
                            if (string.Equals(settings.RecentDocuments[i], oldPath, StringComparison.OrdinalIgnoreCase))
                                settings.RecentDocuments[i] = newPath;
                        if (settings.LastPageByDocument.Remove(oldPath, out var lastPage))
                            settings.LastPageByDocument[newPath] = lastPage;
                        Services.NoteTags.MovePath(oldPath, newPath);
                        App.Services.Settings.Save();
                    }
                    catch (Exception ex) { fileError = ex.Message; }
                }
            }
        }

        App.MainWindow!.Title = $"YuNotes — {System.IO.Path.GetFileName(_doc.Info.FilePath)}";
        SyncActiveTabFromDoc();
        // Persist the new embedded title.
        _doc.IsDirty = true;
        UpdateSaveStateUi();
        ScheduleAutosave();

        if (fileError is not null)
            await Notify("Couldn't rename the file",
                $"The note is now called “{title}”, but its file kept its old name: {fileError}");
    }

    private bool _toolsInitialized;

    private void Load(Document d)
    {
        _doc = d;
        var fileName = System.IO.Path.GetFileName(d.Info.FilePath);
        if (string.IsNullOrEmpty(fileName)) fileName = d.Info.Title;
        App.MainWindow!.Title = $"YuNotes — {fileName}";
        TopBarTitle.Text = d.Info.Title;
        UpdateSaveStateUi();
        var settings = App.Services.Settings.Current;
        var renderer = new PageRenderer(App.Services.Templates);
        Canvas.Bind(d, settings, renderer);
        App.Services.History.Switch(d);   // keeps this note's undo history across tab switches
        UpdateHistoryUi();
        RestoreLastPage();
        SyncActiveTabFromDoc();
        if (_refPath is not null && SamePath(_refPath, d.Info.FilePath)) CloseReference();

        // Tools, colors and widths are set up once per visit to the editor;
        // switching tabs keeps whatever tool the user is holding.
        if (!_toolsInitialized)
        {
            _toolsInitialized = true;

            // Restore persisted tool modes
            _penPressureMode = settings.PenPressureMode;
            _pen.UsePressure = _penPressureMode;
            _eraserPixelMode = settings.EraserPixelMode;
            Canvas.Context.LaserMode = settings.LaserMode;
            SyncLaserMenu();
            _selectMode = settings.SelectRectMode ? ToolKind.RectSelect : ToolKind.Lasso;
            SyncPenMenu();
            SyncEraserMenu();
            SyncSelectMenu();

            Canvas.Context.CurrentColor = Controls.ColorPickerControl.ParseHex(settings.DefaultPenColorHex);
            Canvas.Context.EraserPixelMode = _eraserPixelMode;

            // Initialize per-tool widths from defaults (per-session state starts here)
            _penWidth        = (float)NearestPreset(PenWidths,         settings.DefaultPenWidth);
            _highlighterWidth= (float)NearestPreset(HighlighterWidths, settings.DefaultHighlighterWidth);
            _eraserWidth     = (float)NearestPreset(EraserWidths,      settings.DefaultEraserWidth);

            Canvas.Context.CurrentWidth = _penWidth;
            Canvas.Context.EraserWidth  = _eraserWidth;
            ColorPicker.SetPresets(settings.PenPresetColors, Canvas.Context.CurrentColor);
            WidthPicker.SetPresets(PenWidths, _penWidth);
        }

        ZoomPanel.Visibility = settings.HideZoomBar ? Visibility.Collapsed : Visibility.Visible;

        // Extract selectable text from the source PDF, off the UI thread. The result
        // is wired back into each NotePage so the Hand tool can hit-test against it
        // and provide browser-style text selection over the PDF background.
        if (d.SourcePdfBytes is { Length: > 0 } srcBytes)
        {
            var pages = d.Pages;
            _ = Task.Run(() =>
            {
                var byPage = App.Services.PdfText.Extract(srcBytes);
                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var p in pages)
                    {
                        if (p.SourcePageIndex is int idx && idx >= 0 && idx < byPage.Count)
                        {
                            p.TextRuns.Clear();
                            p.TextRuns.AddRange(byPage[idx]);
                            // If the page has a left extension, the PDF is drawn at BackgroundLeft
                            // in page-space — shift all text runs to match.
                            if (p.BackgroundLeft > 0)
                                foreach (var r in p.TextRuns) r.X += p.BackgroundLeft;
                        }
                    }
                    // PDF text arrived after the note opened: refresh an open search.
                    if (SearchPanel.Visibility == Visibility.Visible && SearchBox.Text.Trim().Length > 0)
                        RunSearch();
                });
            });
        }
    }

    private void DoUndo()
    {
        AfterHistoryStep(App.Services.History.Undo());
        RefreshAfterHistoryStep();
    }
    private void DoRedo()
    {
        AfterHistoryStep(App.Services.History.Redo());
        RefreshAfterHistoryStep();
    }

    private void RefreshAfterHistoryStep()
    {
        if (App.Services.History.LastStepChangedPageSet && _doc is not null)
        {
            // Pages were added or removed (undo/redo of adding pages): rebuild
            // the page views. Drop a selection that may point at a removed page,
            // and keep the active page valid.
            Canvas.ClearSelection();
            if (Canvas.Context.CurrentPage is { } cur && !_doc.Pages.Contains(cur))
                Canvas.Context.CurrentPage = _doc.Pages.LastOrDefault() ?? cur;
            Canvas.Rebuild();
            _selectedPages.Clear(); _selectionAnchor = -1;
            if (_pagePanelOpen) _ = RefreshPagePanelAsync();
            UpdateSelectionUi();
            return;
        }
        Canvas.ResizeAllCanvases();  // page.Width may have changed (extension undo)
        Canvas.RefreshTemplates();   // template may have changed (template undo)
        Canvas.InvalidateAll();
        // Bookmark labels live on the page thumbnails; redraw them if an
        // undo/redo changed a bookmark.
        if (_pagePanelOpen && BookmarkSignature() != _panelBookmarkSig) _ = RefreshPagePanelAsync();
    }

    private string _panelBookmarkSig = "";
    private string BookmarkSignature() =>
        _doc is null ? "" : string.Join("|", _doc.Pages.Select(p => p.Bookmark ?? ""));

    // An undo/redo edits the document like any other mutation: it must dirty
    // the doc (save prompt + autosave) and mark the touched pages' flattened
    // container images stale. (Previously undo left IsDirty untouched, so an
    // undo right after a save could be silently lost on close.)
    private void AfterHistoryStep(IReadOnlyList<string> changedPageIds)
    {
        if (_doc is null) return;
        if (changedPageIds.Count == 0 && !App.Services.History.LastStepChangedPageSet) return;
        _doc.IsDirty = true;
        foreach (var id in changedPageIds) _doc.FlattenDirtyPageIds.Add(id);
        UpdateSaveStateUi();
        ScheduleAutosave();
    }
    private void UpdateHistoryUi()
    {
        UndoBtn.IsEnabled = App.Services.History.CanUndo;
        RedoBtn.IsEnabled = App.Services.History.CanRedo;
    }

    // After any page add/move/delete/duplicate. These ops never set IsDirty
    // before (a reorder followed by close was silently lost). Page CONTENT is
    // untouched, so no flatten invalidation is needed here: new pages aren't in
    // FlattenPageOrder (always rendered fresh) and moved pages are handled by
    // the save-time neighbour check.
    private void MarkPageSetChanged()
    {
        if (_doc is null) return;
        _doc.IsDirty = true;
        UpdateSaveStateUi();
        ScheduleAutosave();
    }

    // Reflects autosave state next to the title: green "✓ Saved" once clean, a
    // pending dot + "Unsaved" while there are edits, "Saving…" during a write.
    private void UpdateSaveStateUi()
    {
        if (SaveStateText is null) return;
        var muted = (Brush)Application.Current.Resources["AppTextSecondaryBrush"];
        var success = (Brush)Application.Current.Resources["AppSuccessBrush"];
        if (_saving)
        {
            SaveStateText.Text = "Saving…";
            SaveStateText.Foreground = muted;
            SaveStateCheck.Visibility = Visibility.Collapsed;
            SaveStateDot.Visibility = Visibility.Collapsed;
        }
        else if (_doc?.IsDirty == true)
        {
            SaveStateText.Text = "Unsaved";
            SaveStateText.Foreground = muted;
            SaveStateCheck.Visibility = Visibility.Collapsed;
            SaveStateDot.Visibility = Visibility.Visible;
        }
        else
        {
            SaveStateText.Text = "Saved";
            SaveStateText.Foreground = success;
            SaveStateCheck.Visibility = Visibility.Visible;
            SaveStateDot.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Ignore shortcuts while the interactive save lock is on (they'd mutate the
        // document being flattened). Scroll/zoom aren't accelerators, so still work.
        if (Canvas.Context.EditingSuspended) { args.Handled = true; return; }
        // While the screenshot overlay is up it owns the keyboard: Esc cancels it,
        // everything else is swallowed so it can't mutate the doc underneath.
        if (ScreenshotOverlay.Visibility == Visibility.Visible)
        {
            if (sender.Key == VirtualKey.Escape) ExitScreenshotMode();
            args.Handled = true;
            return;
        }
        switch (sender.Key)
        {
            case VirtualKey.Delete:
            case VirtualKey.Back:
                Canvas.DeleteSelection(); UpdateSelectionUi(); args.Handled = true; break;
            case VirtualKey.T when sender.Modifiers == VirtualKeyModifiers.Control:
                if (!_presenting) ShowNewTabPicker();
                args.Handled = true; break;
            case VirtualKey.W when sender.Modifiers == VirtualKeyModifiers.Control:
                if (!_presenting) _ = CloseTabAsync(s_activeTab);
                args.Handled = true; break;
            case VirtualKey.Tab when sender.Modifiers.HasFlag(VirtualKeyModifiers.Control):
                if (!_presenting) _ = CycleTabAsync(sender.Modifiers.HasFlag(VirtualKeyModifiers.Shift) ? -1 : +1);
                args.Handled = true; break;
            case VirtualKey.F5:
                if (_presenting) ExitPresentation(); else EnterPresentation();
                args.Handled = true; break;
            case VirtualKey.Escape:
                if (_presenting) { ExitPresentation(); args.Handled = true; break; }
                Canvas.ClearSelection();
                Canvas.ClearTextSelection();
                _image.PendingPng = null;
                HideImageGhost();
                UpdateSelectionUi(); args.Handled = true; break;
            case VirtualKey.C when sender.Modifiers == VirtualKeyModifiers.Control:
                var copy = Canvas.GetSelectedText();
                if (!string.IsNullOrEmpty(copy))
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(copy);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                }
                args.Handled = true; break;
            case VirtualKey.V when sender.Modifiers == VirtualKeyModifiers.Control:
                // Let a focused text field (inline text edit, search box, …) paste
                // text itself; only paste an image when the canvas has focus.
                if (Canvas.Context.EditingTextId is not null) break;
                if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or Microsoft.UI.Xaml.Controls.RichEditBox) break;
                _ = PasteClipboardImageAsync();
                args.Handled = true; break;
            case VirtualKey.D when sender.Modifiers == VirtualKeyModifiers.Control:
                Canvas.DuplicateSelection(); UpdateSelectionUi(); args.Handled = true; break;
            case VirtualKey.S when sender.Modifiers == VirtualKeyModifiers.Control:
                _ = SaveAsync(); args.Handled = true; break;
            case VirtualKey.Z when sender.Modifiers == VirtualKeyModifiers.Control:
                DoUndo(); args.Handled = true; break;
            case VirtualKey.Y when sender.Modifiers == VirtualKeyModifiers.Control:
                DoRedo(); args.Handled = true; break;
            case VirtualKey.F when sender.Modifiers == VirtualKeyModifiers.Control:
                OpenSearch(); args.Handled = true; break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number5 when sender.Modifiers == VirtualKeyModifiers.Control:
                ApplyPenFavorite(sender.Key - VirtualKey.Number1); args.Handled = true; break;
        }
    }

    private void InitPicker(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private async Task Notify(string title, string body)
    {
        var d = new ContentDialog { Title = title, Content = body, CloseButtonText = "OK", XamlRoot = this.XamlRoot };
        await d.ShowAsync();
    }

    // ─── Custom toast (non-modal, auto-dismissing) ───────────────────────────

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _toastTimer;

    private void ShowToast(string message, bool error = false)
    {
        ToastText.Text = message;
        ToastIcon.Glyph = error ? "" : "";   // error : check
        ToastIcon.Foreground = (Brush)Application.Current.Resources[error ? "AppAccentBrush" : "AppSuccessBrush"];
        ToastHost.Visibility = Visibility.Visible;

        if (_toastTimer is null)
        {
            _toastTimer = DispatcherQueue.CreateTimer();
            _toastTimer.IsRepeating = false;
            _toastTimer.Tick += (_, _) => { _toastTimer!.Stop(); ToastHost.Visibility = Visibility.Collapsed; };
        }
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(error ? 3.5 : 1.8);
        _toastTimer.Start();
    }
}
