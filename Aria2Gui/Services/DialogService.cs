using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aria2Gui.Services;

/// <summary>
/// Central dialog host: WinUI allows only ONE open ContentDialog per XAML root —
/// a second ShowAsync throws. All dialog entry points funnel through this guard.
/// </summary>
public static class DialogService
{
    private static bool _open;

    public static async Task ShowAsync(ContentDialog dialog)
    {
        if (_open || App.Window?.Content?.XamlRoot is not { } root)
            return;
        _open = true;
        try
        {
            dialog.XamlRoot = root;
            ApplyTheme(dialog);
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // A dialog from another code path raced us — drop this one.
        }
        finally
        {
            _open = false;
        }
    }

    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (_open || App.Window?.Content?.XamlRoot is not { } root)
            return false;
        _open = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = confirmText,
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = root,
            };
            ApplyTheme(dialog);
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _open = false;
        }
    }

    /// <summary>
    /// A ContentDialog lives in its own popup root, so it does NOT inherit the
    /// theme of the window content — its buttons would render with the system
    /// theme (light) even when the app is forced dark. ActualTheme resolves the
    /// concrete Light/Dark (never Default), so it works for both explicit themes
    /// and "follow system".
    /// </summary>
    private static void ApplyTheme(ContentDialog dialog)
    {
        if (App.Window?.Content is FrameworkElement root)
            dialog.RequestedTheme = root.ActualTheme;
    }
}
