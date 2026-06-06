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

    /// <summary>Seed until this ratio after a torrent completes; 0 stops seeding immediately.</summary>
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
        return settings;
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
