using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using YuNotes.Models;
using YuNotes.Tools;

namespace YuNotes.Views;

public sealed partial class EditorPage
{
    // ─── Tool groups ────────────────────────────────────────────────────────────
    // Groups made in Settings (AppSettings.ToolGroups) fold several drawing
    // tools into one toolbar button with the group's icon. The button opens a
    // menu of its tools; tools with options (pen mode, eraser mode, shape
    // kind, select mode, laser mode) get those options as a submenu. The
    // button lights up while one of its tools is the active tool.

    private readonly List<(ToolGroup Group, Grid Host, ToggleButton Button, FontIcon Icon, FontIcon Chevron)> _groupButtons = new();
    private double _toolBtnSize = 34, _toolIconSize = 19;

    // key → its group, for the tools that end up inside a group: members that
    // exist in the toolbar and aren't hidden. Empty groups are dropped.
    private static Dictionary<string, ToolGroup> GroupedTools(AppSettings s, ICollection<string> toolKeys)
    {
        var map = new Dictionary<string, ToolGroup>();
        foreach (var g in s.ToolGroups)
            foreach (var key in g.Tools)
                if (toolKeys.Contains(key) && !s.HiddenToolbarTools.Contains(key) && !map.ContainsKey(key))
                    map[key] = g;
        return map;
    }

    private void RemoveGroupButtons()
    {
        foreach (var gb in _groupButtons) ToolbarPrimary.Children.Remove(gb.Host);
        _groupButtons.Clear();
    }

