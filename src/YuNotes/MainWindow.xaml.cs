using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    // ─── Windows ────────────────────────────────────────────────────────────────
    // YuNotes can have several windows, each with its own set of editor tabs.
    // A note is open in at most one window: opening it again (from Home, the
    // + menu or Explorer) switches to the window and tab that already has it.

    private static readonly List<MainWindow> s_windows = new();

    // Every open window, most recently used last.
    public static IReadOnlyList<MainWindow> All => s_windows;

    // The window the user last worked in.
    public static MainWindow? Active => s_windows.Count > 0 ? s_windows[^1] : null;

    // This window's editor tabs. They outlive the editor page, so going Home
    // and back keeps them.
    internal EditorTabs Tabs { get; }

    // The window an element (or a page, through its frame) belongs to.
    public static MainWindow? Of(UIElement? element)
    {
        if (element is Microsoft.UI.Xaml.Controls.Page { Frame: { } f }
            && s_windows.FirstOrDefault(w => ReferenceEquals(w.RootFrame, f)) is { } byFrame)
            return byFrame;
        if (element?.XamlRoot is { } root
            && s_windows.FirstOrDefault(w => ReferenceEquals(w.Content?.XamlRoot, root)) is { } byRoot)
            return byRoot;
        return Active;
    }

    // The window and tab holding `path`, if any window has it open.
    internal static (MainWindow Window, OpenTab Tab)? FindOpenTab(string path)
    {
        foreach (var w in s_windows)
            if (w.Tabs.Find(path) is { } t) return (w, t);
        return null;
    }

    public MainWindow() : this(null) { }

    // The last window closed: end the process for real. Any process left alive
    // keeps the package "in use", and Windows then never applies a staged
    // update (it waits until no YuNotes process is running). Give up the
    // single-instance key first, so a launch during shutdown starts fresh
    // instead of redirecting to this closing instance.
    private static void ExitAfterLastWindow()
    {
        try { Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().UnregisterKey(); } catch { }
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dq?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (s_windows.Count == 0) Application.Current.Exit();
        });
        // Backstop in case something still holds the process open after Exit.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(15));
            if (s_windows.Count == 0) Environment.Exit(0);
        });
    }

    // `tabs`: start in the editor with these tabs (a tab dragged out of another
    // window), instead of on Home.
    internal MainWindow(EditorTabs? tabs)
    {
        Tabs = tabs ?? new EditorTabs();
        s_windows.Add(this);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            s_windows.Remove(this);
            s_windows.Add(this);
        };
        Closed += (_, __) =>
        {
            s_windows.Remove(this);
            if (s_windows.Count == 0) ExitAfterLastWindow();
        };

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

        if (tabs is not null && tabs.List.Count > 0) RootFrame.Navigate(typeof(EditorPage), EditorResume.Instance);
        else RootFrame.Navigate(typeof(HomePage));
    }

    // Opens a note file (from Explorer or a second launch of YuNotes): switches
    // to it where it is already open, otherwise adds it as a tab in the window
    // last used.
    public static void OpenFile(string path)
    {
        if (FindOpenTab(path) is { } open)
        {
            open.Window.ShowTab(open.Tab.Path);
            return;
        }
        var w = Active ?? new MainWindow();
        if (w.RootFrame.Content is EditorPage editor) _ = editor.OpenInNewTabAsync(path);
        else w.NavigateTo<EditorPage>(new EditorTabRequest(path), new SuppressNavigationTransitionInfo());
        w.BringToFront();
    }

    // Brings this window forward and shows the tab holding `path`.
    internal void ShowTab(string path)
    {
        if (RootFrame.Content is EditorPage editor) _ = editor.SwitchToPathAsync(path);
        else NavigateTo<EditorPage>(new EditorTabRequest(path), new SuppressNavigationTransitionInfo());
        BringToFront();
    }

    public void BringToFront()
    {
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } op) op.Restore();
        Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    // Places the window with its top-left corner near the mouse pointer (a tab
    // dragged out lands where it was dropped), at the given size.
    internal void PlaceAtCursor(Windows.Graphics.SizeInt32 size)
    {
        if (size.Width > 200 && size.Height > 200) _appWindow.Resize(size);
        if (GetCursorPos(out var p)) _appWindow.Move(new Windows.Graphics.PointInt32(p.X - 120, p.Y - 16));
    }

    internal Windows.Graphics.SizeInt32 Size => _appWindow.Size;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

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

    // Navigates the window `owner` sits in.
    public static void Navigate<T>(UIElement owner, object? parameter = null, NavigationTransitionInfo? transition = null)
        where T : Microsoft.UI.Xaml.Controls.Page
        => Of(owner)?.NavigateTo<T>(parameter, transition);

    public void NavigateTo<T>(object? parameter = null, NavigationTransitionInfo? transition = null)
        where T : Microsoft.UI.Xaml.Controls.Page
    {
        // A note that is already open in another window: go there instead of
        // opening a second, separately edited copy.
        if (typeof(T) == typeof(EditorPage) && EditorPage.PathOf(parameter) is { } path
            && FindOpenTab(path) is { } open && !ReferenceEquals(open.Window, this))
        {
            open.Window.ShowTab(open.Tab.Path);
            return;
        }
        _navTheme.DefaultNavigationTransitionInfo = transition ?? new EntranceNavigationTransitionInfo();
        _transitionStack.Push(transition);
        RootFrame.Navigate(typeof(T), parameter);
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
        if (Of(element) is MainWindow mw) mw.SetTitleBar(element);
    }

    // Carves interactive rects out of the SetTitleBar drag region so they get
    // clicks (e.g. the editor's click-to-rename title). Rects are in physical
    // pixels relative to the window's client area; none clears them. Uses the
    // AppWindow resolved from this window's HWND in the constructor: going
    // through App.MainWindow.AppWindow instead threw a NullReferenceException
    // on every editor open, leaving the title unclickable.
    public static void SetTitleBarPassthrough(UIElement owner, params Windows.Graphics.RectInt32[] rects)
    {
        if (Of(owner) is not MainWindow mw) return;
        var src = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(mw._appWindow.Id)
                  ?? throw new InvalidOperationException(
                         $"InputNonClientPointerSource.GetForWindowId returned null for window {mw._appWindow.Id.Value}");
        src.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, rects);
    }

    // Presentation mode: full screen hides the taskbar and caption buttons;
    // switching back to the overlapped presenter restores the previous size
    // and maximized state.
    public static void SetFullScreen(UIElement owner, bool on)
    {
        if (Of(owner) is not MainWindow mw) return;
        mw._appWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    }

    // Width in DIPs the caption buttons occupy at the window's top-right. Pages
    // pad their top bars by this so content doesn't sit under min/max/close.
    public static double CaptionButtonInset(UIElement owner)
    {
        if (Of(owner) is not MainWindow mw) return 138;
        var tb = mw._appWindow.TitleBar;
        double scale = mw.RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (tb is null || tb.RightInset <= 0 || scale <= 0) return 138;
        return tb.RightInset / scale;
    }

    public static void GoBack(UIElement owner)
    {
        if (Of(owner) is MainWindow mw)
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
