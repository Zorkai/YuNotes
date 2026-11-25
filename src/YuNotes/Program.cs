using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace YuNotes;

// Single instance: the first YuNotes registers itself; any later launch
// (opening a .pdf / .yunote from Explorer, or starting YuNotes again) hands its
// activation to that one and exits. The running YuNotes then opens the file as
// a tab, or opens a new window for a plain launch (see App.OnRedirected).
public static class Program
{
    private const string InstanceKey = "YuNotes.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (TryRedirectToRunningInstance())
        {
            // Exit hard: a redirect that never completes leaves a WinRT call
            // pending, and a process left behind keeps the package "in use",
            // which blocks a staged update from installing.
            Environment.Exit(0);
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    // True when another YuNotes is running and has taken this activation.
    private static bool TryRedirectToRunningInstance()
    {
        AppInstance key;
        AppActivationArguments args;
        try
        {
            args = AppInstance.GetCurrent().GetActivatedEventArgs();
            key = AppInstance.FindOrRegisterForKey(InstanceKey);
        }
        catch { return false; }   // can't tell: just run normally
        if (key.IsCurrent) return false;

        try
        {
            // Let the running instance bring its window to the front.
            AllowSetForegroundWindow(key.ProcessId);
            // Redirecting is async and this is an STA thread: wait on a
            // separate thread while still pumping COM calls here.
            var done = CreateEvent(IntPtr.Zero, true, false, null);
            Task.Run(() =>
            {
                try { key.RedirectActivationToAsync(args).AsTask().Wait(); }
                catch { }
                SetEvent(done);
            });
            // Bounded wait. The running instance can be closing, or stuck, and
            // then the redirect never completes; waiting forever left windowless
            // YuNotes processes behind that kept the package "in use", so
            // Windows never applied a staged update. On timeout, run as our own
            // instance instead: the user still gets a window.
            const uint CWMO_DEFAULT = 0, RedirectTimeoutMs = 8000, RPC_S_CALLPENDING = 0x80010115;
            uint hr = CoWaitForMultipleObjects(CWMO_DEFAULT, RedirectTimeoutMs, 1, new[] { done }, out _);
            if (hr == RPC_S_CALLPENDING) return false;   // keep `done` open: the task may still set it
            CloseHandle(done);
            return true;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
