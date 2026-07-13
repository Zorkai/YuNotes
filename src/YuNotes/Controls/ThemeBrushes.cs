using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace YuNotes.Controls;

// Resolves the app's themed brushes (Colors.xaml ThemeDictionaries) against a
// SPECIFIC theme. Controls that build visuals in code must use this instead of
// Application.Current.Resources[key]: that indexer resolves against the
// application theme, which ignores a RequestedTheme override on the window root
// and so hands back the wrong Light/Dark variant (e.g. near-white text painted
// onto a forced-Light surface — invisible). Pass the control's ActualTheme.
internal static class ThemeBrushes
{
    public static Brush Brush(ElementTheme theme, string key)
    {
        string themeKey = theme == ElementTheme.Dark ? "Dark" : "Light";
        return Find(Application.Current.Resources, key, themeKey)
               ?? (Brush)Application.Current.Resources[key];
    }

    public static Color Color(ElementTheme theme, string key) =>
        ((SolidColorBrush)Brush(theme, key)).Color;

    // The themed brushes live in Colors.xaml's ThemeDictionaries, which is a
    // MergedDictionary of App.Resources — so recurse through merged dictionaries.
    private static Brush? Find(ResourceDictionary dict, string key, string themeKey)
    {
        if (dict.ThemeDictionaries.TryGetValue(themeKey, out var tdObj) &&
            tdObj is ResourceDictionary td &&
            td.TryGetValue(key, out var v) && v is Brush b)
            return b;
        foreach (var md in dict.MergedDictionaries)
            if (Find(md, key, themeKey) is { } found) return found;
        return null;
    }
}
