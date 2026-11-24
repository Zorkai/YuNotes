using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using YuNotes.Services;

namespace YuNotes;

public partial class App : Application
{
    // The window the user last worked in (YuNotes can have several).
    public static Window? MainWindow => YuNotes.MainWindow.Active;
    private static Microsoft.UI.Dispatching.DispatcherQueue? s_ui;
    public static ServiceContainer Services { get; } = new();

    public static string CrashLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YuNotes", "crash.log");

    // WinUI 3's Application.UnhandledException frequently delivers a COMException with its
    // stack stripped, making crash logs useless. We snapshot the throwing stack at first-chance.
    private static string? _lastUnexpectedComStack;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (e.Exception is COMException com && com.HResult == unchecked((int)0x8000FFFF))
        {
            try { _lastUnexpectedComStack = com.ToString(); } catch { }
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Surface XAML load failures (missing StaticResource/ThemeResource keys, bad bindings)
        // that otherwise crash as a stackless COMException with no managed throw to catch.
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.IsBindingTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, ev) => Append($"[XamlResourceReferenceFailed] {ev.Message}");
        DebugSettings.BindingFailed += (_, ev) => Append($"[BindingFailed] {ev.Message}");

        Services.Initialize();
        s_ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var first = new MainWindow();
        first.Activate();

        // If Explorer launched us via a file association (.pdf / .yunote), open
        // that file in the editor on top of the home page.
        try
        {
            var path = FileFromActivation(Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs())
                       ?? FileFromCommandLine(Environment.GetCommandLineArgs(), skipFirst: true);
            if (path is not null) YuNotes.MainWindow.OpenFile(path);
        }
        catch (Exception ex) { LogError(ex, "OnLaunched file activation"); }

        // Later launches hand their activation to this instance (see Program).
        Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, a) =>
            s_ui?.TryEnqueue(() => OnRedirected(a));

        // Background update check (no-op when turned off in Settings).
        _ = YuNotes.Services.UpdateService.CheckOnStartupAsync();
    }

    // Another launch of YuNotes: open its file as a tab in the window last
    // used, or, for a plain launch, a new window (like a browser does).
    private static void OnRedirected(Microsoft.Windows.AppLifecycle.AppActivationArguments args)
    {
        try
        {
            var path = FileFromActivation(args);
            if (path is not null) { YuNotes.MainWindow.OpenFile(path); return; }
            var w = new MainWindow();
            w.Activate();
            w.BringToFront();
        }
        catch (Exception ex) { LogError(ex, "Redirected activation"); }
    }

    // When the app is started by opening an associated file, packaged YuNotes
    // gets the file in File activation args; unpackaged YuNotes gets it on the
    // command line (Launch activation).
    private static string? FileFromActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments? activation)
    {
        if (activation is null) return null;
        if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File
            && activation.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs
            && fileArgs.Files.Count > 0
            && !string.IsNullOrEmpty(fileArgs.Files[0].Path))
            return fileArgs.Files[0].Path;
        if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch
            && activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            && !string.IsNullOrWhiteSpace(launch.Arguments))
            return FileFromCommandLine(SplitCommandLine(launch.Arguments), skipFirst: false);
        return null;
    }

    private static string? FileFromCommandLine(System.Collections.Generic.IEnumerable<string> args, bool skipFirst)
    {
        foreach (var a in skipFirst ? System.Linq.Enumerable.Skip(args, 1) : args)
        {
            var p = a.Trim().Trim('"');
            if ((p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yunote", StringComparison.OrdinalIgnoreCase))
                && File.Exists(p))
                return Path.GetFullPath(p);
        }
        return null;
    }

    // Splits a command line into arguments, keeping quoted paths with spaces whole.
    private static System.Collections.Generic.List<string> SplitCommandLine(string line)
    {
        var result = new System.Collections.Generic.List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    private static void Append(string detail)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"── {DateTime.Now:yyyy-MM-dd HH:mm:ss} ──────────────────────────────────────\n{detail}\n\n");
        }
        catch { }
    }

    private static string Describe(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            sb.AppendLine($"{cur.GetType().FullName}: {cur.Message} (HRESULT 0x{cur.HResult:X8})");
            if (!string.IsNullOrEmpty(cur.StackTrace)) sb.AppendLine(cur.StackTrace);
            if (cur.InnerException is not null) sb.AppendLine("  --- inner exception ---");
        }
        return sb.ToString();
    }

    private static void WriteCrashLog(Exception ex, string? context = null)
    {
        var detail = Describe(ex);
        if (!string.IsNullOrEmpty(context)) detail = $"context: {context}\n{detail}";
        // If WinUI handed us a stackless exception, attach the stack we captured at first-chance.
        if (string.IsNullOrEmpty(ex.StackTrace) && _lastUnexpectedComStack is { } s)
            detail += $"\n[recovered first-chance stack]\n{s}";
        Append(detail);
    }

    /// <summary>Records a handled error (e.g. Frame.NavigationFailed) with full detail to the crash log.</summary>
    internal static void LogError(Exception ex, string context) => WriteCrashLog(ex, context);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, e.Message);
        MessageBox(IntPtr.Zero,
            $"YuNotes encountered an unexpected error and will close.\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"Full crash log saved to:\n{CrashLogPath}",
            "YuNotes — Crash", 0x10 /* MB_ICONERROR */);
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) WriteCrashLog(ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.SetObserved();
    }
}
