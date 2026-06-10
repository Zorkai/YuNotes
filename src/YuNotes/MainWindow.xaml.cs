using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using YuNotes.Views;

namespace YuNotes;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;

    public MainWindow()
    {
        InitializeComponent();
        Title = "YuNotes";

        // Apply Mica on Windows 11; fall back to Desktop Acrylic on Windows 10.
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        else if (DesktopAcrylicController.IsSupported())
            SystemBackdrop = new DesktopAcrylicBackdrop();
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));

        // Merge each page's own top bar into the title bar area. Pages register
        // their drag region via SetDragRegion and pad past CaptionButtonInset.
        ExtendsContentIntoTitleBar = true;
        if (_appWindow.TitleBar is not null)
        {
            _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            UpdateCaptionButtonColors();
            RootGrid.ActualThemeChanged += (_, __) => UpdateCaptionButtonColors();
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            _appWindow.SetIcon(iconPath);

        // Slide pages in/out on navigation. Per-navigation animations are applied
        // by mutating _navTheme.DefaultNavigationTransitionInfo (see Navigate):
        // WinUI 3 ignores the transitionInfoOverride parameter of Frame.Navigate.
        RootFrame.ContentTransitions = new TransitionCollection { _navTheme };

        // A page that throws during construction surfaces here (WinUI swallows it from the
        // Navigate caller). Log the REAL exception and handle it so the app doesn't hard-crash.
        RootFrame.NavigationFailed += (_, e) =>
        {
            App.LogError(e.Exception, $"NavigationFailed -> {e.SourcePageType?.FullName}");
            e.Handled = true;
        };

        RootFrame.Navigate(typeof(HomePage));
    }

    private readonly NavigationThemeTransition _navTheme = new();
    // Transition used to *enter* each page on the back stack, so GoBack can replay
    // it (NavigationThemeTransition reverses it for NavigationMode.Back).
    private readonly System.Collections.Generic.Stack<NavigationTransitionInfo?> _transitionStack = new();

    public static void Navigate<T>(object? parameter = null, NavigationTransitionInfo? transition = null)
        where T : Microsoft.UI.Xaml.Controls.Page
    {
        if (App.MainWindow is MainWindow mw)
        {
            mw._navTheme.DefaultNavigationTransitionInfo = transition ?? new EntranceNavigationTransitionInfo();
            mw._transitionStack.Push(transition);
            mw.RootFrame.Navigate(typeof(T), parameter);
        }
    }

    // The system caption buttons are drawn over our content; keep their chrome
    // transparent and match glyph color to the active theme.
    private void UpdateCaptionButtonColors()
    {
        var tb = _appWindow.TitleBar;
        if (tb is null) return;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var fg = dark ? Colors.White : Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1F, 0x2A);
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = dark
            ? Windows.UI.Color.FromArgb(0xFF, 0x80, 0x86, 0x99)
            : Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9F, 0xAD);
        tb.ButtonHoverBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        tb.ButtonPressedBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x20, 0x00, 0x00, 0x00);
    }

    // Pages call this (from Loaded) with the transparent element that should act
    // as the window drag region while that page is shown.
    public static void SetDragRegion(UIElement element)
    {
        if (App.MainWindow is MainWindow mw) mw.SetTitleBar(element);
    }

    // Width in DIPs the caption buttons occupy at the window's top-right. Pages
    // pad their top bars by this so content doesn't sit under min/max/close.
    public static double CaptionButtonInset
    {
        get
        {
            if (App.MainWindow is not MainWindow mw) return 138;
            var tb = mw._appWindow.TitleBar;
            double scale = mw.RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            if (tb is null || tb.RightInset <= 0 || scale <= 0) return 138;
            return tb.RightInset / scale;
        }
    }

    public static void GoBack()
    {
        if (App.MainWindow is MainWindow mw)
        {
            if (mw.RootFrame.CanGoBack)
            {
                var entry = mw._transitionStack.Count > 0 ? mw._transitionStack.Pop() : null;
                mw._navTheme.DefaultNavigationTransitionInfo = entry ?? new EntranceNavigationTransitionInfo();
                mw.RootFrame.GoBack();
            }
            else
            {
                mw.RootFrame.Navigate(typeof(Views.HomePage));
            }
        }
    }
}
