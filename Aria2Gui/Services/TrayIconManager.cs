using System.ComponentModel;
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

    public TrayIconManager(Window window, AppWindow appWindow)
    {
        _window = window;
        _appWindow = appWindow;
    }

    public void Initialize()
    {
        var restore = new MenuFlyoutItem { Text = "Развернуть" };
        restore.Click += (_, _) => ShowWindow();

        var pauseAll = new MenuFlyoutItem { Text = "Остановить все загрузки" };
        pauseAll.Click += (_, _) => _ = Aria2.Aria2Service.Instance.Rpc.PauseAllAsync();

        var quit = new MenuFlyoutItem { Text = "Закрыть" };
        quit.Click += (_, _) => Quit();

        var menu = new MenuFlyout();
        menu.Items.Add(restore);
        menu.Items.Add(pauseAll);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quit);

        _icon = new TaskbarIcon
        {
            ToolTipText = "Aria2Gui",
            ContextFlyout = menu,
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            NoLeftClickDelay = true,
        };
        _icon.LeftClickCommand = new RelayCommandShim(ShowWindow);
        _icon.ForceCreate();

        // Hide-to-tray when the window is minimised.
        _appWindow.Changed += OnAppWindowChanged;
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
        if (args.DidPresenterChange && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            _appWindow.Hide();
    }

    private void ShowWindow()
    {
        _appWindow.Show();
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore();
        _window.Activate();
        NativeActivate();
    }

    private void NativeActivate()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        SetForegroundWindow(hwnd);
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>Minimal ICommand so the tray's left click can call a delegate.</summary>
    private sealed class RelayCommandShim(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
