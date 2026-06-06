using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Aria2Gui;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // The app ships no custom High Contrast brushes, so let the system colours
        // apply directly instead of having Windows double-adjust them.
        HighContrastAdjustment = ApplicationHighContrastAdjustment.None;

        // Last-resort guard: log UI-thread exceptions and keep the app (and its
        // active downloads) alive instead of tearing the whole process down.
        UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Services.AppPaths.DataDirectory, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
            }
            catch
            {
            }
            e.Handled = true;
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        // Stop aria2c gracefully (saves the session) and remove the tray icon on close.
        Window.Closed += (_, _) =>
        {
            (Window as MainWindow)?.Tray.Dispose();
            Services.Aria2.Aria2Service.Instance.Shutdown();
        };

        // A second app launch redirects here (see Program) — surface our window.
        // Window.Activate() alone does not restore a minimized window.
        Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, _) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter
                    && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }
                Window.Activate();
            });

        Window.Activate();

        Services.NotificationService.Initialize();

        // Start the aria2c engine in the background; the UI reflects service state.
        _ = InitializeEngineAsync();
    }

    private static async Task InitializeEngineAsync()
    {
        try
        {
            await Services.Aria2.Aria2Service.Instance.StartAsync();
        }
        catch
        {
            // Already surfaced via Aria2Service.State / LastError.
        }
    }
}
