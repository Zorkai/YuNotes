using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using YuNotes.Controls;
using YuNotes.Input;
using YuNotes.Models;

namespace YuNotes.Views;

public sealed partial class SettingsPage
{

    // ─── Updates ────────────────────────────────────────────────────────────
    // Ring shows the current step's progress; the four-step track shows where
    // in Check → Download → Prepare → Ready the update is. The header chip
    // shows the same state in a few words.
    private void ShowUpdateStatus()
    {
        var state = Services.UpdateService.Current;
        var avail = Services.UpdateService.AvailableVersion;
        var installed = Services.UpdateService.InstalledVersion;
        double p = Services.UpdateService.Progress;
        var res = Application.Current.Resources;
        var accent = (Brush)res["AppAccentBrush"];
        var ok = (Brush)res["AppSuccessBrush"];
        var muted = (Brush)res["AppTextSecondaryBrush"];
        var primary = (Brush)res["AppTextPrimaryBrush"];

        bool busy = state is Services.UpdateService.State.Checking
                          or Services.UpdateService.State.Downloading
                          or Services.UpdateService.State.Staging;
        bool good = state is Services.UpdateService.State.Ready or Services.UpdateService.State.UpToDate;

        UpdateStatusText.Text = state == Services.UpdateService.State.Idle
            ? "YuNotes checks GitHub for a newer version when it starts."
            : Services.UpdateService.StatusText;
        UpdateTitleText.Text = state switch
        {
            Services.UpdateService.State.Downloading or Services.UpdateService.State.Staging
                when avail is not null => $"Updating to YuNotes {Short(avail)}",
            Services.UpdateService.State.Ready when avail is not null => $"YuNotes {Short(avail)} is ready",
            Services.UpdateService.State.UpToDate => "You're up to date",
            Services.UpdateService.State.Failed => "Update check failed",
            _ => "Updates",
        };

        bool determinate = state is Services.UpdateService.State.Downloading or Services.UpdateService.State.Staging;
        UpdateRing.IsIndeterminate = state == Services.UpdateService.State.Checking;
        UpdateRing.Value = determinate ? p : good ? 100 : 0;
        UpdateRing.Foreground = good ? ok : accent;
        UpdatePercentText.Text = $"{Math.Round(p)}%";
        UpdatePercentText.Visibility = determinate ? Visibility.Visible : Visibility.Collapsed;
        UpdateStateIcon.Visibility = determinate || state == Services.UpdateService.State.Checking
            ? Visibility.Collapsed : Visibility.Visible;
        string glyph = good ? "" : state == Services.UpdateService.State.Failed ? "" : "";
        UpdateStateIcon.Glyph = glyph;
        UpdateStateIcon.Foreground = good ? ok : state == Services.UpdateService.State.Failed ? muted : accent;

        int step = state switch
        {
            Services.UpdateService.State.Checking => 0,
            Services.UpdateService.State.Downloading => 1,
            Services.UpdateService.State.Staging => 2,
            Services.UpdateService.State.Ready => 4,
            _ => -1,
        };
        UpdateSteps.Visibility = step >= 0 ? Visibility.Visible : Visibility.Collapsed;
        var bars = new[] { StepCheckBar, StepDownloadBar, StepStageBar, StepReadyBar };
        var labels = new[] { StepCheckLabel, StepDownloadLabel, StepStageLabel, StepReadyLabel };
        for (int i = 0; i < bars.Length; i++)
        {
            bool done = i < step || step == 4;
            bool cur = i == step;
            bars[i].IsIndeterminate = cur && state == Services.UpdateService.State.Checking;
            bars[i].Value = done ? 100 : cur ? p : 0;
            bars[i].Foreground = step == 4 ? ok : accent;
            labels[i].Foreground = done || cur ? primary : muted;
            labels[i].FontWeight = cur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        if (state == Services.UpdateService.State.Downloading)
        {
            const double MB = 1024 * 1024;
            double got = Services.UpdateService.BytesReceived / MB;
            double rate = Services.UpdateService.BytesPerSecond / MB;
            var parts = new List<string>();
            parts.Add(Services.UpdateService.BytesTotal is { } total
                ? $"{got:0.0} / {total / MB:0.0} MB" : $"{got:0.0} MB");
            if (rate > 0.01) parts.Add($"{rate:0.0} MB/s");
            if (rate > 0.01 && Services.UpdateService.BytesTotal is { } t2)
                parts.Add($"{Math.Max(0, Math.Ceiling((t2 / MB - got) / rate))} s left");
            UpdateMetaText.Text = string.Join("  ·  ", parts);
            UpdateMetaText.Visibility = Visibility.Visible;
        }
        else UpdateMetaText.Visibility = Visibility.Collapsed;

        CheckUpdatesBtn.IsEnabled = Services.UpdateService.IsSupported && !busy
                                    && state != Services.UpdateService.State.Ready;
        string checkedText = Services.UpdateService.LastChecked is { } at ? $" · checked {Ago(at)}" : "";
        AppVersionText.Text = installed is null ? "Development build"
            : Services.UpdateService.IsSupported ? $"Installed version {Short(installed)}{checkedText}"
            : $"Installed version {Short(installed)} · updates come from the Microsoft Store";
        CheckUpdatesBtn.Visibility = state == Services.UpdateService.State.Ready ? Visibility.Collapsed : Visibility.Visible;
        CheckUpdatesBtn.Content = state == Services.UpdateService.State.Failed ? "Try again" : "Check for updates";

        UpdateChipText.Text = state switch
        {
            Services.UpdateService.State.Checking => "Checking…",
            Services.UpdateService.State.Downloading => $"Downloading {Math.Round(p)}%",
            Services.UpdateService.State.Staging => "Preparing update",
            Services.UpdateService.State.Ready => "Update ready",
            Services.UpdateService.State.UpToDate => "Up to date",
            Services.UpdateService.State.Failed => "Update failed",
            _ => "Updates",
        };
        UpdateChipIcon.Glyph = glyph;
        UpdateChipIcon.Foreground = good ? ok : state == Services.UpdateService.State.Failed ? muted : accent;
        NavDotUpdates.Visibility = state == Services.UpdateService.State.Ready || RowIsModified("autoupdate")
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Short(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

    private static string Ago(DateTime at)
    {
        var d = DateTime.Now - at;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        return at.ToString("d MMM");
    }
}
