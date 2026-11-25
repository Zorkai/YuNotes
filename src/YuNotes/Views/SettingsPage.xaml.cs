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

    // The group this tool is folded into, for the row's chip ("" = none).
    public string GroupName { get; private set; } = "";
    public string GroupGlyph { get; private set; } = "";
    public string GroupTooltip => GroupName.Length == 0 ? "" : $"Shown in the toolbar inside the \u201c{GroupName}\u201d group";
    public Microsoft.UI.Xaml.Visibility GroupVisibility =>
        GroupName.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public void SetGroup(string name, string glyph)
    {
        if (GroupName == name && GroupGlyph == glyph) return;
        GroupName = name; GroupGlyph = glyph;
        foreach (var n in new[] { nameof(GroupName), nameof(GroupGlyph), nameof(GroupTooltip), nameof(GroupVisibility) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
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
        ("Tape",        "Tape (hide and reveal)", "\uf1fc"),
        ("Laser",       "Laser pointer", ""),
    };

    private static readonly (string Key, string Label, string Glyph)[] ActionToolDefs =
    {
        ("SideBySide",  "Side by side",  ""),
        ("Present",     "Present",       ""),
        ("Stickers",    "Stickers (in Add)", ""),
        ("Screenshot",  "Screenshot", ""),
        ("Template",    "Page template (in Add)", ""),
        ("AddPdfPages", "Pages from a PDF (in Add)", ""),
        ("AddPage",     "Blank page (in Add)", ""),
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
        ("Open a note in a new tab",  new[] { "Ctrl", "T" }),
        ("Close tab",                new[] { "Ctrl", "W" }),
        ("Next / previous tab",      new[] { "Ctrl", "Tab" }),
        ("Open in a new tab from Home", new[] { "Ctrl", "Click" }),
        ("Zoom in",                  new[] { "Ctrl", "=" }),
        ("Zoom out",                 new[] { "Ctrl", "-" }),
        ("Zoom to 100%",             new[] { "Ctrl", "0" }),
        ("Fit width",                new[] { "Ctrl", "9" }),
        ("Fit page",                 new[] { "Ctrl", "8" }),
        ("Select all notes on Home", new[] { "Ctrl", "A" }),
        ("Select a range of notes",  new[] { "Shift", "Click" }),
        ("Present full screen",      new[] { "F5" }),
        ("Quiz yourself on the tape", new[] { "F6" }),
        ("Show answer / Again / Got it", new[] { "Space", "1", "2" }),
        ("Next / previous page while presenting", new[] { "→", "←" }),
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

        BackBtn.Click += (_, _) => { Commit(); MainWindow.GoBack(this); };
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
        InitBackup();
        InitToolbarSize();
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
            HeaderGrid.Padding = new Thickness(12, 0, 12 + MainWindow.CaptionButtonInset(this), 0);
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

    private async System.Threading.Tasks.Task PickFolder()
    {
        var p = new FolderPicker();
        p.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(p, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Of(this)));
        var f = await p.PickSingleFolderAsync();
        if (f is not null && f.Path != FolderBox.Text) { FolderBox.Text = f.Path; OnChanged(FolderBox); }
    }
}
