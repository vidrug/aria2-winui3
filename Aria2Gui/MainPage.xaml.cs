using Aria2Gui.Services;
using Aria2Gui.ViewModels;
using Aria2Gui.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Aria2Gui;

/// <summary>
/// The main content page: qBittorrent-style layout — filter sidebar, download
/// table, details pane — plus toolbar, status bar and drag&amp;drop target.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        // A separate modal window (not an in-window dialog) hosts the add UI; it
        // shows itself in its constructor and blocks the main window until closed.
        ViewModel.AddDownloadRequested = () =>
        {
            _ = new AddDownloadWindow();
            return Task.CompletedTask;
        };
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => ViewModel.Initialize();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Refresh the settings form from disk each time the page is shown, and play
        // a WinUI-style page-entrance transition (fade + slide up) as it opens.
        if (e.PropertyName == nameof(MainPageViewModel.SettingsOpen) && ViewModel.SettingsOpen)
        {
            SettingsPage.LoadFromSettings();
            AnimateSettingsIn();
        }
    }

    /// <summary>Mimics WinUI's navigation entrance transition for the settings page.</summary>
    private void AnimateSettingsIn()
    {
        var translate = new TranslateTransform { Y = 28 };
        SettingsPage.RenderTransform = translate;
        SettingsPage.Opacity = 0;

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
        };
        Storyboard.SetTarget(fade, SettingsPage);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            From = 28,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, translate);
        Storyboard.SetTargetProperty(slide, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        storyboard.Begin();
    }

    private void SettingsPage_Closed(object? sender, EventArgs e) => ViewModel.SettingsOpen = false;

    /// <summary>x:Bind helper: visible when the given details tab is active AND something is selected.</summary>
    public static Visibility TabVisible(int current, int tab, bool hasSelection) =>
        hasSelection && current == tab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>x:Bind helper: inverse bool→Visibility.</summary>
    public static Visibility CollapsedIf(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>x:Bind helper: bool→Visibility.</summary>
    public static Visibility VisIf(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// AppBarButton doesn't drive AnimatedIcon state on its own (unlike Button or
    /// NavigationViewItem), so play the hover animation from the pointer events.
    /// </summary>
    private void AnimatedButton_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        AnimatedIcon.SetState(SettingsAnimatedIcon, "PointerOver");

    private void AnimatedButton_PointerExited(object sender, PointerRoutedEventArgs e) =>
        AnimatedIcon.SetState(SettingsAnimatedIcon, "Normal");

    private void ColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string key } item)
            ViewModel.Columns.SetVisible(key, item.IsChecked);
    }

    // Width the sidebar returns to when expanded again (remembers user resizing).
    private double _expandedSidebarWidth = 220;

    /// <summary>Hamburger: collapse the sidebar to icons-only (narrow column, labels hidden)
    /// or restore it to its previous width.</summary>
    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        bool collapse = !ViewModel.SidebarCollapsed;
        ViewModel.SidebarCollapsed = collapse;
        if (collapse)
        {
            if (SidebarColumn.ActualWidth > 0)
                _expandedSidebarWidth = SidebarColumn.ActualWidth;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(52);
        }
        else
        {
            SidebarColumn.MinWidth = 160;
            SidebarColumn.Width = new GridLength(_expandedSidebarWidth);
        }
    }

    private void DownloadsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection([.. DownloadsList.SelectedItems.OfType<DownloadItemViewModel>()]);

    private void DetailsTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.DetailsTabIndex = sender.Items.IndexOf(sender.SelectedItem);

    private void HeaderHandle_Resize(object? sender, double horizontalChange)
    {
        if ((sender as FrameworkElement)?.Tag is string key)
            ViewModel.Columns.ResizeBy(key, horizontalChange);
    }

    /// <summary>Header tap → sort by that column (a repeat tap flips the direction).</summary>
    private void Header_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key })
            ViewModel.ToggleSort(key);
    }

    /// <summary>x:Bind helper: resolves the localized column header (by resource key) and
    /// appends a sort-direction arrow when that column is the active sort.</summary>
    public static string HeaderLabel(string resKey, string key, string? sortKey, bool descending)
    {
        string label = Helpers.L.Get(resKey);
        return key == sortKey ? $"{label}  {(descending ? "▾" : "▴")}" : label;
    }

    /// <summary>x:Bind helper: folder vs file glyph for the details file tree.</summary>
    public static string FileNodeGlyph(bool isFolder) => isFolder ? "" : "";

    /// <summary>
    /// Keep the table content at least as wide as the viewport so the columns fill
    /// the window when they fit; when their total exceeds it, the content grows
    /// past the viewport and the shared horizontal scrollbar appears.
    /// </summary>
    private void TableScroll_SizeChanged(object sender, SizeChangedEventArgs e) =>
        TableContent.MinWidth = TableScroll.ViewportWidth;

    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItemViewModel item)
            item.OpenFolder();
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation =
            e.DataView.Contains(StandardDataFormats.StorageItems)
            || e.DataView.Contains(StandardDataFormats.Text)
            || e.DataView.Contains(StandardDataFormats.WebLink)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        e.DragUIOverride.Caption = Helpers.L.Get("DragCaptionAddToDownloads");
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                string? dropDir = ResolveAddDirectory();
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is not StorageFile file || !file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        await DownloadAdder.AddTorrentFileAsync(file, dropDir);
                    }
                    catch (Exception)
                    {
                        // One bad file must not abort the rest of the dropped batch.
                    }
                }
            }
            else if (e.DataView.Contains(StandardDataFormats.WebLink))
            {
                var uri = await e.DataView.GetWebLinkAsync();
                await DownloadAdder.AddUrisAsync(uri.AbsoluteUri, ResolveAddDirectory());
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                await DownloadAdder.AddUrisAsync(await e.DataView.GetTextAsync(), ResolveAddDirectory());
            }
        }
        catch (Exception)
        {
            // Drop is best-effort; bad payloads are ignored.
        }
    }

    /// <summary>Folder that drag-and-drop adds drop into: the one the user last added to
    /// (so the location is remembered, matching the Add dialog), falling back to the
    /// configured download folder. Without this, drops always went to aria2's default.</summary>
    private static string? ResolveAddDirectory()
    {
        var settings = Aria2Gui.Services.Aria2.Aria2Service.Instance.Settings;
        if (!string.IsNullOrWhiteSpace(settings.LastAddDirectory) && System.IO.Directory.Exists(settings.LastAddDirectory))
            return settings.LastAddDirectory;
        return string.IsNullOrWhiteSpace(settings.DownloadDirectory) ? null : settings.DownloadDirectory;
    }
}
