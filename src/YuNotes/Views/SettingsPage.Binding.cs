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

public sealed partial class SettingsPage
{

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
        AppearanceSummary.Text = $"{theme} theme · toolbar {ComboText(ToolbarPositionCombo)}, {SnappedToolbarPercent()}% size" +
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
        foreach (var cb in new[] { BarrelCombo, ThemeCombo, ToolbarPositionCombo })
            cb.SelectionChanged += (s, _) => OnChanged(s);
        CurveEditor.CurveChanged += (s, _) => OnChanged(s);

        CheckUpdatesBtn.Click += async (_, _) => await Services.UpdateService.CheckAndInstallAsync();

        void WireTools(ObservableCollection<ToolEntry> list, ListView view) =>
            list.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                    foreach (ToolEntry item in e.NewItems)
                        item.PropertyChanged += (_, pe) =>
                        {
                            // Group chip updates are display only, not a settings change.
                            if (pe.PropertyName != nameof(ToolEntry.IsVisible)) return;
                            OnChanged(view);
                            RebuildToolbarPreview();
                        };
                OnChanged(view);
                RebuildToolbarPreview();
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
        LoadToolbarSize(s);
        HideZoomBarToggle.IsOn = s.HideZoomBar;
        SeamlessPagesToggle.IsOn = s.SeamlessPages;
        LiquidGlassToggle.IsOn = s.LiquidGlassEnabled;

        var hidden = s.HiddenToolbarTools;
        PopulateToolList(_drawingTools, DrawingToolDefs, s.ToolbarDrawingOrder, hidden);
        PopulateToolList(_actionTools, ActionToolDefs, s.ToolbarActionOrder, hidden);
        DrawingToolsList.ItemsSource = _drawingTools;
        ActionToolsList.ItemsSource = _actionTools;
        ExportToggle.IsOn = !hidden.Contains("Export");
        RebuildToolGroupsUi();

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
}
