using System.Globalization;
using Aria2Gui.Services;
using Aria2Gui.Services.Aria2;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Storage.Pickers;

namespace Aria2Gui.Views;

/// <summary>
/// Settings as an in-app page (WinUI Gallery style). There is no Save button: every
/// change is applied and persisted immediately (Windows 11 Settings style). Options that
/// can only change via an aria2c restart (BT port, DHT/PEX/LPD, encryption, trackers,
/// extra flags) are persisted on the spot but the single engine restart is deferred until
/// the user leaves the page; a language change likewise defers the app relaunch to exit.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    /// <summary>Raised when the page should be dismissed (the user pressed back).</summary>
    public event EventHandler? Closed;

    /// <summary>True while the form is being populated, so change handlers don't fire.</summary>
    private bool _loading;

    /// <summary>Guards <see cref="OnBackClick"/> against re-entrancy (a fast double Back press
    /// landing inside the apply await), which would otherwise restart the engine twice.</summary>
    private bool _exiting;

    /// <summary>Settings as they were when the page opened — the baseline for deciding,
    /// on exit, whether an engine restart or app relaunch is needed. Replaced wholesale by
    /// <see cref="Aria2Service.ApplySettingsAsync"/>, so this reference stays a stable snapshot.</summary>
    private AppSettings _entrySettings = new();

    public SettingsView()
    {
        InitializeComponent();
        WireChangeHandlers();
        LoadFromSettings();
    }

    /// <summary>Subscribes every input control to the auto-apply pipeline. Text boxes apply on
    /// blur (not per keystroke); spinners/toggles/combos apply on change.</summary>
    private void WireChangeHandlers()
    {
        foreach (var nb in new[]
        {
            ConcurrentBox, ConnectionsBox, TimeoutBox, ConnectTimeoutBox,
            MaxTriesBox, RetryWaitBox, ListenPortBox, PeersBox, BtMaxOpenFilesBox, SeedRatioBox,
        })
            nb.ValueChanged += OnNumberChanged;

        foreach (var cb in new[] { ThemeBox, LanguageBox, FileAllocBox })
            cb.SelectionChanged += OnSelectionChanged;

        // Speed limits are editable combo boxes: apply when a preset is picked, when a custom
        // value is submitted (Enter), and when the edit box loses focus.
        foreach (var lb in new[] { DownLimitBox, UpLimitBox })
        {
            lb.SelectionChanged += OnSelectionChanged;
            lb.TextSubmitted += OnLimitSubmitted;
            lb.LostFocus += OnTextCommitted;
        }
        CryptoLevelRadio.SelectionChanged += OnSelectionChanged;

        // CryptoToggle keeps its own handler (OnCryptoToggled) which also applies.
        foreach (var ts in new[] { CheckCertToggle, AllowOverwriteToggle, AutoRenameToggle, DhtToggle, PexToggle, LpdToggle })
            ts.Toggled += OnToggled;

        foreach (var tb in new[] { ProxyBox, MinSplitSizeBox, UserAgentBox, TrackersBox, ExtraOptionsBox })
            tb.LostFocus += OnTextCommitted;
    }

    /// <summary>Reloads the form from the current settings (call each time it opens).</summary>
    public void LoadFromSettings()
    {
        _loading = true;
        _exiting = false;
        try
        {
            var s = Aria2Service.Instance.Settings;
            _entrySettings = s;
            DirText.Text = s.DownloadDirectory;
            SetLimitCombo(DownLimitBox, SpeedToMegabytes(s.MaxDownloadLimit));
            SetLimitCombo(UpLimitBox, SpeedToMegabytes(s.MaxUploadLimit));
            ConcurrentBox.Value = s.MaxConcurrentDownloads;
            ConnectionsBox.Value = s.MaxConnectionsPerServer;
            ThemeBox.SelectedIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string ?? "") == s.Language) ?? LanguageBox.Items[0];

            ProxyBox.Text = s.AllProxy;
            CheckCertToggle.IsOn = s.CheckCertificate;
            TimeoutBox.Value = s.Timeout;
            ConnectTimeoutBox.Value = s.ConnectTimeout;
            MaxTriesBox.Value = s.MaxTries;
            RetryWaitBox.Value = s.RetryWait;
            MinSplitSizeBox.Text = s.MinSplitSize;
            UserAgentBox.Text = s.UserAgent;

            FileAllocBox.SelectedIndex = s.FileAllocation switch { "none" => 1, "prealloc" => 2, "trunc" => 3, "falloc" => 4, _ => 0 };
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
            CryptoExpander.IsExpanded = s.RequireCrypto;
            TrackersBox.Text = s.ExtraTrackers;
            ExtraOptionsBox.Text = s.ExtraAria2Options;
            ErrorBar.IsOpen = false;
            UpdateLanguageRestartHint();
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- auto-apply pipeline ----

    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => _ = ApplyChangeAsync();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => _ = ApplyChangeAsync();
    private void OnToggled(object sender, RoutedEventArgs e) => _ = ApplyChangeAsync();
    private void OnTextCommitted(object sender, RoutedEventArgs e) => _ = ApplyChangeAsync();

    private void OnLimitSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        // Keep the user's typed custom value instead of letting the ComboBox revert it.
        args.Handled = true;
        _ = ApplyChangeAsync();
    }

    /// <summary>Applies and persists the current form state immediately (live aria2 options +
    /// theme). Restart-only options are saved here too but take effect on the engine restart
    /// performed when leaving the page.</summary>
    private async Task ApplyChangeAsync()
    {
        if (_loading)
            return;
        var s = BuildSettings();
        try
        {
            await Aria2Service.Instance.ApplySettingsAsync(s);
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError(Helpers.L.Get("SettingsErrorSave", ex.Message));
        }
        // Theme and the language-restart hint don't depend on the engine RPC, so update them
        // even if the live push failed (the setting is already persisted by ApplySettingsAsync).
        Helpers.ThemeHelper.Apply(s.Theme);
        UpdateLanguageRestartHint();
    }

    /// <summary>Back press: commit any still-focused field, then relaunch the app (language
    /// change) or restart the engine (BT/advanced changes) once. The page closes immediately;
    /// the engine restart runs in the background and surfaces via the main window's status.</summary>
    private async void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;

        // A language change is persisted but NOT auto-applied here — the user relaunches on
        // their own via the in-page hint (the UI language only resolves at startup). Only the
        // engine-restart settings are applied (in the background) on the way out.
        bool needsRestart;
        try
        {
            var s = BuildSettings();
            needsRestart = NeedsEngineRestart(_entrySettings, s);
            await Aria2Service.Instance.ApplySettingsAsync(s);
            Helpers.ThemeHelper.Apply(s.Theme);
        }
        catch (Exception ex)
        {
            // Surface the failure and stay on the page so the user can react.
            ShowError(Helpers.L.Get("SettingsErrorSave", ex.Message));
            _exiting = false;
            return;
        }

        Closed?.Invoke(this, EventArgs.Empty);

        if (needsRestart)
            _ = Aria2Service.Instance.RestartEngineAsync();
    }

    /// <summary>Shows the "restart to change language" hint when the picked language differs
    /// from the one the app is actually running in (it only resolves at startup).</summary>
    private void UpdateLanguageRestartHint()
    {
        string picked = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        LanguageRestartBar.IsOpen = picked != App.ActiveLanguage;
    }

    /// <summary>Relaunch to apply the picked UI language (already saved by auto-apply).</summary>
    private void OnRestartForLanguage(object sender, RoutedEventArgs e)
    {
        // Guarantee the picked language is on disk, then relaunch. AppInstance.Restart
        // force-terminates without raising Window.Closed, so run the graceful teardown first.
        try
        {
            SettingsService.Save(BuildSettings());
        }
        catch (Exception ex)
        {
            ShowError(Helpers.L.Get("SettingsErrorSave", ex.Message));
            return;
        }
        App.RunExitCleanup();
        Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
    }

    /// <summary>Shows only the selected section's panel (WinUI NavigationView style),
    /// with a small fade/slide-in so switching sections feels animated.</summary>
    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
            return;
        GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPanel.Visibility = tag == "connection" ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = tag == "files" ? Visibility.Visible : Visibility.Collapsed;
        BtPanel.Visibility = tag == "bt" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPanel.Visibility = tag == "advanced" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        AnimateSectionIn(tag switch
        {
            "connection" => ConnectionPanel,
            "files" => FilesPanel,
            "bt" => BtPanel,
            "advanced" => AdvancedPanel,
            "about" => AboutPanel,
            _ => GeneralPanel,
        });

        if (tag == "about")
            LoadAboutInfo();
    }

    /// <summary>Fade + slide-up entrance for the panel that just became visible.</summary>
    private static void AnimateSectionIn(FrameworkElement panel)
    {
        var translate = new TranslateTransform { Y = 16 };
        panel.RenderTransform = translate;
        panel.Opacity = 0;

        var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, panel);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(260), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, translate);
        Storyboard.SetTargetProperty(slide, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        storyboard.Begin();
    }

    private bool _aboutLoaded;

    /// <summary>Fills the About cards with the app and aria2 versions (once).</summary>
    private async void LoadAboutInfo()
    {
        if (_aboutLoaded)
            return;
        _aboutLoaded = true;
        try
        {
            AppInfoCard.Description = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        }
        catch
        {
        }
        try
        {
            if (Aria2Service.Instance.Rpc.IsConnected)
                AriaInfoCard.Description = (await Aria2Service.Instance.Rpc.GetVersionAsync()).Version;
        }
        catch
        {
        }
    }

    /// <summary>The encryption-level choice only matters when crypto is required, so the
    /// expander auto-opens to reveal it while encryption is on and collapses when off.</summary>
    private void OnCryptoToggled(object sender, RoutedEventArgs e)
    {
        CryptoExpander.IsExpanded = CryptoToggle.IsOn;
        _ = ApplyChangeAsync();
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
            {
                DirText.Text = folder.Path;
                await ApplyChangeAsync();
            }
        }
        catch (Exception ex)
        {
            ShowError(Helpers.L.Get("ErrorOpenFolderPicker", ex.Message));
        }
    }

    /// <summary>Builds an <see cref="AppSettings"/> from the current control values.</summary>
    private AppSettings BuildSettings()
    {
        var old = Aria2Service.Instance.Settings;
        return new AppSettings
        {
            DownloadDirectory = DirText.Text,
            MaxDownloadLimit = MegabytesToSpeed(ParseLimitToMegabytes(DownLimitBox.Text)),
            MaxUploadLimit = MegabytesToSpeed(ParseLimitToMegabytes(UpLimitBox.Text)),
            MaxConcurrentDownloads = (int)SafeValue(ConcurrentBox.Value, 5),
            MaxConnectionsPerServer = (int)SafeValue(ConnectionsBox.Value, 8),
            Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default",
            Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
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

            ListenPort = ClampListenPort((int)SafeValue(ListenPortBox.Value, 0)),
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

    // Matches SettingsService.Load: 0 means "let aria2 pick"; any explicit port is a
    // non-privileged 1024-65535. Clamping here keeps the live value equal to what reloads.
    private static int ClampListenPort(int port) => port == 0 ? 0 : Math.Clamp(port, 1024, 65535);

    // Preset scale (MB/s) for the editable download/upload limit combo boxes. 0 = unlimited.
    private static readonly double[] LimitPresetsMb = { 0, 1, 2, 5, 10, 20, 50, 100 };

    /// <summary>Fills an editable limit combo with the preset scale and selects the saved value.
    /// Selection (not <see cref="ComboBox.Text"/>) is used so the value shows even on first open:
    /// setting <c>Text</c> directly is silently dropped while the combo is still collapsed/unloaded
    /// and its edit box has no template, which left the field blank. A custom value outside the
    /// preset scale is appended so it too can be selected and displayed.</summary>
    private static void SetLimitCombo(ComboBox box, double megabytes)
    {
        box.Items.Clear();
        int selected = 0;
        for (int i = 0; i < LimitPresetsMb.Length; i++)
        {
            box.Items.Add(FormatLimit(LimitPresetsMb[i]));
            if (LimitPresetsMb[i] == megabytes)
                selected = i;
        }
        if (megabytes > 0 && !Array.Exists(LimitPresetsMb, p => p == megabytes))
        {
            box.Items.Add(FormatLimit(megabytes));
            selected = box.Items.Count - 1;
        }
        box.SelectedIndex = selected;
    }

    /// <summary>Formats a MB/s value for display: 0 → localized "Unlimited", else "N MB/s".</summary>
    private static string FormatLimit(double megabytes)
    {
        if (double.IsNaN(megabytes) || megabytes <= 0)
            return Helpers.L.Get("LimitUnlimited");
        return megabytes.ToString("0.##", CultureInfo.CurrentCulture) + " MB/s";
    }

    /// <summary>Parses a typed or selected limit back to MB/s. Empty or non-numeric text
    /// (e.g. the "Unlimited" preset) means no limit (0). A trailing K/G unit is honored;
    /// the default unit is MB/s, matching the card hint.</summary>
    private static double ParseLimitToMegabytes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"[0-9]+(?:[.,][0-9]+)?");
        if (!m.Success ||
            !double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return 0;
        string unit = text[(m.Index + m.Length)..].TrimStart().ToUpperInvariant();
        if (unit.StartsWith('K'))
            return n / 1024.0;     // KB/s
        if (unit.StartsWith('G'))
            return n * 1024.0;     // GB/s
        return n;                  // MB/s (default; also "M" / "MB/s")
    }

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
