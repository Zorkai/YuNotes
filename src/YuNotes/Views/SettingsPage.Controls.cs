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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(circle, "Edit color");
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(del, "Remove color");
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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(addBtn, "Add color");
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
}
