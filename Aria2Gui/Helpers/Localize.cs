using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Aria2Gui.Helpers;

/// <summary>
/// A drop-in replacement for <c>x:Uid</c> that resolves .resw text through <see cref="L"/> — an
/// explicit, language-pinned resource context — instead of the WinUI framework's default context.
/// The framework's context is stuck on the OS language in the unpackaged (portable) build and can't
/// be redirected, so x:Uid ignores the in-app language picker there; this attached property does not.
/// </summary>
/// <remarks>
/// Usage in XAML: <c>loc:Localize.Uid="SomeUid"</c>. It applies every <c>SomeUid.*</c> resource it
/// finds — the same .resw keys x:Uid used (Header, Content, Text, AutomationProperties.Name, …).
/// </remarks>
public static class Localize
{
    // The plain string properties the .resw localizes, following this app's x:Uid usage.
    private static readonly string[] StringProperties =
        ["Text", "Content", "Header", "Description", "PlaceholderText", "OnContent", "OffContent", "Label", "Title", "Message"];

    private static readonly ConcurrentDictionary<(Type Type, string Property), PropertyInfo?> PropertyCache = new();

    public static readonly DependencyProperty UidProperty = DependencyProperty.RegisterAttached(
        "Uid", typeof(string), typeof(Localize), new PropertyMetadata(null, OnUidChanged));

    public static void SetUid(DependencyObject obj, string value) => obj.SetValue(UidProperty, value);

    public static string GetUid(DependencyObject obj) => (string)obj.GetValue(UidProperty);

    private static void OnUidChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not string uid || string.IsNullOrEmpty(uid))
            return;

        foreach (string property in StringProperties)
            if (L.TryGet($"{uid}.{property}", out string value))
                SetStringProperty(d, property, value);

        if (L.TryGet($"{uid}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name", out string name))
            AutomationProperties.SetName(d, name);
        if (L.TryGet($"{uid}.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip", out string tooltip))
            ToolTipService.SetToolTip(d, tooltip);
    }

    private static void SetStringProperty(DependencyObject d, string property, string value)
    {
        PropertyInfo? info = PropertyCache.GetOrAdd((d.GetType(), property), static key => key.Type.GetProperty(key.Property));
        if (info is not null && info.CanWrite)
            info.SetValue(d, value);
    }
}
