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

    /// <summary>JSON of the last successfully-applied settings, so a blur/toggle that changed
    /// nothing skips the RPC round-trip + disk write (O6).</summary>
    private string _appliedSnapshot = "";

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
            DownLimitBox, UpLimitBox,
            ConcurrentBox, ConnectionsBox, TimeoutBox, ConnectTimeoutBox,
            MaxTriesBox, RetryWaitBox, ListenPortBox, PeersBox, BtMaxOpenFilesBox,
            BtStopTimeoutBox, SeedValueBox,
        })
            nb.ValueChanged += OnNumberChanged;

        foreach (var cb in new[] { DownLimitUnitBox, UpLimitUnitBox, ThemeBox, LanguageBox, FileAllocBox, MinTlsBox })
            cb.SelectionChanged += OnSelectionChanged;
        // The seeding mode also reshapes its value card, so it gets a dedicated handler.
        SeedModeBox.SelectionChanged += OnSeedModeChanged;
        HttpPasswdBox.LostFocus += OnTextCommitted;
        ProxyPasswdBox.LostFocus += OnTextCommitted;

        CryptoLevelRadio.SelectionChanged += OnSelectionChanged;

        // CryptoToggle keeps its own handler (OnCryptoToggled) which also applies.
        foreach (var ts in new[] { CheckCertToggle, AllowOverwriteToggle, AutoRenameToggle, DhtToggle, PexToggle, LpdToggle, DisableIpv6Toggle, BtDetachSeedToggle, ForceEncryptionToggle })
            ts.Toggled += OnToggled;

        foreach (var tb in new[]
        {
            ProxyBox, MinSplitSizeBox, UserAgentBox, TrackersBox, ExtraOptionsBox,
            LowestSpeedLimitBox, DiskCacheBox, BtPeerSpeedBox, HttpUserBox, ProxyUserBox,
        })
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
            _appliedSnapshot = SettingsService.Snapshot(s);
            DirText.Text = s.DownloadDirectory;
            LoadLimit(DownLimitBox, DownLimitUnitBox, s.MaxDownloadLimit, s.MaxDownloadLimitUnit);
            LoadLimit(UpLimitBox, UpLimitUnitBox, s.MaxUploadLimit, s.MaxUploadLimitUnit);
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
            LowestSpeedLimitBox.Text = s.LowestSpeedLimit;
            HttpUserBox.Text = s.HttpUser;
            HttpPasswdBox.Password = s.HttpPasswd;
            ProxyUserBox.Text = s.AllProxyUser;
            ProxyPasswdBox.Password = s.AllProxyPasswd;
            MinTlsBox.SelectedIndex = s.MinTlsVersion switch { "TLSv1.1" => 0, "TLSv1.3" => 2, _ => 1 };
            DisableIpv6Toggle.IsOn = s.DisableIpv6;

            FileAllocBox.SelectedIndex = s.FileAllocation switch { "none" => 1, "prealloc" => 2, "trunc" => 3, "falloc" => 4, _ => 0 };
            AllowOverwriteToggle.IsOn = s.AllowOverwrite;
            AutoRenameToggle.IsOn = s.AutoFileRenaming;
            DiskCacheBox.Text = s.DiskCache;

            ListenPortBox.Value = s.ListenPort;
            PeersBox.Value = s.BtMaxPeers;
            BtMaxOpenFilesBox.Value = s.BtMaxOpenFiles;
            BtPeerSpeedBox.Text = s.BtRequestPeerSpeedLimit;
            BtStopTimeoutBox.Value = s.BtStopTimeout;
            BtDetachSeedToggle.IsOn = s.BtDetachSeedOnly;
            SeedModeBox.SelectedIndex = s.SeedMode switch { "time" => 1, "off" => 2, _ => 0 };
            ApplySeedModeUi(s.SeedMode);
            SeedValueBox.Value = s.SeedMode == "time" ? s.SeedTimeMinutes : s.SeedRatio;
            DhtToggle.IsOn = s.EnableDht;
            PexToggle.IsOn = s.EnablePex;
            LpdToggle.IsOn = s.EnableLpd;
            CryptoToggle.IsOn = s.RequireCrypto;
            CryptoLevelRadio.SelectedIndex = s.BtMinCryptoLevel == "arc4" ? 1 : 0;
            ForceEncryptionToggle.IsOn = s.BtForceEncryption;
            CryptoExpander.IsExpanded = s.RequireCrypto;
            PrivacyToggle.IsOn = s.PrivacyMode;
            _privacySnapshot = s.PrivacySnapshot;
            SetPrivacyControlsEnabled(s.PrivacyMode);
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

    /// <summary>Applies and persists the current form state immediately (live aria2 options +
    /// theme). Restart-only options are saved here too but take effect on the engine restart
    /// performed when leaving the page.</summary>
    private async Task ApplyChangeAsync()
    {
        if (_loading)
            return;
        // B21: flag malformed size fields inline. BuildSettings substitutes the last applied
        // value for an invalid box, so bad text never reaches aria2 — and, unlike a hard gate,
        // changes to every OTHER field still apply normally (a hidden flagged field on another
        // section must not silently swallow e.g. a theme change).
        ValidateFormatFields();
        var s = BuildSettings();
        // The port box accepts 1-1023 but the applied value is clamped — reflect it back so the
        // UI never shows a port that differs from what the engine actually got.
        if ((int)SafeValue(ListenPortBox.Value, 0) != s.ListenPort)
        {
            _loading = true;
            ListenPortBox.Value = s.ListenPort;
            _loading = false;
        }
        // O6: a blur/toggle that changed nothing skips the RPC round-trip and the disk write.
        string snap = SettingsService.Snapshot(s);
        if (snap == _appliedSnapshot)
            return;
        try
        {
            await Aria2Service.Instance.ApplySettingsAsync(s);
            _appliedSnapshot = snap;
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            // Invalidate the no-op cache: ApplySettingsAsync persists BEFORE the RPC push, so a
            // failed push leaves the rejected value in Settings/settings.json while this form may
            // be reverted to the entry state — which would match a stale cache and never re-apply.
            _appliedSnapshot = "";
            ShowError(Helpers.L.Get("SettingsErrorSave", ex.Message));
        }
        // Theme and the language-restart hint don't depend on the engine RPC, so update them
        // even if the live push failed (the setting is already persisted by ApplySettingsAsync).
        Helpers.ThemeHelper.Apply(s.Theme);
        UpdateLanguageRestartHint();
    }

    /// <summary>Validates the aria2 size/speed text fields against the "&lt;digits&gt;[K|M]" grammar
    /// (empty or "0" are allowed). Invalid fields get an inline <see cref="TextBox.Description"/>
    /// error; <see cref="BuildSettings"/> falls back to the last applied value for them (B21).</summary>
    private void ValidateFormatFields()
    {
        foreach (var box in new[] { MinSplitSizeBox, LowestSpeedLimitBox, DiskCacheBox, BtPeerSpeedBox })
            box.Description = IsAriaSizeFormat(box.Text) ? null : Helpers.L.Get("SettingsErrorBadFormat");
    }

    /// <summary>The box's text when it parses, else the last applied value — malformed input must
    /// never reach aria2 or settings.json (every BuildSettings caller is covered, including the
    /// Back/relaunch paths that bypass the auto-apply pipeline).</summary>
    private static string SizeOrLastGood(TextBox box, string lastGood) =>
        IsAriaSizeFormat(box.Text) ? box.Text : lastGood;

    private static bool IsAriaSizeFormat(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t == "0")
            return true;
        int digits = t.Length;
        char suffix = char.ToUpperInvariant(t[^1]);
        if (suffix is 'K' or 'M')
            digits--;
        if (digits == 0)
            return false;
        for (int i = 0; i < digits; i++)
            if (!char.IsAsciiDigit(t[i]))
                return false;
        return true;
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

    /// <summary>Fills the About cards with the app and aria2 versions. Latches only once the
    /// aria2 version was actually fetched — if RPC happened to be down on the first visit
    /// (engine restarting), a later visit retries instead of showing a blank forever.</summary>
    private async void LoadAboutInfo()
    {
        if (_aboutLoaded)
            return;
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
            {
                AriaInfoCard.Description = (await Aria2Service.Instance.Rpc.GetVersionAsync()).Version;
                _aboutLoaded = true;
            }
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

    /// <summary>Snapshot of the DHT/PEX/LPD/encryption choices captured when privacy mode is turned
    /// on, so turning it off restores them ("dht|pex|lpd|crypto|level|force"); empty when off.</summary>
    private string _privacySnapshot = "";

    /// <summary>Privacy mode toggle: turning it ON snapshots the current DHT/PEX/LPD/encryption
    /// choices and forces the private preset (full encryption + DHT/PEX/LPD off); turning it OFF
    /// restores that snapshot. The affected controls are disabled while it's on so they can't drift
    /// out of sync. Like every BT flag it applies on the engine restart performed when leaving.</summary>
    private void OnPrivacyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _loading = true; // flip several controls without one apply per change
        try
        {
            if (PrivacyToggle.IsOn)
            {
                _privacySnapshot = string.Join('|',
                    DhtToggle.IsOn ? "1" : "0",
                    PexToggle.IsOn ? "1" : "0",
                    LpdToggle.IsOn ? "1" : "0",
                    CryptoToggle.IsOn ? "1" : "0",
                    CryptoLevelRadio.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                    ForceEncryptionToggle.IsOn ? "1" : "0");
                DhtToggle.IsOn = false;
                PexToggle.IsOn = false;
                LpdToggle.IsOn = false;
                CryptoToggle.IsOn = true;
                CryptoLevelRadio.SelectedIndex = 1; // arc4
                ForceEncryptionToggle.IsOn = true;
                CryptoExpander.IsExpanded = true;
            }
            else
            {
                var parts = _privacySnapshot.Split('|');
                if (parts.Length == 6)
                {
                    DhtToggle.IsOn = parts[0] == "1";
                    PexToggle.IsOn = parts[1] == "1";
                    LpdToggle.IsOn = parts[2] == "1";
                    CryptoToggle.IsOn = parts[3] == "1";
                    CryptoLevelRadio.SelectedIndex = int.TryParse(parts[4], out var idx) ? idx : 0;
                    ForceEncryptionToggle.IsOn = parts[5] == "1";
                    CryptoExpander.IsExpanded = CryptoToggle.IsOn;
                }
                else
                {
                    // B12: no (or corrupt) snapshot to restore — fall back to the app defaults so
                    // turning privacy off never leaves the forced private preset locked in.
                    DhtToggle.IsOn = true;
                    PexToggle.IsOn = true;
                    LpdToggle.IsOn = false;
                    CryptoToggle.IsOn = false;
                    CryptoLevelRadio.SelectedIndex = 0;
                    ForceEncryptionToggle.IsOn = false;
                    CryptoExpander.IsExpanded = false;
                }
                _privacySnapshot = "";
            }
            SetPrivacyControlsEnabled(PrivacyToggle.IsOn);
        }
        finally
        {
            _loading = false;
        }
        _ = ApplyChangeAsync();
    }

    /// <summary>Disables the peer-discovery / encryption controls while privacy mode owns them.</summary>
    private void SetPrivacyControlsEnabled(bool privacyOn)
    {
        bool enabled = !privacyOn;
        DhtToggle.IsEnabled = enabled;
        PexToggle.IsEnabled = enabled;
        LpdToggle.IsEnabled = enabled;
        CryptoToggle.IsEnabled = enabled;
        CryptoLevelRadio.IsEnabled = enabled;
        ForceEncryptionToggle.IsEnabled = enabled;
    }

    /// <summary>The seeding value (ratio vs minutes) is a parameter of the mode, so its card
    /// reshapes — label, units, range — to the picked mode and collapses for "off", mirroring the
    /// encryption expander.</summary>
    private void OnSeedModeChanged(object sender, SelectionChangedEventArgs e)
    {
        string mode = (SeedModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "ratio";
        if (_loading)
        {
            ApplySeedModeUi(mode);
            return;
        }
        // ApplySeedModeUi changes the box's Min/Max, which coerces its current value
        // SYNCHRONOUSLY and fires ValueChanged -> ApplyChangeAsync with the NEW mode tag but the
        // OTHER mode's (coerced) number — silently corrupting the stored value. Reshape and
        // repopulate under the loading guard, then apply exactly once.
        _loading = true;
        try
        {
            ApplySeedModeUi(mode);
            // Ratio and minutes are different scales — repopulate the box with the stored value
            // for the newly-picked mode so switching modes doesn't reinterpret one as the other.
            var s = Aria2Service.Instance.Settings;
            if (mode == "time")
                SeedValueBox.Value = s.SeedTimeMinutes;
            else if (mode == "ratio")
                SeedValueBox.Value = s.SeedRatio;
        }
        finally
        {
            _loading = false;
        }
        _ = ApplyChangeAsync();
    }

    private void ApplySeedModeUi(string mode)
    {
        bool off = mode == "off";
        bool time = mode == "time";
        SeedExpander.IsExpanded = !off; // nothing to configure when not seeding
        SeedValueCard.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
        if (off)
            return;
        SeedValueCard.Header = Helpers.L.Get(time ? "SeedValueTimeHeader" : "SeedValueRatioHeader");
        SeedValueCard.Description = Helpers.L.Get(time ? "SeedValueTimeDesc" : "SeedValueRatioDesc");
        SeedValueBox.Minimum = time ? 1 : 0.1;
        SeedValueBox.Maximum = time ? 525600 : 100;
        SeedValueBox.SmallChange = time ? 5 : 1; // ratio steps by whole numbers
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
        string seedMode = (SeedModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "ratio";
        double seedValue = SafeValue(SeedValueBox.Value, seedMode == "time" ? 60 : 1.0);
        string downUnit = (DownLimitUnitBox.SelectedValue as string) ?? Helpers.SpeedUnit.Default;
        string upUnit = (UpLimitUnitBox.SelectedValue as string) ?? Helpers.SpeedUnit.Default;
        return new AppSettings
        {
            DownloadDirectory = DirText.Text,
            MaxDownloadLimit = LimitToStored(DownLimitBox.Value, downUnit),
            MaxUploadLimit = LimitToStored(UpLimitBox.Value, upUnit),
            MaxDownloadLimitUnit = downUnit,
            MaxUploadLimitUnit = upUnit,
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
            MinSplitSize = SizeOrLastGood(MinSplitSizeBox, old.MinSplitSize),
            UserAgent = UserAgentBox.Text,
            LowestSpeedLimit = SizeOrLastGood(LowestSpeedLimitBox, old.LowestSpeedLimit),
            HttpUser = HttpUserBox.Text,
            HttpPasswd = HttpPasswdBox.Password,
            AllProxyUser = ProxyUserBox.Text,
            AllProxyPasswd = ProxyPasswdBox.Password,
            MinTlsVersion = (MinTlsBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "TLSv1.2",
            DisableIpv6 = DisableIpv6Toggle.IsOn,
            FileAllocation = (FileAllocBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto",
            AllowOverwrite = AllowOverwriteToggle.IsOn,
            AutoFileRenaming = AutoRenameToggle.IsOn,
            DiskCache = SizeOrLastGood(DiskCacheBox, old.DiskCache),

            ListenPort = ClampListenPort((int)SafeValue(ListenPortBox.Value, 0)),
            BtMaxPeers = (int)SafeValue(PeersBox.Value, 55),
            BtMaxOpenFiles = (int)SafeValue(BtMaxOpenFilesBox.Value, 100),
            BtRequestPeerSpeedLimit = SizeOrLastGood(BtPeerSpeedBox, old.BtRequestPeerSpeedLimit),
            BtStopTimeout = (int)SafeValue(BtStopTimeoutBox.Value, 0),
            BtDetachSeedOnly = BtDetachSeedToggle.IsOn,
            SeedMode = seedMode,
            SeedRatio = seedMode == "ratio" ? seedValue : old.SeedRatio,
            SeedTimeMinutes = seedMode == "time" ? (int)Math.Round(seedValue) : old.SeedTimeMinutes,
            EnableDht = DhtToggle.IsOn,
            EnablePex = PexToggle.IsOn,
            EnableLpd = LpdToggle.IsOn,
            RequireCrypto = CryptoToggle.IsOn,
            BtMinCryptoLevel = CryptoLevelRadio.SelectedIndex == 1 ? "arc4" : "plain",
            BtForceEncryption = ForceEncryptionToggle.IsOn,
            PrivacyMode = PrivacyToggle.IsOn,
            PrivacySnapshot = _privacySnapshot,
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
        || old.BtForceEncryption != updated.BtForceEncryption
        // PrivacyMode gates the ExtraAria2Options filtering on the command line, so toggling it
        // alone (with DHT/PEX/crypto already matching the preset) still needs the restart.
        || old.PrivacyMode != updated.PrivacyMode
        || old.DiskCache != updated.DiskCache
        || old.MinTlsVersion != updated.MinTlsVersion
        || old.DisableIpv6 != updated.DisableIpv6
        || old.ExtraTrackers != updated.ExtraTrackers
        || old.ExtraAria2Options != updated.ExtraAria2Options;

    private static double SafeValue(double value, double fallback) =>
        double.IsNaN(value) ? fallback : value;

    // Matches SettingsService.Load: 0 means "let aria2 pick"; any explicit port is a
    // non-privileged 1024-65535. Clamping here keeps the live value equal to what reloads.
    private static int ClampListenPort(int port) => port == 0 ? 0 : Math.Clamp(port, 1024, 65535);

    /// <summary>Populates a download/upload limit editor (NumberBox value + unit combo) from a
    /// stored byte-count string. The stored unit drives the display unit; legacy "10240K"/"5M"
    /// values still parse (their bytes shown in the stored unit). 0 → empty cap in the default unit.</summary>
    private static void LoadLimit(NumberBox valueBox, ComboBox unitBox, string stored, string storedUnit)
    {
        string unit = Helpers.SpeedUnit.Sanitize(storedUnit);
        long bytes = Helpers.SpeedUnit.ParseStoredBytes(stored);
        (double value, unit) = Helpers.SpeedUnit.FromBytes(bytes, unit);
        valueBox.Value = value;
        unitBox.SelectedValue = unit;
    }

    /// <summary>NumberBox value + unit symbol → aria2 byte-count string ("0" = unlimited).</summary>
    private static string LimitToStored(double value, string unit)
    {
        long bytes = Helpers.SpeedUnit.ToBytes(SafeValue(value, 0), unit);
        return bytes <= 0 ? "0" : bytes.ToString(CultureInfo.InvariantCulture);
    }
}
