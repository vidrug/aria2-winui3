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
    /// The UI language the app launched with (the saved <c>Language</c> at startup, or "" for
    /// the OS default). The language only takes effect at launch, so Settings compares the
    /// picked language against this to know when to offer a restart.
    /// </summary>
    public static string ActiveLanguage { get; set; } = "";

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
                string path = Path.Combine(Services.AppPaths.DataDirectory, "crash.log");
                // B22: a recurring exception would grow this without bound. Rotate to crash.log.old
                // once it passes ~1 MB so total on-disk size stays capped.
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 1_000_000)
                        File.Move(path, path + ".old", overwrite: true);
                }
                catch (IOException)
                {
                }
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
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
        Window.Closed += (_, _) => RunExitCleanup();

        // A second app launch redirects here (see Program) — surface our window. Route through
        // the tray's restore: it runs under the _restoring guard, so the tray's minimize-watcher
        // can't observe the transient Minimized state and re-hide the window mid-restore (N16).
        Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, _) =>
            DispatcherQueue.TryEnqueue(() => (Window as MainWindow)?.Tray.RestoreFromTray());

        Window.Activate();

        Services.StatsService.Load();
        Services.NotificationService.Initialize();

        // Start the aria2c engine in the background; the UI reflects service state.
        _ = InitializeEngineAsync();
    }

    /// <summary>
    /// Graceful teardown: save the aria2 session, stop the engine, and remove the tray icon.
    /// Runs on window close and also before a language-change relaunch — <see cref="Microsoft.Windows.AppLifecycle.AppInstance.Restart"/>
    /// terminates the process without raising <see cref="Window.Closed"/>. Idempotent.
    /// </summary>
    public static void RunExitCleanup()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;
        Services.StatsService.Save();
        (Window as MainWindow)?.Tray.Dispose();
        Services.Aria2.Aria2Service.Instance.Shutdown();
    }

    private static bool _cleanedUp;

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
