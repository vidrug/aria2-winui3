using System.ComponentModel;
using Aria2Gui.Helpers;
using Aria2Gui.ViewModels;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Aria2Gui.Services;

/// <summary>
/// System-tray presence: minimising hides the window to the tray, the icon's
/// tooltip shows live transfer speed, and its menu offers restore / pause-all /
/// quit. Built on H.NotifyIcon.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private TaskbarIcon? _icon;
    private MainPageViewModel? _viewModel;
    private MenuFlyoutItem? _toggleItem;
    private bool _restoring;

    public TrayIconManager(Window window, AppWindow appWindow)
    {
        _window = window;
        _appWindow = appWindow;
    }

    public void Initialize()
    {
        // H.NotifyIcon's default tray menu is a Win32 PopupMenu, where MenuFlyoutItem.Click
        // never fires — only the Command is invoked (that's why the icon's left-click, which
        // uses a Command, worked while the menu items did nothing). So every item drives a
        // Command, not a Click handler.
        // The toggle follows the window: "Hide to tray" while shown, "Restore" while hidden.
        _toggleItem = new MenuFlyoutItem
        {
            Text = L.Get("TrayRestore"),
            Command = new RelayCommandShim(ToggleWindow),
        };

        var pauseAll = new MenuFlyoutItem
        {
            Text = L.Get("TrayPauseAll"),
            Command = new RelayCommandShim(() => _ = Aria2.Aria2Service.Instance.Rpc.PauseAllAsync()),
        };

        var quit = new MenuFlyoutItem
        {
            Text = L.Get("TrayQuit"),
            Command = new RelayCommandShim(Quit),
        };

        var menu = new MenuFlyout();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(pauseAll);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quit);
        // Re-label the toggle for the window's current state each time the menu opens.
        menu.Opening += (_, _) => UpdateToggleText();

        _icon = new TaskbarIcon
        {
            ToolTipText = "Aria2Gui",
            ContextFlyout = menu,
            NoLeftClickDelay = true,
        };
        // Load the .ico at the tray size directly: System.Drawing picks the closest frame
        // from the multi-resolution icon, so the tray shows a crisp image instead of a
        // downscaled 256px bitmap. Loading is synchronous, so unlike a BitmapImage it
        // never flashes blank before decoding.
        _icon.Icon = LoadTrayIcon();
        _icon.LeftClickCommand = new RelayCommandShim(ShowWindow);
        _icon.ForceCreate();

        // Hide-to-tray when the window is minimised.
        _appWindow.Changed += OnAppWindowChanged;
        UpdateToggleText(); // label for the window's current (shown) state at startup
    }

    /// <summary>Loads the app icon at a tray-appropriate size, picking the closest frame
    /// from the multi-resolution .ico so the system tray renders it crisply across DPIs.</summary>
    private static System.Drawing.Icon? LoadTrayIcon()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            return new System.Drawing.Icon(path, 32, 32);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Hooks the page ViewModel so the tooltip can show live speed.</summary>
    public void AttachViewModel(MainPageViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTooltip();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainPageViewModel.GlobalSpeedText) or nameof(MainPageViewModel.CountsText))
            App.DispatcherQueue.TryEnqueue(UpdateTooltip);
    }

    private void UpdateTooltip()
    {
        if (_icon is null || _viewModel is null)
            return;
        _icon.ToolTipText = $"Aria2Gui\n{_viewModel.GlobalSpeedText}\n{_viewModel.CountsText}";
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // React to the presenter STATE (minimize), not DidPresenterChange — the latter
        // fires only when the presenter object is swapped, not on a normal minimize, so
        // gating on it would skip hide-to-tray. _restoring suppresses a re-hide during
        // the brief Minimized moment while we restore.
        if (!_restoring && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            _appWindow.Hide();
        // Keep the toggle's label in sync with visibility even if the menu's Opening
        // event doesn't fire under H.NotifyIcon.
        UpdateToggleText();
    }

    /// <summary>Labels the toggle item for the window's current visibility.</summary>
    private void UpdateToggleText()
    {
        if (_toggleItem is not null)
            _toggleItem.Text = _appWindow.IsVisible ? L.Get("TrayHide") : L.Get("TrayRestore");
    }

    /// <summary>Tray menu toggle: hide the window to the tray when it's showing, restore it
    /// when it's hidden.</summary>
    private void ToggleWindow()
    {
        if (_appWindow.IsVisible)
            _appWindow.Hide();
        else
            ShowWindow();
    }

    private void ShowWindow()
    {
        // The window was AppWindow.Hide()'d while Minimized, so it's both hidden and
        // minimized. Un-hide, un-minimize (both Presenter.Restore and SW_RESTORE for
        // reliability), then force it to the foreground. _restoring stops the Changed
        // handler from re-hiding it mid-flight.
        _restoring = true;
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            _appWindow.Show(true); // un-hide AND activate (Show() alone may leave it inactive)
            if (_appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Restore();
            ShowWindow(hwnd, SW_RESTORE);
            _window.Activate();
            SetForegroundWindow(hwnd);
        }
        finally
        {
            _restoring = false;
        }
    }

    private void Quit()
    {
        Dispose();
        _window.Close();
    }

    public void Dispose()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _appWindow.Changed -= OnAppWindowChanged;
        _icon?.Dispose();
        _icon = null;
    }

    private const int SW_RESTORE = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    /// <summary>Minimal ICommand so the tray's left click can call a delegate.</summary>
    private sealed class RelayCommandShim(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}