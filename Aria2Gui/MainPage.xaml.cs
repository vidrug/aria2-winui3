using Aria2Gui.Services;
using Aria2Gui.ViewModels;
using Aria2Gui.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Aria2Gui;

/// <summary>
/// The main content page: download list, toolbar, status bar, drag&amp;drop target.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new();

    private bool _dialogOpen;

    public MainPage()
    {
        InitializeComponent();
        ViewModel.AddDownloadRequested = () => ShowDialogAsync(new AddDownloadDialog());
        ViewModel.SettingsRequested = () => ShowDialogAsync(new SettingsDialog());
        Loaded += (_, _) => ViewModel.Initialize();
    }

    /// <summary>
    /// WinUI allows only one open ContentDialog per root — a second ShowAsync throws.
    /// Guard so rapid Ctrl+N / toolbar clicks can't crash the app.
    /// </summary>
    private async Task ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen)
            return;
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = XamlRoot;
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // A dialog from another code path raced us — drop this one.
        }
        finally
        {
            _dialogOpen = false;
        }
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
