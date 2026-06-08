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

        // Apply the saved UI language before any XAML/resources load. Empty = follow the OS.
        string language = Services.SettingsService.Load().Language;
        App.ActiveLanguage = language;
        if (!string.IsNullOrEmpty(language))
            ApplyLanguageOverride(language);

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
    /// Applies the saved UI language. A packaged build uses the OS language-override API,
    /// which drives WinUI's resource resolver; the portable (no package identity) throws on
    /// it and is skipped, so the portable always follows the OS UI language. The CLR culture
    /// is set regardless so numbers and dates format for the chosen language.
    /// </summary>
    private static void ApplyLanguageOverride(string language)
    {
        try
        {
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        }
        catch
        {
            // No package identity (portable exe) — PrimaryLanguageOverride is unavailable.
        }
        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch
        {
            // Unknown/invalid culture name — leave the OS default in place.
        }
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
