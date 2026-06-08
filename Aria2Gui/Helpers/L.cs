using System.Globalization;
using System.Text;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Aria2Gui.Helpers;

/// <summary>
/// Localization lookup for both code-behind strings and (via <see cref="Localize"/>) all XAML
/// text. Resolves through an explicit <see cref="ResourceContext"/> whose Language qualifier is
/// pinned to the saved UI language — this is what makes the language override work in the
/// portable (unpackaged) build, where the framework's default context (used by x:Uid) always
/// falls back to the OS language and cannot be redirected.
/// </summary>
internal static class L
{
    private static readonly ResourceMap _map;
    private static readonly ResourceContext _context;

    static L()
    {
        var manager = new ResourceManager();
        _context = manager.CreateResourceContext();
        string lang = Services.SettingsService.Load().Language;
        if (!string.IsNullOrEmpty(lang))
            _context.QualifierValues["Language"] = lang;
        _map = manager.MainResourceMap;
    }

    /// <summary>Returns the localized string for <paramref name="key"/> (the key itself on miss).</summary>
    public static string Get(string key)
    {
        try
        {
            return _map.GetValue($"Resources/{KeyToPath(key)}", _context).ValueAsString;
        }
        catch
        {
            return key;
        }
    }

    /// <summary>Localized string for <paramref name="key"/>, or false if the resource is absent.</summary>
    public static bool TryGet(string key, out string value)
    {
        try
        {
            var candidate = _map.TryGetValue($"Resources/{KeyToPath(key)}", _context);
            if (candidate is null)
            {
                value = "";
                return false;
            }
            value = candidate.ValueAsString;
            return true;
        }
        catch
        {
            value = "";
            return false;
        }
    }

    /// <summary>Returns the localized format string for <paramref name="key"/> filled with <paramref name="args"/>.</summary>
    public static string Get(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    // A .resw "name" maps to a PRI resource path where '.' separators become '/', except dots
    // inside a [using:Namespace] qualifier (e.g. Foo.[using:A.B]C.D -> Foo/[using:A.B]C/D).
    private static string KeyToPath(string key)
    {
        var sb = new StringBuilder(key.Length);
        int depth = 0;
        foreach (char c in key)
        {
            if (c == '[')
                depth++;
            else if (c == ']')
                depth--;
            sb.Append(c == '.' && depth == 0 ? '/' : c);
        }
        return sb.ToString();
    }
}
