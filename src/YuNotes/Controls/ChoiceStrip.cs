using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace YuNotes.Controls;

// A row of tappable choices (stroke previews, "Short / Normal / Long", …) with
// one highlighted. Clicking raises ItemClicked; the owner decides what becomes
// selected, so a "Custom" chip can open a flyout instead of selecting itself.
// SelectedIndex -1 highlights nothing (the current value matches no choice).
public sealed partial class ChoiceStrip : StackPanel
{
    private readonly List<Button> _buttons = new();
    private readonly List<string> _labels = new();
    private int _selected = -1;

    public event EventHandler<int>? ItemClicked;

    public ChoiceStrip()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        ActualThemeChanged += (_, _) => Restyle();
    }

    public int Count => _buttons.Count;
    public string LabelOf(int i) => i >= 0 && i < _labels.Count ? _labels[i] : "";
    public Button ButtonAt(int i) => _buttons[i];

    // label is the accessible name and what the "changed" toast says.
    public void Add(string label, object content, double minWidth = 44)
    {
        int index = _buttons.Count;
        var b = new Button
        {
            Content = content,
            MinWidth = minWidth,
            Height = 36,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(b, label);
        ToolTipService.SetToolTip(b, label);
        b.Click += (_, _) => ItemClicked?.Invoke(this, index);
        _buttons.Add(b);
        _labels.Add(label);
        Children.Add(b);
        Restyle();
    }

    public void SetLabel(int i, string label, object? content = null)
    {
        _labels[i] = label;
        AutomationProperties.SetName(_buttons[i], label);
        ToolTipService.SetToolTip(_buttons[i], label);
        if (content is not null) _buttons[i].Content = content;
    }

    public int SelectedIndex
    {
        get => _selected;
        set { _selected = value; Restyle(); }
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void Restyle()
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            bool on = i == _selected;
            var b = _buttons[i];
            b.Background = on ? Res("AppAccentSoftBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            b.BorderBrush = on ? Res("AppAccentBrush") : Res("AppBorderBrush");
            b.BorderThickness = new Thickness(on ? 2 : 1);
            b.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }
}
