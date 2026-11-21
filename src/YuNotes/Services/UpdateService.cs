using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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

    public enum State { Idle, Checking, UpToDate, Downloading, Ready, Failed, Unsupported }

    public static State Current { get; private set; } = State.Idle;
    public static string StatusText { get; private set; } = "";
    public static Version? AvailableVersion { get; private set; }

    // Raised on the calling thread whenever State/StatusText change.
    public static event Action? StatusChanged;

    private static bool _busy;

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
        try
        {
            Set(State.Checking, "Checking for updates…");
            var release = await GetLatestReleaseAsync();
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
            var result = await pm.AddPackageByUriAsync(new Uri(file), options);
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
        await using (var fs = File.Create(path))
            await resp.Content.CopyToAsync(fs);
        return path;
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

    private static void Set(State state, string text)
    {
        Current = state;
        StatusText = text;
        StatusChanged?.Invoke();
    }
}
