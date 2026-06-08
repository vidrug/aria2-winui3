using Aria2Gui.Services;
using Aria2Gui.ViewModels;
using Aria2Gui.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

    /// <summary>Suppresses the filter transition on the very first selection (the initial
    /// "All" filter set in the ViewModel ctor) so the table doesn't slide in on launch.</summary>
    private bool _firstFilterChange = true;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Refresh the settings form from disk each time the page is shown, and play
        // a WinUI-style page-entrance transition (fade + slide up) as it opens.
        if (e.PropertyName == nameof(MainPageViewModel.SettingsOpen) && ViewModel.SettingsOpen)
        {
            SettingsPage.LoadFromSettings();
            AnimateSettingsIn();
        }
        else if (e.PropertyName == nameof(MainPageViewModel.SelectedFilter))
        {
            if (_firstFilterChange)
                _firstFilterChange = false;
            else
                AnimateFilterChange();
        }
    }

    /// <summary>
    /// Reproduces NavigationView's page-change transition for the in-place filtered table:
    /// a quick fade-in plus a small upward slide on the table container, mirroring the
    /// <see cref="AnimateSettingsIn"/> helper but shorter/snappier to suit a filter swap.
    /// </summary>
    private void AnimateFilterChange()
    {
        var translate = new TranslateTransform { Y = 16 };
        TableScroll.RenderTransform = translate;
        TableScroll.Opacity = 0;

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
        };
        Storyboard.SetTarget(fade, TableScroll);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            From = 16,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, translate);
        Storyboard.SetTargetProperty(slide, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        storyboard.Begin();
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

    /// <summary>Guards <see cref="NavView_SelectionChanged"/> against the re-entrant change
    /// raised when we restore the filter selection after the footer Settings gear is invoked.</summary>
    private bool _restoringNavSelection;

    /// <summary>
    /// NavigationView selection: filter items flow through the TwoWay SelectedItem binding
    /// (which drives <see cref="MainPageViewModel.SelectedFilter"/> and rebuilds the table).
    /// The footer Settings gear instead opens the settings overlay and is bounced back to the
    /// current filter, so returning from settings restores the previous filter and the gear
    /// is never left selected.
    /// </summary>
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_restoringNavSelection)
            return;

        if (args.IsSettingsSelected)
        {
            ViewModel.OpenSettingsCommand.Execute(null);
            _restoringNavSelection = true;
            sender.SelectedItem = ViewModel.SelectedFilter;
            _restoringNavSelection = false;
        }
    }

    /// <summary>x:Bind helper: visible when the given details tab is active AND something is selected.</summary>
    public static Visibility TabVisible(int current, int tab, bool hasSelection) =>
        hasSelection && current == tab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>x:Bind helper: inverse bool→Visibility.</summary>
    public static Visibility CollapsedIf(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>x:Bind helper: bool→Visibility.</summary>
    public static Visibility VisIf(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>x:Bind helper: error rows show their status in the critical (red) colour.</summary>
    public static Microsoft.UI.Xaml.Media.Brush? StatusBrush(bool isError)
    {
        string key = isError ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush";
        return Application.Current.Resources.TryGetValue(key, out var value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;
    }

    private void ColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string key } item)
            ViewModel.Columns.SetVisible(key, item.IsChecked);
    }

    private void DownloadsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection([.. DownloadsList.SelectedItems.OfType<DownloadItemViewModel>()]);

    /// <summary>
    /// Zebra-stripes a ListView/TreeViewList in place: every odd realized row gets a
    /// faint subtle tint, even rows stay transparent. Keying off the live realized index
    /// (<see cref="ContainerContentChangingEventArgs.ItemIndex"/>) rather than a stored flag
    /// makes the stripe survive virtualization recycling and add/remove/expand reordering —
    /// a recycled container is restamped for whatever row it now shows. The tint is a
    /// background fill, so the selection/hover visual states (drawn above it) still win.
    /// </summary>
    private void ZebraList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not SelectorItem container)
            return;
        container.Background = (args.ItemIndex & 1) == 1 ? ZebraBrush : null;
    }

    /// <summary>The subtle, theme-aware tint used for odd zebra rows. Resolved once per call
    /// from the live ThemeResource so it tracks Light/Dark switches.</summary>
    private static Microsoft.UI.Xaml.Media.Brush? ZebraBrush =>
        Application.Current.Resources.TryGetValue("SubtleFillColorTertiaryBrush", out var value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;

    /// <summary>
    /// The details Files <see cref="TreeView"/> renders its rows through an inner
    /// <c>TreeViewList</c> (a <see cref="ListViewBase"/>); hook that list's
    /// ContainerContentChanging so the same zebra striping applies to the flattened,
    /// currently-visible file rows and recalculates as folders expand/collapse.
    /// </summary>
    private void FilesTree_Loaded(object sender, RoutedEventArgs e)
    {
        if (FindDescendant<ListViewBase>((DependencyObject)sender) is { } innerList)
        {
            innerList.ContainerContentChanging -= ZebraList_ContainerContentChanging;
            innerList.ContainerContentChanging += ZebraList_ContainerContentChanging;
        }
    }

    /// <summary>Depth-first search of the visual tree for the first descendant of type T.</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

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
                int added = 0, duplicates = 0;
                var errors = new List<string>();
                foreach (var item in items)
                {
                    if (item is not StorageFile file || !file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        await DownloadAdder.AddTorrentFileAsync(file, dropDir);
                        added++;
                    }
                    catch (Aria2Gui.Services.Aria2.Aria2RpcException ex) when (IsDuplicate(ex))
                    {
                        duplicates++;
                    }
                    catch (Exception ex)
                    {
                        // One bad file must not abort the batch, but the user should see why.
                        errors.Add($"{file.Name}: {ex.Message}");
                    }
                }
                if (errors.Count > 0)
                    ShowAddNotice(Helpers.L.Get("AddErrorAddFailed", string.Join("; ", errors)), InfoBarSeverity.Error);
                else if (added == 0 && duplicates > 0)
                    ShowAddNotice(Helpers.L.Get("AddNoticeAlreadyAdded"), InfoBarSeverity.Informational);
            }
            else if (e.DataView.Contains(StandardDataFormats.WebLink))
            {
                var uri = await e.DataView.GetWebLinkAsync();
                await AddDroppedUrisAsync(uri.AbsoluteUri);
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                await AddDroppedUrisAsync(await e.DataView.GetTextAsync());
            }
        }
        catch (Exception ex)
        {
            ShowAddNotice(Helpers.L.Get("AddErrorAddFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>Adds dropped URI text and surfaces any failure or skipped lines.</summary>
    private async Task AddDroppedUrisAsync(string text)
    {
        var result = await DownloadAdder.AddUrisAsync(text, ResolveAddDirectory());
        if (result.Error is not null)
            ShowAddNotice(Helpers.L.Get("AddErrorPartial", result.Added, result.Error.Message), InfoBarSeverity.Error);
        else if (result.Skipped > 0)
            ShowAddNotice(Helpers.L.Get("AddErrorSkipped", result.Skipped), InfoBarSeverity.Warning);
    }

    /// <summary>aria2 reports a re-added torrent/URI with an "already exists" message.</summary>
    private static bool IsDuplicate(Aria2Gui.Services.Aria2.Aria2RpcException ex) =>
        ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("exists", StringComparison.OrdinalIgnoreCase);

    /// <summary>Shows a transient notice (error / warning / info) at the top of the window.</summary>
    private void ShowAddNotice(string message, InfoBarSeverity severity)
    {
        AddNoticeBar.Severity = severity;
        AddNoticeBar.Message = message;
        AddNoticeBar.IsOpen = true;
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
