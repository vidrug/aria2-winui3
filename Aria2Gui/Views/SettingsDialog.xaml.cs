using System.Globalization;
using Aria2Gui.Services;
using Aria2Gui.Services.Aria2;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Aria2Gui.Views;

/// <summary>
/// Settings form with two tabs: general limits and BitTorrent engine flags.
/// Speed limits are edited in MB/s and stored in aria2's "NNNK" format.
/// BT-tab changes require an engine restart (command-line flags) — done gracefully
/// with session save, so unfinished downloads resume.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog()
    {
        InitializeComponent();

        var s = Aria2Service.Instance.Settings;
        DirText.Text = s.DownloadDirectory;
        DownLimitBox.Value = SpeedToMegabytes(s.MaxDownloadLimit);
        UpLimitBox.Value = SpeedToMegabytes(s.MaxUploadLimit);
        ConcurrentBox.Value = s.MaxConcurrentDownloads;
        ConnectionsBox.Value = s.MaxConnectionsPerServer;
        ThemeBox.SelectedIndex = s.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };

        ListenPortBox.Value = s.ListenPort;
        PeersBox.Value = s.BtMaxPeers;
        SeedRatioBox.Value = s.SeedRatio;
        DhtToggle.IsOn = s.EnableDht;
        PexToggle.IsOn = s.EnablePex;
        LpdToggle.IsOn = s.EnableLpd;
        CryptoToggle.IsOn = s.RequireCrypto;
        TrackersBox.Text = s.ExtraTrackers;
        ExtraOptionsBox.Text = s.ExtraAria2Options;
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                DirText.Text = folder.Path;
        }
        catch (Exception ex)
        {
            // async void handler — an escaped exception would kill the process.
            ErrorBar.Message = $"Не удалось открыть выбор папки: {ex.Message}";
            ErrorBar.IsOpen = true;
        }
    }

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var old = Aria2Service.Instance.Settings;
            var s = new AppSettings
            {
                DownloadDirectory = DirText.Text,
                MaxDownloadLimit = MegabytesToSpeed(DownLimitBox.Value),
                MaxUploadLimit = MegabytesToSpeed(UpLimitBox.Value),
                MaxConcurrentDownloads = (int)SafeValue(ConcurrentBox.Value, 5),
                MaxConnectionsPerServer = (int)SafeValue(ConnectionsBox.Value, 8),
                Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default",
                LastAddDirectory = old.LastAddDirectory,

                ListenPort = (int)SafeValue(ListenPortBox.Value, 0),
                BtMaxPeers = (int)SafeValue(PeersBox.Value, 55),
                SeedRatio = SafeValue(SeedRatioBox.Value, 1.0),
                EnableDht = DhtToggle.IsOn,
                EnablePex = PexToggle.IsOn,
                EnableLpd = LpdToggle.IsOn,
                RequireCrypto = CryptoToggle.IsOn,
                ExtraTrackers = TrackersBox.Text,
                ExtraAria2Options = ExtraOptionsBox.Text,
            };

            bool needsRestart = NeedsEngineRestart(old, s);
            await Aria2Service.Instance.ApplySettingsAsync(s);
            Helpers.ThemeHelper.Apply(s.Theme);
            if (needsRestart)
                await Aria2Service.Instance.RestartEngineAsync();
        }
        catch (Exception ex)
        {
            // Dialog-local catch-all: an escaped exception here would close the
            // dialog silently and could take the process down (async void).
            ErrorBar.Message = $"Не удалось сохранить: {ex.Message}";
            ErrorBar.IsOpen = true;
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>These options only exist as aria2c command-line flags.</summary>
    private static bool NeedsEngineRestart(AppSettings old, AppSettings updated) =>
        old.ListenPort != updated.ListenPort
        || old.EnableDht != updated.EnableDht
        || old.EnablePex != updated.EnablePex
        || old.EnableLpd != updated.EnableLpd
        || old.RequireCrypto != updated.RequireCrypto
        || old.ExtraTrackers != updated.ExtraTrackers
        || old.ExtraAria2Options != updated.ExtraAria2Options;

    /// <summary>NumberBox yields NaN when cleared.</summary>
    private static double SafeValue(double value, double fallback) =>
        double.IsNaN(value) ? fallback : value;

    /// <summary>"5120K" / "5M" / "0" → MB/s.</summary>
    private static double SpeedToMegabytes(string aria2Speed)
    {
        if (string.IsNullOrWhiteSpace(aria2Speed))
            return 0;
        string trimmed = aria2Speed.Trim();
        double multiplier = 1;
        char last = char.ToUpperInvariant(trimmed[^1]);
        if (last == 'K')
        {
            multiplier = 1024;
            trimmed = trimmed[..^1];
        }
        else if (last == 'M')
        {
            multiplier = 1024 * 1024;
            trimmed = trimmed[..^1];
        }
        // 4 decimals so small limits ("1K" = 0.001 MB/s) survive the open→save round-trip.
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Round(value * multiplier / (1024 * 1024), 4)
            : 0;
    }

    /// <summary>MB/s → aria2 format in KiB ("0" = unlimited).</summary>
    private static string MegabytesToSpeed(double megabytes)
    {
        if (double.IsNaN(megabytes) || megabytes <= 0)
            return "0";
        // A nonzero limit must never round down to "0" (= unlimited in aria2).
        long kib = Math.Max(1, (long)Math.Round(megabytes * 1024));
        return kib.ToString(CultureInfo.InvariantCulture) + "K";
    }
}
