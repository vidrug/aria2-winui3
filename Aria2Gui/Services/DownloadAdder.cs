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
    public static async Task<AddUrisResult> AddUrisAsync(string text, string? directory = null)
    {
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = BuildOptions(directory, null);
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
                await Aria2Service.Instance.Rpc.AddUriAsync([lines[i]], options);
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

    /// <param name="selectFile">aria2 select-file value ("1,3,5") or null for all files.</param>
    public static async Task AddTorrentBytesAsync(byte[] torrent, string? directory = null, string? selectFile = null) =>
        await Aria2Service.Instance.Rpc.AddTorrentAsync(torrent, BuildOptions(directory, selectFile));

    public static async Task AddTorrentFileAsync(IStorageFile file, string? directory = null, string? selectFile = null) =>
        await AddTorrentBytesAsync(await ReadTorrentBytesAsync(file), directory, selectFile);

    public static async Task<byte[]> ReadTorrentBytesAsync(IStorageFile file)
    {
        if (!string.IsNullOrEmpty(file.Path) && File.Exists(file.Path))
            return await File.ReadAllBytesAsync(file.Path);

        // Virtual items (e.g. dropped from archive views) have no filesystem path.
        using var winrtStream = await file.OpenReadAsync();
        using var stream = winrtStream.AsStreamForRead();
        using var ms = new MemoryStream((int)winrtStream.Size);
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static Dictionary<string, string>? BuildOptions(string? directory, string? selectFile)
    {
        if (directory is null && selectFile is null)
            return null;
        var options = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(directory))
            options["dir"] = directory;
        if (!string.IsNullOrEmpty(selectFile))
            options["select-file"] = selectFile;
        return options.Count > 0 ? options : null;
    }

    public static bool IsSupportedUri(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
}
