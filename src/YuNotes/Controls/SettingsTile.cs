using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace YuNotes.Controls;

// Quick-settings tile on the Settings page: a ToggleButton that carries an icon
// glyph and a one-line caption ("On · 500 ms", "Off") for its template to show.
// The look lives in SettingsPage.xaml (SettingsTileStyle).
public sealed partial class SettingsTile : ToggleButton
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsTile), new PropertyMetadata(""));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(SettingsTile), new PropertyMetadata("Off"));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }
}
