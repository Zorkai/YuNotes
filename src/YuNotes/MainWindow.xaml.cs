using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using YuNotes.Models;
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

        // Guard the window's close (X) button: if the editor has unsaved changes,
        // prompt to save before letting the app exit (same dialog the Back button uses).
        _appWindow.Closing += OnAppWindowClosing;

        // Apply the saved light/dark theme to the content root (cascades to every
        // page; the XAML MicaBackdrop and caption buttons follow ActualTheme), and
        // re-apply whenever settings change so the Settings page takes effect live.
        ApplyThemeFromSettings();
        App.Services.Settings.Changed += ApplyThemeFromSettings;

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

    // Set once the user has resolved the unsaved-changes prompt (Save or Don't save),
    // so the re-issued Close() sails through instead of re-prompting.
    private bool _closeConfirmed;

    // AppWindow.Closing is cancelable (unlike Window.Closed). If the editor holds
    // unsaved edits, cancel the close, run the same Save/Don't save/Cancel dialog the
    // Back button uses, and only re-close if the user didn't cancel. Cancel must be
    // set synchronously before the first await, so it's assigned up front.
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed) return;
        if (RootFrame.Content is not EditorPage editor || !editor.HasUnsavedChanges) return;

        args.Cancel = true;
        if (await editor.ConfirmLeaveAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    // Maps the persisted theme choice onto the content root. RequestedTheme
    // cascades to all child content; Default = follow the OS. Guarded so the
    // frequent settings saves (e.g. last-page tracking) don't churn the tree.
    private void ApplyThemeFromSettings()
    {
        var target = App.Services.Settings.Current.Theme switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark  => ElementTheme.Dark,
            _                  => ElementTheme.Default,
        };
        if (RootGrid.RequestedTheme != target)
            RootGrid.RequestedTheme = target;
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
