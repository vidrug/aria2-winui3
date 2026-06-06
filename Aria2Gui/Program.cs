using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Aria2Gui;

/// <summary>
/// Custom entry point enforcing a single instance per app copy: two GUIs sharing
/// one aria2 session file would corrupt each other's state. A second launch
/// redirects its activation to the running instance and exits.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var mainInstance = AppInstance.FindOrRegisterForKey(GetInstanceKey());
        if (!mainInstance.IsCurrent)
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            mainInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait(3000);
            return 0;
        }

        Application.Start(static p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// Two different portable copies may run side by side (separate data dirs);
    /// two instances of the same copy must not (shared session file).
    /// </summary>
    private static string GetInstanceKey()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant()));
        return "Aria2Gui-" + Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
