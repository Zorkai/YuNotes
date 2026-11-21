using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using Windows.UI;
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

    private readonly ObservableCollection<ToolEntry> _drawingTools = new();
    private readonly ObservableCollection<ToolEntry> _actionTools = new();

    private readonly StringBuilder _log = new();
    private readonly HashSet<uint> _activeTouchIds = new();
    private bool _settingsDirty;
    private bool _settingsLoading;

    public SettingsPage()
    {
        InitializeComponent();
        BackBtn.Click += async (_, __) => { if (await ConfirmLeaveSettingsAsync()) GoBack(); };
        SaveBtn.Click += (_, __) => Save();
        DiscardBtn.Click += (_, __) => { RestoreColorSnapshot(); LoadIntoUi(); };
        SettingsSearch.TextChanged += (_, __) => ApplySearch(SettingsSearch.Text);
        TileGrid.SizeChanged += (_, e) => LayoutTiles(e.NewSize.Width);
        var findKey = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        findKey.Invoked += (_, e) => { SettingsSearch.Focus(FocusState.Keyboard); e.Handled = true; };
        KeyboardAccelerators.Add(findKey);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        PickFolderBtn.Click += async (_, __) => await PickFolder();
        ShowLogsBtn.Click += async (_, __) => await ShowLogs();
        ExportDebugBtn.Click += async (_, __) => await ExportDebug();
        CrashLogBtn.Click += async (_, __) => await ShowCrashLog();

        // Stylus & pen input live bindings
        MinPressureSlider.ValueChanged += (_, e) => MinPressureLabel.Text = $"{e.NewValue:0.00}";
        PressureMultSlider.ValueChanged += (_, e) => PressureMultLabel.Text = $"{e.NewValue:0.00}";

        // Pressure preview sandbox
        PressureSandbox.PointerPressed += OnPressureSandbox;
        PressureSandbox.PointerMoved += OnPressureSandbox;
        PressureSandbox.PointerReleased += OnPressureSandbox;
        PressureClearBtn.Click += (_, __) => { PressureCanvas.Children.Clear(); PressureHint.Visibility = Visibility.Visible; };

        // Diagnostics sandbox
        Sandbox.PointerEntered += OnSandbox;
        Sandbox.PointerMoved += OnSandbox;
        Sandbox.PointerPressed += OnSandboxPressed;
        Sandbox.PointerReleased += OnSandboxEnded;
        Sandbox.PointerCanceled += OnSandboxEnded;
        Sandbox.PointerCaptureLost += OnSandboxEnded;
        Sandbox.PointerExited += OnSandboxEnded;

        // The outer ScrollViewer's DirectManipulation grabs pen/touch input for pan/zoom
        // before the inner sandboxes can capture it. We suppress scroll/zoom on the
        // RootScroller while any pen is pressed (anywhere on the page) or any pointer
        // is pressed inside a sandbox, so the second finger isn't stolen for pinch-zoom.
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPagePointerPressed), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPagePointerEnded), handledEventsToo: true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(OnPagePointerEnded), handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnPagePointerEnded), handledEventsToo: true);

        ColorToolPen.Checked += (_, __) => RebuildColorSwatches();
        ColorToolHighlighter.Checked += (_, __) => RebuildColorSwatches();

        Loaded += (_, __) =>
        {
            MainWindow.SetDragRegion(HeaderDragRegion);
            HeaderGrid.Padding = new Thickness(12, 0, 12 + MainWindow.CaptionButtonInset, 0);
            TakeColorSnapshot();
            LoadIntoUi();
        };
        WireDirtyHandlers();
    }

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

    private void OnSandboxPressed(object sender, PointerRoutedEventArgs e)
    {
        // Disable outer-page pan/zoom BEFORE handing off so a second finger isn't
        // stolen by DirectManipulation for pinch-zoom.
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

    private void Mark(object? s = null, object? e = null)
    {
        if (_settingsLoading) return;
        _settingsDirty = true;
        UpdateSaveBar();
    }

    private void UpdateSaveBar() =>
        SaveBar.Visibility = _settingsDirty ? Visibility.Visible : Visibility.Collapsed;

    // Preset-color edits change the settings lists in place, so keep a copy to
    // put back when the user discards.
    private List<string> _penColorsSnapshot = new();
    private List<string> _highlighterColorsSnapshot = new();

    private void TakeColorSnapshot()
    {
        var s = App.Services.Settings.Current;
        _penColorsSnapshot = new List<string>(s.PenPresetColors);
        _highlighterColorsSnapshot = new List<string>(s.HighlighterPresetColors);
    }

    private void RestoreColorSnapshot()
    {
        var s = App.Services.Settings.Current;
        s.PenPresetColors.Clear(); s.PenPresetColors.AddRange(_penColorsSnapshot);
        s.HighlighterPresetColors.Clear(); s.HighlighterPresetColors.AddRange(_highlighterColorsSnapshot);
    }

    // ─── Quick tiles: 4 per row, 2 when the window is narrow ────────────────
    private int _tileColumns;
    private int _tileCount;

    private void LayoutTiles(double width)
    {
        int cols = width < 560 ? 2 : 4;
        var visible = TileGrid.Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        if (cols == _tileColumns && visible.Count == _tileCount) return;
        _tileColumns = cols;
        _tileCount = visible.Count;
        TileGrid.ColumnDefinitions.Clear();
        TileGrid.RowDefinitions.Clear();
        for (int c = 0; c < cols; c++) TileGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < (visible.Count + cols - 1) / cols; r++)
            TileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < visible.Count; i++)
        {
            Grid.SetColumn((FrameworkElement)visible[i], i % cols);
            Grid.SetRow((FrameworkElement)visible[i], i / cols);
        }
    }

    // ─── Search ─────────────────────────────────────────────────────────────
    // Filters tiles by their label and tooltip, the Updates card by a few
    // keywords, and each section by its Tag keywords plus header text.
    // Matching sections expand so the hit is visible.
    private void ApplySearch(string? query)
    {
        var terms = (query ?? "").Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Match(string haystack) => terms.All(t => haystack.Contains(t));
        bool searching = terms.Length > 0;
        int shown = 0;

        foreach (var tile in TileGrid.Children.OfType<ToggleButton>())
        {
            var text = (TileLabel(tile) + " " + ToolTipService.GetToolTip(tile)).ToLowerInvariant();
            bool ok = !searching || Match(text);
            tile.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            if (ok) shown++;
        }
        TileGrid.Visibility = TileGrid.Children.Any(c => c.Visibility == Visibility.Visible)
            ? Visibility.Visible : Visibility.Collapsed;
        LayoutTiles(RootStack.ActualWidth);

        bool updOk = !searching || Match("updates update version download install automatically check");
        UpdatesCard.Visibility = updOk ? Visibility.Visible : Visibility.Collapsed;
        if (updOk) shown++;

        foreach (var ex in new[] { PenSection, PalmSection, InkSection, AppearanceSection, StorageSection, AdvancedSection })
        {
            var text = ((ex.Tag as string ?? "") + " " + HeaderText(ex)).ToLowerInvariant();
            bool ok = !searching || Match(text);
            ex.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            if (searching && ok) ex.IsExpanded = true;
            if (ok) shown++;
        }
        NoResultsText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string TileLabel(ToggleButton tile) =>
        tile.Content is Panel p ? string.Join(" ", p.Children.OfType<TextBlock>().Select(t => t.Text)) : "";

    private static string HeaderText(Expander ex) =>
        ex.Header is Panel p
            ? string.Join(" ", p.Children.OfType<Panel>().SelectMany(c => c.Children.OfType<TextBlock>()).Select(t => t.Text))
            : "";

    private void WireDirtyHandlers()
    {
        PressureEnabledBox.Checked += Mark; PressureEnabledBox.Unchecked += Mark;
        EmulateTipBox.Checked += Mark; EmulateTipBox.Unchecked += Mark;

        MinPressureSlider.ValueChanged += (s, e) => Mark();
        PressureMultSlider.ValueChanged += (s, e) => Mark();
        GraceBox.ValueChanged += (s, e) => Mark();
        PenWidthBox.ValueChanged += (s, e) => Mark();
        HighWidthBox.ValueChanged += (s, e) => Mark();
        EraserWidthBox.ValueChanged += (s, e) => Mark();

        BarrelCombo.SelectionChanged += Mark;
        ThemeCombo.SelectionChanged += Mark;
        ToolbarPositionCombo.SelectionChanged += Mark;
        ToolbarSizeCombo.SelectionChanged += Mark;
        HideZoomBarBox.Checked += Mark; HideZoomBarBox.Unchecked += Mark;
        SeamlessPagesBox.Checked += Mark; SeamlessPagesBox.Unchecked += Mark;
        LiquidGlassBox.Checked += Mark; LiquidGlassBox.Unchecked += Mark;

        PalmToggle.Checked += Mark; PalmToggle.Unchecked += Mark;
        IgnoreTouchToggle.Toggled += Mark;
        HoldToSnapToggle.Checked += Mark; HoldToSnapToggle.Unchecked += Mark;

        AutosaveToggle.Checked += Mark; AutosaveToggle.Unchecked += Mark;
        AutosaveToggle.Checked   += (_, _) => AutosaveDelayBox.IsEnabled = true;
        AutosaveToggle.Unchecked += (_, _) => AutosaveDelayBox.IsEnabled = false;
        AutosaveDelayBox.ValueChanged += (_, _) => Mark();

        // Updates
        AutoUpdateToggle.Checked += Mark; AutoUpdateToggle.Unchecked += Mark;
        CheckUpdatesBtn.Click += async (_, _) => await Services.UpdateService.CheckAndInstallAsync();
        Loaded += (_, _) =>
        {
            Services.UpdateService.StatusChanged -= ShowUpdateStatus;
            Services.UpdateService.StatusChanged += ShowUpdateStatus;
            ShowUpdateStatus();
        };
        Unloaded += (_, _) => Services.UpdateService.StatusChanged -= ShowUpdateStatus;

        ToolVisExport.Checked   += Mark;
        ToolVisExport.Unchecked += Mark;

        _drawingTools.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ToolEntry item in e.NewItems) item.PropertyChanged += (_, _) => Mark();
            Mark();
        };
        _actionTools.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ToolEntry item in e.NewItems) item.PropertyChanged += (_, _) => Mark();
            Mark();
        };
    }

    private async System.Threading.Tasks.Task<bool> ConfirmLeaveSettingsAsync()
    {
        if (!_settingsDirty) return true;
        var dlg = new ContentDialog
        {
            Title = "Unsaved settings",
            Content = "Save changes to your settings before leaving?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.None) return false;
        if (res == ContentDialogResult.Primary) { Save(); return false; }   // Save() also goes back
        RestoreColorSnapshot();
        return true;                                                        // Discard → proceed
    }

    private void LoadIntoUi()
    {
        _settingsLoading = true;
        try { LoadIntoUiCore(); }
        finally { _settingsLoading = false; _settingsDirty = false; UpdateSaveBar(); }
    }

    private void LoadIntoUiCore()
    {
        var s = App.Services.Settings.Current;

        // Rebuild color swatches for the currently selected tool tab
        RebuildColorSwatches();

        // Stylus & pen input — pressure
        PressureEnabledBox.IsChecked = s.PressureEnabled;
        MinPressureSlider.Value = s.MinPressure;
        PressureMultSlider.Value = s.PressureMultiplier;
        MinPressureLabel.Text = $"{s.MinPressure:0.00}";
        PressureMultLabel.Text = $"{s.PressureMultiplier:0.00}";

        // Button mappings — populate dropdown with friendly labels
        BindButtonCombo(BarrelCombo, s.BarrelButtonAction);
        EmulateTipBox.IsChecked = s.EmulateTipOnButtonPress;

        // Palm rejection
        PalmToggle.IsChecked = s.PalmRejectionEnabled;
        IgnoreTouchToggle.IsOn = s.IgnoreTouchWhilePenActive;
        GraceBox.Value = s.PalmRejectionGraceMs;

        // Shape recognition
        HoldToSnapToggle.IsChecked = s.HoldToSnapEnabled;

        // Appearance
        ThemeCombo.SelectedIndex = (int)s.Theme;

        // Layout
        ToolbarPositionCombo.SelectedIndex = (int)s.ToolbarPosition;
        ToolbarSizeCombo.SelectedIndex = (int)s.ToolbarSize;
        HideZoomBarBox.IsChecked = s.HideZoomBar;
        SeamlessPagesBox.IsChecked = s.SeamlessPages;
        LiquidGlassBox.IsChecked = s.LiquidGlassEnabled;

        // Toolbar tool order + visibility
        var hidden = s.HiddenToolbarTools;
        PopulateToolList(_drawingTools, DrawingToolDefs, s.ToolbarDrawingOrder, hidden);
        PopulateToolList(_actionTools, ActionToolDefs, s.ToolbarActionOrder, hidden);
        DrawingToolsList.ItemsSource = _drawingTools;
        ActionToolsList.ItemsSource  = _actionTools;
        ToolVisExport.IsChecked      = !hidden.Contains("Export");

        // Defaults
        PenWidthBox.Value = s.DefaultPenWidth;
        HighWidthBox.Value = s.DefaultHighlighterWidth;
        EraserWidthBox.Value = s.DefaultEraserWidth;
        FolderBox.Text  = s.DocumentsFolder;

        // Autosave
        AutosaveToggle.IsChecked = s.AutosaveEnabled;
        AutosaveDelayBox.Value = s.AutosaveDelaySeconds;
        AutosaveDelayBox.IsEnabled = s.AutosaveEnabled;

        // Updates
        AutoUpdateToggle.IsChecked = s.AutoUpdateEnabled;
    }

    // ─── Updates card ────────────────────────────────────────────────────────
    // Ring shows the current step's progress; the four-step track shows where
    // in Check → Download → Prepare → Ready the update is.
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

        // Ring
        bool determinate = state is Services.UpdateService.State.Downloading or Services.UpdateService.State.Staging;
        UpdateRing.IsIndeterminate = state == Services.UpdateService.State.Checking;
        UpdateRing.Value = determinate ? p
            : state is Services.UpdateService.State.Ready or Services.UpdateService.State.UpToDate ? 100 : 0;
        UpdateRing.Foreground = state is Services.UpdateService.State.Ready or Services.UpdateService.State.UpToDate ? ok : accent;
        UpdatePercentText.Text = $"{Math.Round(p)}%";
        UpdatePercentText.Visibility = determinate ? Visibility.Visible : Visibility.Collapsed;
        UpdateStateIcon.Visibility = determinate || state == Services.UpdateService.State.Checking
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateStateIcon.Glyph = state switch
        {
            Services.UpdateService.State.Ready or Services.UpdateService.State.UpToDate => "\uE5CA",   // check
            Services.UpdateService.State.Failed => "\uE000",                                           // error
            _ => "\uE923",                                                                              // update
        };
        UpdateStateIcon.Foreground = state switch
        {
            Services.UpdateService.State.Ready or Services.UpdateService.State.UpToDate => ok,
            Services.UpdateService.State.Failed => muted,
            _ => accent,
        };

        // Step track: only meaningful once a newer version was found (or while checking).
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

        // Download readout
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
        AppVersionText.Text = installed is null ? "Development build"
            : Services.UpdateService.IsSupported ? $"Installed version {Short(installed)}"
            : $"Installed version {Short(installed)} · updates come from the Microsoft Store";
    }

    private static string Short(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    private static void PopulateToolList(
        ObservableCollection<ToolEntry> collection,
        (string Key, string Label, string Glyph)[] defs,
        List<string> savedOrder,
        List<string> hidden)
    {
        collection.Clear();

        // Build ordered sequence: saved order first, then any new keys not yet in the order
        var order = savedOrder.Count > 0
            ? savedOrder.Where(k => defs.Any(d => d.Key == k))
                        .Concat(defs.Select(d => d.Key).Where(k => !savedOrder.Contains(k)))
                        .ToList()
            : defs.Select(d => d.Key).ToList();

        foreach (var key in order)
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

    private void RebuildColorSwatches()
    {
        var s = App.Services.Settings.Current;
        bool isPen = ColorToolPen.IsChecked == true;
        var list = isPen ? s.PenPresetColors : s.HighlighterPresetColors;

        ColorsPanel.Children.Clear();

        for (int i = 0; i < list.Count; i++)
        {
            int idx = i;
            var parsed = Controls.ColorPickerControl.ParseHex(list[i]);

            // Container grid: color circle + small delete button in corner
            var cell = new Grid { Width = 44, Height = 44 };

            var circle = new Button
            {
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(parsed),
                BorderThickness = new Thickness(1.5),
                BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"]
            };
            circle.Click += async (_, __) =>
            {
                var isAlpha = !isPen;  // allow alpha for highlighter
                var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
                {
                    IsAlphaEnabled = isAlpha,
                    Color = Controls.ColorPickerControl.ParseHex(list[idx])
                };
                var dlg = new ContentDialog
                {
                    Title = "Edit color", Content = picker,
                    PrimaryButtonText = "OK", CloseButtonText = "Cancel",
                    XamlRoot = this.XamlRoot
                };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    list[idx] = Controls.ColorPickerControl.ToHex(picker.Color);
                    Mark();
                    RebuildColorSwatches();
                }
            };

            var del = new Button
            {
                Width = 16, Height = 16,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = (Brush)Application.Current.Resources["AppSurfaceBrush"],
                BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
                BorderThickness = new Thickness(1),
                Content = new FontIcon { Glyph = "", FontSize = 8 }
            };
            del.Click += (_, __) =>
            {
                list.RemoveAt(idx);
                Mark();
                RebuildColorSwatches();
            };

            cell.Children.Add(circle);
            cell.Children.Add(del);
            ColorsPanel.Children.Add(cell);
        }

        // "Add color" button
        var addBtn = new Button
        {
            Width = 36, Height = 36,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(0),
            Background = (Brush)Application.Current.Resources["AppSurfaceVariantBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(addBtn, "Add color");
        addBtn.Click += async (_, __) =>
        {
            var isAlpha = !isPen;
            var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
            {
                IsAlphaEnabled = isAlpha,
                Color = isPen
                    ? Windows.UI.Color.FromArgb(255, 27, 31, 42)
                    : Windows.UI.Color.FromArgb(128, 255, 235, 59)
            };
            var dlg = new ContentDialog
            {
                Title = "Add color", Content = picker,
                PrimaryButtonText = "Add", CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                list.Add(Controls.ColorPickerControl.ToHex(picker.Color));
                Mark();
                RebuildColorSwatches();
            }
        };
        ColorsPanel.Children.Add(addBtn);
    }

    private async System.Threading.Tasks.Task PickFolder()
    {
        var p = new FolderPicker();
        p.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(p, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var f = await p.PickSingleFolderAsync();
        if (f is not null) FolderBox.Text = f.Path;
    }

    private void Save()
    {
        var s = App.Services.Settings.Current;
        s.PressureEnabled = PressureEnabledBox.IsChecked == true;
        s.MinPressure = MinPressureSlider.Value;
        s.PressureMultiplier = PressureMultSlider.Value;
        s.BarrelButtonAction = ReadCombo(BarrelCombo);
        s.EmulateTipOnButtonPress = EmulateTipBox.IsChecked == true;
        s.PalmRejectionEnabled = PalmToggle.IsChecked == true;
        s.IgnoreTouchWhilePenActive = IgnoreTouchToggle.IsOn;
        s.PalmRejectionGraceMs = (int)GraceBox.Value;
        s.HoldToSnapEnabled = HoldToSnapToggle.IsChecked == true;
        s.Theme = (AppThemeMode)Math.Max(0, ThemeCombo.SelectedIndex);
        s.DefaultPenWidth = (float)PenWidthBox.Value;
        s.DefaultHighlighterWidth = (float)HighWidthBox.Value;
        s.DefaultEraserWidth = (float)EraserWidthBox.Value;
        s.DocumentsFolder  = FolderBox.Text;
        var oldToolbarPos = s.ToolbarPosition;
        s.ToolbarPosition = (ToolbarPosition)ToolbarPositionCombo.SelectedIndex;
        // Changing the dock edge moves the anchor, so a saved free-drag offset no
        // longer makes sense — start the new side at its default position.
        if (s.ToolbarPosition != oldToolbarPos) { s.ToolbarOffsetX = 0; s.ToolbarOffsetY = 0; }
        s.ToolbarSize = (ToolbarSize)ToolbarSizeCombo.SelectedIndex;
        s.HideZoomBar = HideZoomBarBox.IsChecked == true;
        s.SeamlessPages = SeamlessPagesBox.IsChecked == true;
        s.LiquidGlassEnabled = LiquidGlassBox.IsChecked == true;

        s.AutosaveEnabled = AutosaveToggle.IsChecked == true;
        // NumberBox yields NaN if the field is cleared — fall back to the default.
        s.AutosaveDelaySeconds = double.IsNaN(AutosaveDelayBox.Value)
            ? 2.5 : Math.Clamp(AutosaveDelayBox.Value, 0.5, 120);

        s.AutoUpdateEnabled = AutoUpdateToggle.IsChecked == true;

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
        if (ToolVisExport.IsChecked != true) s.HiddenToolbarTools.Add("Export");

        App.Services.Settings.Save();
        GoBack();
    }

    private static void GoBack() => MainWindow.GoBack();

    // ─── Diagnostics sandbox ────────────────────────────────────────────────

    private void OnSandbox(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(Sandbox);
        var props = pt.Properties;
        var kind = e.Pointer.PointerDeviceType;

        // Log a one-liner
        _log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {e.Pointer.PointerId} {kind} contact={pt.IsInContact} eraser={props.IsEraser} L={props.IsLeftButtonPressed} M={props.IsMiddleButtonPressed} R={props.IsRightButtonPressed} barrel={props.IsBarrelButtonPressed} P={props.Pressure:0.00}");

        // Last device text
        LastDeviceText.Text = $"{kind} (id {e.Pointer.PointerId})";

        UpdateTouchDots(_activeTouchIds.Count);

        // Reset all chips then highlight active ones
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

    private void Highlight(Border b, bool on)
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
                Content = new TextBlock { Text = _log.Length == 0 ? "(no events yet)" : _log.ToString(), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") }
            },
            CloseButtonText = "Close",
            XamlRoot = this.XamlRoot
        };
        await dlg.ShowAsync();
    }

    private void OnPressureSandbox(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(PressureSandbox);
        if (!pt.IsInContact) return;

        // Apply the same transform as the editor so the preview reflects current settings.
        var s = App.Services.Settings.Current;
        float rawPressure = pt.Properties.Pressure;
        if (rawPressure <= 0) rawPressure = 0.5f;
        bool isPen = e.Pointer.PointerDeviceType == PointerDeviceType.Pen;

        float pressure;
        if (isPen && s.PressureEnabled)
        {
            if (rawPressure < (float)s.MinPressure) return;  // dropped sample
            pressure = (rawPressure - (float)s.MinPressure) * (float)s.PressureMultiplier;
            if (pressure < 0) pressure = 0;
            if (pressure > 1) pressure = 1;
        }
        else
        {
            pressure = isPen ? rawPressure : 0.6f;
        }

        // Map pressure to circle radius (4–22 px).
        var radius = 4 + pressure * 18;

        // Color the dot by pressure: low = light blue, high = accent.
        byte a = (byte)(80 + pressure * 175);
        var accent = ((SolidColorBrush)Application.Current.Resources["AppAccentBrush"]).Color;
        var fill = new SolidColorBrush(Windows.UI.Color.FromArgb(a, accent.R, accent.G, accent.B));

        var dot = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = fill,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(dot, pt.Position.X - radius);
        Canvas.SetTop(dot, pt.Position.Y - radius);
        PressureCanvas.Children.Add(dot);

        if (PressureHint.Visibility == Visibility.Visible)
            PressureHint.Visibility = Visibility.Collapsed;

        // Cap the number of dots so the canvas doesn't grow without bound.
        const int max = 1500;
        while (PressureCanvas.Children.Count > max)
            PressureCanvas.Children.RemoveAt(0);
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