    private Grid CreateGroupButton(ToolGroup g)
    {
        var icon = new FontIcon { Glyph = g.Glyph, FontSize = _toolIconSize };
        var btn = new ToggleButton
        {
            Style = (Style)Application.Current.Resources["ToolToggleButtonStyle"],
            Content = icon,
            Width = _toolBtnSize, Height = _toolBtnSize,
            CornerRadius = new CornerRadius(_toolBtnSize / 2),
        };
        ToolTipService.SetToolTip(btn, string.IsNullOrWhiteSpace(g.Name) ? "Tool group" : g.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"Tool group {g.Name}");
        // A small corner arrow, like the tools that have option menus.
        var chevron = new FontIcon
        {
            Glyph = "", FontSize = Math.Max(7, _toolIconSize * 0.55),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 5, 1), IsHitTestVisible = false,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        var host = new Grid { VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(btn);
        host.Children.Add(chevron);
        var group = g;
        btn.Click += (_, __) =>
        {
            // The toggle reflects "a tool of this group is active", not clicks.
            UpdateGroupButtonsState(_selected);
            var menu = BuildGroupMenu(group);
            menu.ShowAt(btn, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
        };
        _groupButtons.Add((g, host, btn, icon, chevron));
        return host;
    }

    private static bool GroupHasTool(ToolGroup g, ToolKind k) => g.Tools.Any(key => key switch
    {
        "Hand" => k == ToolKind.Pan,
        "Pen" => k == ToolKind.Pen,
        "Highlighter" => k == ToolKind.Highlighter,
        "Eraser" => k == ToolKind.Eraser,
        "Text" => k == ToolKind.Text,
        "Image" => k == ToolKind.Image,
        "Shape" => k == ToolKind.Shape,
        "Select" => k is ToolKind.Lasso or ToolKind.RectSelect,
        "Laser" => k == ToolKind.Laser,
        "Tape" => k == ToolKind.Tape,
        _ => false,
    });

    private void UpdateGroupButtonsState(ToolKind k)
    {
        var accent = (Brush)Application.Current.Resources["AppAccentBrush"];
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        foreach (var gb in _groupButtons)
        {
            bool on = GroupHasTool(gb.Group, k);
            gb.Button.IsChecked = on;
            gb.Chevron.Foreground = on ? accent : white;
        }
    }

    private void ScaleGroupButtons(double btn, double icon)
    {
        _toolBtnSize = btn; _toolIconSize = icon;
        foreach (var gb in _groupButtons)
        {
            gb.Button.Width = btn; gb.Button.Height = btn;
            gb.Button.CornerRadius = new CornerRadius(btn / 2);
            gb.Icon.FontSize = icon;
            gb.Chevron.FontSize = Math.Max(7, icon * 0.55);
        }
    }

    // One entry per tool, in toolbar order; options as a submenu.
    private MenuFlyout BuildGroupMenu(ToolGroup g)
    {
        var res = Application.Current.Resources;
        var font = (FontFamily)res["MaterialIconsFamily"];
        FontIcon Ico(string glyph) => new() { Glyph = glyph, FontFamily = font };
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = true };
        if (!string.IsNullOrWhiteSpace(g.Name))
            menu.Items.Add(new MenuFlyoutItem { Text = g.Name, IsEnabled = false });

        var s = App.Services.Settings.Current;
        var order = s.ToolbarDrawingOrder.Concat(new[] { "Hand", "Pen", "Highlighter", "Eraser", "Text", "Image", "Shape", "Select", "Laser", "Tape" })
            .Distinct().Where(k => g.Tools.Contains(k) && !s.HiddenToolbarTools.Contains(k));

        MenuFlyoutItem Item(string text, string glyph, Action act, bool current = false)
        {
            var it = new MenuFlyoutItem { Text = text, Icon = Ico(glyph) };
            if (current) it.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            it.Click += (_, __) => act();
            return it;
        }
        MenuFlyoutSubItem Sub(string text, string glyph, bool current, params MenuFlyoutItemBase[] items)
        {
            var sub = new MenuFlyoutSubItem { Text = text, Icon = Ico(glyph) };
            if (current) sub.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            foreach (var i in items) sub.Items.Add(i);
            return sub;
        }
        MenuFlyoutItem Opt(string text, bool chosen, Action act)
        {
            var it = new ToggleMenuFlyoutItem { Text = text, IsChecked = chosen };
            it.Click += (_, __) => act();
            return it;
        }
        void Shape(ShapeKind kind) { _shapeKind = kind; _shape.ShapeKind = kind; SyncShapeMenu(); SelectTool(ToolKind.Shape); }

        foreach (var key in order)
        {
            switch (key)
            {
                case "Hand":
                    menu.Items.Add(Item("Hand (pan)", "", () => SelectTool(ToolKind.Pan), _selected == ToolKind.Pan));
                    break;
                case "Pen":
                    menu.Items.Add(Sub("Pen", PenIcon.Glyph, _selected == ToolKind.Pen,
                        Item("Use pen", PenIcon.Glyph, () => SelectTool(ToolKind.Pen)),
                        new MenuFlyoutSeparator(),
                        Opt("Even width", !_penPressureMode, () => { _penPressureMode = false; _pen.UsePressure = false; SyncPenMenu(); SaveToolModes(); SelectTool(ToolKind.Pen); }),
                        Opt("Pressure", _penPressureMode, () => { _penPressureMode = true; _pen.UsePressure = true; SyncPenMenu(); SaveToolModes(); SelectTool(ToolKind.Pen); })));
                    break;
                case "Highlighter":
                    menu.Items.Add(Item("Highlighter", "", () => SelectTool(ToolKind.Highlighter), _selected == ToolKind.Highlighter));
                    break;
                case "Eraser":
                    menu.Items.Add(Sub("Eraser", EraserIcon.Glyph, _selected == ToolKind.Eraser,
                        Item("Use eraser", EraserIcon.Glyph, () => SelectTool(ToolKind.Eraser)),
                        new MenuFlyoutSeparator(),
                        Opt("Whole strokes", !_eraserPixelMode, () => { _eraserPixelMode = false; SyncEraserMenu(); Canvas.Context.EraserPixelMode = false; SaveToolModes(); SelectTool(ToolKind.Eraser); }),
                        Opt("Only what you touch", _eraserPixelMode, () => { _eraserPixelMode = true; SyncEraserMenu(); Canvas.Context.EraserPixelMode = true; SaveToolModes(); SelectTool(ToolKind.Eraser); })));
                    break;
                case "Text":
                    menu.Items.Add(Item("Text", "", () => SelectTool(ToolKind.Text), _selected == ToolKind.Text));
                    break;
                case "Image":
                    menu.Items.Add(Item("Image", "", async () => { SelectTool(ToolKind.Image); await PickImageAsync(); }, _selected == ToolKind.Image));
                    break;
                case "Shape":
                    menu.Items.Add(Sub("Shape", ShapeIcon.Glyph, _selected == ToolKind.Shape,
                        Item("Use shapes", ShapeIcon.Glyph, () => SelectTool(ToolKind.Shape)),
                        new MenuFlyoutSeparator(),
                        Opt("Rectangle", _shapeKind == ShapeKind.Rectangle, () => Shape(ShapeKind.Rectangle)),
                        Opt("Ellipse", _shapeKind == ShapeKind.Ellipse, () => Shape(ShapeKind.Ellipse)),
                        Opt("Line", _shapeKind == ShapeKind.Line, () => Shape(ShapeKind.Line)),
                        Opt("Triangle", _shapeKind == ShapeKind.Triangle, () => Shape(ShapeKind.Triangle)),
                        Opt("Arrow", _shapeKind == ShapeKind.Arrow, () => Shape(ShapeKind.Arrow)),
                        Opt("Axes", _shapeKind == ShapeKind.Axes, () => Shape(ShapeKind.Axes)),
                        Opt("Grid", _shapeKind == ShapeKind.Grid, () => Shape(ShapeKind.Grid))));
                    break;
                case "Select":
                    menu.Items.Add(Sub("Select", SelectIcon.Glyph, _selected is ToolKind.Lasso or ToolKind.RectSelect,
                        Item("Use select", SelectIcon.Glyph, () => SelectTool(_selectMode)),
                        new MenuFlyoutSeparator(),
                        Opt("Lasso", _selectMode == ToolKind.Lasso, () => { _selectMode = ToolKind.Lasso; SyncSelectMenu(); SaveToolModes(); SelectTool(_selectMode); }),
                        Opt("Rectangle", _selectMode == ToolKind.RectSelect, () => { _selectMode = ToolKind.RectSelect; SyncSelectMenu(); SaveToolModes(); SelectTool(_selectMode); })));
                    break;
                case "Laser":
                    menu.Items.Add(Sub("Laser pointer", "", _selected == ToolKind.Laser,
                        Item("Use laser", "", () => SelectTool(ToolKind.Laser)),
                        new MenuFlyoutSeparator(),
                        Opt("Dot", Canvas.Context.LaserMode == LaserMode.Dot, () => SetLaserMode(LaserMode.Dot)),
                        Opt("Trail", Canvas.Context.LaserMode == LaserMode.Trail, () => SetLaserMode(LaserMode.Trail))));
                    break;
                case "Tape":
                    menu.Items.Add(Sub("Tape", "", _selected == ToolKind.Tape,
                        Item("Use tape", "", () => SelectTool(ToolKind.Tape)),
                        new MenuFlyoutSeparator(),
                        Item("Cover all tape", "", () => Canvas.SetAllTapeRevealed(false)),
                        Item("Reveal all tape", "", () => Canvas.SetAllTapeRevealed(true)),
                        new MenuFlyoutSeparator(),
                        Item("Quiz me (F6)", "", () => StartQuiz()),
                        Item("Flashcards", "", () => _ = ShowFlashcardsAsync(null))));
                    break;
            }
        }
        return menu;
    }
}
