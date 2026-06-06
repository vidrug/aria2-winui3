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
        try
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
        catch (Exception ex)
        {
            // async void handler — an escaped exception would kill the process.
            ShowError($"Не удалось открыть выбор файла: {ex.Message}");
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

            // The torrent goes first: it's a single atomic add, so a later URI
            // failure can't duplicate it on retry.
            if (_torrentFile is not null)
            {
                await DownloadAdder.AddTorrentFileAsync(_torrentFile);
                _torrentFile = null;
                TorrentFileName.Text = "";
            }

            if (hasUris)
            {
                var result = await DownloadAdder.AddUrisAsync(text);
                // Queued lines leave the box — a retry resubmits only the remainder.
                UrlsBox.Text = string.Join(Environment.NewLine, result.Remaining);

                if (result.Error is not null)
                {
                    ShowError($"Добавлено: {result.Added}. Остальные не добавились: {result.Error.Message}");
                    args.Cancel = true;
                    return;
                }
                if (result.Skipped > 0)
                {
                    ShowError($"Строк пропущено: {result.Skipped} — поддерживаются только http, https, ftp и magnet.");
                    args.Cancel = true;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Dialog-local catch-all: an escaped exception here closes the dialog
            // with zero feedback (the deferral completes in finally regardless).
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
