namespace Aria2Gui.Services;

/// <summary>
/// Resolves where the app keeps its mutable state. Portable-first: if the folder
/// next to the executable is writable, everything lives in &lt;exe&gt;\data so the
/// whole app can be moved/zipped as one folder. Otherwise (e.g. installed under a
/// read-only location) falls back to %LOCALAPPDATA%\Aria2Gui.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = ComputeDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>aria2 session file — unfinished downloads survive app restarts.</summary>
    public static string SessionFile => Path.Combine(DataDirectory, "aria2.session");

    public static string DefaultDownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string ComputeDataDirectory()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(portable);
            string probe = Path.Combine(portable, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portable;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aria2Gui");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
