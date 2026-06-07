using System.Globalization;
using Aria2Gui.Services;
using Aria2Gui.Services.Aria2;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Aria2Gui.Views;

/// <summary>
/// Settings as an in-app page (WinUI Gallery style) rather than a dialog. The host
/// shows/hides it; <see cref="Closed"/> fires when the user goes back or saves.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    /// <summary>Raised when the page should be dismissed (back or save completed).</summary>
    public event EventHandler? Closed;

    public SettingsView()
    {
        InitializeComponent();
        LoadFromSettings();
    }

    /// <summary>Reloads the form from the current settings (call each time it opens).</summary>
    public void LoadFromSettings()
    {
        var s = Aria2Service.Instance.Settings;
        DirText.Text = s.DownloadDirectory;
        DownLimitBox.Value = SpeedToMegabytes(s.MaxDownloadLimit);
        UpLimitBox.Value = SpeedToMegabytes(s.MaxUploadLimit);
        ConcurrentBox.Value = s.MaxConcurrentDownloads;
        ConnectionsBox.Value = s.MaxConnectionsPerServer;
        ThemeBox.SelectedIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };

        ProxyBox.Text = s.AllProxy;
        CheckCertToggle.IsOn = s.CheckCertificate;
        TimeoutBox.Value = s.Timeout;
        ConnectTimeoutBox.Value = s.ConnectTimeout;
        MaxTriesBox.Value = s.MaxTries;
        RetryWaitBox.Value = s.RetryWait;
        MinSplitSizeBox.Text = s.MinSplitSize;
        UserAgentBox.Text = s.UserAgent;

        FileAllocBox.SelectedIndex = s.FileAllocation switch { "none" => 1, "prealloc" => 2, "falloc" => 3, _ => 0 };
        AllowOverwriteToggle.IsOn = s.AllowOverwrite;
        AutoRenameToggle.IsOn = s.AutoFileRenaming;

        ListenPortBox.Value = s.ListenPort;
        PeersBox.Value = s.BtMaxPeers;
        BtMaxOpenFilesBox.Value = s.BtMaxOpenFiles;
        SeedRatioBox.Value = s.SeedRatio;
        DhtToggle.IsOn = s.EnableDht;
        PexToggle.IsOn = s.EnablePex;
        LpdToggle.IsOn = s.EnableLpd;
        CryptoToggle.IsOn = s.RequireCrypto;
        CryptoLevelRadio.SelectedIndex = s.BtMinCryptoLevel == "arc4" ? 1 : 0;
        CryptoLevelCard.Visibility = s.RequireCrypto ? Visibility.Visible : Visibility.Collapsed;
        TrackersBox.Text = s.ExtraTrackers;
        ExtraOptionsBox.Text = s.ExtraAria2Options;
        ErrorBar.IsOpen = false;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows only the selected section's panel (WinUI NavigationView style).</summary>
    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
            return;
        GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPanel.Visibility = tag == "connection" ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = tag == "files" ? Visibility.Visible : Visibility.Collapsed;
        BtPanel.Visibility = tag == "bt" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPanel.Visibility = tag == "advanced" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The encryption-level choice only matters when crypto is required.</summary>
    private void OnCryptoToggled(object sender, RoutedEventArgs e)
    {
        if (CryptoLevelCard is not null)
            CryptoLevelCard.Visibility = CryptoToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
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
            ShowError($"Не удалось открыть выбор папки: {ex.Message}");
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
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

                AllProxy = ProxyBox.Text,
                CheckCertificate = CheckCertToggle.IsOn,
                Timeout = (int)SafeValue(TimeoutBox.Value, 60),
                ConnectTimeout = (int)SafeValue(ConnectTimeoutBox.Value, 60),
                MaxTries = (int)SafeValue(MaxTriesBox.Value, 5),
                RetryWait = (int)SafeValue(RetryWaitBox.Value, 0),
                MinSplitSize = MinSplitSizeBox.Text,
                UserAgent = UserAgentBox.Text,
                FileAllocation = (FileAllocBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto",
                AllowOverwrite = AllowOverwriteToggle.IsOn,
                AutoFileRenaming = AutoRenameToggle.IsOn,

                ListenPort = (int)SafeValue(ListenPortBox.Value, 0),
                BtMaxPeers = (int)SafeValue(PeersBox.Value, 55),
                BtMaxOpenFiles = (int)SafeValue(BtMaxOpenFilesBox.Value, 100),
                SeedRatio = SafeValue(SeedRatioBox.Value, 1.0),
                EnableDht = DhtToggle.IsOn,
                EnablePex = PexToggle.IsOn,
                EnableLpd = LpdToggle.IsOn,
                RequireCrypto = CryptoToggle.IsOn,
                BtMinCryptoLevel = CryptoLevelRadio.SelectedIndex == 1 ? "arc4" : "plain",
                ExtraTrackers = TrackersBox.Text,
                ExtraAria2Options = ExtraOptionsBox.Text,
            };

            bool needsRestart = NeedsEngineRestart(old, s);
            await Aria2Service.Instance.ApplySettingsAsync(s);
            Helpers.ThemeHelper.Apply(s.Theme);
            if (needsRestart)
                await Aria2Service.Instance.RestartEngineAsync();
            Closed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось сохранить: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    /// <summary>These options only exist as aria2c command-line flags.</summary>
    private static bool NeedsEngineRestart(AppSettings old, AppSettings updated) =>
        old.ListenPort != updated.ListenPort
        || old.EnableDht != updated.EnableDht
        || old.EnablePex != updated.EnablePex
        || old.EnableLpd != updated.EnableLpd
        || old.RequireCrypto != updated.RequireCrypto
        || old.BtMinCryptoLevel != updated.BtMinCryptoLevel
        || old.ExtraTrackers != updated.ExtraTrackers
        || old.ExtraAria2Options != updated.ExtraAria2Options;

    private static double SafeValue(double value, double fallback) =>
        double.IsNaN(value) ? fallback : value;

    private static double SpeedToMegabytes(string aria2Speed)
    {
        if (string.IsNullOrWhiteSpace(aria2Speed))
            return 0;
        string trimmed = aria2Speed.Trim();
        double multiplier = 1;
        char last = char.ToUpperInvariant(trimmed[^1]);
        if (last == 'K') { multiplier = 1024; trimmed = trimmed[..^1]; }
        else if (last == 'M') { multiplier = 1024 * 1024; trimmed = trimmed[..^1]; }
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Round(value * multiplier / (1024 * 1024), 4)
            : 0;
    }

    private static string MegabytesToSpeed(double megabytes)
    {
        if (double.IsNaN(megabytes) || megabytes <= 0)
            return "0";
        long kib = Math.Max(1, (long)Math.Round(megabytes * 1024));
        return kib.ToString(CultureInfo.InvariantCulture) + "K";
    }
}
