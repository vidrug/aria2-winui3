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
        return settings;
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
