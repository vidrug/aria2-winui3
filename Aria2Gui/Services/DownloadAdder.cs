using Aria2Gui.Services.Aria2;
using Windows.Storage;

namespace Aria2Gui.Services;

/// <summary>Shared add-download logic used by the add dialog and drag&amp;drop.</summary>
public static class DownloadAdder
{
    /// <summary>Splits free-form text into URI lines and queues each as a download.</summary>
    public static async Task<int> AddUrisAsync(string text)
    {
        int added = 0;
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsSupportedUri(raw))
                continue;
            await Aria2Service.Instance.Rpc.AddUriAsync([raw]);
            added++;
        }
        return added;
    }

    public static async Task AddTorrentFileAsync(IStorageFile file)
    {
        byte[] bytes;
        if (!string.IsNullOrEmpty(file.Path) && File.Exists(file.Path))
        {
            bytes = await File.ReadAllBytesAsync(file.Path);
        }
        else
        {
            // Virtual items (e.g. dropped from archive views) have no filesystem path.
            using var winrtStream = await file.OpenReadAsync();
            using var stream = winrtStream.AsStreamForRead();
            using var ms = new MemoryStream((int)winrtStream.Size);
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        await Aria2Service.Instance.Rpc.AddTorrentAsync(bytes);
    }

    public static bool IsSupportedUri(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
}
