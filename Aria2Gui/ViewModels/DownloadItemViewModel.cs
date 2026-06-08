using System.Diagnostics;
using System.Globalization;
using Aria2Gui.Helpers;
using Aria2Gui.Services;
using Aria2Gui.Services.Aria2;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Aria2Gui.ViewModels;

/// <summary>Sidebar filter buckets (qBittorrent-style status groups).</summary>
public enum DownloadCategory
{
    Downloading,
    Seeding,
    Queued,
    Paused,
    Completed,
    Error,
}

/// <summary>
/// Observable wrapper for one aria2 download (keyed by gid), exposing one property
/// per table column. Instances are updated in place on every poll tick;
/// [ObservableProperty] setters no-op when the value is unchanged, so a quiet list
/// raises no change notifications at all.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly Aria2Service _service = Aria2Service.Instance;

    public string Gid { get; }

    /// <summary>Shared column widths — row templates bind cell widths to these.</summary>
    public TableColumns Columns { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string SizeText { get; set; } = "—";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusGlyph { get; set; } = "";

    [ObservableProperty]
    public partial string SeedsText { get; set; } = "—";

    [ObservableProperty]
    public partial string PeersText { get; set; } = "—";

    [ObservableProperty]
    public partial string DownSpeedText { get; set; } = "";

    [ObservableProperty]
    public partial string UpSpeedText { get; set; } = "";

    [ObservableProperty]
    public partial string EtaText { get; set; } = "";

    [ObservableProperty]
    public partial string RatioText { get; set; } = "—";

    [ObservableProperty]
    public partial string UploadedText { get; set; } = "—";

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>aria2's error reason, shown as a row tooltip so failures are visible
    /// without opening the details pane. Null on non-errored rows (no tooltip).</summary>
    [ObservableProperty]
    public partial string? StatusTooltip { get; set; }

    [ObservableProperty]
    public partial string PauseResumeText { get; set; } = L.Get("ActionPause");

    [ObservableProperty]
    public partial bool CanPauseResume { get; set; }

    [ObservableProperty]
    public partial bool HasMagnet { get; set; }

    /// <summary>Filter bucket; recomputed on every poll tick.</summary>
    public DownloadCategory Category { get; private set; } = DownloadCategory.Downloading;

    /// <summary>True while the item is present in the filtered view collection.</summary>
    public bool InView { get; set; }

    /// <summary>Set once the first completion toast decision was made (dedupes the
    /// second onDownloadComplete aria2 fires when seeding stops).</summary>
    public bool CompletionNotified { get; set; }

    // Raw state for the details pane (read on tick — no change notifications needed).
    public string RawStatus { get; private set; } = "";
    public string? Directory { get; private set; }
    public string? InfoHash { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsTorrent { get; private set; }
    public long TotalLength { get; private set; }
    public long CompletedLength { get; private set; }
    public long UploadLength { get; private set; }
    public long DownloadSpeed { get; private set; }
    public long UploadSpeed { get; private set; }
    public long Connections { get; private set; }
    public long NumSeeders { get; private set; }
    public IReadOnlyList<Aria2File>? Files { get; private set; }

    private bool _isStopped;
    private string? _firstFilePath;

    public DownloadItemViewModel(string gid, TableColumns columns)
    {
        Gid = gid;
        Columns = columns;
    }

    /// <summary>Applies the latest aria2 snapshot entry to the bindable properties.</summary>
    public void UpdateFrom(Aria2Download d)
    {
        RawStatus = d.Status;
        _isStopped = d.Status is Aria2Status.Complete or Aria2Status.Error or Aria2Status.Removed;
        Directory = NormalizePath(d.Dir);
        InfoHash = d.InfoHash;
        ErrorMessage = d.ErrorMessage;
        IsTorrent = d.IsTorrent;
        TotalLength = d.TotalLength;
        CompletedLength = d.CompletedLength;
        UploadLength = d.UploadLength;
        DownloadSpeed = d.DownloadSpeed;
        UploadSpeed = d.UploadSpeed;
        Connections = d.Connections;
        NumSeeders = d.NumSeeders;
        Files = d.Files;

        var firstFile = d.Files?.FirstOrDefault();
        bool isMetadata = firstFile?.Path.StartsWith("[METADATA]", StringComparison.Ordinal) == true;
        _firstFilePath = !isMetadata && firstFile is not null && Path.IsPathRooted(firstFile.Path)
            ? NormalizePath(firstFile.Path)
            : null;

        Name = d.GetDisplayName();
        Progress = d.TotalLength > 0
            ? d.CompletedLength * 100.0 / d.TotalLength
            : d.Status == Aria2Status.Complete ? 100 : 0;
        IsProgressIndeterminate = d.Status == Aria2Status.Active && d.TotalLength == 0;
        ProgressText = IsProgressIndeterminate
            ? "…"
            : Progress.ToString("0.#", CultureInfo.CurrentCulture) + " %";

        SizeText = d.TotalLength > 0 ? FormatUtils.FormatSize(d.TotalLength) : "—";

        (StatusText, StatusGlyph, Category) = (d.Status, isMetadata, d.Seeder) switch
        {
            (Aria2Status.Active, true, _) => (L.Get("StatusMetadata"), "", DownloadCategory.Downloading),
            (Aria2Status.Active, _, true) => (L.Get("StatusSeeding"), "", DownloadCategory.Seeding),
            (Aria2Status.Active, _, _) => (L.Get("StatusDownloading"), "", DownloadCategory.Downloading),
            (Aria2Status.Waiting, _, _) => (L.Get("StatusQueued"), "", DownloadCategory.Queued),
            (Aria2Status.Paused, _, _) => (L.Get("StatusPaused"), "", DownloadCategory.Paused),
            (Aria2Status.Complete, _, _) => (L.Get("StatusCompleted"), "", DownloadCategory.Completed),
            (Aria2Status.Error, _, _) => (L.Get("StatusError"), "", DownloadCategory.Error),
            (Aria2Status.Removed, _, _) => (L.Get("StatusRemoved"), "", DownloadCategory.Completed),
            _ => (d.Status, "", DownloadCategory.Completed),
        };
        IsError = d.Status == Aria2Status.Error;
        StatusTooltip = IsError && !string.IsNullOrEmpty(d.ErrorMessage) ? d.ErrorMessage : null;

        bool active = d.Status == Aria2Status.Active;
        SeedsText = IsTorrent && active ? d.NumSeeders.ToString(CultureInfo.CurrentCulture) : "—";
        PeersText = IsTorrent && active ? d.Connections.ToString(CultureInfo.CurrentCulture)
            : !IsTorrent && active && d.Connections > 0 ? d.Connections.ToString(CultureInfo.CurrentCulture)
            : "—";
        DownSpeedText = active && d.DownloadSpeed > 0 ? FormatUtils.FormatSpeed(d.DownloadSpeed) : "";
        UpSpeedText = active && d.UploadSpeed > 0 ? FormatUtils.FormatSpeed(d.UploadSpeed) : "";
        EtaText = active && !d.Seeder
            ? FormatUtils.FormatEta(d.TotalLength, d.CompletedLength, d.DownloadSpeed)
            : "—";
        RatioText = IsTorrent && d.TotalLength > 0 && d.UploadLength > 0
            ? (d.UploadLength / (double)d.TotalLength).ToString("0.00", CultureInfo.CurrentCulture)
            : "—";
        UploadedText = d.UploadLength > 0 ? FormatUtils.FormatSize(d.UploadLength) : "—";

        bool paused = d.Status == Aria2Status.Paused;
        IsPaused = paused;
        CanPauseResume = !_isStopped;
        PauseResumeText = paused ? L.Get("ActionResume") : L.Get("ActionPause");
        HasMagnet = !string.IsNullOrEmpty(d.InfoHash);
    }

    [RelayCommand]
    private async Task PauseResumeAsync()
    {
        try
        {
            if (RawStatus == Aria2Status.Paused)
                await _service.Rpc.UnpauseAsync(Gid);
            else if (!_isStopped)
                await _service.Rpc.PauseAsync(Gid);
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            // Status changed under us or the engine is reconnecting — next tick resyncs.
        }
    }

    [RelayCommand]
    public async Task RemoveAsync()
    {
        try
        {
            if (!_isStopped)
            {
                try
                {
                    await _service.Rpc.RemoveAsync(Gid);
                }
                catch (Aria2RpcException)
                {
                    // Stopped between polls (_isStopped is up to 1s stale) — fall
                    // through to the result removal below.
                }
            }

            // aria2.remove is asynchronous on the aria2 side: the download may still be
            // tearing down (tracker notify) for a moment, so retry the result removal.
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    await _service.Rpc.RemoveDownloadResultAsync(Gid);
                    return;
                }
                catch (Aria2RpcException)
                {
                    await Task.Delay(200);
                }
            }
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            // Already gone or engine reconnecting — the list resyncs on the next tick.
        }
    }

    [RelayCommand]
    private async Task RemoveWithFilesAsync()
    {
        bool confirmed = await DialogService.ConfirmAsync(
            L.Get("RemoveWithFilesTitle"),
            L.Get("RemoveWithFilesMessage", Name),
            L.Get("RemoveWithFilesConfirm"));
        if (!confirmed)
            return;
        await RemoveWithFilesNoConfirmAsync();
    }

    /// <summary>Removes the entry AND deletes its files on disk, WITHOUT prompting —
    /// for callers that already confirmed (e.g. a bulk delete that asks once).</summary>
    public async Task RemoveWithFilesNoConfirmAsync()
    {
        var files = Files;
        await RemoveAsync();

        // Best-effort disk cleanup: listed files + aria2 control files.
        if (files is null)
            return;
        foreach (var file in files)
        {
            try
            {
                if (Path.IsPathRooted(file.Path) && File.Exists(file.Path))
                    File.Delete(file.Path);
                string control = file.Path + ".aria2";
                if (File.Exists(control))
                    File.Delete(control);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Locked/protected file — leave it; the list entry is already gone.
            }
        }
    }

    [RelayCommand]
    private void CopyMagnet()
    {
        if (string.IsNullOrEmpty(InfoHash))
            return;
        try
        {
            var package = new DataPackage();
            package.SetText($"magnet:?xt=urn:btih:{InfoHash}&dn={Uri.EscapeDataString(Name)}");
            Clipboard.SetContent(package);
        }
        catch (Exception)
        {
            // Clipboard is flaky when another app holds it — best effort.
        }
    }

    /// <summary>
    /// Re-hash and continue a torrent whose data exists on disk but whose aria2 state
    /// was lost: remove the entry keeping files, then re-add via magnet. BuildOptions
    /// sets check-integrity, so aria2 re-hashes the existing files and resumes seeding.
    /// </summary>
    [RelayCommand]
    private async Task RecheckAsync()
    {
        if (string.IsNullOrEmpty(InfoHash))
            return;
        try
        {
            string magnet = $"magnet:?xt=urn:btih:{InfoHash}&dn={Uri.EscapeDataString(Name)}";
            await RemoveAsync();
            await DownloadAdder.AddUrisAsync(magnet, Directory);
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            // Engine reconnecting or the entry vanished under us — the list resyncs next tick.
        }
    }

    [RelayCommand]
    public void OpenFolder()
    {
        try
        {
            // Open the folder that holds the files — the file's own directory
            // (dir/<torrent name>/ for a multi-file torrent) when known, else the
            // download dir. We open the FOLDER directly rather than Explorer's
            // "/select,<file>": for the long, special-character paths torrents produce,
            // /select silently falls back to the user's profile folder (the reported bug).
            string? fileDir = !string.IsNullOrEmpty(_firstFilePath) ? Path.GetDirectoryName(_firstFilePath) : null;
            foreach (var folder in new[] { fileDir, Directory })
            {
                if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Explorer launch is best-effort.
        }
    }

    /// <summary>
    /// aria2 reports forward-slash/mixed-separator paths, which explorer.exe's
    /// "/select," argument silently ignores; a trailing "\" would also escape the
    /// closing quote. Canonicalize before handing to Explorer.
    /// </summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }
}
