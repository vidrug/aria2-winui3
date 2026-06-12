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

        // Close-to-tray: the X button hides the window instead of exiting; the tray menu's
        // Quit (and a language-change relaunch) set IsQuitting to take the real exit path.
        Window.AppWindow.Closing += (sender, e) =>
        {
            if (!IsQuitting && Services.Aria2.Aria2Service.Instance.Settings.CloseToTray)
            {
                e.Cancel = true;
                sender.Hide();
            }
        };

        // A second app launch redirects here (see Program) — surface our window, then open the
        // add dialog pre-filled with whatever magnet/.torrent the launch carried, so the user
        // picks the save folder before anything is added. The restore routes through the tray:
        // it runs under the _restoring guard, so the minimize-watcher can't re-hide the window
        // mid-restore (N16).
        Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, e) =>
        {
            var items = ExtractActivationItems(e);
            DispatcherQueue.TryEnqueue(() =>
            {
                (Window as MainWindow)?.Tray.RestoreFromTray();
                foreach (var item in items)
                    ShowAddDialogFor(item);
            });
        };

        // This (first) launch may itself carry a magnet/.torrent — e.g. the app was closed
        // when the user clicked the link. Open the dialog once the window is up; the engine
        // connects in the background while the user picks the folder.
        var launchItems = Environment.GetCommandLineArgs().Skip(1).Where(IsActivationItem).ToList();
        if (launchItems.Count > 0)
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var item in launchItems)
                    ShowAddDialogFor(item);
            });

        var settings = Services.SettingsService.Load();
        if (settings.StartMinimized)
            Window.AppWindow.Hide(); // live in the tray; the icon's menu/click restores
        else
            Window.Activate();

        Services.StatsService.Load();
        Services.NotificationService.Initialize();

        // Start the aria2c engine in the background; the UI reflects service state.
        _ = InitializeEngineAsync();
    }

    /// <summary>Set by exit paths that must bypass close-to-tray (tray Quit, relaunch).</summary>
    public static bool IsQuitting { get; set; }

    // ---- magnet: / .torrent activation (I3) ----

    private static bool IsActivationItem(string arg) =>
        arg.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
        || arg.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    /// <summary>Routes an activated magnet/.torrent into the add dialog (creating or reusing
    /// it) so the user picks the save folder and, for torrents, the files — exactly like a
    /// manual add. UI thread only.</summary>
    private static void ShowAddDialogFor(string item)
    {
        // The modal needs a visible owner; also the user should see what's being added.
        (Window as MainWindow)?.Tray.RestoreFromTray();
        var dialog = Views.AddDownloadWindow.ActiveInstance ?? new Views.AddDownloadWindow();
        if (item.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            dialog.PrefillUri(item);
        else if (File.Exists(item))
            dialog.LoadTorrentFromPath(item);
        dialog.Activate();
    }

    /// <summary>Pulls magnet links / .torrent paths out of a redirected activation. Unpackaged
    /// launches arrive as ExtendedActivationKind.Launch with a raw command line; packaged
    /// file/protocol activations come typed.</summary>
    private static List<string> ExtractActivationItems(Microsoft.Windows.AppLifecycle.AppActivationArguments e)
    {
        var items = new List<string>();
        try
        {
            switch (e.Kind)
            {
                case Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol
                    when e.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol:
                    items.Add(protocol.Uri.AbsoluteUri);
                    break;
                case Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File
                    when e.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs files:
                    foreach (var f in files.Files)
                        if (f is Windows.Storage.IStorageFile sf)
                            items.Add(sf.Path);
                    break;
                case Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch
                    when e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch:
                    // Raw command line: a magnet URI (always one shell-quoted argument thanks to
                    // the "%1" registration) or a quoted/bare .torrent path.
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                        launch.Arguments ?? "", "\"(?<q>[^\"]+)\"|(?<u>magnet:[^\\s\"]+)|(?<p>[^\\s\"]+\\.torrent)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        string value = m.Groups["q"].Success ? m.Groups["q"].Value
                            : m.Groups["u"].Success ? m.Groups["u"].Value : m.Groups["p"].Value;
                        if (IsActivationItem(value))
                            items.Add(value);
                    }
                    break;
            }
        }
        catch
        {
            // Malformed activation payload — nothing to add.
        }
        return items;
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
