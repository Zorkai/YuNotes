using System.Diagnostics;

namespace YuNotes.Services;

// Launches the Windows built-in Snipping Tool (ms-screenclip:), which copies the captured
// region to the clipboard. We intentionally don't read it back — the screenshot lives only
// on the clipboard for the user to paste wherever they want.
public sealed class ScreenshotService
{
    public void LaunchRegionCapture()
        => Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
}
