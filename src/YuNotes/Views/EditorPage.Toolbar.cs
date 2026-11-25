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
    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragActive = DragHandle.CapturePointer(e.Pointer);
        if (!_dragActive) return;
        _dragStart = e.GetCurrentPoint(null).Position;
        _dragStartTx = ToolbarTranslate.X;
        _dragStartTy = ToolbarTranslate.Y;
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragActive) return;
        var p = e.GetCurrentPoint(null).Position;
        ToolbarTranslate.X = _dragStartTx + (p.X - _dragStart.X);
        ToolbarTranslate.Y = _dragStartTy + (p.Y - _dragStart.Y);
        ClampToolbarIntoView();
        // A render-transform change doesn't trigger layout, so nudge an arrange so
        // the liquid-glass backdrop re-syncs its refraction offset to the new spot.
        ToolbarHost.InvalidateArrange();
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragActive) return;
        _dragActive = false;
        DragHandle.ReleasePointerCapture(e.Pointer);
        var s = App.Services.Settings.Current;
        s.ToolbarOffsetX = ToolbarTranslate.X;
        s.ToolbarOffsetY = ToolbarTranslate.Y;
        App.Services.Settings.Save();
    }

    // Keeps at least a sliver of the toolbar on-screen after a drag so it can't be
    // lost off an edge (and then persisted off-screen).
    private void ClampToolbarIntoView()
    {
        if (RootGrid.ActualWidth < 1 || RootGrid.ActualHeight < 1 || ToolbarHost.ActualWidth < 1) return;
        try
        {
            var tl = ToolbarHost.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
            double w = ToolbarHost.ActualWidth, h = ToolbarHost.ActualHeight;
            double availW = RootGrid.ActualWidth, availH = RootGrid.ActualHeight;
            const double keep = 32;
            if (tl.X > availW - keep) ToolbarTranslate.X -= tl.X - (availW - keep);
            else if (tl.X + w < keep) ToolbarTranslate.X += keep - (tl.X + w);
            if (tl.Y > availH - keep) ToolbarTranslate.Y -= tl.Y - (availH - keep);
            else if (tl.Y + h < keep) ToolbarTranslate.Y += keep - (tl.Y + h);
        }
        catch { }
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

        // Flip the pickers between horizontal/vertical.
        ColorPicker.SetOrientation(vertical ? Orientation.Vertical : Orientation.Horizontal);
        WidthPicker.SetOrientation(vertical ? Orientation.Vertical : Orientation.Horizontal);

        // The floating selection bar (Duplicate/Delete/Clear) is positioned dynamically
        // just above the selected content in PositionSelectionActions(), so it no longer
        // parks at a fixed spot relative to the toolbar.

        // WinUI's default ToggleButton theme style carries HorizontalAlignment=Left.
        // A horizontal StackPanel ignores it (slot = desired width), but in the
        // vertical toolbar it pins every tool icon a few px left of the pill's
        // centerline while the drag handle and pickers center. Center the buttons
        // when vertical; restore the theme default when horizontal.
        var toolAlign = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        foreach (var tool in new FrameworkElement[]
        {
            ToolHand, ToolPen, ToolHighlighter, ToolEraser, ToolText, ToolImage, ToolPaste,
            ToolShape, ToolRuler, ToolSelect, ToolExtend
        })
            tool.HorizontalAlignment = toolAlign;

        // Restore the persisted free-drag offset on top of the docked anchor. The
        // transform itself is declared in XAML (baked into the first frame); we
        // only update its values here.
        var settings = App.Services.Settings.Current;
        ToolbarTranslate.X = settings.ToolbarOffsetX;
        ToolbarTranslate.Y = settings.ToolbarOffsetY;
        // Pull it back on-screen once laid out, in case the window is now smaller.
        if (settings.ToolbarOffsetX != 0 || settings.ToolbarOffsetY != 0)
            DispatcherQueue.TryEnqueue(ClampToolbarIntoView);
    }

    private void ApplyToolbarScaleForWidth(double pageWidth)
    {
        // The top pinned bar is a fixed Normal size, styled entirely in XAML
        // (compact segmented groups) — it isn't scaled here.

        // ── Floating drawing toolbar — scales with window width AND ToolbarSize ──
        // Same numbers as the live preview in Settings (ToolbarMetrics).
        var m = Models.ToolbarMetrics.For(pageWidth, Models.ToolbarMetrics.ScaleOf(App.Services.Settings.Current));
        double btn = m.Button, icon = m.Icon, colorBtn = m.ColorButton, chip = m.Chip, chipDot = m.ChipDot;

        Control[] floatingBtns = {
            ToolHand, ToolPen, ToolHighlighter, ToolEraser, ToolText, ToolImage, ToolPaste, ToolShape, ToolRuler, ToolSelect, ToolExtend,
            ToolLaser, ToolTape,
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

        ScaleGroupButtons(btn, icon);

        // Drag handle — not a Control, scaled separately
        DragHandle.Width = btn; DragHandle.Height = btn;
        DragHandleIcon.FontSize = icon;

        // Chevron sub-buttons — width matches main button, height ~30%
        double chevronHeight = Math.Max(10, btn * 0.32);
        foreach (var cb in new[] { ToolPenChevron, ToolEraserChevron, ToolShapeChevron, ToolSelectChevron, ToolLaserChevron, ToolTapeChevron })
        {
            cb.Width = btn;
            cb.Height = chevronHeight;
        }
        PenChevronIcon.FontSize    = Math.Max(7, icon * 0.55);
        EraserChevronIcon.FontSize = Math.Max(7, icon * 0.55);
        LaserChevronIcon.FontSize  = Math.Max(7, icon * 0.55);
        TapeChevronIcon.FontSize   = Math.Max(7, icon * 0.55);
        ShapeChevronIcon.FontSize  = Math.Max(7, icon * 0.55);
        SelectChevronIcon.FontSize = Math.Max(7, icon * 0.55);

        // Color and width pickers follow the floating toolbar size
        ColorPicker.SetButtonSize(colorBtn);
        SelColorPicker.SetButtonSize(colorBtn);
        SelWidthPicker.SetChipSize(chip, chipDot);
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
            ["Shape"] = ShapeGroup, ["Select"] = SelectGroup, ["Laser"] = LaserGroup, ["Tape"] = TapeGroup,
        };
        var defaultDrawing = new[] { "Hand", "Pen", "Highlighter", "Eraser", "Text", "Image", "Shape", "Select", "Laser", "Tape" };
        // Saved custom order first, then any default keys it omits, de-duplicated. Distinct is
        // essential: inserting the same element twice throws COMException 0x800F1000 ("element
        // already has a parent"). An empty saved order falls through to the defaults.
        var drawOrder = s.ToolbarDrawingOrder.Where(toolMap.ContainsKey)
                        .Concat(defaultDrawing)
                        .Distinct()
                        .ToList();

        foreach (var el in toolMap.Values) ToolbarPrimary.Children.Remove(el);
        RemoveGroupButtons();
        // Insert after DragHandle (0) and Sep1 (1). Grouped tools are replaced by
        // their group's button, placed where the group's first tool would be.
        var grouped = GroupedTools(s, toolMap.Keys);
        var placedGroups = new HashSet<string>();
        int idx = 2;
        foreach (var key in drawOrder)
        {
            if (grouped.TryGetValue(key, out var g))
            {
                if (placedGroups.Add(g.Id)) ToolbarPrimary.Children.Insert(idx++, CreateGroupButton(g));
            }
            else ToolbarPrimary.Children.Insert(idx++, toolMap[key]);
        }
        UpdateGroupButtonsState(_selected);

        // Reorderable action slots in ToolbarSecondary. The insert actions
        // (blank page / PDF pages) live inside AddMenuBtn's menu, and the
        // template and screenshot buttons have FIXED slots left of the page
        // sorter (user request) — so only the Add menu itself is placed here.
        // Saved "Template"/"Screenshot" entries in ToolbarActionOrder are ignored.
        var actionMap = new Dictionary<string, UIElement>
        {
            ["Add"] = AddMenuShell,
        };
        var defaultAction = new[] { "Add" };
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
        LaserGroup.Visibility     = Show("Laser");
        TapeGroup.Visibility      = Show("Tape");

        // Title bar. The Add menu holds pages, sticker and template;
        // hiding one hides its row, and the Add button only goes when all are
        // hidden. Side by side and Present can be hidden too.
        ScreenshotBtn.Visibility   = Show("Screenshot");
        TemplateBtn.Visibility     = Show("Template");
        StickersItem.Visibility    = Show("Stickers");
        AddPageBtn.Visibility      = Show("AddPage");
        AddPdfPagesBtn.Visibility  = Show("AddPdfPages");
        ExportBtn.Visibility       = Show("Export");
        bool anyPages = !IsHidden("AddPage") || !IsHidden("AddPdfPages");
        bool anyInserts = !IsHidden("Stickers") || !IsHidden("Template");
        AddMenuSep.Visibility = anyPages && anyInserts ? Visibility.Visible : Visibility.Collapsed;
        AddMenuShell.Visibility = anyPages || anyInserts ? Visibility.Visible : Visibility.Collapsed;
        SideBySideSubItem.Visibility = Show("SideBySide");
        PresentItem.Visibility = PresentSep.Visibility = Show("Present");
        // TopSep1 is an invisible layout anchor for ApplyToolbarOrder — leave it collapsed.
    }

    private void UpdateChevronColors()
    {
        // Active: accent color (matches the icon's ToggleButtonForegroundChecked).
        // Inactive: white (on the opaque unchecked button background).
        var accent = (Brush)Application.Current.Resources["AppAccentBrush"];
        var white  = new SolidColorBrush(Colors.White);
        PenChevronIcon.Foreground    = ToolPen.IsChecked    == true ? accent : white;
        EraserChevronIcon.Foreground = ToolEraser.IsChecked == true ? accent : white;
        LaserChevronIcon.Foreground  = ToolLaser.IsChecked  == true ? accent : white;
        TapeChevronIcon.Foreground   = ToolTape.IsChecked   == true ? accent : white;
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
        ToolLaser.IsChecked = k == ToolKind.Laser;
        ToolTape.IsChecked = k == ToolKind.Tape;
        UpdateGroupButtonsState(k);
        PresentLaserDotBtn.IsChecked = k == ToolKind.Laser && Canvas.Context.LaserMode == LaserMode.Dot;
        PresentLaserTrailBtn.IsChecked = k == ToolKind.Laser && Canvas.Context.LaserMode == LaserMode.Trail;
        PresentPenBtn.IsChecked = k == ToolKind.Pen;
        PresentHandBtn.IsChecked = k == ToolKind.Pan;
        UpdateChevronColors();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        // Only on losing focus: on regaining it, the activating pen press may
        // already be a live stroke, and resetting would cut it off. Anything
        // stale after that is cleared by PageCanvas on the next press.
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        Canvas.ResetInputState();
        // A momentary override (middle-mouse pan) cut off mid-gesture never
        // restores the toolbar; put it back on the real tool.
        if (_restoreToolKind is ToolKind k)
        {
            _restoreToolKind = null;
            UpdateToolUi(k);
        }
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
            Canvas.ClearSelectionArea();
            UpdateSelectionUi();
        }

        // The laser and the tape have colors of their own: remember the ink color
        // on the way in and put it back on the way out, so text and shapes don't
        // turn laser-red or tape-yellow.
        static bool OwnColor(ToolKind t) => t is ToolKind.Laser or ToolKind.Tape;
        bool enteringLaser = OwnColor(k) && !OwnColor(_selected);
        bool leavingLaser = !OwnColor(k) && OwnColor(_selected);
        if (enteringLaser) _colorBeforeLaser = Canvas.Context.CurrentColor;

        _selected = k;
        Canvas.SetExtendMode(k == ToolKind.ExtendPage);
        UpdateToolUi(k);

        var s = App.Services.Settings.Current;
        if (leavingLaser && k is not (ToolKind.Pen or ToolKind.Highlighter))
        {
            Canvas.Context.CurrentColor = _colorBeforeLaser;
            ColorPicker.SetPresets(s.PenPresetColors, _colorBeforeLaser);
        }
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
            case ToolKind.Laser:
                Canvas.Context.CurrentColor = _laserColor;
                ColorPicker.SetPresets(LaserPresetColors, _laserColor);
                break;
            case ToolKind.Tape:
                Canvas.Context.CurrentColor = _tapeColor;
                ColorPicker.SetPresets(TapePresetColors, _tapeColor);
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

    // Picking a mode also picks the laser, so one tap is enough mid-talk.
    private void SetLaserMode(LaserMode mode)
    {
        Canvas.Context.LaserMode = mode;
        Canvas.ClearLaser();
        SyncLaserMenu();
        App.Services.Settings.Current.LaserMode = mode;
        App.Services.Settings.Save();
        SelectTool(ToolKind.Laser);
    }

    private void SyncLaserMenu()
    {
        LaserDotItem.IsChecked = Canvas.Context.LaserMode == LaserMode.Dot;
        LaserTrailItem.IsChecked = Canvas.Context.LaserMode == LaserMode.Trail;
    }

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
        ShapeArrowItem.IsChecked    = _shapeKind == ShapeKind.Arrow;
        ShapeAxesItem.IsChecked     = _shapeKind == ShapeKind.Axes;
        ShapeGridItem.IsChecked     = _shapeKind == ShapeKind.Grid;
        ShapeIcon.Glyph = _shapeKind switch
        {
            ShapeKind.Rectangle => GlyphRectangle,
            ShapeKind.Ellipse   => GlyphCircle,
            ShapeKind.Line      => GlyphDiagonalLine,
            ShapeKind.Triangle  => GlyphSignalCellularNull,
            ShapeKind.Arrow     => "",
            ShapeKind.Axes      => "",
            ShapeKind.Grid      => "",
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

    // ─── Pen favorites ───────────────────────────────────────────────────────────

    private void RebuildPenFavorites()
    {
        var s = App.Services.Settings.Current;
        PenFavoritesPanel.Children.Clear();
        var res = Application.Current.Resources;
        for (int i = 0; i < s.PenFavorites.Count; i++)
        {
            int idx = i;
            var f = s.PenFavorites[i];
            var color = Controls.ColorPickerControl.ParseHex(f.ColorHex);
            float dot = Math.Clamp(f.Width * 2.2f + 4, 6, 22);
            var face = new Grid { Width = 26, Height = 26 };
            face.Children.Add(new Ellipse
            {
                Width = dot, Height = dot, Fill = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            if (f.Pressure)
                face.Children.Add(new FontIcon
                {
                    Glyph = "", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom, Foreground = (Brush)res["AppTextSecondaryBrush"],
                    FontFamily = (FontFamily)res["MaterialIconsFamily"],
                });
            var b = new Button
            {
                Content = face, Width = 36, Height = 36, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
            };
            string label = $"{NearestPreset(PenWidths, f.Width):0.#} px{(f.Pressure ? ", pressure" : "")}" + (idx < 5 ? $" · Ctrl+{idx + 1}" : "");
            ToolTipService.SetToolTip(b, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"Pen favorite {idx + 1}, {label}");
            b.Click += (_, __) => ApplyPenFavorite(idx);
            b.RightTapped += (_, e) =>
            {
                s.PenFavorites.RemoveAt(idx);
                App.Services.Settings.Save();
                RebuildPenFavorites();
                e.Handled = true;
            };
            PenFavoritesPanel.Children.Add(b);
        }

        if (s.PenFavorites.Count < 5)
        {
            var add = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 16, FontFamily = (FontFamily)res["MaterialIconsFamily"] },
                Width = 36, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(8),
            };
            ToolTipService.SetToolTip(add, "Save the current pen as a favorite");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, "Save the current pen as a favorite");
            add.Click += (_, __) =>
            {
                var hex = Controls.ColorPickerControl.ToHex(Canvas.Context.CurrentColor);
                bool exists = s.PenFavorites.Any(p => p.ColorHex == hex && Math.Abs(p.Width - _penWidth) < 0.01f && p.Pressure == _penPressureMode);
                if (!exists)
                {
                    s.PenFavorites.Add(new PenFavorite { ColorHex = hex, Width = _penWidth, Pressure = _penPressureMode });
                    App.Services.Settings.Save();
                }
                RebuildPenFavorites();
            };
            PenFavoritesPanel.Children.Add(add);
        }
    }

    private void ApplyPenFavorite(int index)
    {
        var favs = App.Services.Settings.Current.PenFavorites;
        if (index < 0 || index >= favs.Count || _doc is null) return;
        var f = favs[index];
        SelectTool(ToolKind.Pen);
        var color = Controls.ColorPickerControl.ParseHex(f.ColorHex);
        Canvas.Context.CurrentColor = color;
        ColorPicker.SetPresets(App.Services.Settings.Current.PenPresetColors, color);
        // Snap to the toolbar's widths so the width picker shows it as selected.
        _penWidth = (float)NearestPreset(PenWidths, f.Width);
        Canvas.Context.CurrentWidth = _penWidth;
        WidthPicker.SetPresets(PenWidths, _penWidth);
        _penPressureMode = f.Pressure;
        _pen.UsePressure = f.Pressure;
        SyncPenMenu();
        SaveToolModes();
        PenPreview.Invalidate();
    }

    private void UpdateSelectionUi()
    {
        if (Canvas.HasSelection)
        {
            SyncSelectionColorPicker();
            SelToTextBtn.Visibility = Canvas.SelectionHasPenInk ? Visibility.Visible : Visibility.Collapsed;
            SelectionActions.Visibility = Visibility.Visible;
            PositionSelectionActions();
        }
        else
        {
            SelectionActions.Visibility = Visibility.Collapsed;
        }
        // Outline the drawn area while it's what a sticker would be cut from.
        Canvas.UpdateStickerAreaOutline();
        // An empty rectangle/lasso over a PDF: offer to save that piece.
        if (Canvas.HasStickerArea && _selected is ToolKind.Lasso or ToolKind.RectSelect)
        {
            AreaActions.Visibility = Visibility.Visible;
            PositionAreaActions();
        }
        else AreaActions.Visibility = Visibility.Collapsed;
    }

    // Shows the selection bar's color button only when something colorable is
    // selected (not just images), with the highlighter presets for an
    // all-highlighter selection and the first item's color marked as current.
    private void SyncSelectionColorPicker()
    {
        bool show = Canvas.SelectionHasRecolorable;
        SelColorPicker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SyncSelectionWidthPicker();
        SelColorSep.Visibility = show || SelWidthPicker.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var s = App.Services.Settings.Current;
        bool hl = Canvas.SelectionIsAllHighlighter;
        var presets = hl ? s.HighlighterPresetColors : s.PenPresetColors;
        var current = Canvas.SelectionColor() ?? Canvas.Context.CurrentColor;
        // Highlighter presets carry their alpha, so match them raw; pen presets
        // are opaque.
        SelColorPicker.SetPresets(presets, hl ? current : Color.FromArgb(255, current.R, current.G, current.B));
    }

    // Width dots on the selection bar: shown when ink or shapes are selected,
    // with highlighter widths for an all-highlighter selection (pen widths
    // otherwise) and the dot nearest the first item's width marked.
    private void SyncSelectionWidthPicker()
    {
        bool show = Canvas.SelectionHasResizableWidth;
        SelWidthPicker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var presets = Canvas.SelectionIsAllHighlighter ? HighlighterWidths : PenWidths;
        var current = Canvas.SelectionWidth() ?? presets[1];
        SelWidthPicker.SetPresets(presets, NearestPreset(presets, current));
    }

    // Float the Duplicate/Delete/Clear bar just above the selected content (like the
    // screenshot action bar), centred over it and flipping below when there's no room
    // above. Re-run whenever the selection moves or the view scrolls/zooms.
    private void PositionSelectionActions()
    {
        if (!Canvas.TryGetSelectionScreenBounds(RootGrid, out var sel)) return;

        SelectionActions.UpdateLayout();
        double bw = SelectionActions.ActualWidth, bh = SelectionActions.ActualHeight;
        if (bw <= 0 || bh <= 0) return;

        const double gap = 10, edge = 8;

        // Visible canvas area within RootGrid: row 1 / column 1, i.e. the InkCanvasControl.
        double areaLeft   = ColLeft.ActualWidth;
        double areaTop    = RowTop.ActualHeight;
        double areaRight  = areaLeft + Canvas.ActualWidth;
        double areaBottom = areaTop + Canvas.ActualHeight;

        // Centre horizontally over the selection; sit just above it, flipping below
        // when the bar would clear the top of the canvas.
        double left = sel.X + sel.Width / 2 - bw / 2;
        double top  = sel.Y - bh - gap;
        if (top < areaTop + edge) top = sel.Y + sel.Height + gap;

        left = Math.Clamp(left, areaLeft + edge, Math.Max(areaLeft + edge, areaRight - bw - edge));
        top  = Math.Clamp(top,  areaTop  + edge, Math.Max(areaTop  + edge, areaBottom - bh - edge));

        // SelectionActions is a grid child in row 1 spanning columns 0-2, so its
        // margin is measured from that cell's top-left (x = 0, y = RowTop height).
        SelectionActions.HorizontalAlignment = HorizontalAlignment.Left;
        SelectionActions.VerticalAlignment   = VerticalAlignment.Top;
        SelectionActions.Margin = new Thickness(left, top - areaTop, 0, 0);
    }
}
