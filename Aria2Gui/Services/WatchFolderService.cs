using Aria2Gui.Services.Aria2;

namespace Aria2Gui.Services;

/// <summary>
/// Watches a user-chosen folder for new .torrent files and queues them automatically
/// (qBittorrent's "monitored folder"). A queued file is renamed to *.added so it is
/// never re-added and never silently deleted. A periodic rescan (plus one on every
/// engine connect) catches files dropped while the app or engine was down — the
/// FileSystemWatcher alone would miss those.
/// </summary>
public sealed class WatchFolderService
{
    public static WatchFolderService Instance { get; } = new();

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private Timer? _rescanTimer;
    private string _folder = "";

    private WatchFolderService()
    {
    }

    /// <summary>Starts/stops/redirects the watcher to match the current settings.
    /// Safe to call repeatedly (every settings save does).</summary>
    public void Reconfigure(AppSettings settings)
    {
        bool enable = settings.WatchFolderEnabled
            && !string.IsNullOrWhiteSpace(settings.WatchFolder)
            && Directory.Exists(settings.WatchFolder);
        string folder = enable ? settings.WatchFolder : "";

        if (!enable)
        {
            Stop();
            return;
        }
        if (_watcher is not null && string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase))
            return; // unchanged

        Stop();
        _folder = folder;
        try
        {
            _watcher = new FileSystemWatcher(folder, "*.torrent")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => _ = ScanAsync();
            _watcher.Renamed += (_, _) => _ = ScanAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Stop();
            return;
        }
        // Rescan every minute: retries transient add failures and files that were still
        // being written (locked) when their event fired.
        _rescanTimer = new Timer(_ => _ = ScanAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1));
    }

    private void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _rescanTimer?.Dispose();
        _rescanTimer = null;
        _folder = "";
    }

    private async Task ScanAsync()
    {
        if (_folder.Length == 0 || !Aria2Service.Instance.Rpc.IsConnected)
            return;
        if (!await _scanLock.WaitAsync(0))
            return; // a scan is already running; the timer retries soon anyway
        try
        {
            string folder = _folder;
            foreach (var path in Directory.EnumerateFiles(folder, "*.torrent"))
            {
                try
                {
                    byte[] bytes = await File.ReadAllBytesAsync(path); // throws while still locked
                    try
                    {
                        await DownloadAdder.AddTorrentBytesAsync(bytes);
                    }
                    catch (Aria2RpcException ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
                    {
                        // Already in the list — still mark the file consumed below.
                    }
                    File.Move(path, path + ".added", overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or Aria2RpcException or InvalidOperationException or TimeoutException or InvalidDataException)
                {
                    // Locked/garbled file or engine blip — left in place; the next rescan retries.
                }
            }
        }
        finally
        {
            _scanLock.Release();
        }
    }
}
