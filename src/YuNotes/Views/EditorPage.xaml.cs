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

public sealed partial class EditorPage : Page
{
    private Document? _doc;
    private ToolKind _selected = ToolKind.Pen;
    private ToolKind _selectMode = ToolKind.Lasso;
    private ToolKind? _restoreToolKind;
    private bool _eraserPixelMode;
    private bool _penPressureMode;
    private bool _suppressZoomSlider;

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
    private ShapeKind _shapeKind = ShapeKind.Rectangle;

    public EditorPage()
    {
        InitializeComponent();
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
        ScreenshotBtn.Click += (_, __) => App.Services.Screenshot.LaunchRegionCapture();
        TemplateBtn.Click += async (_, __) => await ChooseTemplateAsync();

        ToolHand.Click += (_, __) => SelectTool(ToolKind.Pan);
        ToolShape.Click += (_, __) => SelectTool(ToolKind.Shape);
        ToolPen.Click += (_, __) => SelectTool(ToolKind.Pen);
        ToolHighlighter.Click += (_, __) => SelectTool(ToolKind.Highlighter);
        ToolEraser.Click += (_, __) => SelectTool(ToolKind.Eraser);
        ToolText.Click += (_, __) => SelectTool(ToolKind.Text);
        ToolImage.Click += async (_, __) => { SelectTool(ToolKind.Image); await PickImageAsync(); };
        ToolSelect.Click += (_, __) => SelectTool(_selectMode);
        ToolExtend.Click += (_, __) => SelectTool(ToolKind.ExtendPage);

        ShapeRectItem.Click     += (_, __) => { _shapeKind = ShapeKind.Rectangle; _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ShapeEllipseItem.Click  += (_, __) => { _shapeKind = ShapeKind.Ellipse;   _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ShapeLineItem.Click     += (_, __) => { _shapeKind = ShapeKind.Line;      _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ShapeTriangleItem.Click += (_, __) => { _shapeKind = ShapeKind.Triangle;  _shape.ShapeKind = _shapeKind; SyncShapeMenu(); SelectTool(ToolKind.Shape); ShapeFlyout.Hide(); };
        ToolRuler.Click += (_, __) => Canvas.SetRulerVisible(ToolRuler.IsChecked == true);
        EraserStrokeItem.Click += (_, __) => { _eraserPixelMode = false; SyncEraserMenu(); Canvas.Context.EraserPixelMode = false; SaveToolModes(); SelectTool(ToolKind.Eraser); EraserFlyout.Hide(); };
        EraserPixelItem.Click += (_, __) => { _eraserPixelMode = true; SyncEraserMenu(); Canvas.Context.EraserPixelMode = true; SaveToolModes(); SelectTool(ToolKind.Eraser); EraserFlyout.Hide(); };
        // The pen flyout stays open on selection so the preview shows the new mode.
        PenSolidItem.Click += (_, __) => { _penPressureMode = false; _pen.UsePressure = false; SyncPenMenu(); SaveToolModes(); SelectTool(ToolKind.Pen); PenPreview.Invalidate(); };
        PenPressureItem.Click += (_, __) => { _penPressureMode = true; _pen.UsePressure = true; SyncPenMenu(); SaveToolModes(); SelectTool(ToolKind.Pen); PenPreview.Invalidate(); };
        PenFlyout.Opened += (_, __) => PenPreview.Invalidate();
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
            _ => _pen
        };
        Canvas.DocumentMutated += (_, __) => { if (_doc is not null) _doc.IsDirty = true; App.Services.History.RecordMutation(); UpdateSelectionUi(); };
        Canvas.SelectionChanged += (_, __) => UpdateSelectionUi();
        Canvas.ToolRequested += (_, kind) => { SelectTool(kind); HideImageGhost(); };
        Canvas.AddPageRequested += (_, __) => AddBlankPage();

        // Image ghost: follows the pointer until the user clicks to place
        RootGrid.AddHandler(PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), handledEventsToo: true);

        // Finger touch or middle-mouse temporarily overrides the active tool with the
        // Hand/Pan tool. Only flash the toolbar's checked state — the heavy SelectTool
        // path (ColorPicker / WidthPicker rebuild) was making every gesture feel slow.
        // RectSelectionCompleted is no longer used (copy-as-image is now a toolbar button).

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

        PagePanelBtn.Click += (_, __) => TogglePagePanel();
        PageEditModeBtn.Click += (_, __) => { _pagePanelEditMode = !_pagePanelEditMode; ClearPageSelection(); if (_pagePanelOpen) _ = RefreshPagePanelAsync(); };
        PageSelDeleteBtn.Click += async (_, __) => await DeleteSelectedPagesAsync();
        PageSelClearBtn.Click  += (_, __) => ClearPageSelection();

        Canvas.ActivePageScrolled += (_, __) => UpdatePagePanelHighlight();

        SearchBtn.Click += (_, __) => OpenSearch();
        SearchCloseBtn.Click += (_, __) => CloseSearch();
        SearchBox.TextChanged += (_, __) => RunSearch();
        SearchBox.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Escape) CloseSearch(); };
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
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control });
        foreach (var a in KeyboardAccelerators) a.Invoked += OnAccelerator;

        ColorPicker.ColorChanged += (_, c) =>
        {
            Canvas.Context.CurrentColor = c;
        };
        WidthPicker.WidthChanged += (_, w) =>
        {
            switch (_selected)
            {
                case ToolKind.Pen:        _penWidth = (float)w; Canvas.Context.CurrentWidth = _penWidth; break;
                case ToolKind.Highlighter: _highlighterWidth = (float)w; Canvas.Context.CurrentWidth = _highlighterWidth; break;
                case ToolKind.Eraser:     _eraserWidth = (float)w; Canvas.Context.EraserWidth = _eraserWidth; break;
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
        };

        SizeChanged += (_, e) => ApplyToolbarScaleForWidth(e.NewSize.Width);
        Loaded += (_, __) => ApplyToolbarPosition(App.Services.Settings.Current.ToolbarPosition);
        Loaded += (_, __) =>
        {
            MainWindow.SetDragRegion(TopBarDragRegion);
            TopBarGrid.Padding = new Thickness(6, 2, 6 + MainWindow.CaptionButtonInset, 2);
        };

        DragHandle.PointerPressed += DragHandle_PointerPressed;
        DragHandle.PointerMoved += DragHandle_PointerMoved;
        DragHandle.PointerReleased += DragHandle_PointerReleased;
        DragHandle.PointerCaptureLost += DragHandle_PointerReleased;
    }

    private bool _dragActive;
    private Windows.Foundation.Point _dragStart;
    private Thickness _dragStartMargin;

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragActive = DragHandle.CapturePointer(e.Pointer);
        if (!_dragActive) return;
        _dragStart = e.GetCurrentPoint(null).Position;
        _dragStartMargin = ToolbarHost.Margin;
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragActive) return;
        var p = e.GetCurrentPoint(null).Position;
        double dx = p.X - _dragStart.X;
        double dy = p.Y - _dragStart.Y;

        // Constrain to the perpendicular axis of the anchored edge so the toolbar
        // doesn't drift sideways out of the canvas area.
        var pos = App.Services.Settings.Current.ToolbarPosition;
        var m = _dragStartMargin;
        switch (pos)
        {
            case ToolbarPosition.Top:
                m.Top = Math.Max(0, _dragStartMargin.Top + dy);
                break;
            case ToolbarPosition.Bottom:
                m.Bottom = Math.Max(0, _dragStartMargin.Bottom - dy);
                break;
            case ToolbarPosition.Left:
                m.Left = Math.Max(0, _dragStartMargin.Left + dx);
                break;
            case ToolbarPosition.Right:
                m.Right = Math.Max(0, _dragStartMargin.Right - dx);
                break;
        }
        ToolbarHost.Margin = m;
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragActive) return;
        _dragActive = false;
        DragHandle.ReleasePointerCapture(e.Pointer);
    }

    private void ApplyToolbarPosition(ToolbarPosition pos)
    {
        bool vertical = pos is ToolbarPosition.Left or ToolbarPosition.Right;

        // Floating pill: always live in the canvas row, anchored to an edge.
        Grid.SetRow(ToolbarHost, 1);
        Grid.SetColumn(ToolbarHost, 0);
        Grid.SetRowSpan(ToolbarHost, 1);
        Grid.SetColumnSpan(ToolbarHost, 3);
        ToolbarHost.BorderThickness = new Thickness(1);
        ToolbarHost.CornerRadius = new CornerRadius(22);
        const double gap = 14;
        switch (pos)
        {
            case ToolbarPosition.Top:
                ToolbarHost.HorizontalAlignment = HorizontalAlignment.Center;
                ToolbarHost.VerticalAlignment = VerticalAlignment.Top;
                ToolbarHost.Margin = new Thickness(0, gap, 0, 0);
                break;
            case ToolbarPosition.Bottom:
                ToolbarHost.HorizontalAlignment = HorizontalAlignment.Center;
                ToolbarHost.VerticalAlignment = VerticalAlignment.Bottom;
                ToolbarHost.Margin = new Thickness(0, 0, 0, gap);
                break;
            case ToolbarPosition.Left:
                ToolbarHost.HorizontalAlignment = HorizontalAlignment.Left;
                ToolbarHost.VerticalAlignment = VerticalAlignment.Center;
                ToolbarHost.Margin = new Thickness(gap, 0, 0, 0);
                break;
            case ToolbarPosition.Right:
                ToolbarHost.HorizontalAlignment = HorizontalAlignment.Right;
                ToolbarHost.VerticalAlignment = VerticalAlignment.Center;
                ToolbarHost.Margin = new Thickness(0, 0, gap, 0);
                break;
        }

        // Re-orient the floating toolbar's inner stack between horizontal/vertical.
        ToolbarPrimary.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ToolbarPrimary.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        ToolbarPrimary.VerticalAlignment = vertical ? VerticalAlignment.Top : VerticalAlignment.Center;
        ToolbarPrimary.Padding = vertical ? new Thickness(6, 12, 6, 12) : new Thickness(12, 6, 12, 6);

        ToolbarScroller.HorizontalScrollMode = vertical ? ScrollMode.Disabled : ScrollMode.Auto;
        ToolbarScroller.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        ToolbarScroller.VerticalScrollMode = vertical ? ScrollMode.Auto : ScrollMode.Disabled;
        ToolbarScroller.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;

        // Flip the inline separators between vertical/horizontal lines.
        // Negative margins pull adjacent buttons closer, fighting the StackPanel Spacing.
        foreach (var sep in new[] { Sep1, Sep2, Sep3 })
        {
            if (vertical) { sep.Width = 28; sep.Height = 1; sep.Margin = new Thickness(0, -3, 0, -3); }
            else          { sep.Width = 1;  sep.Height = 28; sep.Margin = new Thickness(-3, 0, -3, 0); }
        }

        // Flip the selection action chip's inner panel + the pickers
        SelectionActionsPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        ColorPicker.SetOrientation(vertical ? Orientation.Vertical : Orientation.Horizontal);
        WidthPicker.SetOrientation(vertical ? Orientation.Vertical : Orientation.Horizontal);

        // WinUI's default ToggleButton theme style carries HorizontalAlignment=Left.
        // A horizontal StackPanel ignores it (slot = desired width), but in the
        // vertical toolbar it pins every tool icon a few px left of the pill's
        // centerline while the drag handle and pickers center. Center the buttons
        // when vertical; restore the theme default when horizontal.
        var toolAlign = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        foreach (var tool in new FrameworkElement[]
        {
            ToolHand, ToolPen, ToolHighlighter, ToolEraser, ToolText, ToolImage,
            ToolShape, ToolRuler, ToolSelect, ToolExtend,
            SelDuplicateBtn, SelDeleteBtn, SelClearBtn
        })
            tool.HorizontalAlignment = toolAlign;
    }

    private void ApplyToolbarScaleForWidth(double pageWidth)
    {
        // ── Top pinned bar — always Normal size, never affected by ToolbarSize ──
        const double topBtn  = 44;
        const double topIcon = 24;
        var topRadius = new CornerRadius(topBtn / 2);
        Control[] topBarBtns = {
            BackBtn, UndoBtn, RedoBtn, SaveBtn, ExportBtn,
            PagePanelBtn, SearchBtn, ScreenshotBtn, AddPageBtn, AddPdfPagesBtn, TemplateBtn, SettingsBtn
        };
        foreach (var c in topBarBtns)
        {
            c.Width = topBtn; c.Height = topBtn;
            c.CornerRadius = topRadius;
            if (c is ContentControl cc && cc.Content is FontIcon fi) fi.FontSize = topIcon;
        }

        // ── Floating drawing toolbar — scales with window width AND ToolbarSize ──
        double btn, icon, colorBtn, chip, chipDot;
        if (pageWidth >= 1200)      { btn = 44; icon = 24; colorBtn = 30; chip = 36; chipDot = 22; }
        else if (pageWidth >= 1000) { btn = 38; icon = 21; colorBtn = 26; chip = 32; chipDot = 18; }
        else if (pageWidth >= 800)  { btn = 32; icon = 18; colorBtn = 22; chip = 28; chipDot = 14; }
        else                        { btn = 28; icon = 16; colorBtn = 20; chip = 24; chipDot = 12; }

        double scale = App.Services.Settings.Current.ToolbarSize switch
        {
            Models.ToolbarSize.Small  => 0.65,
            Models.ToolbarSize.Large  => 1.00,
            _                         => 0.78   // Normal
        };
        if (scale != 1.0)
        {
            btn      = Math.Round(btn      * scale);
            icon     = Math.Round(icon     * scale);
            colorBtn = Math.Round(colorBtn * scale);
            chip     = Math.Round(chip     * scale);
            chipDot  = Math.Round(chipDot  * scale);
        }

        Control[] floatingBtns = {
            ToolHand, ToolPen, ToolHighlighter, ToolEraser, ToolText, ToolImage, ToolShape, ToolRuler, ToolSelect, ToolExtend,
            SelDuplicateBtn, SelDeleteBtn, SelClearBtn
        };
        var floatRadius = new CornerRadius(btn / 2);
        foreach (var c in floatingBtns)
        {
            c.Width = btn; c.Height = btn;
            c.CornerRadius = floatRadius;
            if (c is ContentControl cc && cc.Content is FontIcon fi) fi.FontSize = icon;
        }

        // Named icons inside composite tools (belt-and-suspenders in case template doesn't re-read Content)
        PenIcon.FontSize = icon;
        HighlighterIcon.FontSize = icon;
        EraserIcon.FontSize = icon;
        ShapeIcon.FontSize = icon;
        SelectIcon.FontSize = icon;

        // Drag handle — not a Control, scaled separately
        DragHandle.Width = btn; DragHandle.Height = btn;
        DragHandleIcon.FontSize = icon;

        // Chevron sub-buttons — width matches main button, height ~30%
        double chevronHeight = Math.Max(10, btn * 0.32);
        foreach (var cb in new[] { ToolPenChevron, ToolEraserChevron, ToolShapeChevron, ToolSelectChevron })
        {
            cb.Width = btn;
            cb.Height = chevronHeight;
        }
        PenChevronIcon.FontSize    = Math.Max(7, icon * 0.55);
        EraserChevronIcon.FontSize = Math.Max(7, icon * 0.55);
        ShapeChevronIcon.FontSize  = Math.Max(7, icon * 0.55);
        SelectChevronIcon.FontSize = Math.Max(7, icon * 0.55);

        // Color and width pickers follow the floating toolbar size
        ColorPicker.SetButtonSize(colorBtn);
        WidthPicker.SetChipSize(chip, chipDot);
    }

    private void ApplyToolbarOrder()
    {
        var s = App.Services.Settings.Current;

        // Drawing tools in ToolbarPrimary
        var toolMap = new Dictionary<string, UIElement>
        {
            ["Hand"] = ToolHand, ["Pen"] = PenGroup, ["Highlighter"] = ToolHighlighter,
            ["Eraser"] = EraserGroup, ["Text"] = ToolText, ["Image"] = ToolImage,
            ["Shape"] = ShapeGroup, ["Select"] = SelectGroup,
        };
        var defaultDrawing = new[] { "Hand", "Pen", "Highlighter", "Eraser", "Text", "Image", "Shape", "Select" };
        // Saved custom order first, then any default keys it omits, de-duplicated. Distinct is
        // essential: inserting the same element twice throws COMException 0x800F1000 ("element
        // already has a parent"). An empty saved order falls through to the defaults.
        var drawOrder = s.ToolbarDrawingOrder.Where(toolMap.ContainsKey)
                        .Concat(defaultDrawing)
                        .Distinct()
                        .ToList();

        foreach (var el in toolMap.Values) ToolbarPrimary.Children.Remove(el);
        // Insert after DragHandle (0) and Sep1 (1)
        int idx = 2;
        foreach (var key in drawOrder) ToolbarPrimary.Children.Insert(idx++, toolMap[key]);

        // Action buttons in ToolbarSecondary
        var actionMap = new Dictionary<string, UIElement>
        {
            ["Screenshot"] = ScreenshotBtn, ["Template"] = TemplateBtn,
            ["AddPdfPages"] = AddPdfPagesBtn, ["AddPage"] = AddPageBtn,
        };
        var defaultAction = new[] { "Screenshot", "Template", "AddPdfPages", "AddPage" };
        var actionOrder = s.ToolbarActionOrder.Where(actionMap.ContainsKey)
                          .Concat(defaultAction)
                          .Distinct()
                          .ToList();

        foreach (var el in actionMap.Values) ToolbarSecondary.Children.Remove(el);
        // Re-insert after TopSep1
        int sep1Idx = -1;
        for (int i = 0; i < ToolbarSecondary.Children.Count; i++)
            if (ToolbarSecondary.Children[i] == TopSep1) { sep1Idx = i; break; }
        if (sep1Idx >= 0)
        {
            int insertAt = sep1Idx + 1;
            foreach (var key in actionOrder)
                ToolbarSecondary.Children.Insert(insertAt++, actionMap[key]);
        }
    }

    private void ApplyToolbarVisibility()
    {
        var hidden = App.Services.Settings.Current.HiddenToolbarTools;
        bool IsHidden(string key) => hidden.Contains(key);
        Visibility Show(string key) => IsHidden(key) ? Visibility.Collapsed : Visibility.Visible;

        // Primary floating toolbar — drawing tools
        ToolHand.Visibility       = Show("Hand");
        PenGroup.Visibility       = Show("Pen");
        ToolHighlighter.Visibility = Show("Highlighter");
        EraserGroup.Visibility    = Show("Eraser");
        ToolText.Visibility       = Show("Text");
        ToolImage.Visibility      = Show("Image");
        ShapeGroup.Visibility     = Show("Shape");
        SelectGroup.Visibility    = Show("Select");

        // Secondary bar — action buttons
        ScreenshotBtn.Visibility   = Show("Screenshot");
        AddPageBtn.Visibility      = Show("AddPage");
        AddPdfPagesBtn.Visibility  = Show("AddPdfPages");
        ExportBtn.Visibility       = Show("Export");
        TemplateBtn.Visibility     = Show("Template");

        // Hide the first separator if the entire insert group collapsed —
        // otherwise two dividers would stack with nothing between them.
        bool anyInsertVisible =
            !IsHidden("Screenshot") || !IsHidden("AddPage") ||
            !IsHidden("AddPdfPages") || !IsHidden("Template");
        TopSep1.Visibility = anyInsertVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is Document d) Load(d);
        else if (e.Parameter is string path) _ = LoadFromPathAsync(path);
        var s = App.Services.Settings.Current;
        _pen.HoldToSnapEnabled = s.HoldToSnapEnabled;
        ApplyToolbarPosition(s.ToolbarPosition);
        ApplyToolbarScaleForWidth(ActualWidth > 0 ? ActualWidth : 1200);
        ApplyToolbarOrder();
        ApplyToolbarVisibility();
        ToolbarGlass.RefractionSource = Canvas;
        ToolbarGlass.IsGlassActive = s.LiquidGlassEnabled;
        if (_doc is not null) ZoomPanel.Visibility = s.HideZoomBar ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.MainWindow!.Title = "YuNotes";
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
            Load(doc);
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
            MainWindow.GoBack();
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Load(Document d)
    {
        _doc = d;
        var fileName = System.IO.Path.GetFileName(d.Info.FilePath);
        if (string.IsNullOrEmpty(fileName)) fileName = d.Info.Title;
        App.MainWindow!.Title = $"YuNotes — {fileName}";
        TopBarTitle.Text = d.Info.Title;
        var settings = App.Services.Settings.Current;

        // Restore persisted tool modes
        _penPressureMode = settings.PenPressureMode;
        _pen.UsePressure = _penPressureMode;
        _eraserPixelMode = settings.EraserPixelMode;
        _selectMode = settings.SelectRectMode ? ToolKind.RectSelect : ToolKind.Lasso;
        SyncPenMenu();
        SyncEraserMenu();
        SyncSelectMenu();

        var renderer = new PageRenderer(App.Services.Templates);
        Canvas.Bind(d, settings, renderer);
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

        ZoomPanel.Visibility = settings.HideZoomBar ? Visibility.Collapsed : Visibility.Visible;

        App.Services.History.Bind(d);
        UpdateHistoryUi();

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
                });
            });
        }
    }

    private void UpdateChevronColors()
    {
        // Active: accent color (matches the icon's ToggleButtonForegroundChecked).
        // Inactive: white (on the opaque unchecked button background).
        var accent = (Brush)Application.Current.Resources["AppAccentBrush"];
        var white  = new SolidColorBrush(Colors.White);
        PenChevronIcon.Foreground    = ToolPen.IsChecked    == true ? accent : white;
        EraserChevronIcon.Foreground = ToolEraser.IsChecked == true ? accent : white;
        SelectChevronIcon.Foreground = ToolSelect.IsChecked == true ? accent : white;
        ShapeChevronIcon.Foreground  = ToolShape.IsChecked  == true ? accent : white;
    }

    // UI-only toolbar toggle (no Canvas.Context / ColorPicker / WidthPicker work).
    // ColorPicker.SetCurrent and WidthPicker.SetPresets each rebuild their child
    // visuals — too heavy to call on every touch pan or pinch.
    private void UpdateToolUi(ToolKind k)
    {
        ToolPen.IsChecked = k == ToolKind.Pen;
        ToolHighlighter.IsChecked = k == ToolKind.Highlighter;
        ToolEraser.IsChecked = k == ToolKind.Eraser;
        ToolText.IsChecked = k == ToolKind.Text;
        ToolImage.IsChecked = k == ToolKind.Image;
        ToolShape.IsChecked = k == ToolKind.Shape;
        ToolHand.IsChecked = k == ToolKind.Pan;
        ToolSelect.IsChecked = k is ToolKind.Lasso or ToolKind.RectSelect;
        ToolExtend.IsChecked = k == ToolKind.ExtendPage;
        UpdateChevronColors();
    }

    private void SelectTool(ToolKind k)
    {
        // Switching from a select tool to anything else (Pen, Eraser, …) drops any
        // pending pen selection — otherwise the toolbar's Delete chip lingers and
        // the user sees its tooltip with no obvious source. Skip when this is a
        // momentary override (finger / middle-mouse pan) so a real selection isn't
        // wiped out just because the user briefly panned.
        bool isMomentaryRevert = _restoreToolKind is not null;
        bool wasSelect = _selected is ToolKind.Lasso or ToolKind.RectSelect;
        bool nowSelect = k is ToolKind.Lasso or ToolKind.RectSelect;
        if (!isMomentaryRevert && wasSelect && !nowSelect)
        {
            Canvas.ClearSelection();
            UpdateSelectionUi();
        }

        _selected = k;
        Canvas.SetExtendMode(k == ToolKind.ExtendPage);
        UpdateToolUi(k);

        var s = App.Services.Settings.Current;
        switch (k)
        {
            case ToolKind.Pen:
                Canvas.Context.CurrentColor = Controls.ColorPickerControl.ParseHex(s.DefaultPenColorHex);
                Canvas.Context.CurrentWidth = _penWidth;
                ColorPicker.SetPresets(s.PenPresetColors, Canvas.Context.CurrentColor);
                WidthPicker.SetPresets(PenWidths, _penWidth);
                break;
            case ToolKind.Highlighter:
                Canvas.Context.CurrentColor = Controls.ColorPickerControl.ParseHex(s.DefaultHighlighterColorHex);
                Canvas.Context.CurrentWidth = _highlighterWidth;
                ColorPicker.SetPresets(s.HighlighterPresetColors, Canvas.Context.CurrentColor);
                WidthPicker.SetPresets(HighlighterWidths, _highlighterWidth);
                break;
            case ToolKind.Eraser:
                Canvas.Context.EraserWidth = _eraserWidth;
                Canvas.Context.EraserPixelMode = _eraserPixelMode;
                WidthPicker.SetPresets(EraserWidths, _eraserWidth);
                break;
        }
    }

    private static double NearestPreset(double[] presets, double v)
    {
        double best = presets[0]; double bestDiff = double.MaxValue;
        foreach (var p in presets) { var d = Math.Abs(p - v); if (d < bestDiff) { bestDiff = d; best = p; } }
        return best;
    }

    // Material Symbols glyphs used by both the flyout items and the main tool icons.
    // The main tool icon mirrors the currently chosen sub-option.
    private const string GlyphStylus            = "";
    private const string GlyphBrush             = "";
    private const string GlyphEraserSize1       = "";
    private const string GlyphRectangle         = "";
    private const string GlyphCircle            = "";
    private const string GlyphDiagonalLine      = "";
    private const string GlyphSignalCellularNull = "";
    private const string GlyphLassoSelect       = "";
    private const string GlyphInkSelection      = "";

    private void SyncEraserMenu()
    {
        EraserStrokeItem.IsChecked = !_eraserPixelMode;
        EraserPixelItem.IsChecked = _eraserPixelMode;
    }

    private void SyncSelectMenu()
    {
        SelectLassoItem.IsChecked = _selectMode == ToolKind.Lasso;
        SelectRectItem.IsChecked = _selectMode == ToolKind.RectSelect;
        SelectIcon.Glyph = _selectMode == ToolKind.Lasso ? GlyphLassoSelect : GlyphInkSelection;
    }

    private void SyncPenMenu()
    {
        PenSolidItem.IsChecked = !_penPressureMode;
        PenPressureItem.IsChecked = _penPressureMode;
        PenIcon.Glyph = _penPressureMode ? GlyphBrush : GlyphStylus;
    }

    // Sample stroke in the pen flyout: a calligraphic S-curve drawn by the real
    // stroke renderer with the pen's width/mode — white ink on a dark card,
    // independent of theme and current pen color.
    private PageRenderer? _previewRenderer;

    private void PenPreview_Draw(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender,
                                 Microsoft.Graphics.Canvas.UI.Xaml.CanvasDrawEventArgs args)
    {
        float w = (float)sender.ActualWidth, h = (float)sender.ActualHeight;
        if (w < 1 || h < 1) return;

        var ds = args.DrawingSession;
        ds.FillRoundedRectangle(0, 0, w, h, 10, 10, Color.FromArgb(0xFF, 0x20, 0x24, 0x2F));
        ds.DrawRoundedRectangle(0.5f, 0.5f, w - 1, h - 1, 10, 10, Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), 1f);

        var s = new Stroke
        {
            Kind = StrokeKind.Pen,
            Color = Colors.White,
            Width = _penWidth > 0 ? _penWidth : Canvas.Context.CurrentWidth,
            PressureMode = _penPressureMode
        };
        const int N = 48;
        float margin = w * 0.12f;
        for (int i = 0; i <= N; i++)
        {
            float t = i / (float)N;
            float x = margin + t * (w - 2 * margin);
            float y = h * 0.5f + MathF.Sin(t * MathF.Tau) * h * 0.26f;
            float pressure = 0.2f + 0.8f * MathF.Sin(t * MathF.PI);
            s.Points.Add(new InkPoint(x, y, _penPressureMode ? pressure : 1f));
        }
        _previewRenderer ??= new PageRenderer(App.Services.Templates);
        _previewRenderer.DrawStroke(ds, s);
    }

    private void SyncShapeMenu()
    {
        ShapeRectItem.IsChecked     = _shapeKind == ShapeKind.Rectangle;
        ShapeEllipseItem.IsChecked  = _shapeKind == ShapeKind.Ellipse;
        ShapeLineItem.IsChecked     = _shapeKind == ShapeKind.Line;
        ShapeTriangleItem.IsChecked = _shapeKind == ShapeKind.Triangle;
        ShapeIcon.Glyph = _shapeKind switch
        {
            ShapeKind.Rectangle => GlyphRectangle,
            ShapeKind.Ellipse   => GlyphCircle,
            ShapeKind.Line      => GlyphDiagonalLine,
            ShapeKind.Triangle  => GlyphSignalCellularNull,
            _                   => GlyphRectangle,
        };
    }

    private void SaveToolModes()
    {
        var s = App.Services.Settings.Current;
        s.PenPressureMode = _penPressureMode;
        s.EraserPixelMode = _eraserPixelMode;
        s.SelectRectMode  = _selectMode == ToolKind.RectSelect;
        App.Services.Settings.Save();
    }

    // ─── Text search ─────────────────────────────────────────────────────────────

    private List<(int PageIndex, string TextId)> _searchMatches = new();
    private int _searchMatchIndex = -1;

    private void OpenSearch()
    {
        SearchPanel.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        _searchMatches.Clear();
        _searchMatchIndex = -1;
        UpdateSearchUi();
    }

    private void RunSearch()
    {
        _searchMatches.Clear();
        _searchMatchIndex = -1;
        var q = SearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(q) || _doc is null) { UpdateSearchUi(); return; }

        for (int pi = 0; pi < _doc.Pages.Count; pi++)
        {
            foreach (var t in _doc.Pages[pi].Texts)
            {
                if (!string.IsNullOrEmpty(t.Text) &&
                    t.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                    _searchMatches.Add((pi, t.Id));
            }
        }

        if (_searchMatches.Count > 0) _searchMatchIndex = 0;
        UpdateSearchUi();
        NavigateToCurrentMatch();
    }

    private void StepSearch(int delta)
    {
        if (_searchMatches.Count == 0) return;
        _searchMatchIndex = (_searchMatchIndex + delta + _searchMatches.Count) % _searchMatches.Count;
        UpdateSearchUi();
        NavigateToCurrentMatch();
    }

    private void UpdateSearchUi()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        if (_searchMatches.Count == 0)
            SearchCountLabel.Text = string.IsNullOrEmpty(q) ? "" : "No matches";
        else
            SearchCountLabel.Text = $"{_searchMatchIndex + 1} / {_searchMatches.Count}";
        SearchPrevBtn.IsEnabled = _searchMatches.Count > 1;
        SearchNextBtn.IsEnabled = _searchMatches.Count > 1;
    }

    private void NavigateToCurrentMatch()
    {
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Count) return;
        var (pageIdx, _) = _searchMatches[_searchMatchIndex];
        Canvas.ScrollToPage(pageIdx);
    }

    private void DoUndo()
    {
        App.Services.History.Undo();
        Canvas.ResizeAllCanvases();  // page.Width may have changed (extension undo)
        Canvas.InvalidateAll();
    }
    private void DoRedo()
    {
        App.Services.History.Redo();
        Canvas.ResizeAllCanvases();
        Canvas.InvalidateAll();
    }
    private void UpdateHistoryUi()
    {
        UndoBtn.IsEnabled = App.Services.History.CanUndo;
        RedoBtn.IsEnabled = App.Services.History.CanRedo;
    }

    private void AddBlankPage()
    {
        if (_doc is null) return;
        var last = _doc.Pages.LastOrDefault();
        // Use the pre-extension width so extended PDF pages don't dictate new-page size.
        double newW = last?.BackgroundContentWidth > 0 ? last.BackgroundContentWidth : last?.Width ?? 1240;
        _doc.Pages.Add(new NotePage { Index = _doc.Pages.Count, Width = newW, Height = last?.Height ?? 1754 });
        Canvas.Rebuild();
        App.Services.History.Bind(_doc);   // page set changed; reset history
        UpdateHistoryUi();
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
            App.Services.History.Bind(_doc);
            UpdateHistoryUi();
            if (_pagePanelOpen) _ = RefreshPagePanelAsync();
        }
        catch (Exception ex) { await Notify("Add pages failed", ex.Message); }
    }

    private async Task PickImageAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg");
        InitPicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        _image.PendingPng = File.ReadAllBytes(file.Path);
        await ShowImageGhostAsync(_image.PendingPng);
    }

    private async Task ShowImageGhostAsync(byte[] png)
    {
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(ms))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        ms.Seek(0);
        await bmp.SetSourceAsync(ms);
        ImageGhostImg.Source = bmp;
        ImageGhost.Visibility = Visibility.Visible;
    }

    private void HideImageGhost()
    {
        ImageGhost.Visibility = Visibility.Collapsed;
        ImageGhostImg.Source = null;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ImageGhost.Visibility != Visibility.Visible) return;
        var pt = e.GetCurrentPoint(GhostLayer).Position;
        XamlCanvas.SetLeft(ImageGhost, pt.X - ImageGhostImg.Width / 2);
        XamlCanvas.SetTop(ImageGhost,  pt.Y - ImageGhostImg.Height / 2);
    }

    private async Task ExportAsync()
    {
        if (_doc is null) return;
        var dlg = new ContentDialog
        {
            Title = "Export",
            XamlRoot = this.XamlRoot,
            PrimaryButtonText = "PDF (all pages)",
            SecondaryButtonText = "PNG (all pages)",
            CloseButtonText = "Cancel"
        };
        var res = await dlg.ShowAsync();
        try
        {
            if (res == ContentDialogResult.Primary)
            {
                var save = new FileSavePicker();
                save.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
                save.SuggestedFileName = _doc.Info.Title;
                InitPicker(save);
                var file = await save.PickSaveFileAsync();
                if (file is null) return;
                var pngs = Canvas.RenderAllToPng();
                App.Services.PdfExport.Export(_doc, pngs, file.Path);
                await Notify("Exported", "PDF saved.");
            }
            else if (res == ContentDialogResult.Secondary)
            {
                var folder = new FolderPicker();
                folder.FileTypeFilter.Add("*");
                InitPicker(folder);
                var f = await folder.PickSingleFolderAsync();
                if (f is null) return;
                var pngs = Canvas.RenderAllToPng();
                App.Services.PngExport.ExportAll(pngs, f.Path, _doc.Info.Title);
                await Notify("Exported", "PNGs saved.");
            }
        }
        catch (Exception ex) { await Notify("Export failed", ex.Message); }
    }

    // Template dialog with visual previews and apply-current / apply-all choice
    private async Task ChooseTemplateAsync()
    {
        if (_doc is null) return;

        var kinds = (TemplateKind[])Enum.GetValues(typeof(TemplateKind));
        var selected = _doc.Template.Kind;

        var previewsPanel = new ItemsControl();
        previewsPanel.ItemsPanel = MakeWrap();
        var items = new List<UIElement>();
        foreach (var k in kinds)
        {
            var preview = BuildTemplatePreview(k, 110, 140);
            var border = new Border
            {
                BorderThickness = new Thickness(selected == k ? 2.5 : 1),
                BorderBrush = selected == k
                    ? (Brush)Application.Current.Resources["AppAccentBrush"]
                    : (Brush)Application.Current.Resources["AppBorderBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6),
                Background = (Brush)Application.Current.Resources["AppSurfaceBrush"],
                Margin = new Thickness(0, 0, 8, 8),
            };
            var stack = new StackPanel();
            stack.Children.Add(preview);
            stack.Children.Add(new TextBlock { Text = k.ToString(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            border.Child = stack;

            var btn = new Button
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Content = border,
                Tag = k
            };
            btn.Click += (s, _) =>
            {
                selected = (TemplateKind)((Button)s).Tag;
                foreach (Button candidate in previewsPanel.Items.OfType<Button>())
                {
                    var b = (Border)candidate.Content;
                    var match = (TemplateKind)candidate.Tag == selected;
                    b.BorderThickness = new Thickness(match ? 2.5 : 1);
                    b.BorderBrush = match
                        ? (Brush)Application.Current.Resources["AppAccentBrush"]
                        : (Brush)Application.Current.Resources["AppBorderBrush"];
                }
            };
            items.Add(btn);
        }
        previewsPanel.ItemsSource = items;

        var root = new StackPanel { Spacing = 12, MinWidth = 540 };
        root.Children.Add(new TextBlock { Text = "Choose a template, then apply to the current page or to every page.", Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"] });
        root.Children.Add(previewsPanel);

        var dlg = new ContentDialog
        {
            Title = "Page template",
            Content = root,
            PrimaryButtonText = "Apply to current page",
            SecondaryButtonText = "Apply to all pages",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.None) return;

        if (res == ContentDialogResult.Primary)
        {
            // Apply to current page only — set or update this page's override.
            var page = Canvas.ActivePage;
            if (page is null) return;
            page.TemplateOverride = new TemplateSettings
            {
                Kind = selected,
                Spacing = _doc.Template.Spacing,
                LineColorHex = _doc.Template.LineColorHex,
                LineThickness = _doc.Template.LineThickness
            };
        }
        else
        {
            // Apply to all pages — set document template and drop every per-page override.
            _doc.Template.Kind = selected;
            foreach (var p in _doc.Pages) p.TemplateOverride = null;
        }

        Canvas.RefreshTemplates();
        Canvas.InvalidateAll();
        App.Services.History.RecordMutation();
    }

    private static ItemsPanelTemplate MakeWrap() =>
        (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Orientation='Horizontal' /></ItemsPanelTemplate>");

    // ── Extend Page Width dialog ───────────────────────────────────────────────

    private async Task ShowExtendPageDialogAsync(NotePage page)
    {
        double pdfW = page.BackgroundContentWidth > 0 ? page.BackgroundContentWidth : page.Width;

        // Side selector
        var leftBtn  = new RadioButton { Content = "Left",  GroupName = "ExtendSide", Margin = new Thickness(0, 0, 16, 0) };
        var rightBtn = new RadioButton { Content = "Right", GroupName = "ExtendSide" };
        rightBtn.IsChecked = true;
        var sideRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        sideRow.Children.Add(leftBtn);
        sideRow.Children.Add(rightBtn);

        // Amount slider with preset buttons
        double initialAmount = 0;

        // Figure out existing extension to show as initial value
        if (page.BackgroundLeft > 0 || (page.BackgroundContentWidth > 0 && page.Width > page.BackgroundContentWidth + page.BackgroundLeft))
        {
            double existingLeft  = page.BackgroundLeft;
            double existingRight = page.BackgroundContentWidth > 0
                                   ? Math.Max(0, page.Width - page.BackgroundLeft - page.BackgroundContentWidth)
                                   : 0;
            if (existingLeft > 0)  { leftBtn.IsChecked  = true;  rightBtn.IsChecked = false; initialAmount = existingLeft;  }
            else if (existingRight > 0) initialAmount = existingRight;
        }

        var amountLabel = new TextBlock { Margin = new Thickness(0, 0, 0, 6) };
        void UpdateLabel(double v)
        {
            int pct = pdfW > 0 ? (int)Math.Round(v / pdfW * 100) : 0;
            amountLabel.Text = $"Amount: {(int)v} px  ({pct}% of page width)";
        }

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = pdfW * 2.5,
            StepFrequency = 1,
            Value = initialAmount,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TickFrequency = pdfW * 0.5,
            TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside
        };
        UpdateLabel(slider.Value);
        slider.ValueChanged += (_, e) => UpdateLabel(e.NewValue);

        // Preset buttons
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        (string Label, double Value)[] presets = { ("None (0)", 0), ("¼ page", pdfW * 0.25), ("½ page", pdfW * 0.5), ("1×", pdfW), ("1.5×", pdfW * 1.5), ("2×", pdfW * 2.0) };
        foreach (var (label, value) in presets)
        {
            var btn = new Button { Content = label };
            var v = value;
            btn.Click += (_, __) => slider.Value = v;
            presetRow.Children.Add(btn);
        }

        // Reset hyperlink (only shown when page already has an extension)
        bool hasExtension = page.BackgroundLeft > 0 || (page.BackgroundContentWidth > 0 && page.Width > page.BackgroundContentWidth + page.BackgroundLeft);
        var resetLink = new HyperlinkButton { Content = "Reset extension", Margin = new Thickness(0, 8, 0, 0), Visibility = hasExtension ? Visibility.Visible : Visibility.Collapsed };

        var root = new StackPanel { Spacing = 4, MinWidth = 400 };
        root.Children.Add(new TextBlock
        {
            Text = "Extend this page to the left or right. The extension area fills with the page's current template.",
            Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(new TextBlock { Text = "Direction", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        root.Children.Add(sideRow);
        root.Children.Add(amountLabel);
        root.Children.Add(slider);
        root.Children.Add(presetRow);
        root.Children.Add(resetLink);

        var dlg = new ContentDialog
        {
            Title = "Extend page width",
            Content = root,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        bool resetRequested = false;
        resetLink.Click += (_, __) => { resetRequested = true; dlg.Hide(); };

        var result = await dlg.ShowAsync();

        if (resetRequested)
        {
            Canvas.ResetPageExtension(page);
            App.Services.History.RecordMutation();
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        double amount = slider.Value;

        bool isLeft = leftBtn.IsChecked == true;
        Canvas.CommitPageExtension(page, isLeft ? YuNotes.Controls.ExtendSide.Left : YuNotes.Controls.ExtendSide.Right, amount);
        App.Services.History.RecordMutation();
    }

    internal static UIElement BuildTemplatePreview(TemplateKind kind, double w, double h)
    {
        // Render the template lines as XAML primitives (cheap and crisp).
        var canvas = new XamlCanvas { Width = w, Height = h, Background = new SolidColorBrush(Colors.White) };
        var lineBrush = new SolidColorBrush(Color.FromArgb(255, 200, 205, 215));
        double spacing = 14;
        switch (kind)
        {
            case TemplateKind.Blank:
                break;
            case TemplateKind.Grid:
                for (double x = spacing; x < w; x += spacing)
                    canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = h, Stroke = lineBrush, StrokeThickness = 1 });
                for (double y = spacing; y < h; y += spacing)
                    canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = lineBrush, StrokeThickness = 1 });
                break;
            case TemplateKind.Dots:
                for (double x = spacing; x < w; x += spacing)
                    for (double y = spacing; y < h; y += spacing)
                    {
                        var dot = new Ellipse { Width = 2, Height = 2, Fill = lineBrush };
                        XamlCanvas.SetLeft(dot, x - 1); XamlCanvas.SetTop(dot, y - 1);
                        canvas.Children.Add(dot);
                    }
                break;
            case TemplateKind.Lined:
                for (double y = spacing; y < h; y += spacing)
                    canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = lineBrush, StrokeThickness = 1 });
                break;
            case TemplateKind.Cornell:
                canvas.Children.Add(new Line { X1 = w * 0.25, X2 = w * 0.25, Y1 = 0, Y2 = h, Stroke = lineBrush, StrokeThickness = 1 });
                canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h * 0.85, Y2 = h * 0.85, Stroke = lineBrush, StrokeThickness = 1 });
                for (double y = spacing; y < h; y += spacing)
                    canvas.Children.Add(new Line { X1 = w * 0.25, X2 = w, Y1 = y, Y2 = y, Stroke = lineBrush, StrokeThickness = 0.6 });
                break;
        }
        return new Border
        {
            Width = w, Height = h,
            BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = canvas
        };
    }

    private async Task SaveAsync(bool toast = true)
    {
        if (_doc is null) return;
        try
        {
            if (string.Equals(System.IO.Path.GetExtension(_doc.Info.FilePath), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // When a source PDF is embedded we keep the original vector pages and
                // only flatten the user's drawings as transparent overlays — otherwise
                // we flatten the whole page (white background + content).
                bool useOverlay = _doc.SourcePdfBytes is { Length: > 0 };
                var pngs = Canvas.RenderAllToPng(scale: 1f, overlayOnly: useOverlay);
                byte[]? thumb = null;
                try { thumb = Canvas.RenderCurrentPagePng(0, scale: 0.2f); } catch { }
                App.Services.Documents.SavePdfContainer(_doc, pngs, thumb, App.Services.PdfContainer);
            }
            else
            {
                App.Services.Documents.Save(_doc);
                try
                {
                    var thumb = Canvas.RenderCurrentPagePng(0, scale: 0.2f);
                    if (thumb is not null)
                        App.Services.Documents.SaveThumbnail(_doc.Info.FilePath, thumb);
                }
                catch { }
            }

            _doc.IsDirty = false;
            if (toast) await Notify("Saved", _doc.Info.Title);
        }
        catch (Exception ex) { await Notify("Save failed", ex.Message); }
    }

    // Returns true if it's OK to leave the editor (saved, discarded, or no changes).
    // Returns false if the user cancels.
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (_doc is null || !_doc.IsDirty) return true;
        var dlg = new ContentDialog
        {
            Title = "Unsaved changes",
            Content = $"Save changes to “{_doc.Info.Title}” before leaving?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.None) return false;          // Cancel
        if (res == ContentDialogResult.Primary) await SaveAsync(toast: false);
        return true;                                                // Save or Discard → proceed
    }

    private void OnAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        switch (sender.Key)
        {
            case VirtualKey.Delete:
            case VirtualKey.Back:
                Canvas.DeleteSelection(); UpdateSelectionUi(); args.Handled = true; break;
            case VirtualKey.Escape:
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
        }
    }

    private void UpdateSelectionUi() =>
        SelectionActions.Visibility = Canvas.HasSelection ? Visibility.Visible : Visibility.Collapsed;

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
        App.Services.History.Bind(_doc);
        UpdateHistoryUi();

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
            Content = $"Delete {indices.Count} selected pages? This cannot be undone.",
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
        App.Services.History.Bind(_doc);
        UpdateHistoryUi();

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
        App.Services.History.Bind(_doc);
        UpdateHistoryUi();
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
                      LineColorHex = t.LineColorHex, LineThickness = t.LineThickness }
                : null
        };

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
        _doc.Pages.Insert(index, newPage);
        for (int i = 0; i < _doc.Pages.Count; i++) _doc.Pages[i].Index = i;
        Canvas.Rebuild();
        App.Services.History.Bind(_doc);
        UpdateHistoryUi();
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
        App.Services.History.Bind(_doc);
        UpdateHistoryUi();
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
            Content = $"Delete page {index + 1}? This cannot be undone.",
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
        App.Services.History.Bind(_doc);
        UpdateHistoryUi();
        await RefreshPagePanelAsync();
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
}
