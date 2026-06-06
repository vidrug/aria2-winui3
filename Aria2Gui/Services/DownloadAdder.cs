using Aria2Gui.Services.Aria2;
using Windows.Storage;

namespace Aria2Gui.Services;

/// <summary>
/// Outcome of a multi-line add. <paramref name="Remaining"/> holds the lines that
/// were NOT queued (unsupported or failed + everything after the failure) so the
/// caller can put them back into the input box — retrying never duplicates
/// already-queued downloads.
/// </summary>
public sealed record AddUrisResult(int Added, int Skipped, List<string> Remaining, Exception? Error);

/// <summary>Shared add-download logic used by the add dialog and drag&amp;drop.</summary>
public static class DownloadAdder
{
    /// <summary>Splits free-form text into URI lines and queues each as a download.</summary>
    public static async Task<AddUrisResult> AddUrisAsync(string text)
    {
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var remaining = new List<string>();
        int added = 0;
        int skipped = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsSupportedUri(lines[i]))
            {
                skipped++;
                remaining.Add(lines[i]);
                continue;
            }
            try
            {
                await Aria2Service.Instance.Rpc.AddUriAsync([lines[i]]);
                added++;
            }
            catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
            {
                // Keep the failed line and everything after it for a clean retry.
                remaining.AddRange(lines[i..]);
                return new AddUrisResult(added, skipped, remaining, ex);
            }
        }
        return new AddUrisResult(added, skipped, remaining, null);
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
