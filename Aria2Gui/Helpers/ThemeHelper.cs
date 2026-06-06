using Microsoft.UI.Xaml;

namespace Aria2Gui.Helpers;

public static class ThemeHelper
{
    /// <summary>Applies "Default"/"Light"/"Dark" to the given root element.</summary>
    public static void Apply(FrameworkElement? root, string theme)
    {
        if (root is null)
            return;
        root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>Applies the theme to the main window's content.</summary>
    public static void Apply(string theme) => Apply(App.Window?.Content as FrameworkElement, theme);
}
