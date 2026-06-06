using Aria2Gui.Services;
using Aria2Gui.Services.Aria2;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Aria2Gui.Views;

/// <summary>Dialog for queueing new downloads: URI/magnet lines and/or a .torrent file.</summary>
public sealed partial class AddDownloadDialog : ContentDialog
{
    private StorageFile? _torrentFile;

    public AddDownloadDialog()
    {
        InitializeComponent();
    }

    private async void OnPickTorrentClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.FileTypeFilter.Add(".torrent");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            _torrentFile = file;
            TorrentFileName.Text = file.Name;
        }
    }

    private async void OnAddClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            string text = UrlsBox.Text;
            bool hasUris = !string.IsNullOrWhiteSpace(text);
            if (!hasUris && _torrentFile is null)
            {
                ShowError("Укажите хотя бы одну ссылку или выберите .torrent-файл.");
                args.Cancel = true;
                return;
            }

            if (hasUris)
            {
                int added = await DownloadAdder.AddUrisAsync(text);
                if (added == 0 && _torrentFile is null)
                {
                    ShowError("Не найдено поддерживаемых ссылок (http, https, ftp, magnet).");
                    args.Cancel = true;
                    return;
                }
            }

            if (_torrentFile is not null)
                await DownloadAdder.AddTorrentFileAsync(_torrentFile);
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException or IOException)
        {
            ShowError($"Не удалось добавить загрузку: {ex.Message}");
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
