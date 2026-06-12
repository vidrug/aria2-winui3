using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aria2Gui.Services;

/// <summary>User-facing application settings, persisted as JSON in <see cref="AppPaths.SettingsFile"/>.</summary>
public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = "";

    /// <summary>Download cap aria2 receives: a plain byte count ("0" = unlimited). Legacy
    /// "500K"/"5M" values still load. The matching <see cref="MaxDownloadLimitUnit"/> is only a
    /// display hint for the settings editor and does not change how aria2 consumes this.</summary>
    public string MaxDownloadLimit { get; set; } = "0";

    /// <summary>Upload cap aria2 receives: a plain byte count ("0" = unlimited). Legacy
    /// "500K"/"5M" values still load.</summary>
    public string MaxUploadLimit { get; set; } = "0";

    /// <summary>Unit symbol (B/KB/Kb/MB/Mb) the download-limit editor displays the value in.</summary>
    public string MaxDownloadLimitUnit { get; set; } = Helpers.SpeedUnit.Default;

    /// <summary>Unit symbol (B/KB/Kb/MB/Mb) the upload-limit editor displays the value in.</summary>
    public string MaxUploadLimitUnit { get; set; } = Helpers.SpeedUnit.Default;

    public int MaxConcurrentDownloads { get; set; } = 5;

    /// <summary>Connections per server for HTTP(S)/FTP downloads (aria2 max is 16).</summary>
    public int MaxConnectionsPerServer { get; set; } = 8;

    public int BtMaxPeers { get; set; } = 55;

    /// <summary>How seeding stops after a torrent completes: "ratio" (until
    /// <see cref="SeedRatio"/>), "time" (until <see cref="SeedTimeMinutes"/>), or "off"
    /// (don't seed). Empty is migrated from the legacy <see cref="SeedRatio"/> in Load.</summary>
    public string SeedMode { get; set; } = "";

    /// <summary>Share ratio at which seeding stops in "ratio" mode.</summary>
    public double SeedRatio { get; set; } = 1.0;

    /// <summary>Minutes to seed in "time" mode before stopping.</summary>
    public int SeedTimeMinutes { get; set; } = 60;

    /// <summary>Hold the system awake (no sleep) while downloads or seeding are active.</summary>
    public bool PreventSleep { get; set; } = true;

    /// <summary>Launch the app at Windows sign-in (HKCU Run key, current user).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Start hidden in the tray instead of showing the window.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>The window close button hides to the tray; exit via the tray menu.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Register as a per-user handler for magnet: links and .torrent files.</summary>
    public bool RegisterFileAssociations { get; set; }

    // ---- Alternative ("turtle mode") speed limits ----

    /// <summary>While true, the alternative limits below replace the main ones.</summary>
    public bool AltSpeedEnabled { get; set; }

    /// <summary>Alternative download cap, plain byte count ("0" = unlimited).</summary>
    public string AltDownloadLimit { get; set; } = "0";

    /// <summary>Alternative upload cap, plain byte count ("0" = unlimited).</summary>
    public string AltUploadLimit { get; set; } = "0";

    /// <summary>Display unit for the alternative download limit editor.</summary>
    public string AltDownloadLimitUnit { get; set; } = Helpers.SpeedUnit.Default;

    /// <summary>Display unit for the alternative upload limit editor.</summary>
    public string AltUploadLimitUnit { get; set; } = Helpers.SpeedUnit.Default;

    /// <summary>"Default" (follow system), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>UI language as a BCP-47 tag (e.g. "en-US", "ru"); empty = follow the OS.</summary>
    public string Language { get; set; } = "";

    /// <summary>Folder chosen in the add dialog last time; falls back to
    /// <see cref="DownloadDirectory"/> when it no longer exists (e.g. removable drive).</summary>
    public string LastAddDirectory { get; set; } = "";

    // ---- BitTorrent engine flags (applied on the aria2c command line) ----

    /// <summary>BT/DHT listen port; 0 = aria2 default range (6881–6999).</summary>
    public int ListenPort { get; set; }

    public bool EnableDht { get; set; } = true;

    /// <summary>Peer exchange (PEX).</summary>
    public bool EnablePex { get; set; } = true;

    /// <summary>Local peer discovery (LPD).</summary>
    public bool EnableLpd { get; set; }

    /// <summary>Require encrypted peer connections (bt-require-crypto).</summary>
    public bool RequireCrypto { get; set; }

    /// <summary>
    /// Minimum BT encryption method (bt-min-crypto-level): "plain" obfuscates only
    /// the handshake, "arc4" encrypts the whole data stream with RC4.
    /// </summary>
    public string BtMinCryptoLevel { get; set; } = "plain";

    /// <summary>Refuse unencrypted peer connections (bt-force-encryption); implies require-crypto
    /// + arc4. Set by the privacy mode toggle.</summary>
    public bool BtForceEncryption { get; set; }

    /// <summary>Whether privacy mode is on (forces encryption + turns DHT/PEX/LPD off).</summary>
    public bool PrivacyMode { get; set; }

    /// <summary>The DHT/PEX/LPD/encryption values captured when privacy mode was switched on, so
    /// switching it off restores them. Internal "dht|pex|lpd|crypto|level|force" snapshot; empty
    /// when privacy mode is off.</summary>
    public string PrivacySnapshot { get; set; } = "";

    /// <summary>Extra trackers appended to every torrent (one URI per line).</summary>
    public string ExtraTrackers { get; set; } = "";

    /// <summary>Max simultaneously open files across all BT downloads.</summary>
    public int BtMaxOpenFiles { get; set; } = 100;

    /// <summary>Per-peer download speed cap that triggers requesting more peers, aria2 speed
    /// format (e.g. "50K"); "0" = unlimited.</summary>
    public string BtRequestPeerSpeedLimit { get; set; } = "50K";

    /// <summary>Keep seeding-only torrents out of the concurrent-downloads count.</summary>
    public bool BtDetachSeedOnly { get; set; }

    /// <summary>Stop a BT download this many seconds after it makes no progress; 0 = never.</summary>
    public int BtStopTimeout { get; set; }

    // ---- Connection / HTTP(S)/FTP ----

    /// <summary>Timeout in seconds for a stalled connection.</summary>
    public int Timeout { get; set; } = 60;

    /// <summary>Initial connection timeout in seconds.</summary>
    public int ConnectTimeout { get; set; } = 60;

    /// <summary>Retry attempts per download (0 = no retry).</summary>
    public int MaxTries { get; set; } = 5;

    /// <summary>Seconds to wait between retries.</summary>
    public int RetryWait { get; set; }

    /// <summary>Verify HTTPS server certificates.</summary>
    public bool CheckCertificate { get; set; } = true;

    /// <summary>HTTP User-Agent header (blank = aria2 default).</summary>
    public string UserAgent { get; set; } = "";

    /// <summary>Proxy for all protocols, e.g. http://host:port or socks5://host:port (blank = none).</summary>
    public string AllProxy { get; set; } = "";

    /// <summary>Lowest size, aria2 format (e.g. "1M"), at which a download is split for a new connection.</summary>
    public string MinSplitSize { get; set; } = "20M";

    /// <summary>Abort a download whose speed stays below this (aria2 speed format); "0" = off.</summary>
    public string LowestSpeedLimit { get; set; } = "0";

    /// <summary>HTTP basic-auth user / password applied to all HTTP downloads (blank = none).</summary>
    public string HttpUser { get; set; } = "";

    public string HttpPasswd { get; set; } = "";

    /// <summary>Credentials for the proxy in <see cref="AllProxy"/> (blank = none).</summary>
    public string AllProxyUser { get; set; } = "";

    public string AllProxyPasswd { get; set; } = "";

    /// <summary>Minimum TLS version for HTTPS: "TLSv1.1", "TLSv1.2" or "TLSv1.3".</summary>
    public string MinTlsVersion { get; set; } = "TLSv1.2";

    /// <summary>Disable IPv6 name resolution / connections.</summary>
    public bool DisableIpv6 { get; set; }

    // ---- Files ----

    /// <summary>"none", "prealloc", "trunc", "falloc", or "auto" (NTFS→falloc, else prealloc).</summary>
    public string FileAllocation { get; set; } = "auto";

    /// <summary>Overwrite an existing file instead of renaming.</summary>
    public bool AllowOverwrite { get; set; }

    /// <summary>Rename file to file.1, file.2 … when a name collides.</summary>
    public bool AutoFileRenaming { get; set; } = true;

    /// <summary>In-memory disk cache size, aria2 size format (e.g. "16M"); "0" = off.</summary>
    public string DiskCache { get; set; } = "16M";

    /// <summary>Raw aria2 options, one "key=value" per line — full access to any flag.</summary>
    public string ExtraAria2Options { get; set; } = "";
}

