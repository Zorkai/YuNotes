using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using YuNotes.Views;

namespace YuNotes;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "YuNotes";
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(id);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        if (appWindow.TitleBar is not null)
            appWindow.TitleBar.ExtendsContentIntoTitleBar = false;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            appWindow.SetIcon(iconPath);

        // A page that throws during construction surfaces here (WinUI swallows it from the
        // Navigate caller). Log the REAL exception and handle it so the app doesn't hard-crash.
        RootFrame.NavigationFailed += (_, e) =>
        {
            App.LogError(e.Exception, $"NavigationFailed -> {e.SourcePageType?.FullName}");
            e.Handled = true;
        };

        RootFrame.Navigate(typeof(HomePage));
    }

    public static void Navigate<T>(object? parameter = null) where T : Microsoft.UI.Xaml.Controls.Page
    {
        if (App.MainWindow is MainWindow mw)
            mw.RootFrame.Navigate(typeof(T), parameter);
    }

    public static void GoBack()
    {
        if (App.MainWindow is MainWindow mw)
        {
            if (mw.RootFrame.CanGoBack) mw.RootFrame.GoBack();
            else mw.RootFrame.Navigate(typeof(Views.HomePage));
        }
    }
}
