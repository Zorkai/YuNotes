using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace YuNotes.Controls;

// One row on the Settings page: title, optional description, and the control
// that edits it, either on the right or (Stacked) full width underneath. When
// IsModified is set the row shows a dot and a Reset link. SettingKey ties the
// row to its value in AppSettings; Keywords extend what search matches.
// The template lives in SettingsPage.xaml.
public sealed partial class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata("", (d, _) => ((SettingRow)d).Sync()));
    public static readonly DependencyProperty StackedProperty =
        DependencyProperty.Register(nameof(Stacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false, (d, _) => ((SettingRow)d).Sync()));
    public static readonly DependencyProperty IsModifiedProperty =
        DependencyProperty.Register(nameof(IsModified), typeof(bool), typeof(SettingRow), new PropertyMetadata(false, (d, _) => ((SettingRow)d).Sync()));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public bool Stacked { get => (bool)GetValue(StackedProperty); set => SetValue(StackedProperty, value); }
    public bool IsModified { get => (bool)GetValue(IsModifiedProperty); set => SetValue(IsModifiedProperty, value); }

    public string Keywords { get; set; } = "";
    public string SettingKey { get; set; } = "";

    public event EventHandler? ResetRequested;

    private ContentPresenter? _side, _below;
    private FrameworkElement? _dot, _description;
    private ButtonBase? _reset;

    public SettingRow() => IsTabStop = false;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_reset is not null) _reset.Click -= OnResetClick;
        _side = GetTemplateChild("SideHost") as ContentPresenter;
        _below = GetTemplateChild("BelowHost") as ContentPresenter;
        _dot = GetTemplateChild("ModifiedDot") as FrameworkElement;
        _description = GetTemplateChild("DescriptionText") as FrameworkElement;
        _reset = GetTemplateChild("ResetLink") as ButtonBase;
        if (_reset is not null) _reset.Click += OnResetClick;
        Sync();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        Sync();
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => ResetRequested?.Invoke(this, EventArgs.Empty);

    private void Sync()
    {
        if (_side is null || _below is null) return;
        // Detach first: a UIElement can only have one parent.
        _side.Content = null;
        _below.Content = null;
        if (Stacked) _below.Content = Content; else _side.Content = Content;
        _below.Visibility = Stacked && Content is not null ? Visibility.Visible : Visibility.Collapsed;
        _side.Visibility = !Stacked && Content is not null ? Visibility.Visible : Visibility.Collapsed;

        var mod = IsModified && SettingKey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_dot is not null) _dot.Visibility = mod;
        if (_reset is not null) _reset.Visibility = mod;
        if (_description is not null)
            _description.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
    }
}
