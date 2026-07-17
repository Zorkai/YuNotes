using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using YuNotes.Services;

namespace YuNotes;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
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
        MainWindow = new MainWindow();
        MainWindow.Activate();

        // If Explorer launched us via a file association (.pdf / .yunote), open
        // that file in the editor on top of the home page.
        TryOpenActivationFile();
    }

    // When the app is started by opening an associated file, Windows delivers the
    // file through the AppLifecycle activation args (not OnLaunched's args). Read
    // them and route the path through the normal editor-open flow.
    private static void TryOpenActivationFile()
    {
        try
        {
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File
                && activation.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs
                && fileArgs.Files.Count > 0
                && fileArgs.Files[0] is Windows.Storage.StorageFile file
                && !string.IsNullOrEmpty(file.Path))
            {
                YuNotes.MainWindow.Navigate<Views.EditorPage>(file.Path);
            }
        }
        catch (Exception ex) { LogError(ex, "OnLaunched file activation"); }
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
