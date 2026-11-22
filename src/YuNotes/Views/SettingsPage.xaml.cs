using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using YuNotes.Controls;
using YuNotes.Input;
using YuNotes.Models;

namespace YuNotes.Views;

public sealed class ToolEntry : INotifyPropertyChanged
{
    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ToolEntry(string key, string label, string glyph, bool visible = true)
    {
        Key = key; Label = label; Glyph = glyph; _isVisible = visible;
    }
}

// Settings: a sidebar of categories, one page each. Changes apply as you make
// them and are saved after a short pause; a toast offers Undo for the last
// batch. Rows whose value differs from the default show a dot and a Reset link.
public sealed partial class SettingsPage : Page
{
    private static readonly (PenButtonAction Value, string Label)[] ButtonActions =
    {
        (PenButtonAction.None,         "Tool — don't change"),
        (PenButtonAction.Eraser,       "Eraser"),
        (PenButtonAction.Highlighter,  "Highlighter"),
        (PenButtonAction.LassoSelect,  "Lasso select"),
        (PenButtonAction.RectSelect,   "Rectangle select"),
        (PenButtonAction.Pen,          "Pen"),
        (PenButtonAction.Undo,         "Undo"),
        (PenButtonAction.Redo,         "Redo"),
    };

    private static readonly (string Key, string Label, string Glyph)[] DrawingToolDefs =
    {
        ("Hand",        "Hand (pan)",    ""),
        ("Pen",         "Pen",           ""),
        ("Highlighter", "Highlighter",   ""),
        ("Eraser",      "Eraser",        ""),
        ("Text",        "Text",          ""),
        ("Image",       "Image",         ""),
        ("Shape",       "Shape",         ""),
        ("Select",      "Select",        ""),
    };

    private static readonly (string Key, string Label, string Glyph)[] ActionToolDefs =
    {
        ("Screenshot",  "Screenshot",    ""),
        ("Template",    "Template",      ""),
        ("AddPdfPages", "Add PDF pages", ""),
        ("AddPage",     "Add blank page",""),
    };

    private static readonly (string Title, string[] Keys)[] Shortcuts =
    {
        ("Undo",                     new[] { "Ctrl", "Z" }),
        ("Redo",                     new[] { "Ctrl", "Y" }),
        ("Save",                     new[] { "Ctrl", "S" }),
        ("Find in note, handwriting too", new[] { "Ctrl", "F" }),
        ("Use pen favorite 1–5",     new[] { "Ctrl", "1–5" }),
        ("Copy selected text",       new[] { "Ctrl", "C" }),
        ("Paste image",              new[] { "Ctrl", "V" }),
        ("Duplicate selection",      new[] { "Ctrl", "D" }),
        ("Delete selection",         new[] { "Delete" }),
        ("Clear selection",          new[] { "Esc" }),
        ("Pan the page",             new[] { "Middle mouse button" }),
        ("Switch tool while held",   new[] { "Pen barrel button" }),
    };

    // Named steps for settings that used to be raw numbers. Index 0 is the left
    // end of the slider / first chip. Each list contains the default value.
    private static readonly (string Name, double Value)[] MinPressureSteps =
        { ("Feather", 0), ("Light", 0.05), ("Medium", 0.12), ("Firm", 0.2), ("Very firm", 0.3) };
    // Left = a light press already reaches full width (big boost).
    private static readonly (string Name, double Value)[] BoostSteps =
        { ("Barely", 3.0), ("Gently", 1.6), ("Normally", 1.0), ("Firmly", 0.75), ("Hard", 0.5) };
    private static readonly (string Name, int Value, string Description)[] SmoothingSteps =
    {
        ("Off",    0, "Lines stay exactly as you draw them."),
        ("Light",  3, "Irons out small wobbles."),
        ("Medium", 6, "Steadier lines; the ink lags the pen slightly."),
        ("Strong", 9, "Very steady lines; the ink trails the pen."),
    };
    private static readonly (string Name, int Ms, string Phrase)[] GraceSteps =
        { ("Short", 250, "a quarter second"), ("Normal", 500, "half a second"), ("Long", 1000, "a full second") };
    private static readonly float[] PenWidths = { 1f, 2.5f, 4f, 6f };
    private static readonly float[] HighlighterWidths = { 10f, 18f, 28f };
    private static readonly float[] EraserWidths = { 10f, 20f, 40f };

    private readonly ObservableCollection<ToolEntry> _drawingTools = new();
    private readonly ObservableCollection<ToolEntry> _actionTools = new();

    private readonly StringBuilder _log = new();
    private readonly HashSet<uint> _activeTouchIds = new();
    private bool _loading;

    // Category that was open last time, so Settings reopens where you left it.
    private static string s_category = "pen";

