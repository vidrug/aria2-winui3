using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Aria2Gui.Helpers;

/// <summary>
/// Tiny localization helper for code-behind strings. Looks up a value from the app's
/// .resw resources via the WinApp SDK <see cref="ResourceLoader"/>, which works for
/// both packaged and unpackaged apps. XAML text is localized separately via x:Uid.
/// </summary>
internal static class L
{
    // Lazily created so the resource map is touched only after the app is up.
    private static ResourceLoader? _loader;
    private static ResourceLoader Loader => _loader ??= new ResourceLoader();

    /// <summary>Returns the localized string for <paramref name="key"/>.</summary>
    public static string Get(string key) => Loader.GetString(key);

    /// <summary>Returns the localized format string for <paramref name="key"/> filled with <paramref name="args"/>.</summary>
    public static string Get(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Loader.GetString(key), args);
}
