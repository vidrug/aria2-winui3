using Microsoft.Win32;

namespace Aria2Gui.Services;

/// <summary>
/// "Start with Windows" via the current user's Run key — the right mechanism for the
/// portable (unpackaged) build. <see cref="Sync"/> is idempotent and tracks the exe path,
/// so a moved portable folder re-registers itself on the next settings save.
/// </summary>
public static class StartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Aria2Gui";

    public static void Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
                return;
            if (enabled)
            {
                if (Environment.ProcessPath is not { Length: > 0 } exe)
                    return;
                string command = $"\"{exe}\"";
                if (key.GetValue(ValueName) as string != command)
                    key.SetValue(ValueName, command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Registry blocked (policy/AV) — the toggle simply has no effect; nothing to break.
        }
    }
}
