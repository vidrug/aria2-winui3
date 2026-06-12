using System.Globalization;
using System.Text.Json;

namespace Aria2Gui.Services;

/// <summary>
/// All-time transfer totals, persisted as a tiny JSON next to the settings. Session totals
/// live in MainPageViewModel; this only accumulates and persists the lifetime numbers
/// (written on exit and every few minutes — <see cref="Save"/> skips unchanged values).
/// </summary>
public static class StatsService
{
    public static long AllTimeDownloaded;
    public static long AllTimeUploaded;

    private static long _savedDown = -1;
    private static long _savedUp = -1;

    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "stats.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty("downloaded", out var d) && d.TryGetInt64(out var down))
                AllTimeDownloaded = Math.Max(0, down);
            if (doc.RootElement.TryGetProperty("uploaded", out var u) && u.TryGetInt64(out var up))
                AllTimeUploaded = Math.Max(0, up);
            _savedDown = AllTimeDownloaded;
            _savedUp = AllTimeUploaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt/unreadable stats are not worth failing startup over — start from zero.
        }
    }

    public static void Save()
    {
        if (AllTimeDownloaded == _savedDown && AllTimeUploaded == _savedUp)
            return;
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, string.Create(CultureInfo.InvariantCulture,
                $"{{\"downloaded\":{AllTimeDownloaded},\"uploaded\":{AllTimeUploaded}}}"));
            File.Move(tmp, FilePath, overwrite: true);
            _savedDown = AllTimeDownloaded;
            _savedUp = AllTimeUploaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — totals survive in memory until the next periodic save.
        }
    }
}
