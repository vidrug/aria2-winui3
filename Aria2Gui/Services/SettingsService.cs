using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aria2Gui.Services;

/// <summary>User-facing application settings, persisted as JSON in <see cref="AppPaths.SettingsFile"/>.</summary>
public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = "";

    /// <summary>aria2 speed format: "0" = unlimited, otherwise e.g. "500K", "5M".</summary>
    public string MaxDownloadLimit { get; set; } = "0";

    /// <summary>aria2 speed format: "0" = unlimited, otherwise e.g. "500K", "5M".</summary>
    public string MaxUploadLimit { get; set; } = "0";

    public int MaxConcurrentDownloads { get; set; } = 5;

    /// <summary>Connections per server for HTTP(S)/FTP downloads (aria2 max is 16).</summary>
    public int MaxConnectionsPerServer { get; set; } = 8;

    public int BtMaxPeers { get; set; } = 55;

    /// <summary>
    /// Seed until this ratio after a torrent completes; 0 disables seeding
    /// (mapped to aria2 --seed-time=0, since --seed-ratio=0 means "seed forever").
    /// </summary>
    public double SeedRatio { get; set; } = 1.0;

    /// <summary>"Default" (follow system), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "Default";

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

    /// <summary>Extra trackers appended to every torrent (one URI per line).</summary>
    public string ExtraTrackers { get; set; } = "";

    /// <summary>Max simultaneously open files across all BT downloads.</summary>
    public int BtMaxOpenFiles { get; set; } = 100;

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

    // ---- Files ----

    /// <summary>"none", "prealloc", "trunc", "falloc", or "auto" (NTFS→falloc, else prealloc).</summary>
    public string FileAllocation { get; set; } = "auto";

    /// <summary>Overwrite an existing file instead of renaming.</summary>
    public bool AllowOverwrite { get; set; }

    /// <summary>Rename file to file.1, file.2 … when a name collides.</summary>
    public bool AutoFileRenaming { get; set; } = true;

    /// <summary>Raw aria2 options, one "key=value" per line — full access to any flag.</summary>
    public string ExtraAria2Options { get; set; } = "";
}

public static class SettingsService
{
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
        settings.SeedRatio = double.IsFinite(settings.SeedRatio) ? Math.Clamp(settings.SeedRatio, 0, 1000) : 1.0;
        settings.MaxDownloadLimit = SanitizeSpeed(settings.MaxDownloadLimit);
        settings.MaxUploadLimit = SanitizeSpeed(settings.MaxUploadLimit);
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
        return settings;
    }

    /// <summary>aria2 size format "&lt;digits&gt;[K|M]"; falls back to <paramref name="fallback"/>.</summary>
    private static string SanitizeSize(string value, string fallback)
    {
        string s = SanitizeSpeed(value);
        return s == "0" ? fallback : s;
    }

    /// <summary>aria2 accepts only "&lt;digits&gt;[K|M]" for speed limits.</summary>
    private static string SanitizeSpeed(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "0";
        string trimmed = value.Trim();
        int digits = trimmed.Length;
        char suffix = char.ToUpperInvariant(trimmed[^1]);
        if (suffix is 'K' or 'M')
            digits--;
        if (digits == 0)
            return "0";
        for (int i = 0; i < digits; i++)
        {
            if (!char.IsAsciiDigit(trimmed[i]))
                return "0";
        }
        return trimmed;
    }

    public static void Save(AppSettings settings)
    {
        // Atomic write: never leave a torn settings.json behind.
        string tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
