using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace YuNotes.Services;

// Self-update from GitHub Releases.
//
// CI (.github/workflows/release.yml) publishes every build of main as a release
// tagged v<version> with the signed YuNotes_<version>_x64.msix attached. This
// service asks GitHub for the latest release, and when it is newer than the
// installed package it downloads the .msix and hands it to Windows with
// "defer registration while in use": the update is staged now and Windows
// swaps it in the next time YuNotes is closed, so open notes are never
// interrupted.
//
// Store installs are left alone (the Store updates them), as are unpackaged
// dev runs (no package identity to update).
public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Zorkai/YuNotes/releases/latest";

    public enum State { Idle, Checking, UpToDate, Downloading, Staging, Ready, Failed, Unsupported }

    public static State Current { get; private set; } = State.Idle;
    public static string StatusText { get; private set; } = "";
    public static Version? AvailableVersion { get; private set; }
    public static DateTime? LastChecked { get; private set; }   // when GitHub last answered

    // Progress of the current Downloading / Staging step, 0–100.
    public static double Progress { get; private set; }
    public static long BytesReceived { get; private set; }
    public static long? BytesTotal { get; private set; }     // null when the server sends no length
    public static double BytesPerSecond { get; private set; }

    // Raised on the thread that started the check (the UI thread) whenever the
    // state, status text or progress changes. Progress raises are throttled.
    public static event Action? StatusChanged;

    private static bool _busy;
    private static SynchronizationContext? _ui;

    public static Version? InstalledVersion
    {
        get
        {
            try
            {
                var v = Package.Current.Id.Version;
                return new Version(v.Major, v.Minor, v.Build, v.Revision);
            }
            catch { return null; }   // unpackaged dev run
        }
    }

    // Packaged, sideloaded (not Store) installs can update themselves.
    public static bool IsSupported
    {
        get
        {
            try { return Package.Current.SignatureKind != PackageSignatureKind.Store; }
            catch { return false; }
        }
    }

    // Startup check: waits a little so it never competes with app launch,
    // then checks and installs if the user has automatic updates on.
    public static async Task CheckOnStartupAsync()
    {
        if (!App.Services.Settings.Current.AutoUpdateEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(8));
        await CheckAndInstallAsync();
    }

    public static async Task CheckAndInstallAsync()
    {
        if (_busy || Current == State.Ready) return;
        if (!IsSupported || InstalledVersion is not { } installed)
        {
            Set(State.Unsupported, "Updates are handled by the Microsoft Store for this install.");
            return;
        }

        _busy = true;
        _ui = SynchronizationContext.Current;
        try
        {
            Set(State.Checking, "Checking for updates…");
            var release = await GetLatestReleaseAsync();
            LastChecked = DateTime.Now;
            if (release is null)
            {
                Set(State.UpToDate, $"You're on the latest version ({Short(installed)}).");
                return;
            }
            var (version, msixUrl) = release.Value;
            if (version <= installed)
            {
                Set(State.UpToDate, $"You're on the latest version ({Short(installed)}).");
                return;
            }

            AvailableVersion = version;
            Set(State.Downloading, $"Downloading YuNotes {Short(version)}…");
            var file = await DownloadAsync(msixUrl, version);

            // Stage the update; Windows registers it once YuNotes closes.
            var pm = new PackageManager();
            var options = new AddPackageOptions { DeferRegistrationWhenPackagesAreInUse = true };
            Set(State.Staging, $"Preparing YuNotes {Short(version)}…", 0);
            var op = pm.AddPackageByUriAsync(new Uri(file), options);
            // Deployment progress arrives on a background thread.
            // A late report must not pull the state back from Ready/Failed.
            op.Progress = (_, p) => Post(() =>
            {
                if (Current == State.Staging) Set(State.Staging, StatusText, p.percentage);
            });
            var result = await op;
            if (result.ExtendedErrorCode is { } err)
                throw new InvalidOperationException(result.ErrorText, err);

            Set(State.Ready, $"YuNotes {Short(version)} is ready. It installs when you close YuNotes.");
        }
        catch (Exception ex)
        {
            App.LogError(ex, "UpdateService.CheckAndInstallAsync");
            Set(State.Failed, "Couldn't check for updates. Try again later.");
        }
        finally { _busy = false; }
    }

    // Latest release's version (from its v<version> tag) and x64 .msix URL,
    // or null when there is no usable release yet.
    private static async Task<(Version, string)?> GetLatestReleaseAsync()
    {
        using var http = NewClient();
        using var resp = await http.GetAsync(LatestReleaseUrl);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no releases yet
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor,
                              Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

        var url = root.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "",
                          Url:  a.GetProperty("browser_download_url").GetString() ?? ""))
            .FirstOrDefault(a => a.Name.EndsWith("_x64.msix", StringComparison.OrdinalIgnoreCase)).Url;
        return string.IsNullOrEmpty(url) ? null : (version, url);
    }

    private static async Task<string> DownloadAsync(string url, Version version)
    {
        var dir = Path.Combine(Path.GetTempPath(), "YuNotesUpdate");
        Directory.CreateDirectory(dir);
        // Old downloads are useless once a newer one exists.
        foreach (var old in Directory.GetFiles(dir, "*.msix"))
            try { File.Delete(old); } catch { }

        var path = Path.Combine(dir, $"YuNotes_{version}_x64.msix");
        using var http = NewClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        BytesTotal = total; BytesReceived = 0; BytesPerSecond = 0; Progress = 0;
        StatusChanged?.Invoke();

        // Copy in chunks off the UI thread, reporting at most ~10 times a second.
        var clock = Stopwatch.StartNew();
        long lastReportMs = 0, lastBytes = 0, got = 0;
        double rate = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var fs = File.Create(path))
        {
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                got += n;
                long now = clock.ElapsedMilliseconds;
                if (now - lastReportMs >= 100)
                {
                    double inst = (got - lastBytes) * 1000.0 / Math.Max(1, now - lastReportMs);
                    rate = rate == 0 ? inst : rate * 0.7 + inst * 0.3;   // smooth the speed readout
                    lastReportMs = now; lastBytes = got;
                    ReportDownload(got, total, rate);
                }
            }
        }
        ReportDownload(got, total ?? got, rate);
        return path;
    }

    private static void ReportDownload(long got, long? total, double rate) => Post(() =>
    {
        BytesReceived = got;
        BytesTotal = total;
        BytesPerSecond = rate;
        Progress = total is > 0 ? Math.Min(100, got * 100.0 / total.Value) : 0;
        StatusChanged?.Invoke();
    });

    private static void Post(Action a)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) a();
        else _ui.Post(_ => a(), null);
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient();
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"YuNotes-Updater/{InstalledVersion?.ToString() ?? "dev"}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static string Short(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    private static void Set(State state, string text, double? progress = null)
    {
        Current = state;
        StatusText = text;
        if (progress is { } p) Progress = p;
        else if (state != State.Downloading) Progress = state == State.Ready ? 100 : 0;
        StatusChanged?.Invoke();
    }
}
