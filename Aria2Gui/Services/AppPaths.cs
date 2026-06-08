using System.Runtime.InteropServices;

namespace Aria2Gui.Services;

/// <summary>
/// Resolves where the app keeps its mutable state. Portable-first: if the app runs
/// without package identity and the folder next to the executable is writable,
/// everything lives in &lt;exe&gt;\data so the whole app can be moved/zipped as one
/// folder. Packaged (MSIX/dev-deploy) or read-only installs fall back to
/// %LOCALAPPDATA%\Aria2Gui so state survives redeploys.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = ComputeDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Persisted downloads-table column layout (widths + visibility).</summary>
    public static string ColumnLayoutFile => Path.Combine(DataDirectory, "columns.txt");

    /// <summary>aria2 session file — unfinished downloads survive app restarts.</summary>
    public static string SessionFile => Path.Combine(DataDirectory, "aria2.session");

    /// <summary>The real shell Downloads folder (it may be relocated by the user).</summary>
    public static string DefaultDownloadDirectory => GetKnownDownloadsFolder()
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string ComputeDataDirectory()
    {
        // Never throw out of a static initializer: a TypeInitializationException here
        // would permanently poison every AppPaths access for the process.
        try
        {
            if (!HasPackageIdentity())
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
                catch
                {
                    // Read-only install location — fall through to LOCALAPPDATA.
                }
            }

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aria2Gui");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
        catch
        {
            // Last resort (quota, AV locks, broken profile): a temp dir beats a crash.
            string temp = Path.Combine(Path.GetTempPath(), "Aria2Gui");
            try { Directory.CreateDirectory(temp); } catch { }
            return temp;
        }
    }

    /// <summary>True when running with MSIX package identity (incl. winapp dev deploys).</summary>
    private static bool HasPackageIdentity()
    {
        int length = 0;
        // APPMODEL_ERROR_NO_PACKAGE = 15700 when identity-less.
        return GetCurrentPackageFullName(ref length, null) != 15700;
    }

    private static string? GetKnownDownloadsFolder()
    {
        Guid downloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");
        nint pathPtr = 0;
        try
        {
            if (SHGetKnownFolderPath(ref downloadsFolderId, 0, 0, out pathPtr) == 0)
                return Marshal.PtrToStringUni(pathPtr);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        finally
        {
            if (pathPtr != 0)
                Marshal.FreeCoTaskMem(pathPtr);
        }
        return null;
    }

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}
