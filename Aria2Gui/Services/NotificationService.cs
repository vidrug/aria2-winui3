using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Aria2Gui.Services;

/// <summary>Best-effort toast notifications; failures never affect the app.</summary>
public static class NotificationService
{
    private static bool _registered;

    public static void Initialize()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception)
        {
            // Toasts unavailable (e.g. identity/registration issues) — run without them.
        }
    }

    public static void ShowDownloadComplete(string name)
    {
        Show(new AppNotificationBuilder()
            .AddText(Aria2Gui.Helpers.L.Get("NotifyDownloadComplete"))
            .AddText(name));
    }

    public static void ShowDownloadError(string name)
    {
        Show(new AppNotificationBuilder()
            .AddText(Aria2Gui.Helpers.L.Get("NotifyDownloadError"))
            .AddText(name));
    }

    private static void Show(AppNotificationBuilder builder)
    {
        if (!_registered)
            return;
        try
        {
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception)
        {
        }
    }
}