    public SettingsPage()
    {
        InitializeComponent();
        BuildShortcutRows();
        BuildCurvePresets();
        BuildChoiceControls();
        CollectRows();

        _commitTimer = DispatcherQueue.CreateTimer();
        _commitTimer.Interval = TimeSpan.FromMilliseconds(600);
        _commitTimer.IsRepeating = false;
        _commitTimer.Tick += (_, _) => Commit();
        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(6);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => HideToast();

        BackBtn.Click += (_, _) => { Commit(); MainWindow.GoBack(); };
        UndoBtn.Click += (_, _) => UndoLast();
        Unloaded += (_, _) => Commit();

        SettingsSearch.TextChanged += (_, _) => ApplySearch();
        NavList.SelectionChanged += (_, _) =>
        {
            if (NavList.SelectedItem is ListViewItem { Tag: string key }) ShowCategory(key);
        };
        UpdateChip.Click += (_, _) => SelectCategory("updates");
        RootScroller.SizeChanged += (_, e) => FitContentWidth(e.NewSize.Width);
        SizeChanged += (_, e) => FitSidebar(e.NewSize.Width);

        var findKey = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        findKey.Invoked += (_, e) => { SettingsSearch.Focus(FocusState.Keyboard); e.Handled = true; };
        KeyboardAccelerators.Add(findKey);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        PickFolderBtn.Click += async (_, _) => await PickFolder();
        ShowLogsBtn.Click += async (_, _) => await ShowLogs();
        ExportDebugBtn.Click += async (_, _) => await ExportDebug();
        CrashLogBtn.Click += async (_, _) => await ShowCrashLog();

        // Pressure test area
        PressureSandbox.PointerPressed += (_, e) => { BeginSuppress(e.Pointer.PointerId); PressureSandbox.CapturePointer(e.Pointer); OnPressureSandbox(e); };
        PressureSandbox.PointerMoved += (_, e) => OnPressureSandbox(e);
        PressureSandbox.PointerReleased += (_, e) => { EndSuppress(e.Pointer.PointerId); OnPressureSandbox(e); };
        PressureSandbox.PointerCaptureLost += (_, e) => EndSuppress(e.Pointer.PointerId);
        PressureClearBtn.Click += (_, _) =>
        {
            PressureCanvas.Children.Clear();
            PressureHint.Visibility = Visibility.Visible;
            PressureReadout.Text = "";
            CurveEditor.HideLive();
        };
        CurveEditor.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) => BeginSuppress(e.Pointer.PointerId)), true);
        CurveEditor.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) => EndSuppress(e.Pointer.PointerId)), true);
        CurveEditor.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, e) => EndSuppress(e.Pointer.PointerId)), true);

        // Diagnostics sandbox
        Sandbox.PointerEntered += OnSandbox;
        Sandbox.PointerMoved += OnSandbox;
        Sandbox.PointerPressed += OnSandboxPressed;
        Sandbox.PointerReleased += OnSandboxEnded;
        Sandbox.PointerCanceled += OnSandboxEnded;
        Sandbox.PointerCaptureLost += OnSandboxEnded;
        Sandbox.PointerExited += OnSandboxEnded;

        // The ScrollViewer's DirectManipulation grabs pen/touch input for pan/zoom
        // before the test areas can capture it, so scrolling is suspended while a
        // pen is down anywhere on the page or a pointer is down in a test area.
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPagePointerPressed), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPagePointerEnded), handledEventsToo: true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(OnPagePointerEnded), handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnPagePointerEnded), handledEventsToo: true);

        WireChangeHandlers();

        Loaded += async (_, _) =>
        {
            MainWindow.SetDragRegion(HeaderDragRegion);
            HeaderGrid.Padding = new Thickness(12, 0, 12 + MainWindow.CaptionButtonInset, 0);
            await FillHandwritingLanguagesAsync();
            LoadIntoUi();
            _committed = App.Services.Settings.Snapshot();
            SelectCategory(s_category);

            Services.UpdateService.StatusChanged -= ShowUpdateStatus;
            Services.UpdateService.StatusChanged += ShowUpdateStatus;
            ShowUpdateStatus();
        };
        Unloaded += (_, _) => Services.UpdateService.StatusChanged -= ShowUpdateStatus;
    }

    // ─── Saving, undo and reset ─────────────────────────────────────────────
    private readonly DispatcherQueueTimer _commitTimer;
    private readonly DispatcherQueueTimer _toastTimer;
    private string _committed = "";   // settings as last saved
    private string? _undoBase;        // settings before the change being made now
    private string? _toastUndo;       // what the toast's Undo goes back to
    private string _pendingLabel = "";

    // Any control changed: copy the UI into the settings right away (the editor
    // reads them live) and save once the user pauses.
    private void OnChanged(object? sender)
    {
        if (_loading) return;
        _undoBase ??= _committed;
        ApplyUiToSettings();
        _pendingLabel = DescribeChange(sender);
        RefreshSummaries();
        RefreshModified();
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void Commit()
    {
        _commitTimer.Stop();
        if (_undoBase is null) return;
        App.Services.Settings.Save();
        _committed = App.Services.Settings.Snapshot();
        if (_committed != _undoBase) ShowToast(_pendingLabel, _undoBase);
        _undoBase = null;
    }

    private void UndoLast()
    {
        var target = _toastUndo;
        HideToast();
        if (target is null) return;
        _commitTimer.Stop();
        _undoBase = null;
        App.Services.Settings.Restore(target);
        _committed = App.Services.Settings.Snapshot();
        LoadIntoUi();
    }

    private void ResetRow(SettingRow row)
    {
        if (!Defaults.TryGetValue(row.SettingKey, out var def)) return;
        _undoBase ??= _committed;
        def.Reset(App.Services.Settings.Current);
        LoadIntoUi();
        _pendingLabel = $"{row.Title} reset to default";
        Commit();
    }

    private void ShowToast(string text, string undoTo)
    {
        _toastUndo = undoTo;
        ToastText.Text = text;
        Toast.Opacity = 1;
        Toast.IsHitTestVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toastUndo = null;
        Toast.Opacity = 0;
        Toast.IsHitTestVisible = false;
    }

    // "Smoothing set to 4", "Palm rejection off", "Theme: Dark".
    private string DescribeChange(object? sender)
    {
        var row = sender is DependencyObject d ? RowOf(d) : null;
        string title = row?.Title ?? "Setting";
        return sender switch
        {
            ChoiceStrip c when c.SelectedIndex >= 0 => $"{title}: {c.LabelOf(c.SelectedIndex)}",
            Slider s when s == MinPressureSlider => $"{title}: {MinPressureSteps[Step(s)].Name}",
            Slider s when s == PressureMultSlider => $"{title}: {BoostSteps[Step(s)].Name}",
            ToggleSwitch t => $"{title} {(t.IsOn ? "on" : "off")}",
            ComboBox c => $"{title}: {(c.SelectedItem as ComboBoxItem)?.Content}",
            Slider s => $"{title} set to {s.Value:0.##}",
            NumberBox n when !double.IsNaN(n.Value) => $"{title} set to {n.Value:0.##}",
            _ => $"{title} changed",
        };
    }

    private static SettingRow? RowOf(DependencyObject d)
    {
        for (var cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            if (cur is SettingRow r) return r;
        return null;
    }

    // ─── Defaults: how to compare and reset each setting ────────────────────
    private static readonly AppSettings D = new();

    private static string J(IEnumerable<string> xs) => string.Join("|", xs);

    private static string ToolSig(AppSettings s, List<string> order, (string Key, string Label, string Glyph)[] defs) =>
        J(EffectiveOrder(order, defs)) + "/" + J(defs.Select(x => x.Key).Where(s.HiddenToolbarTools.Contains));

    private static void ResetTools(AppSettings s, List<string> order, (string Key, string Label, string Glyph)[] defs)
    {
        order.Clear();
        s.HiddenToolbarTools.RemoveAll(k => defs.Any(x => x.Key == k));
    }

    private static readonly Dictionary<string, (Func<AppSettings, string> Sig, Action<AppSettings> Reset)> Defaults = new()
    {
        ["pressure"]      = (s => $"{s.PressureEnabled}", s => s.PressureEnabled = D.PressureEnabled),
        ["curve"]         = (s => $"{s.PressureCurveX1:0.###},{s.PressureCurveY1:0.###},{s.PressureCurveX2:0.###},{s.PressureCurveY2:0.###}",
                             s => { s.PressureCurveX1 = D.PressureCurveX1; s.PressureCurveY1 = D.PressureCurveY1;
                                    s.PressureCurveX2 = D.PressureCurveX2; s.PressureCurveY2 = D.PressureCurveY2; }),
        ["minpressure"]   = (s => $"{s.MinPressure:0.##}", s => s.MinPressure = D.MinPressure),
        ["boost"]         = (s => $"{s.PressureMultiplier:0.##}", s => s.PressureMultiplier = D.PressureMultiplier),
        ["smoothing"]     = (s => $"{s.SmoothingLevel}", s => s.SmoothingLevel = D.SmoothingLevel),
        ["barrel"]        = (s => $"{s.BarrelButtonAction}", s => s.BarrelButtonAction = D.BarrelButtonAction),
        ["snap"]          = (s => $"{s.HoldToSnapEnabled}", s => s.HoldToSnapEnabled = D.HoldToSnapEnabled),
        ["palm"]          = (s => $"{s.PalmRejectionEnabled}", s => s.PalmRejectionEnabled = D.PalmRejectionEnabled),
        ["ignoretouch"]   = (s => $"{s.IgnoreTouchWhilePenActive}", s => s.IgnoreTouchWhilePenActive = D.IgnoreTouchWhilePenActive),
        ["grace"]         = (s => $"{s.PalmRejectionGraceMs}", s => s.PalmRejectionGraceMs = D.PalmRejectionGraceMs),
        ["pencolors"]     = (s => J(s.PenPresetColors), s => s.PenPresetColors = new List<string>(D.PenPresetColors)),
        ["hicolors"]      = (s => J(s.HighlighterPresetColors), s => s.HighlighterPresetColors = new List<string>(D.HighlighterPresetColors)),
        ["penwidth"]      = (s => $"{s.DefaultPenWidth:0.##}", s => s.DefaultPenWidth = D.DefaultPenWidth),
        ["hiwidth"]       = (s => $"{s.DefaultHighlighterWidth:0.##}", s => s.DefaultHighlighterWidth = D.DefaultHighlighterWidth),
        ["eraserwidth"]   = (s => $"{s.DefaultEraserWidth:0.##}", s => s.DefaultEraserWidth = D.DefaultEraserWidth),
        ["theme"]         = (s => $"{s.Theme}", s => s.Theme = D.Theme),
        ["glass"]         = (s => $"{s.LiquidGlassEnabled}", s => s.LiquidGlassEnabled = D.LiquidGlassEnabled),
        ["seamless"]      = (s => $"{s.SeamlessPages}", s => s.SeamlessPages = D.SeamlessPages),
        ["zoombar"]       = (s => $"{s.HideZoomBar}", s => s.HideZoomBar = D.HideZoomBar),
        ["tbpos"]         = (s => $"{s.ToolbarPosition}", s => { if (s.ToolbarPosition != D.ToolbarPosition) { s.ToolbarOffsetX = 0; s.ToolbarOffsetY = 0; } s.ToolbarPosition = D.ToolbarPosition; }),
        ["tbsize"]        = (s => $"{s.ToolbarSize}", s => s.ToolbarSize = D.ToolbarSize),
        ["drawtools"]     = (s => ToolSig(s, s.ToolbarDrawingOrder, DrawingToolDefs), s => ResetTools(s, s.ToolbarDrawingOrder, DrawingToolDefs)),
        ["actiontools"]   = (s => ToolSig(s, s.ToolbarActionOrder, ActionToolDefs), s => ResetTools(s, s.ToolbarActionOrder, ActionToolDefs)),
        ["export"]        = (s => $"{s.HiddenToolbarTools.Contains("Export")}", s => s.HiddenToolbarTools.Remove("Export")),
        ["autosave"]      = (s => $"{s.AutosaveEnabled}", s => s.AutosaveEnabled = D.AutosaveEnabled),
        ["autosavedelay"] = (s => $"{s.AutosaveDelaySeconds:0.##}", s => s.AutosaveDelaySeconds = D.AutosaveDelaySeconds),
        ["autoupdate"]    = (s => $"{s.AutoUpdateEnabled}", s => s.AutoUpdateEnabled = D.AutoUpdateEnabled),
        ["emulatetip"]    = (s => $"{s.EmulateTipOnButtonPress}", s => s.EmulateTipOnButtonPress = D.EmulateTipOnButtonPress),
        ["hwlang"]        = (s => s.HandwritingLanguage ?? "", s => s.HandwritingLanguage = D.HandwritingLanguage),
    };

    private static string DefaultSig(string key) =>
        Defaults[key].Sig(D);

    private void RefreshModified()
    {
        var s = App.Services.Settings.Current;
        var changedCategories = new HashSet<string>();
        foreach (var (row, category) in _rows)
        {
            bool mod = Defaults.TryGetValue(row.SettingKey, out var def) && def.Sig(s) != DefaultSig(row.SettingKey);
            row.IsModified = mod;
            if (mod) changedCategories.Add(category);
        }
        foreach (var (key, dot) in NavDots())
            dot.Visibility = changedCategories.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<(string, Ellipse)> NavDots() => new[]
    {
        ("pen", NavDotPen), ("touch", NavDotTouch), ("ink", NavDotInk), ("appearance", NavDotAppearance),
        ("storage", NavDotStorage), ("updates", NavDotUpdates), ("diagnostics", NavDotDiagnostics),
    };

    // ─── Categories, rows and search ────────────────────────────────────────
    private readonly List<(SettingRow Row, string Category)> _rows = new();

    private IEnumerable<StackPanel> Panels() => new[]
    {
        PenPanel, TouchPanel, InkPanel, AppearancePanel, StoragePanel, UpdatesPanel, ShortcutsPanel, DiagnosticsPanel,
    };

    private void CollectRows()
    {
        foreach (var panel in Panels())
            foreach (var row in LogicalDescendants(panel).OfType<SettingRow>())
            {
                _rows.Add((row, (string)panel.Tag));
                row.ResetRequested += (s, _) => ResetRow((SettingRow)s!);
            }
    }

    // Walks panels, borders and content controls without needing the visual
    // tree, so rows on pages that were never shown are found too.
    private static IEnumerable<UIElement> LogicalDescendants(UIElement root)
    {
        var stack = new Stack<UIElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            switch (e)
            {
                case Panel p: foreach (var c in p.Children) stack.Push(c); break;
                case Border b when b.Child is not null: stack.Push(b.Child); break;
                case SettingRow r: break;   // a row's own control holds no rows
                case ContentControl cc when cc.Content is UIElement u: stack.Push(u); break;
            }
        }
    }

    private void SelectCategory(string key)
    {
        var item = NavList.Items.OfType<ListViewItem>().FirstOrDefault(i => (string)i.Tag == key)
                   ?? NavList.Items.OfType<ListViewItem>().First();
        if (ReferenceEquals(NavList.SelectedItem, item)) ShowCategory((string)item.Tag);
        else NavList.SelectedItem = item;
    }

    private void ShowCategory(string key)
    {
        s_category = key;
        if (SettingsSearch.Text.Length > 0) { SettingsSearch.Text = ""; return; }   // ApplySearch shows the category
        ApplySearch();
        RootScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private bool _searching;

    // While searching, every category shows its matching rows; otherwise only
    // the selected category is shown, in full.
    private void ApplySearch()
    {
        var terms = SettingsSearch.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _searching = terms.Length > 0;
        int shown = 0;

        foreach (var panel in Panels())
        {
            string category = (string)panel.Tag;
            string categoryTitle = ((panel.Children[0] as StackPanel)?.Children[0] as TextBlock)?.Text ?? "";
            int panelShown = 0;
            foreach (var (row, cat) in _rows.Where(r => r.Category == category))
            {
                var hay = $"{row.Title} {row.Description} {row.Keywords} {categoryTitle}".ToLowerInvariant();
                bool ok = !_searching || terms.All(hay.Contains);
                row.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
                if (ok) panelShown++;
            }
            // Hide cards whose rows are all filtered out, and fix row hairlines.
            foreach (var card in panel.Children.OfType<Border>())
            {
                var rows = (card.Child as Panel)?.Children.OfType<SettingRow>().ToList() ?? new();
                bool any = rows.Any(r => r.Visibility == Visibility.Visible);
                card.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                bool first = true;
                foreach (var r in rows.Where(r => r.Visibility == Visibility.Visible))
                {
                    r.BorderThickness = first ? new Thickness(0) : new Thickness(0, 1, 0, 0);
                    first = false;
                }
            }
            bool visible = _searching ? panelShown > 0 : category == s_category;
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) shown++;
        }
        NoResultsText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        NavList.Opacity = _searching ? 0.5 : 1;
    }

    // ─── Layout ─────────────────────────────────────────────────────────────
    private const double ContentMaxWidth = 760;

    private void FitContentWidth(double viewport)
    {
        double w = Math.Max(0, viewport - RootStack.Margin.Left - RootStack.Margin.Right);
        RootStack.Width = Math.Min(ContentMaxWidth, w);
    }

    // Narrow windows get an icon-only sidebar.
    private void FitSidebar(double width)
    {
        bool narrow = width < 760;
        NavColumn.Width = new GridLength(narrow ? 64 : 220);
        NavLegend.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in NavList.Items.OfType<ListViewItem>())
            if (item.Content is Grid g && g.Children.Count > 1)
            {
                g.Children[1].Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                ToolTipService.SetToolTip(item, narrow ? (g.Children[1] as TextBlock)?.Text : null);
            }
    }

    private void BuildShortcutRows()
    {
        foreach (var (title, keys) in Shortcuts)
        {
            var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var k in keys)
                caps.Children.Add(new Border
                {
                    Style = (Style)Resources["KeyCapStyle"],
                    Child = new TextBlock { Text = k, FontSize = 12 },
                });
            ShortcutRows.Children.Add(new SettingRow
            {
                Title = title,
                Keywords = "shortcut keyboard " + string.Join(" ", keys),
                Content = caps,
            });
        }
    }

    private void BuildCurvePresets()
    {
        foreach (var p in PressureCurve.Presets) CurvePresets.Add(p.Name, p.Name, minWidth: 64);
        CurvePresets.ItemClicked += (_, i) =>
        {
            var p = PressureCurve.Presets[i];
            CurveEditor.SetCurve(p.X1, p.Y1, p.X2, p.Y2);
            OnChanged(CurveEditor);
            _pendingLabel = $"Pressure curve set to {p.Name}";
        };
    }

    // Highlights the preset that matches the saved curve, or none plus a
    // "Custom curve" note when the handles were dragged.
    private void RefreshPresetButtons()
    {
        var s = App.Services.Settings.Current;
        CurvePresets.SelectedIndex = Array.FindIndex(PressureCurve.Presets, p =>
            Math.Abs(p.X1 - s.PressureCurveX1) < 0.01 && Math.Abs(p.Y1 - s.PressureCurveY1) < 0.01 &&
            Math.Abs(p.X2 - s.PressureCurveX2) < 0.01 && Math.Abs(p.Y2 - s.PressureCurveY2) < 0.01);
        CustomCurveText.Visibility = CurvePresets.SelectedIndex < 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── Handwriting language ───────────────────────────────────────────────
    // Lists the handwriting languages installed in Windows. Picking one makes
    // search re-read notes with it (the index tracks the language per note).
    private bool _hwLangFilled;

    private async System.Threading.Tasks.Task FillHandwritingLanguagesAsync()
    {
        if (_hwLangFilled) return;
        _hwLangFilled = true;
        HwLangCombo.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = "" });
        foreach (var name in await Services.HandwritingRecognizer.GetLanguagesAsync())
            HwLangCombo.Items.Add(new ComboBoxItem { Content = Services.HandwritingRecognizer.ShortName(name), Tag = name });
        HwLangCombo.SelectionChanged += (s, _) =>
        {
            if (_loading || HwLangCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            App.Services.Settings.Current.HandwritingLanguage = tag;
            OnChanged(s);
        };
    }

    // ─── Named steps and choice chips ───────────────────────────────────────
    private static int Step(Slider s) => (int)Math.Round(s.Value);

    private static int Nearest((string Name, double Value)[] steps, double v)
    {
        int best = 0;
        for (int i = 1; i < steps.Length; i++)
            if (Math.Abs(steps[i].Value - v) < Math.Abs(steps[best].Value - v)) best = i;
        return best;
    }

    // "Light · default", "Firm", or "Custom (0.07)" for a value between steps.
    private static string StepWord((string Name, double Value)[] steps, double v, double def)
    {
        var hit = steps.FirstOrDefault(x => Math.Abs(x.Value - v) < 0.001);
        if (hit.Name is null) return $"Custom ({v:0.##})";
        return Math.Abs(v - def) < 0.001 ? $"{hit.Name} · default" : hit.Name;
    }

    private void BuildChoiceControls()
    {
        var s = App.Services.Settings.Current;

        for (int i = 0; i < SmoothingSteps.Length; i++)
            SmoothingChoice.Add(SmoothingSteps[i].Name, SmoothingSteps[i].Name);
        SmoothingChoice.ItemClicked += (strip, i) =>
        {
            App.Services.Settings.Current.SmoothingLevel = SmoothingSteps[i].Value;
            SmoothingChoice.SelectedIndex = i;
            OnChanged(strip);
        };

        foreach (var g in GraceSteps) GraceChoice.Add(g.Name, g.Name, minWidth: 72);
        GraceChoice.ItemClicked += (strip, i) =>
        {
            App.Services.Settings.Current.PalmRejectionGraceMs = GraceSteps[i].Ms;
            GraceChoice.SelectedIndex = i;
            OnChanged(strip);
        };

        var ink = (Brush)Application.Current.Resources["AppTextPrimaryBrush"];
        BuildWidthChoice(PenWidthChoice, PenWidths, 1, 40, 0.5,
            w => new Polyline { Points = Squiggle(28, 14), Stroke = ink, StrokeThickness = w,
                                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round },
            v => App.Services.Settings.Current.DefaultPenWidth = v);
        var marker = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0xFF, 0xD5, 0x3B));
        BuildWidthChoice(HighWidthChoice, HighlighterWidths, 2, 60, 1,
            w => new Rectangle { Width = 26, Height = Math.Min(24, w * 0.65), Fill = marker, RadiusX = 2, RadiusY = 2 },
            v => App.Services.Settings.Current.DefaultHighlighterWidth = v);
        var rim = (Brush)Application.Current.Resources["AppTextSecondaryBrush"];
        BuildWidthChoice(EraserWidthChoice, EraserWidths, 2, 80, 1,
            w => new Ellipse { Width = Math.Min(26, w * 0.6), Height = Math.Min(26, w * 0.6), Stroke = rim, StrokeThickness = 1.5 },
            v => App.Services.Settings.Current.DefaultEraserWidth = v);
    }

    // Preset chips draw a stroke at that width; the last chip is "Custom" and
    // opens a number box for any other width.
    private void BuildWidthChoice(ChoiceStrip strip, float[] presets, double min, double max, double step,
                                  Func<float, FrameworkElement> preview, Action<float> set)
    {
        foreach (var w in presets)
            strip.Add($"{w:0.#} px", preview(w));
        int custom = presets.Length;
        strip.Add("Custom", new TextBlock { Text = "Custom", FontSize = 12 }, minWidth: 64);

        strip.ItemClicked += (_, i) =>
        {
            if (i < custom)
            {
                set(presets[i]);
                strip.SelectedIndex = i;
                OnChanged(strip);
                return;
            }
            var box = new NumberBox
            {
                Header = "Width in pixels",
                Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 4,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Width = 170,
                Value = CurrentWidth(strip),
            };
            box.ValueChanged += (_, e) =>
            {
                if (double.IsNaN(e.NewValue)) return;
                set((float)Math.Clamp(e.NewValue, min, max));
                OnChanged(strip);
                _pendingLabel = $"{RowOf(strip)?.Title ?? "Width"} set to {e.NewValue:0.#} px";
            };
            var flyout = new Flyout { Content = box };
            flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
            flyout.ShowAt(strip.ButtonAt(custom));
        };
    }

    private float CurrentWidth(ChoiceStrip strip)
    {
        var s = App.Services.Settings.Current;
        return strip == PenWidthChoice ? s.DefaultPenWidth
             : strip == HighWidthChoice ? s.DefaultHighlighterWidth
             : s.DefaultEraserWidth;
    }

    private static void SyncWidthChoice(ChoiceStrip strip, float[] presets, float value)
    {
        int i = Array.FindIndex(presets, p => Math.Abs(p - value) < 0.01f);
        int custom = presets.Length;
        strip.SelectedIndex = i >= 0 ? i : custom;
        var text = i >= 0 ? "Custom" : $"{value:0.#} px";
        strip.SetLabel(custom, i >= 0 ? "Custom" : $"Custom: {value:0.#} px",
                       new TextBlock { Text = text, FontSize = 12 });
    }

    private static PointCollection Squiggle(double w, double h)
    {
        var pts = new PointCollection();
        for (int i = 0; i <= 16; i++)
        {
            double t = i / 16.0;
            pts.Add(new Windows.Foundation.Point(2 + t * (w - 4), h / 2 + Math.Sin(t * Math.PI * 2) * (h / 2 - 4)));
        }
        return pts;
    }

    // Draws a shaky line run through the same smoothing as PenTool, so the
    // preview shows what that level really does.
    private void DrawSmoothingPreview(int level)
    {
        double w = SmoothingPreview.Width, h = SmoothingPreview.Height;
        var raw = new List<Windows.Foundation.Point>();
        var rng = new Random(7);
        for (int i = 0; i <= 40; i++)
        {
            double t = i / 40.0;
            raw.Add(new Windows.Foundation.Point(2 + t * (w - 4),
                h / 2 + Math.Sin(t * Math.PI * 1.5) * 5 + (rng.NextDouble() - 0.5) * 9));
        }
        float follow = 1f - Math.Clamp(level, 0, 10) * 0.08f;
        var pts = new PointCollection();
        var cur = raw[0];
        foreach (var p in raw)
        {
            cur = level == 0 ? p : new Windows.Foundation.Point(cur.X + (p.X - cur.X) * follow, cur.Y + (p.Y - cur.Y) * follow);
            pts.Add(cur);
        }
        SmoothingPreview.Children.Clear();
        SmoothingPreview.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = (Brush)Application.Current.Resources["AppTextPrimaryBrush"],
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        });
    }

    // ─── Summaries and dependent rows ───────────────────────────────────────
    private static void SetRowEnabled(SettingRow row, bool on)
    {
        row.IsEnabled = on;
        row.Opacity = on ? 1 : 0.5;
    }

    private void RefreshSummaries()
    {
        var st = App.Services.Settings.Current;
        bool pressure = PressureToggle.IsOn;
        bool palm = PalmToggle.IsOn;
        bool autosave = AutosaveToggle.IsOn;

        SetRowEnabled(CurveRow, pressure);
        SetRowEnabled(MinPressureRow, pressure);
        SetRowEnabled(BoostRow, pressure);
        SetRowEnabled(IgnoreTouchRow, palm);
        SetRowEnabled(GraceRow, palm);
        SetRowEnabled(AutosaveDelayRow, autosave);

        string touchWord = StepWord(MinPressureSteps, st.MinPressure, D.MinPressure);
        string pressWord = StepWord(BoostSteps, st.PressureMultiplier, D.PressureMultiplier);
        MinPressureLabel.Text = touchWord;
        PressureMultLabel.Text = pressWord;
        RefreshPresetButtons();

        int smooth = Array.FindIndex(SmoothingSteps, x => x.Value == st.SmoothingLevel);
        SmoothingChoice.SelectedIndex = smooth;
        SmoothingRow.Description = smooth >= 0 ? SmoothingSteps[smooth].Description : $"Custom level {st.SmoothingLevel}.";
        DrawSmoothingPreview(st.SmoothingLevel);

        int grace = Array.FindIndex(GraceSteps, x => x.Ms == st.PalmRejectionGraceMs);
        GraceChoice.SelectedIndex = grace;
        string graceText = grace >= 0 ? GraceSteps[grace].Phrase : $"{st.PalmRejectionGraceMs} ms";
        GraceRow.Description = $"Your hand can't draw for {graceText} after the pen lifts.";

        SyncWidthChoice(PenWidthChoice, PenWidths, st.DefaultPenWidth);
        SyncWidthChoice(HighWidthChoice, HighlighterWidths, st.DefaultHighlighterWidth);
        SyncWidthChoice(EraserWidthChoice, EraserWidths, st.DefaultEraserWidth);

        string smoothName = smooth >= 0 ? SmoothingSteps[smooth].Name.ToLowerInvariant() : "custom";
        var barrel = (BarrelCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        PenSummary.Text = (pressure ? $"Pressure on, full width when pressing {pressWord.Split(' ')[0].ToLowerInvariant()}" : "Pressure off") +
                          $" · Smoothing {smoothName} · Barrel button: {barrel}";

        PalmSummary.Text = !palm ? "Palm rejection off · touch always draws"
            : $"{(IgnoreTouchToggle.IsOn ? "Touch ignored near the pen" : "Touch allowed near the pen")} · blocked for {graceText} after the pen lifts";

        InkSummary.Text = $"{st.PenPresetColors.Count} pen and {st.HighlighterPresetColors.Count} highlighter colors · " +
                          $"pen {st.DefaultPenWidth:0.#} px, highlighter {st.DefaultHighlighterWidth:0.#} px, eraser {st.DefaultEraserWidth:0.#} px";

        string ComboText(ComboBox c) => ((c.SelectedItem as ComboBoxItem)?.Content as string ?? "").ToLowerInvariant();
        string theme = ThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        int hidden = _drawingTools.Count(t => !t.IsVisible) + _actionTools.Count(t => !t.IsVisible);
        AppearanceSummary.Text = $"{theme} theme · toolbar {ComboText(ToolbarPositionCombo)}, {ComboText(ToolbarSizeCombo)}" +
                                 (hidden > 0 ? $" · {hidden} tool{(hidden == 1 ? "" : "s")} hidden" : "");

        string delay = double.IsNaN(AutosaveDelayBox.Value) ? "2.5" : $"{AutosaveDelayBox.Value:0.#}";
        StorageSummary.Text = $"{(autosave ? $"Autosave after {delay} s" : "Autosave off")} · {FolderBox.Text}";
    }

    // ─── Change wiring ──────────────────────────────────────────────────────
    private void WireChangeHandlers()
    {
        foreach (var t in new[] { PressureToggle, HoldToSnapToggle, PalmToggle, IgnoreTouchToggle, LiquidGlassToggle,
                                  SeamlessPagesToggle, HideZoomBarToggle, ExportToggle, AutosaveToggle, AutoUpdateToggle,
                                  EmulateTipToggle })
            t.Toggled += (s, _) => OnChanged(s);
        // Stepped controls write their setting directly, and only when the user
        // moves them, so a value that sits between steps survives other edits.
        MinPressureSlider.ValueChanged += (s, _) =>
        {
            if (_loading) return;
            App.Services.Settings.Current.MinPressure = MinPressureSteps[Step(MinPressureSlider)].Value;
            OnChanged(s);
        };
        PressureMultSlider.ValueChanged += (s, _) =>
        {
            if (_loading) return;
            App.Services.Settings.Current.PressureMultiplier = BoostSteps[Step(PressureMultSlider)].Value;
            OnChanged(s);
        };
        AutosaveDelayBox.ValueChanged += (s, _) => OnChanged(s);
        foreach (var cb in new[] { BarrelCombo, ThemeCombo, ToolbarPositionCombo, ToolbarSizeCombo })
            cb.SelectionChanged += (s, _) => OnChanged(s);
        CurveEditor.CurveChanged += (s, _) => OnChanged(s);

        CheckUpdatesBtn.Click += async (_, _) => await Services.UpdateService.CheckAndInstallAsync();

        void WireTools(ObservableCollection<ToolEntry> list, ListView view) =>
            list.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                    foreach (ToolEntry item in e.NewItems) item.PropertyChanged += (_, _) => OnChanged(view);
                OnChanged(view);
            };
        WireTools(_drawingTools, DrawingToolsList);
        WireTools(_actionTools, ActionToolsList);
    }

    private void LoadIntoUi()
    {
        _loading = true;
        try { LoadIntoUiCore(); }
        finally { _loading = false; }
        RefreshSummaries();
        RefreshModified();
        ApplySearch();
    }

    private void LoadIntoUiCore()
    {
        var s = App.Services.Settings.Current;

        RebuildColorSwatches(PenColorsPanel, isPen: true);
        RebuildColorSwatches(HighlighterColorsPanel, isPen: false);

        PressureToggle.IsOn = s.PressureEnabled;
        CurveEditor.SetCurve(s.PressureCurveX1, s.PressureCurveY1, s.PressureCurveX2, s.PressureCurveY2);
        MinPressureSlider.Value = Nearest(MinPressureSteps, s.MinPressure);
        PressureMultSlider.Value = Nearest(BoostSteps, s.PressureMultiplier);
        BindButtonCombo(BarrelCombo, s.BarrelButtonAction);
        HoldToSnapToggle.IsOn = s.HoldToSnapEnabled;
        HwLangCombo.SelectedItem = HwLangCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == (s.HandwritingLanguage ?? "")) ?? HwLangCombo.Items.FirstOrDefault();
        EmulateTipToggle.IsOn = s.EmulateTipOnButtonPress;

        PalmToggle.IsOn = s.PalmRejectionEnabled;
        IgnoreTouchToggle.IsOn = s.IgnoreTouchWhilePenActive;

        ThemeCombo.SelectedIndex = (int)s.Theme;
        ToolbarPositionCombo.SelectedIndex = (int)s.ToolbarPosition;
        ToolbarSizeCombo.SelectedIndex = (int)s.ToolbarSize;
        HideZoomBarToggle.IsOn = s.HideZoomBar;
        SeamlessPagesToggle.IsOn = s.SeamlessPages;
        LiquidGlassToggle.IsOn = s.LiquidGlassEnabled;

        var hidden = s.HiddenToolbarTools;
        PopulateToolList(_drawingTools, DrawingToolDefs, s.ToolbarDrawingOrder, hidden);
        PopulateToolList(_actionTools, ActionToolDefs, s.ToolbarActionOrder, hidden);
        DrawingToolsList.ItemsSource = _drawingTools;
        ActionToolsList.ItemsSource = _actionTools;
        ExportToggle.IsOn = !hidden.Contains("Export");

        FolderBox.Text = s.DocumentsFolder;

        AutosaveToggle.IsOn = s.AutosaveEnabled;
        AutosaveDelayBox.Value = s.AutosaveDelaySeconds;
        AutoUpdateToggle.IsOn = s.AutoUpdateEnabled;
    }

    private void ApplyUiToSettings()
    {
        var s = App.Services.Settings.Current;
        s.PressureEnabled = PressureToggle.IsOn;
        s.PressureCurveX1 = CurveEditor.X1; s.PressureCurveY1 = CurveEditor.Y1;
        s.PressureCurveX2 = CurveEditor.X2; s.PressureCurveY2 = CurveEditor.Y2;
        // Pressure steps, smoothing, palm block and widths are written by their
        // own handlers (see WireChangeHandlers / BuildChoiceControls).
        s.BarrelButtonAction = ReadCombo(BarrelCombo);
        s.HoldToSnapEnabled = HoldToSnapToggle.IsOn;
        s.EmulateTipOnButtonPress = EmulateTipToggle.IsOn;

        s.PalmRejectionEnabled = PalmToggle.IsOn;
        s.IgnoreTouchWhilePenActive = IgnoreTouchToggle.IsOn;

        s.Theme = (AppThemeMode)Math.Max(0, ThemeCombo.SelectedIndex);
        var oldToolbarPos = s.ToolbarPosition;
        s.ToolbarPosition = (ToolbarPosition)Math.Max(0, ToolbarPositionCombo.SelectedIndex);
        // Changing the dock edge moves the anchor, so a saved free-drag offset no
        // longer makes sense — start the new side at its default position.
        if (s.ToolbarPosition != oldToolbarPos) { s.ToolbarOffsetX = 0; s.ToolbarOffsetY = 0; }
        s.ToolbarSize = (ToolbarSize)Math.Max(0, ToolbarSizeCombo.SelectedIndex);
        s.HideZoomBar = HideZoomBarToggle.IsOn;
        s.SeamlessPages = SeamlessPagesToggle.IsOn;
        s.LiquidGlassEnabled = LiquidGlassToggle.IsOn;

        s.DocumentsFolder = FolderBox.Text;

        s.AutosaveEnabled = AutosaveToggle.IsOn;
        if (!double.IsNaN(AutosaveDelayBox.Value)) s.AutosaveDelaySeconds = Math.Clamp(AutosaveDelayBox.Value, 0.5, 120);
        s.AutoUpdateEnabled = AutoUpdateToggle.IsOn;

        s.HiddenToolbarTools.Clear();
        s.ToolbarDrawingOrder.Clear();
        s.ToolbarActionOrder.Clear();
        foreach (var item in _drawingTools)
        {
            s.ToolbarDrawingOrder.Add(item.Key);
            if (!item.IsVisible) s.HiddenToolbarTools.Add(item.Key);
        }
        foreach (var item in _actionTools)
        {
            s.ToolbarActionOrder.Add(item.Key);
            if (!item.IsVisible) s.HiddenToolbarTools.Add(item.Key);
        }
        if (!ExportToggle.IsOn) s.HiddenToolbarTools.Add("Export");
    }

    // ─── Updates ────────────────────────────────────────────────────────────
    // Ring shows the current step's progress; the four-step track shows where
    // in Check → Download → Prepare → Ready the update is. The header chip
    // shows the same state in a few words.
    private void ShowUpdateStatus()
    {
        var state = Services.UpdateService.Current;
        var avail = Services.UpdateService.AvailableVersion;
        var installed = Services.UpdateService.InstalledVersion;
        double p = Services.UpdateService.Progress;
        var res = Application.Current.Resources;
        var accent = (Brush)res["AppAccentBrush"];
        var ok = (Brush)res["AppSuccessBrush"];
        var muted = (Brush)res["AppTextSecondaryBrush"];
        var primary = (Brush)res["AppTextPrimaryBrush"];

        bool busy = state is Services.UpdateService.State.Checking
                          or Services.UpdateService.State.Downloading
                          or Services.UpdateService.State.Staging;
        bool good = state is Services.UpdateService.State.Ready or Services.UpdateService.State.UpToDate;

        UpdateStatusText.Text = state == Services.UpdateService.State.Idle
            ? "YuNotes checks GitHub for a newer version when it starts."
            : Services.UpdateService.StatusText;
        UpdateTitleText.Text = state switch
        {
            Services.UpdateService.State.Downloading or Services.UpdateService.State.Staging
                when avail is not null => $"Updating to YuNotes {Short(avail)}",
            Services.UpdateService.State.Ready when avail is not null => $"YuNotes {Short(avail)} is ready",
            Services.UpdateService.State.UpToDate => "You're up to date",
            Services.UpdateService.State.Failed => "Update check failed",
            _ => "Updates",
        };

        bool determinate = state is Services.UpdateService.State.Downloading or Services.UpdateService.State.Staging;
        UpdateRing.IsIndeterminate = state == Services.UpdateService.State.Checking;
        UpdateRing.Value = determinate ? p : good ? 100 : 0;
        UpdateRing.Foreground = good ? ok : accent;
        UpdatePercentText.Text = $"{Math.Round(p)}%";
        UpdatePercentText.Visibility = determinate ? Visibility.Visible : Visibility.Collapsed;
        UpdateStateIcon.Visibility = determinate || state == Services.UpdateService.State.Checking
            ? Visibility.Collapsed : Visibility.Visible;
        string glyph = good ? "" : state == Services.UpdateService.State.Failed ? "" : "";
        UpdateStateIcon.Glyph = glyph;
        UpdateStateIcon.Foreground = good ? ok : state == Services.UpdateService.State.Failed ? muted : accent;

        int step = state switch
        {
            Services.UpdateService.State.Checking => 0,
            Services.UpdateService.State.Downloading => 1,
            Services.UpdateService.State.Staging => 2,
            Services.UpdateService.State.Ready => 4,
            _ => -1,
        };
        UpdateSteps.Visibility = step >= 0 ? Visibility.Visible : Visibility.Collapsed;
        var bars = new[] { StepCheckBar, StepDownloadBar, StepStageBar, StepReadyBar };
        var labels = new[] { StepCheckLabel, StepDownloadLabel, StepStageLabel, StepReadyLabel };
        for (int i = 0; i < bars.Length; i++)
        {
            bool done = i < step || step == 4;
            bool cur = i == step;
            bars[i].IsIndeterminate = cur && state == Services.UpdateService.State.Checking;
            bars[i].Value = done ? 100 : cur ? p : 0;
            bars[i].Foreground = step == 4 ? ok : accent;
            labels[i].Foreground = done || cur ? primary : muted;
            labels[i].FontWeight = cur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        if (state == Services.UpdateService.State.Downloading)
        {
            const double MB = 1024 * 1024;
            double got = Services.UpdateService.BytesReceived / MB;
            double rate = Services.UpdateService.BytesPerSecond / MB;
            var parts = new List<string>();
            parts.Add(Services.UpdateService.BytesTotal is { } total
                ? $"{got:0.0} / {total / MB:0.0} MB" : $"{got:0.0} MB");
            if (rate > 0.01) parts.Add($"{rate:0.0} MB/s");
            if (rate > 0.01 && Services.UpdateService.BytesTotal is { } t2)
                parts.Add($"{Math.Max(0, Math.Ceiling((t2 / MB - got) / rate))} s left");
            UpdateMetaText.Text = string.Join("  ·  ", parts);
            UpdateMetaText.Visibility = Visibility.Visible;
        }
        else UpdateMetaText.Visibility = Visibility.Collapsed;

        CheckUpdatesBtn.IsEnabled = Services.UpdateService.IsSupported && !busy
                                    && state != Services.UpdateService.State.Ready;
        string checkedText = Services.UpdateService.LastChecked is { } at ? $" · checked {Ago(at)}" : "";
        AppVersionText.Text = installed is null ? "Development build"
            : Services.UpdateService.IsSupported ? $"Installed version {Short(installed)}{checkedText}"
            : $"Installed version {Short(installed)} · updates come from the Microsoft Store";
        CheckUpdatesBtn.Visibility = state == Services.UpdateService.State.Ready ? Visibility.Collapsed : Visibility.Visible;
        CheckUpdatesBtn.Content = state == Services.UpdateService.State.Failed ? "Try again" : "Check for updates";

        UpdateChipText.Text = state switch
        {
            Services.UpdateService.State.Checking => "Checking…",
            Services.UpdateService.State.Downloading => $"Downloading {Math.Round(p)}%",
            Services.UpdateService.State.Staging => "Preparing update",
            Services.UpdateService.State.Ready => "Update ready",
            Services.UpdateService.State.UpToDate => "Up to date",
            Services.UpdateService.State.Failed => "Update failed",
            _ => "Updates",
        };
        UpdateChipIcon.Glyph = glyph;
        UpdateChipIcon.Foreground = good ? ok : state == Services.UpdateService.State.Failed ? muted : accent;
        NavDotUpdates.Visibility = state == Services.UpdateService.State.Ready || RowIsModified("autoupdate")
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool RowIsModified(string key) => _rows.Any(r => r.Row.SettingKey == key && r.Row.IsModified);

    private static string Short(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    private static string Ago(DateTime at)
    {
        var d = DateTime.Now - at;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        return at.ToString("d MMM");
    }

    // ─── Toolbar tool lists ─────────────────────────────────────────────────
    // Saved order first, then any keys added since that aren't in it yet.
    private static List<string> EffectiveOrder(List<string> savedOrder, (string Key, string Label, string Glyph)[] defs) =>
        savedOrder.Count > 0
            ? savedOrder.Where(k => defs.Any(d => d.Key == k))
                        .Concat(defs.Select(d => d.Key).Where(k => !savedOrder.Contains(k)))
                        .ToList()
            : defs.Select(d => d.Key).ToList();

    private static void PopulateToolList(
        ObservableCollection<ToolEntry> collection,
        (string Key, string Label, string Glyph)[] defs,
        List<string> savedOrder,
        List<string> hidden)
    {
        collection.Clear();
        foreach (var key in EffectiveOrder(savedOrder, defs))
        {
            var def = defs.FirstOrDefault(d => d.Key == key);
            if (def == default) continue;
            collection.Add(new ToolEntry(def.Key, def.Label, def.Glyph, !hidden.Contains(def.Key)));
        }
    }

    private static void BindButtonCombo(ComboBox combo, PenButtonAction selected)
    {
        combo.Items.Clear();
        foreach (var (val, label) in ButtonActions)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = val });
        combo.SelectedIndex = 0;
        for (int i = 0; i < ButtonActions.Length; i++)
            if (ButtonActions[i].Value == selected) { combo.SelectedIndex = i; break; }
    }

    private static PenButtonAction ReadCombo(ComboBox combo)
        => combo.SelectedItem is ComboBoxItem it && it.Tag is PenButtonAction a ? a : PenButtonAction.None;

    // ─── Preset colors ──────────────────────────────────────────────────────
    // Edits change the settings lists directly; OnChanged then saves them and
    // offers Undo like any other change.
    private void RebuildColorSwatches(StackPanel panel, bool isPen)
    {
        var s = App.Services.Settings.Current;
        var list = isPen ? s.PenPresetColors : s.HighlighterPresetColors;
        var res = Application.Current.Resources;
        panel.Children.Clear();

        for (int i = 0; i < list.Count; i++)
        {
            int idx = i;
            var cell = new Grid { Width = 44, Height = 44 };
            var circle = new Button
            {
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(ColorPickerControl.ParseHex(list[i])),
                BorderThickness = new Thickness(1.5),
                BorderBrush = (Brush)res["AppBorderBrush"]
            };
            ToolTipService.SetToolTip(circle, "Edit color");
            circle.Click += async (_, _) =>
            {
                var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
                {
                    IsAlphaEnabled = !isPen,
                    Color = ColorPickerControl.ParseHex(list[idx])
                };
                var dlg = new ContentDialog
                {
                    Title = "Edit color", Content = picker,
                    PrimaryButtonText = "OK", CloseButtonText = "Cancel",
                    XamlRoot = XamlRoot
                };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    list[idx] = ColorPickerControl.ToHex(picker.Color);
                    RebuildColorSwatches(panel, isPen);
                    OnChanged(panel);
                }
            };

            var del = new Button
            {
                Width = 16, Height = 16,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = (Brush)res["AppSurfaceBrush"],
                BorderBrush = (Brush)res["AppBorderBrush"],
                BorderThickness = new Thickness(1),
                Content = new FontIcon { Glyph = "", FontSize = 8 }
            };
            ToolTipService.SetToolTip(del, "Remove color");
            del.Click += (_, _) =>
            {
                list.RemoveAt(idx);
                RebuildColorSwatches(panel, isPen);
                OnChanged(panel);
            };

            cell.Children.Add(circle);
            cell.Children.Add(del);
            panel.Children.Add(cell);
        }

        var addBtn = new Button
        {
            Width = 36, Height = 36,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(0),
            Background = (Brush)res["AppSurfaceVariantBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)res["AppBorderBrush"],
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(addBtn, "Add color");
        addBtn.Click += async (_, _) =>
        {
            var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
            {
                IsAlphaEnabled = !isPen,
                Color = isPen
                    ? Windows.UI.Color.FromArgb(255, 27, 31, 42)
                    : Windows.UI.Color.FromArgb(128, 255, 235, 59)
            };
            var dlg = new ContentDialog
            {
                Title = "Add color", Content = picker,
                PrimaryButtonText = "Add", CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                list.Add(ColorPickerControl.ToHex(picker.Color));
                RebuildColorSwatches(panel, isPen);
                OnChanged(panel);
            }
        };
        panel.Children.Add(addBtn);
    }

    private async System.Threading.Tasks.Task PickFolder()
    {
        var p = new FolderPicker();
        p.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(p, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var f = await p.PickSingleFolderAsync();
        if (f is not null && f.Path != FolderBox.Text) { FolderBox.Text = f.Path; OnChanged(FolderBox); }
    }

    // ─── Scroll suppression for the test areas ──────────────────────────────
    private readonly HashSet<uint> _suppressPointers = new();
    private bool _suppressing;
    private ScrollMode _savedHScroll;
    private ScrollMode _savedVScroll;
    private ZoomMode _savedZoom;

    private void BeginSuppress(uint id)
    {
        if (!_suppressPointers.Add(id)) return;
        if (_suppressing) return;
        _savedHScroll = RootScroller.HorizontalScrollMode;
        _savedVScroll = RootScroller.VerticalScrollMode;
        _savedZoom = RootScroller.ZoomMode;
        RootScroller.HorizontalScrollMode = ScrollMode.Disabled;
        RootScroller.VerticalScrollMode = ScrollMode.Disabled;
        RootScroller.ZoomMode = ZoomMode.Disabled;
        _suppressing = true;
    }

    private void EndSuppress(uint id)
    {
        if (!_suppressPointers.Remove(id)) return;
        if (_suppressPointers.Count > 0 || !_suppressing) return;
        RootScroller.HorizontalScrollMode = _savedHScroll;
        RootScroller.VerticalScrollMode = _savedVScroll;
        RootScroller.ZoomMode = _savedZoom;
        _suppressing = false;
    }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen) BeginSuppress(e.Pointer.PointerId);
    }

    private void OnPagePointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen) EndSuppress(e.Pointer.PointerId);
    }

    // ─── Pressure test area ─────────────────────────────────────────────────
    // Runs each sample through the same mapping as the editor, draws a circle
    // sized to the result, and moves the live dot on the curve editor.
    private void OnPressureSandbox(PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(PressureSandbox);
        if (!pt.IsInContact) return;

        var s = App.Services.Settings.Current;
        float raw = pt.Properties.Pressure;
        if (raw <= 0) raw = 0.5f;
        bool isPen = e.Pointer.PointerDeviceType == PointerDeviceType.Pen;

        float pressure;
        if (isPen && s.PressureEnabled)
        {
            if (!PressureCurve.TryMap(raw, s, out float input, out pressure))
            {
                PressureReadout.Text = $"{raw:0.00} · ignored (too light)";
                return;
            }
            CurveEditor.ShowLive(input);
            PressureReadout.Text = $"pen {raw:0.00} → width {pressure:0.00}";
        }
        else
        {
            pressure = isPen ? raw : 0.6f;
            PressureReadout.Text = isPen ? "Pressure is off" : "Use a pen to test pressure";
        }

        var radius = 3 + pressure * 16;
        byte a = (byte)(80 + pressure * 175);
        var accent = ((SolidColorBrush)Application.Current.Resources["AppAccentBrush"]).Color;
        var dot = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(a, accent.R, accent.G, accent.B)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(dot, pt.Position.X - radius);
        Canvas.SetTop(dot, pt.Position.Y - radius);
        PressureCanvas.Children.Add(dot);
        PressureHint.Visibility = Visibility.Collapsed;

        const int max = 1500;
        while (PressureCanvas.Children.Count > max)
            PressureCanvas.Children.RemoveAt(0);
    }

    // ─── Diagnostics ────────────────────────────────────────────────────────
    private void OnSandboxPressed(object sender, PointerRoutedEventArgs e)
    {
        // Disable outer-page pan/zoom before capturing so a second finger isn't
        // taken by DirectManipulation for pinch-zoom.
        BeginSuppress(e.Pointer.PointerId);
        Sandbox.CapturePointer(e.Pointer);
        TrackTouch(e, contact: true);
        OnSandbox(sender, e);
    }

    private void OnSandboxEnded(object sender, PointerRoutedEventArgs e)
    {
        EndSuppress(e.Pointer.PointerId);
        TrackTouch(e, contact: false);
        OnSandbox(sender, e);
    }

    private void OnSandbox(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(Sandbox);
        var props = pt.Properties;
        var kind = e.Pointer.PointerDeviceType;

        _log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {e.Pointer.PointerId} {kind} contact={pt.IsInContact} eraser={props.IsEraser} L={props.IsLeftButtonPressed} M={props.IsMiddleButtonPressed} R={props.IsRightButtonPressed} barrel={props.IsBarrelButtonPressed} P={props.Pressure:0.00}");
        LastDeviceText.Text = $"{kind} (id {e.Pointer.PointerId})";
        UpdateTouchDots(_activeTouchIds.Count);

        Highlight(IndMouseInUse, kind == PointerDeviceType.Mouse);
        Highlight(IndMouseL, kind == PointerDeviceType.Mouse && props.IsLeftButtonPressed);
        Highlight(IndMouseM, kind == PointerDeviceType.Mouse && props.IsMiddleButtonPressed);
        Highlight(IndMouseR, kind == PointerDeviceType.Mouse && props.IsRightButtonPressed);

        var isPen = kind == PointerDeviceType.Pen && !props.IsEraser;
        var isEraserTip = kind == PointerDeviceType.Pen && props.IsEraser;
        Highlight(IndPenHover, isPen && !pt.IsInContact);
        Highlight(IndPenTip, isPen && pt.IsInContact);
        Highlight(IndPenB1, isPen && props.IsBarrelButtonPressed);
        Highlight(IndEraserHover, isEraserTip && !pt.IsInContact);
        Highlight(IndEraserTip, isEraserTip && pt.IsInContact);
    }

    private static void Highlight(Border b, bool on)
    {
        b.Background = on
            ? (Brush)Application.Current.Resources["AppAccentBrush"]
            : (Brush)Application.Current.Resources["AppSurfaceBrush"];
        if (b.Child is TextBlock t)
            t.Foreground = on
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : (Brush)Application.Current.Resources["AppTextPrimaryBrush"];
    }

    private void TrackTouch(PointerRoutedEventArgs e, bool contact)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch) return;
        if (contact) _activeTouchIds.Add(e.Pointer.PointerId);
        else _activeTouchIds.Remove(e.Pointer.PointerId);
    }

    private void UpdateTouchDots(int active)
    {
        var on = (Brush)Application.Current.Resources["AppAccentBrush"];
        var off = (Brush)Application.Current.Resources["AppBorderBrush"];
        Touch1.Fill = active >= 1 ? on : off;
        Touch2.Fill = active >= 2 ? on : off;
        Touch3.Fill = active >= 3 ? on : off;
        Touch4.Fill = active >= 4 ? on : off;
        Touch5.Fill = active >= 5 ? on : off;
    }

    private async System.Threading.Tasks.Task ShowLogs()
    {
        var dlg = new ContentDialog
        {
            Title = "Diagnostics log",
            Content = new ScrollViewer
            {
                Height = 380,
                Content = new TextBlock { Text = _log.Length == 0 ? "No events yet." : _log.ToString(), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") }
            },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        await dlg.ShowAsync();
    }

    private async System.Threading.Tasks.Task ExportDebug()
    {
        try
        {
            var save = new FileSavePicker();
            save.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
            save.SuggestedFileName = $"YuNotes-debug-{DateTime.Now:yyyyMMdd-HHmmss}";
            WinRT.Interop.InitializeWithWindow.Initialize(save, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await save.PickSaveFileAsync();
            if (file is null) return;
            var body = $"YuNotes debug info {DateTime.Now:O}\n\nPointer log:\n{_log}\n";
            File.WriteAllText(file.Path, body);
        }
        catch { }
    }

    private async System.Threading.Tasks.Task ShowCrashLog()
    {
        var path = App.CrashLogPath;
        if (!File.Exists(path))
        {
            var d = new ContentDialog
            {
                Title = "No crash log",
                Content = "No crashes have been recorded yet.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await d.ShowAsync();
            return;
        }

        var text = File.ReadAllText(path);
        var dlg = new ContentDialog
        {
            Title = "Crash log",
            Content = new ScrollViewer
            {
                Height = 400,
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                }
            },
            PrimaryButtonText = "Open folder",
            SecondaryButtonText = "Clear log",
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await Windows.System.Launcher.LaunchFolderPathAsync(System.IO.Path.GetDirectoryName(path)!);
        else if (result == ContentDialogResult.Secondary)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