public static class SettingsService
{
    /// <summary>BCP-47 tags shipped as .resw; "" means follow the OS language.</summary>
    public static readonly string[] SupportedLanguages =
        ["", "en-US", "ru", "es", "de", "fr", "pt-BR", "it", "zh-Hans", "ja", "uk", "pl", "tr"];

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SettingsFile), SettingsJsonContext.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(settings.DownloadDirectory))
            settings.DownloadDirectory = AppPaths.DefaultDownloadDirectory;

        // Values reach the aria2c command line — clamp hand-edited files so a bad
        // settings.json can't keep the engine from starting.
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 50);
        settings.MaxConnectionsPerServer = Math.Clamp(settings.MaxConnectionsPerServer, 1, 16);
        settings.BtMaxPeers = Math.Clamp(settings.BtMaxPeers, 0, 1000);
        // Keep the stored ceiling in lock-step with the editor's NumberBox (Max=100), or a
        // hand-edited 300 gets coerced down to 100 and silently re-saved as 100 (B8).
        settings.SeedRatio = double.IsFinite(settings.SeedRatio) ? Math.Clamp(settings.SeedRatio, 0, 100) : 1.0;
        settings.MaxDownloadLimit = SanitizeSpeed(settings.MaxDownloadLimit);
        settings.MaxUploadLimit = SanitizeSpeed(settings.MaxUploadLimit);
        settings.MaxDownloadLimitUnit = Helpers.SpeedUnit.Sanitize(settings.MaxDownloadLimitUnit);
        settings.MaxUploadLimitUnit = Helpers.SpeedUnit.Sanitize(settings.MaxUploadLimitUnit);
        settings.AltDownloadLimit = SanitizeSpeed(settings.AltDownloadLimit);
        settings.AltUploadLimit = SanitizeSpeed(settings.AltUploadLimit);
        settings.AltDownloadLimitUnit = Helpers.SpeedUnit.Sanitize(settings.AltDownloadLimitUnit);
        settings.AltUploadLimitUnit = Helpers.SpeedUnit.Sanitize(settings.AltUploadLimitUnit);
        settings.ListenPort = settings.ListenPort is 0 ? 0 : Math.Clamp(settings.ListenPort, 1024, 65535);
        settings.BtMaxOpenFiles = Math.Clamp(settings.BtMaxOpenFiles, 1, 10000);
        settings.Timeout = Math.Clamp(settings.Timeout, 1, 600);
        settings.ConnectTimeout = Math.Clamp(settings.ConnectTimeout, 1, 600);
        settings.MaxTries = Math.Clamp(settings.MaxTries, 0, 100);
        settings.RetryWait = Math.Clamp(settings.RetryWait, 0, 600);
        settings.MinSplitSize = SanitizeSize(settings.MinSplitSize, "20M");
        if (settings.FileAllocation is not ("none" or "prealloc" or "trunc" or "falloc" or "auto"))
            settings.FileAllocation = "auto";
        if (settings.BtMinCryptoLevel is not ("plain" or "arc4"))
            settings.BtMinCryptoLevel = "plain";
        settings.LowestSpeedLimit = SanitizeSpeed(settings.LowestSpeedLimit);
        // For these, a literal "0" is a meaningful value ("request all peers" / "cache off"),
        // so only a *malformed* value falls back to the default — it isn't silently turned off (B20).
        settings.BtRequestPeerSpeedLimit = SanitizeSpeed(settings.BtRequestPeerSpeedLimit, "50K");
        settings.DiskCache = SanitizeSpeed(settings.DiskCache, "16M"); // size shares the speed grammar
        settings.BtStopTimeout = Math.Clamp(settings.BtStopTimeout, 0, 86400);
        settings.SeedTimeMinutes = Math.Clamp(settings.SeedTimeMinutes, 1, 525600);
        if (settings.MinTlsVersion is not ("TLSv1.1" or "TLSv1.2" or "TLSv1.3"))
            settings.MinTlsVersion = "TLSv1.2";
        // Migrate the legacy single SeedRatio (0 meant "don't seed") into the explicit mode.
        if (settings.SeedMode is not ("ratio" or "time" or "off"))
            settings.SeedMode = settings.SeedRatio <= 0 ? "off" : "ratio";
        if (!SupportedLanguages.Contains(settings.Language))
            settings.Language = "";

        // B2: secrets are stored DPAPI-encrypted; bring them back to plaintext for the UI/engine.
        settings.HttpPasswd = Unprotect(settings.HttpPasswd);
        settings.AllProxyPasswd = Unprotect(settings.AllProxyPasswd);
        return settings;
    }

    /// <summary>aria2 size format "&lt;digits&gt;[K|M]"; "0" and malformed both fall back
    /// to <paramref name="fallback"/> (a size of 0 is not meaningful here, e.g. min-split-size).</summary>
    private static string SanitizeSize(string value, string fallback)
    {
        string s = SanitizeSpeed(value, fallback);
        return s == "0" ? fallback : s;
    }

    /// <summary>aria2 accepts only "&lt;digits&gt;[K|M]" for speed limits. A literal "0" (and empty)
    /// is preserved as "0"; only a non-empty *malformed* value yields <paramref name="fallback"/>,
    /// so we never conflate "off" with "couldn't parse".</summary>
    private static string SanitizeSpeed(string value, string fallback = "0")
    {
        if (string.IsNullOrWhiteSpace(value))
            return "0";
        string trimmed = value.Trim();
        if (trimmed == "0")
            return "0";
        int digits = trimmed.Length;
        char suffix = char.ToUpperInvariant(trimmed[^1]);
        if (suffix is 'K' or 'M')
            digits--;
        bool ok = digits > 0;
        for (int i = 0; ok && i < digits; i++)
            if (!char.IsAsciiDigit(trimmed[i]))
                ok = false;
        return ok ? trimmed : fallback;
    }

    private const string DpapiPrefix = "dpapi:";

    /// <summary>DPAPI-encrypts a secret for at-rest storage (current Windows user). Returns ""
    /// for empty input, and — if encryption itself fails — the plaintext, so we never drop the value.
    /// Note: this ties settings.json secrets to this user/machine (no cross-machine copy).</summary>
    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
            return "";
        try
        {
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(enc);
        }
        catch (CryptographicException)
        {
            return plain;
        }
    }

    /// <summary>Reverses <see cref="Protect"/>. Legacy plaintext (no prefix) is accepted as-is and
    /// gets re-encrypted on the next Save; an undecryptable blob (different user/machine) yields "".</summary>
    private static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return "";
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            return stored;
        try
        {
            byte[] enc = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }

    public static void Save(AppSettings settings)
    {
        // B2: encrypt secrets at rest. Swap to ciphertext only for the serialize+write, then
        // restore the live plaintext so callers and two-way bindings are unaffected.
        string http = settings.HttpPasswd, proxy = settings.AllProxyPasswd;
        settings.HttpPasswd = Protect(http);
        settings.AllProxyPasswd = Protect(proxy);
        try
        {
            // Atomic write: never leave a torn settings.json behind.
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        finally
        {
            settings.HttpPasswd = http;
            settings.AllProxyPasswd = proxy;
        }

        // Keep the per-user registry integrations in lock-step with the persisted intent —
        // every save path syncs them, and both helpers no-op when already in the right state.
        StartupHelper.Sync(settings.StartWithWindows);
        AssociationHelper.Sync(settings.RegisterFileAssociations);
    }

    /// <summary>Stable JSON form of the settings, used to skip a redundant apply/persist when a
    /// blur or toggle changed nothing (O6). Secrets stay plaintext here (in-memory comparison only).</summary>
    public static string Snapshot(AppSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
