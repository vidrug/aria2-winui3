using Aria2Gui.Services;
using Aria2Gui.ViewModels;
using Aria2Gui.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        ViewModel.AddDownloadRequested = () => DialogService.ShowAsync(new AddDownloadDialog());
        ViewModel.SettingsRequested = () => DialogService.ShowAsync(new SettingsDialog());
        Loaded += (_, _) => ViewModel.Initialize();
    }

    /// <summary>x:Bind helper: visible when the given details tab is active AND something is selected.</summary>
    public static Visibility TabVisible(int current, int tab, bool hasSelection) =>
        hasSelection && current == tab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>x:Bind helper: inverse bool→Visibility.</summary>
    public static Visibility CollapsedIf(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    private void DownloadsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection([.. DownloadsList.SelectedItems.OfType<DownloadItemViewModel>()]);

    private void DetailsTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.DetailsTabIndex = sender.Items.IndexOf(sender.SelectedItem);

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
        e.DragUIOverride.Caption = "Добавить в загрузки";
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is not StorageFile file || !file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        await DownloadAdder.AddTorrentFileAsync(file);
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
                await DownloadAdder.AddUrisAsync(uri.AbsoluteUri);
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                await DownloadAdder.AddUrisAsync(await e.DataView.GetTextAsync());
            }
        }
        catch (Exception)
        {
            // Drop is best-effort; bad payloads are ignored.
        }
    }
}
