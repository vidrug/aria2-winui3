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

    // Fixed status/action labels resolved once. The UI language is pinned at startup (see L's
    // static ctor), so these never change between ticks; UpdateFrom runs per row per second on
    // the UI thread, so caching avoids an MRT lookup + StringBuilder alloc per call (cf. FormatUtils).
    private static readonly string _statusMetadata = L.Get("StatusMetadata");
    private static readonly string _statusSeeding = L.Get("StatusSeeding");
    private static readonly string _statusDownloading = L.Get("StatusDownloading");
    private static readonly string _statusQueued = L.Get("StatusQueued");
    private static readonly string _statusPaused = L.Get("StatusPaused");
    private static readonly string _statusCompleted = L.Get("StatusCompleted");
    private static readonly string _statusError = L.Get("StatusError");
    private static readonly string _statusRemoved = L.Get("StatusRemoved");
    private static readonly string _actionPause = L.Get("ActionPause");
    private static readonly string _actionResume = L.Get("ActionResume");

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

    /// <summary>Per-download speed cap as a value in the currently selected unit
    /// (<see cref="SpeedLimitDownUnit"/>); 0 = unlimited. This is the real, UNCAPPED value bound to
    /// the number box — you can enter any speed. The matching <see cref="SpeedLimitDownSlider"/>/
    /// <see cref="SpeedLimitUpSlider"/> mirror it but clamp to the slider's 0–<see cref="SpeedSliderMax"/>
    /// range, so a value above the slider maximum shows the typed number in the box while the slider
    /// pins at its top. A change is pushed to aria2 live (per gid) as a byte count via the unit.</summary>
    [ObservableProperty]
    public partial double SpeedLimitDownMb { get; set; }

    [ObservableProperty]
    public partial double SpeedLimitUpMb { get; set; }

    /// <summary>Unit symbol (B/KB/Kb/MB/Mb) the download value above is expressed in. Changing it
    /// re-interprets the typed number in the new unit and re-applies; the slider scale follows.</summary>
    [ObservableProperty]
    public partial string SpeedLimitDownUnit { get; set; } = SpeedUnit.Default;

    [ObservableProperty]
    public partial string SpeedLimitUpUnit { get; set; } = SpeedUnit.Default;

    /// <summary>Slider position (0–<see cref="SpeedSliderMax"/> of the selected unit) mirroring the limit.</summary>
    [ObservableProperty]
    public partial double SpeedLimitDownSlider { get; set; }

    [ObservableProperty]
    public partial double SpeedLimitUpSlider { get; set; }

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
    public string? ErrorCode { get; private set; }
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

    /// <summary>Share ratio (bytes uploaded ÷ bytes downloaded). 0 until something has been
    /// downloaded. The single source of truth for both the Ratio column and ratio sorting, so
    /// the number shown always matches the sort order.</summary>
    public double Ratio => CompletedLength > 0 ? UploadLength / (double)CompletedLength : 0;

    private bool _isStopped;
    private string? _firstFilePath;

    // O5/O8: skip the per-tick string formatting when nothing that feeds a bindable column
    // changed since the last snapshot, so a quiet row costs only the raw field stores below.
    private (string, bool, bool, bool, long, long, long, long, long, long, long, string?) _lastFormatSig;
    private string _lastName = "";
    private bool _formatted;

    /// <summary>True while <see cref="LoadSpeedLimitsAsync"/> seeds the flyout fields, so the
    /// resulting property changes don't immediately push the same value back to aria2.</summary>
    private bool _suppressSpeedApply;

    /// <summary>Guards the two-way mirror between a real limit and its slider position so syncing
    /// one doesn't recurse back into the other.</summary>
    private bool _syncingSpeed;

    /// <summary>Upper bound of the speed-limit slider (in the selected unit); the box is uncapped.</summary>
    private const double SpeedSliderMax = 100;

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
        ErrorCode = d.ErrorCode;
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

        // O5/O8: bail out of the formatting below when no formatting input changed since the
        // last tick. Category/IsError/etc. all derive from these fields too, so skipping is safe.
        var sig = (d.Status, d.Seeder, isMetadata, d.IsTorrent, d.TotalLength, d.CompletedLength,
            d.UploadLength, d.DownloadSpeed, d.UploadSpeed, d.Connections, d.NumSeeders, d.ErrorMessage);
        string name = d.GetDisplayName();
        if (_formatted && _lastFormatSig == sig && _lastName == name)
            return;
        _formatted = true;
        _lastFormatSig = sig;
        _lastName = name;

        Name = name;
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
            (Aria2Status.Active, true, _) => (_statusMetadata, "", DownloadCategory.Downloading),
            (Aria2Status.Active, _, true) => (_statusSeeding, "", DownloadCategory.Seeding),
            (Aria2Status.Active, _, _) => (_statusDownloading, "", DownloadCategory.Downloading),
            (Aria2Status.Waiting, _, _) => (_statusQueued, "", DownloadCategory.Queued),
            (Aria2Status.Paused, _, _) => (_statusPaused, "", DownloadCategory.Paused),
            (Aria2Status.Complete, _, _) => (_statusCompleted, "", DownloadCategory.Completed),
            (Aria2Status.Error, _, _) => (_statusError, "", DownloadCategory.Error),
            (Aria2Status.Removed, _, _) => (_statusRemoved, "", DownloadCategory.Completed),
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
        RatioText = IsTorrent && Ratio > 0
            ? Ratio.ToString("0.00", CultureInfo.CurrentCulture)
            : "—";
        UploadedText = d.UploadLength > 0 ? FormatUtils.FormatSize(d.UploadLength) : "—";

        bool paused = d.Status == Aria2Status.Paused;
        IsPaused = paused;
        CanPauseResume = !_isStopped;
        PauseResumeText = paused ? _actionResume : _actionPause;
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
    public Task RemoveAsync() => RemoveCoreAsync();

    /// <summary>Removes the entry from aria2. Returns true only when the result purge was
    /// CONFIRMED — callers that go on to delete files from disk must not do so when the
    /// download may still be alive in the engine (it would re-create/redownload them).</summary>
    private async Task<bool> RemoveCoreAsync()
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
                    return true;
                }
                catch (Aria2RpcException)
                {
                    await Task.Delay(200);
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            // Already gone or engine reconnecting — the list resyncs on the next tick.
            return false;
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
        string? root = Directory;
        // N11: only touch the disk when aria2 CONFIRMED the entry is gone. Deleting while the
        // download is still alive in the engine (removal failed / engine reconnecting) would
        // have aria2 re-create and redownload the files — or fight us over open handles.
        if (!await RemoveCoreAsync())
            return;

        if (files is null)
            return;
        // B4: delete off the UI thread — a multi-file torrent or a slow/network disk would
        // otherwise freeze the window while File.Delete runs synchronously per file.
        await Task.Run(() => DeleteFilesAndEmptyDirs(files, root));
    }

    /// <summary>Best-effort disk cleanup: removes each listed file + its aria2 control file,
    /// then the now-empty container folders this torrent created (e.g. dir/&lt;torrent name&gt;/…) —
    /// walking up to, but never including, the shared download directory.</summary>
    private static void DeleteFilesAndEmptyDirs(IReadOnlyList<Aria2File> files, string? root)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                if (Path.IsPathRooted(file.Path) && File.Exists(file.Path))
                    File.Delete(file.Path);
                string control = file.Path + ".aria2";
                if (File.Exists(control))
                    File.Delete(control);
                if (Path.IsPathRooted(file.Path) && Path.GetDirectoryName(file.Path) is { Length: > 0 } d)
                    dirs.Add(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Locked/protected file — leave it; the list entry is already gone.
            }
        }

        // B16: prune empty folders. Without a known download root we have no safe boundary,
        // so we never touch directories in that case.
        string? stop = NormalizePath(root);
        if (string.IsNullOrEmpty(stop))
            return;
        string boundary = stop + Path.DirectorySeparatorChar;
        foreach (var start in dirs.OrderByDescending(d => d.Length))
        {
            string? dir = NormalizePath(start);
            while (!string.IsNullOrEmpty(dir) && dir.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!System.IO.Directory.Exists(dir) || System.IO.Directory.EnumerateFileSystemEntries(dir).Any())
                        break;
                    System.IO.Directory.Delete(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    break;
                }
                dir = NormalizePath(Path.GetDirectoryName(dir));
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

    public enum RecheckOutcome
    {
        Succeeded,
        /// <summary>RPC blip / re-add failed — the recheck may be retried later.</summary>
        TransientFailure,
        /// <summary>Preconditions missing (no info hash, or no metadata source) — the entry was
        /// left untouched and retrying won't help until conditions change.</summary>
        NotPossible,
    }

    [RelayCommand]
    private Task RecheckAsync() => RecheckCoreAsync();

    /// <summary>
    /// Re-hash and continue a torrent whose data exists on disk but whose aria2 state
    /// was lost: remove the entry keeping files, then re-add. BuildOptions sets
    /// check-integrity, so aria2 re-hashes the existing files and resumes seeding.
    /// Prefers the .torrent aria2 saved (bt-save-metadata) — it keeps the tracker list;
    /// the magnet fallback carries no trackers and needs DHT to fetch metadata.
    /// </summary>
    public async Task<RecheckOutcome> RecheckCoreAsync()
    {
        if (string.IsNullOrEmpty(InfoHash))
            return RecheckOutcome.NotPossible;
        string hash = InfoHash;
        try
        {
            // N12: resolve the metadata source BEFORE destroying the entry. Without the saved
            // .torrent and without DHT (e.g. privacy mode), a bare magnet could never fetch
            // metadata — the recheck would replace a working entry with a stuck [METADATA] stub.
            byte[]? torrentBytes = null;
            if (!string.IsNullOrEmpty(Directory))
            {
                string metaPath = Path.Combine(Directory, hash.ToLowerInvariant() + ".torrent");
                try
                {
                    if (File.Exists(metaPath))
                        torrentBytes = await File.ReadAllBytesAsync(metaPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable metadata file — fall back to the magnet path below.
                }
            }
            var settings = _service.Settings;
            bool dhtAvailable = settings.EnableDht && !settings.PrivacyMode;
            if (torrentBytes is null && !dhtAvailable)
                return RecheckOutcome.NotPossible;

            // B6: carry over the per-download options that a re-add would otherwise drop —
            // the file selection and the speed caps. Read them BEFORE removing the entry.
            // Auto-recovery routes through this same path, so it benefits too.
            var preserve = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var opts = await _service.Rpc.GetOptionAsync(Gid);
                foreach (var key in new[] { "select-file", "max-download-limit", "max-upload-limit" })
                    if (opts.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) && v != "0")
                        preserve[key] = v;
            }
            catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
            {
                // Options gone with the entry (or engine blip) — re-add without them.
            }

            // Force-stop, then keep purging the result until aria2's BitTorrent engine has fully
            // released this info hash. Re-adding the magnet before that makes aria2 spawn a second
            // entry that immediately errors with "already downloading" — the duplicate the user saw.
            try { await _service.Rpc.RemoveAsync(Gid, force: true); }
            catch (Aria2RpcException) { /* already stopped */ }
            for (int i = 0; i < 25; i++)
            {
                try
                {
                    try { await _service.Rpc.RemoveDownloadResultAsync(Gid); }
                    catch (Aria2RpcException) { /* not yet removable — retry */ }
                    var snap = await _service.Rpc.GetSnapshotAsync();
                    if (!snap.Downloads.Any(d => string.Equals(d.InfoHash, hash, StringComparison.OrdinalIgnoreCase)))
                        break;
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    // N9: engine blip mid-purge must not abort the whole recheck — the entry is
                    // already being torn down, so press on and retry the poll.
                }
                await Task.Delay(200);
            }

            // N9: from here the entry is GONE — a failed re-add silently loses the download,
            // so retry transient failures instead of swallowing them.
            string magnet = $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(Name)}";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (torrentBytes is not null)
                    {
                        await DownloadAdder.AddTorrentBytesAsync(torrentBytes, Directory, null, preserve);
                        return RecheckOutcome.Succeeded;
                    }
                    var result = await DownloadAdder.AddUrisAsync(magnet, Directory, preserve);
                    if (result.Error is null && result.Added > 0)
                        return RecheckOutcome.Succeeded;
                }
                catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
                {
                }
                await Task.Delay(1000);
            }
            return RecheckOutcome.TransientFailure;
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            return RecheckOutcome.TransientFailure;
        }
    }

    /// <summary>Pre-fills the speed-limit flyout with this download's current per-gid limits,
    /// without re-pushing them back to aria2 (suppressed). Called as the flyout opens.</summary>
    public async Task LoadSpeedLimitsAsync()
    {
        try
        {
            var (down, up) = await _service.Rpc.GetSpeedLimitsAsync(Gid);
            _suppressSpeedApply = true;
            long downBytes = SpeedUnit.ParseStoredBytes(down);
            long upBytes = SpeedUnit.ParseStoredBytes(up);
            // Pick the largest unit giving a value ≥ 1 (MB when 0), then show the value in it.
            (SpeedLimitDownMb, SpeedLimitDownUnit) = SpeedUnit.FromBytes(downBytes, SpeedUnit.BestUnit(downBytes));
            (SpeedLimitUpMb, SpeedLimitUpUnit) = SpeedUnit.FromBytes(upBytes, SpeedUnit.BestUnit(upBytes));
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            // Engine reconnecting — leave the fields at their last values.
        }
        finally
        {
            _suppressSpeedApply = false;
        }
    }

    // The number box holds the real (uncapped) value in the selected unit; the slider mirrors it
    // clamped to its range. Each handler syncs its counterpart (guarded against recursion) then
    // pushes once. Changing the unit keeps the typed number (it's re-interpreted in the new unit
    // by ApplySpeedLimitAsync) and just re-applies — only the byte count sent to aria2 changes.
    partial void OnSpeedLimitDownMbChanged(double value)
    {
        if (_syncingSpeed) return;
        _syncingSpeed = true;
        SpeedLimitDownSlider = Math.Clamp(value, 0, SpeedSliderMax);
        _syncingSpeed = false;
        PushSpeedLimit();
    }

    partial void OnSpeedLimitUpMbChanged(double value)
    {
        if (_syncingSpeed) return;
        _syncingSpeed = true;
        SpeedLimitUpSlider = Math.Clamp(value, 0, SpeedSliderMax);
        _syncingSpeed = false;
        PushSpeedLimit();
    }

    partial void OnSpeedLimitDownSliderChanged(double value)
    {
        if (_syncingSpeed) return;
        _syncingSpeed = true;
        SpeedLimitDownMb = value;
        _syncingSpeed = false;
        PushSpeedLimit();
    }

    partial void OnSpeedLimitUpSliderChanged(double value)
    {
        if (_syncingSpeed) return;
        _syncingSpeed = true;
        SpeedLimitUpMb = value;
        _syncingSpeed = false;
        PushSpeedLimit();
    }

    // Changing the unit re-interprets the same typed number in the new unit and re-applies.
    partial void OnSpeedLimitDownUnitChanged(string value) => PushSpeedLimit();
    partial void OnSpeedLimitUpUnitChanged(string value) => PushSpeedLimit();

    private void PushSpeedLimit()
    {
        if (!_suppressSpeedApply)
            _ = ApplySpeedLimitAsync();
    }

    /// <summary>Pushes the current per-download limits to aria2 via changeOption (per gid),
    /// converting each value+unit to a plain byte count ("0" = no limit).</summary>
    private async Task ApplySpeedLimitAsync()
    {
        try
        {
            await _service.Rpc.ChangeOptionAsync(Gid, new Dictionary<string, string>
            {
                ["max-download-limit"] = ToAriaBytes(SpeedLimitDownMb, SpeedLimitDownUnit),
                ["max-upload-limit"] = ToAriaBytes(SpeedLimitUpMb, SpeedLimitUpUnit),
            });
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            // Status changed under us or the engine is reconnecting — harmless; next open resyncs.
        }
    }

    /// <summary>value + unit → aria2 byte-count string ("0" = no limit).</summary>
    private static string ToAriaBytes(double value, string unit)
    {
        long bytes = SpeedUnit.ToBytes(value, unit);
        return bytes <= 0 ? "0" : bytes.ToString(CultureInfo.InvariantCulture);
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
