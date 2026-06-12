using Microsoft.Win32;

namespace Aria2Gui.Services;

/// <summary>
/// Per-user (HKCU) registration as a handler for magnet: links and .torrent files —
/// the portable-build counterpart of a packaged file-type association. Registering adds
/// the app to the .torrent "Open with" list (and becomes the default only when nothing
/// else owns the extension) and claims the magnet: protocol. <see cref="Sync"/> is
/// idempotent; unregistering removes only what we own.
/// </summary>
public static class AssociationHelper
{
    private const string ProgId = "Aria2Gui.torrent";

    public static void Sync(bool enabled)
    {
        try
        {
            if (enabled)
                Register();
            else
                Unregister();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Registry blocked (policy/AV) — the toggle simply has no effect.
        }
    }

    private static void Register()
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe)
            return;
        string command = $"\"{exe}\" \"%1\"";

        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        if (classes is null)
            return;

        // ProgId for .torrent files.
        using (var progId = classes.CreateSubKey(ProgId))
        {
            progId.SetValue("", "Torrent file");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"{exe},0");
            using var cmd = progId.CreateSubKey(@"shell\open\command");
            if (cmd.GetValue("") as string != command)
                cmd.SetValue("", command);
        }

        // .torrent: always join the "Open with" list; take the DEFAULT only when the
        // extension is unowned (Windows protects the user's explicit choice anyway).
        using (var ext = classes.CreateSubKey(".torrent"))
        {
            using var openWith = ext.CreateSubKey("OpenWithProgids");
            if (openWith.GetValue(ProgId) is null)
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            if (string.IsNullOrEmpty(ext.GetValue("") as string))
                ext.SetValue("", ProgId);
        }

        // magnet: protocol handler (per-user; last registered client wins, like other clients).
        using (var magnet = classes.CreateSubKey("magnet"))
        {
            magnet.SetValue("", "URL:Magnet link");
            magnet.SetValue("URL Protocol", "");
            using var cmd = magnet.CreateSubKey(@"shell\open\command");
            if (cmd.GetValue("") as string != command)
                cmd.SetValue("", command);
        }
    }

    private static void Unregister()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        if (classes is null)
            return;

        using (var ext = classes.OpenSubKey(".torrent", writable: true))
        {
            if (ext is not null)
            {
                using var openWith = ext.OpenSubKey("OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
                if (ext.GetValue("") as string == ProgId)
                    ext.SetValue("", "");
            }
        }

        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);

        // Remove the magnet handler only if it is OURS — never break another client's claim.
        bool magnetIsOurs = false;
        using (var cmd = classes.OpenSubKey(@"magnet\shell\open\command"))
        {
            string? exe = Environment.ProcessPath;
            magnetIsOurs = exe is { Length: > 0 }
                && cmd?.GetValue("") is string current
                && current.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        if (magnetIsOurs)
            classes.DeleteSubKeyTree("magnet", throwOnMissingSubKey: false);
    }
}
