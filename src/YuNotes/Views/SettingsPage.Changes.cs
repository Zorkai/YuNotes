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
            Slider s when s == ToolbarSizeSlider => $"{title}: {SnappedToolbarPercent()}%",
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
        ["tbsize"]        = (s => $"{ToolbarMetrics.PercentOf(ToolbarMetrics.ScaleOf(s))}", s => { s.ToolbarScale = null; s.ToolbarSize = D.ToolbarSize; }),
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

    private bool RowIsModified(string key) => _rows.Any(r => r.Row.SettingKey == key && r.Row.IsModified);
}
