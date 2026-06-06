using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Aria2Gui;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Tray icon: minimise-to-tray, live tooltip, restore/pause-all/quit menu.</summary>
    public Services.TrayIconManager Tray { get; }

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Comfortable default size for the qBittorrent-style table layout,
        // scaled to the monitor DPI; the column set needs ~1100px of width.
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1280 * scale), (int)(800 * scale)));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(980 * scale);
            presenter.PreferredMinimumHeight = (int)(620 * scale);
        }

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));

        // Apply the saved theme before the first frame renders. App.Window is not
        // assigned yet in this constructor, so pass our own content explicitly.
        Helpers.ThemeHelper.Apply(Content as FrameworkElement, Services.SettingsService.Load().Theme);

        Tray = new Services.TrayIconManager(this, AppWindow);
        Tray.Initialize();
        if (RootFrame.Content is MainPage page)
            Tray.AttachViewModel(page.ViewModel);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
