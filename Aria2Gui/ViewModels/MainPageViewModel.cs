using System.Collections.ObjectModel;
using System.Globalization;
using Aria2Gui.Helpers;
using Aria2Gui.Services;
using Aria2Gui.Services.Aria2;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace Aria2Gui.ViewModels;

/// <summary>
/// Main page ViewModel: qBittorrent-style layout state — status filters in the
/// sidebar, a filtered table of downloads, a details pane for the selection —
/// fed by a 1-second multicall poll merged into existing item ViewModels in place.
/// </summary>
public sealed partial class MainPageViewModel : ObservableObject
{
    private readonly Aria2Service _service = Aria2Service.Instance;
    private readonly Dictionary<string, DownloadItemViewModel> _byGid = [];
    private readonly List<DownloadItemViewModel> _all = []; // master order (arrival)
    private IReadOnlyList<DownloadItemViewModel> _selection = [];
    private DispatcherQueueTimer? _timer;
    private bool _refreshing;
    private bool _peersRefreshing;
    private bool _initialized;

    /// <summary>Filtered projection of <see cref="_all"/> shown in the table.</summary>
    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

    /// <summary>Shared resizable column widths (header + all rows).</summary>
    public TableColumns Columns { get; } = new();

    public FilterItemViewModel[] Filters { get; } =
    [
        new("all", "Все", ""),
        new("downloading", "Загружаются", ""),
        new("seeding", "Раздаются", ""),
        new("completed", "Завершённые", ""),
        new("paused", "Приостановленные", ""),
        new("queued", "В очереди", ""),
        new("error", "Ошибки", ""),
    ];

    [ObservableProperty]
    public partial FilterItemViewModel? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial DownloadItemViewModel? SelectedDownload { get; set; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial int DetailsTabIndex { get; set; }

    /// <summary>Active sort column key (null = aria2 master order); see <see cref="ToggleSort"/>.</summary>
    [ObservableProperty]
    public partial string? SortKey { get; set; }

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    [ObservableProperty]
    public partial string EngineStatusText { get; set; } = "Запуск aria2…";

    [ObservableProperty]
    public partial bool IsEngineReady { get; set; }

    [ObservableProperty]
    public partial bool HasEngineError { get; set; }

    [ObservableProperty]
    public partial string EngineErrorText { get; set; } = "";

    [ObservableProperty]
    public partial string GlobalSpeedText { get; set; } = "↓ 0 Б/с   ↑ 0 Б/с";

    [ObservableProperty]
    public partial string CountsText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>True while the in-app settings page covers the download list.</summary>
    [ObservableProperty]
    public partial bool SettingsOpen { get; set; }

    // ---- details pane (Общие) ----
    [ObservableProperty]
    public partial string DetSavePath { get; set; } = "";

    [ObservableProperty]
    public partial string DetSize { get; set; } = "";

    [ObservableProperty]
    public partial string DetStatus { get; set; } = "";

    [ObservableProperty]
    public partial string DetHash { get; set; } = "";

    [ObservableProperty]
    public partial string DetRatio { get; set; } = "";

    [ObservableProperty]
    public partial string DetUploaded { get; set; } = "";

    [ObservableProperty]
    public partial string DetSpeed { get; set; } = "";

    [ObservableProperty]
    public partial string DetConnections { get; set; } = "";

    [ObservableProperty]
    public partial string DetError { get; set; } = "";

    [ObservableProperty]
    public partial bool DetHasError { get; set; }

    /// <summary>Folder/file tree of the selected torrent, shown in the details "Files" tab.</summary>
    public ObservableCollection<FileTreeNodeViewModel> FileTree { get; } = [];

    private readonly Dictionary<int, FileTreeNodeViewModel> _fileLeaves = [];
    private string? _fileTreeGid;

    /// <summary>
    /// The user's just-requested selected-file set, held as the source of truth for
    /// the checkboxes until aria2 confirms it (so a poll can't revert the choice mid-flight).
    /// </summary>
    private (string Gid, HashSet<int> Selected, int Attempts)? _pendingSelection;

    public ObservableCollection<PeerRowViewModel> Peers { get; } = [];

    /// <summary>Hooked by MainPage to open the add dialog (views own dialogs).</summary>
    public Func<Task>? AddDownloadRequested { get; set; }

    public MainPageViewModel()
    {
        SelectedFilter = Filters[0];
    }

    /// <summary>Called once from MainPage.Loaded — events may fire before the page exists.</summary>
    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        _service.StateChanged += state => App.DispatcherQueue.TryEnqueue(() => ApplyEngineState(state));
        _service.DownloadNotification += (method, gid) =>
            App.DispatcherQueue.TryEnqueue(() => _ = HandleNotificationAsync(method, gid));
        ApplyEngineState(_service.State); // catch up on anything missed before Loaded

        _timer = App.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
    }

    /// <summary>Called by the page on ListView.SelectionChanged.</summary>
    public void SetSelection(IReadOnlyList<DownloadItemViewModel> items)
    {
        _selection = items;
        HasSelection = items.Count > 0;
        SelectedDownload = items.Count > 0 ? items[0] : null;
        UpdateDetails();
    }

    partial void OnSelectedFilterChanged(FilterItemViewModel? value) => RebuildFilteredView();

    partial void OnDetailsTabIndexChanged(int value)
    {
        if (value == 2)
            _ = RefreshPeersAsync();
    }

    private void ApplyEngineState(Aria2ServiceState state)
    {
        IsEngineReady = state == Aria2ServiceState.Running;
        HasEngineError = state == Aria2ServiceState.Failed;
        EngineStatusText = state switch
        {
            Aria2ServiceState.Starting => "Запуск aria2…",
            Aria2ServiceState.Running => "aria2 работает",
            Aria2ServiceState.Restarting => "Перезапуск aria2…",
            Aria2ServiceState.Failed => "aria2 не запустился",
            _ => "aria2 остановлен",
        };
        if (state == Aria2ServiceState.Failed)
            EngineErrorText = _service.LastError ?? "Неизвестная ошибка.";
        if (state == Aria2ServiceState.Running)
            _ = RefreshAsync();
    }

    /// <summary>
    /// Refresh BEFORE toasting so notifications use final state: intermediate
    /// entries (magnet metadata, .torrent-file fetches) gain followedBy and get
    /// pruned by the refresh, which suppresses their toasts naturally, and
    /// just-stopped downloads are registered before the gid lookup.
    /// </summary>
    private async Task HandleNotificationAsync(string method, string gid)
    {
        await RefreshAsync();
        NotifyDownloadEvent(method, gid);
    }

    /// <summary>Toasts for completions/errors — only when the app is in the background.</summary>
    private void NotifyDownloadEvent(string method, string gid)
    {
        if (!_byGid.TryGetValue(gid, out var item))
            return;
        if (method is "aria2.onDownloadComplete" or "aria2.onBtDownloadComplete")
        {
            // onBtDownloadComplete fires when the payload finishes and seeding
            // starts; onDownloadComplete fires again when seeding stops — only
            // the first completion deserves a toast.
            bool alreadyNotified = item.CompletionNotified;
            item.CompletionNotified = true;
            if (!alreadyNotified && !IsWindowForeground()
                && !item.Name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
            {
                NotificationService.ShowDownloadComplete(item.Name);
            }
        }
        else if (method == "aria2.onDownloadError" && !IsWindowForeground())
        {
            NotificationService.ShowDownloadError(item.Name);
        }
    }

    private static bool IsWindowForeground() => GetForegroundWindow() == App.WindowHandle;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private async Task RefreshAsync()
    {
        if (_refreshing || !_service.Rpc.IsConnected)
            return;
        _refreshing = true;
        try
        {
            var snapshot = await _service.Rpc.GetSnapshotAsync();
            ApplySnapshot(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Engine restarting/disconnected — state events drive the UI; next tick retries.
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplySnapshot(Aria2Snapshot snapshot)
    {
        var seen = new HashSet<string>(snapshot.Downloads.Count);
        foreach (var download in snapshot.Downloads)
        {
            // Hide intermediate entries (magnet metadata / .torrent-file fetches)
            // once they have spawned the real download — they only add clutter.
            if (download.FollowedBy is { Count: > 0 })
                continue;

            seen.Add(download.Gid);
            if (_byGid.TryGetValue(download.Gid, out var item))
            {
                item.UpdateFrom(download);
            }
            else
            {
                item = new DownloadItemViewModel(download.Gid, Columns);
                item.UpdateFrom(download);
                _byGid[download.Gid] = item;
                // A download spawned from a metadata fetch takes its parent's
                // position instead of jumping to the bottom of the list.
                int masterIndex = _all.Count;
                if (download.Following is { Length: > 0 } parentGid
                    && _byGid.TryGetValue(parentGid, out var parent)
                    && _all.IndexOf(parent) is int parentIndex and >= 0)
                {
                    masterIndex = parentIndex;
                }
                _all.Insert(masterIndex, item);
            }
            SyncViewMembership(item);
        }

        // Prune rows only when the snapshot is complete: if the tell* windows were
        // truncated, a missing gid does not mean the download is gone.
        long expectedTotal = snapshot.GlobalStat.NumActive + snapshot.GlobalStat.NumWaiting + snapshot.GlobalStat.NumStopped;
        if (snapshot.Downloads.Count >= expectedTotal)
        {
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                var item = _all[i];
                if (seen.Contains(item.Gid))
                    continue;
                _byGid.Remove(item.Gid);
                _all.RemoveAt(i);
                if (item.InView)
                {
                    item.InView = false;
                    Downloads.Remove(item);
                }
            }
        }

        UpdateFilterCounts();
        UpdateDetails();
        ApplySortToView();

        IsEmpty = Downloads.Count == 0;
        GlobalSpeedText = $"↓ {FormatUtils.FormatSpeed(snapshot.GlobalStat.DownloadSpeed)}   ↑ {FormatUtils.FormatSpeed(snapshot.GlobalStat.UploadSpeed)}";
        CountsText = $"Активных: {snapshot.GlobalStat.NumActive} • В очереди: {snapshot.GlobalStat.NumWaiting} • Завершённых: {snapshot.GlobalStat.NumStopped}";
    }

    // ------------------------------------------------------------------ filtering

    private bool MatchesFilter(DownloadItemViewModel item) => SelectedFilter?.Key switch
    {
        "downloading" => item.Category == DownloadCategory.Downloading,
        "seeding" => item.Category == DownloadCategory.Seeding,
        "completed" => item.Category == DownloadCategory.Completed,
        "paused" => item.Category == DownloadCategory.Paused,
        "queued" => item.Category == DownloadCategory.Queued,
        "error" => item.Category == DownloadCategory.Error,
        _ => true,
    };

    /// <summary>Adds/removes one item from the filtered view, keeping master order.</summary>
    private void SyncViewMembership(DownloadItemViewModel item)
    {
        bool matches = MatchesFilter(item);
        if (matches == item.InView)
            return;
        if (matches)
        {
            // Position = number of in-view items that precede it in master order.
            int viewIndex = 0;
            foreach (var other in _all)
            {
                if (ReferenceEquals(other, item))
                    break;
                if (other.InView)
                    viewIndex++;
            }
            item.InView = true;
            Downloads.Insert(viewIndex, item);
        }
        else
        {
            item.InView = false;
            Downloads.Remove(item);
        }
    }

    private void RebuildFilteredView()
    {
        foreach (var item in _all)
            item.InView = false;
        Downloads.Clear();
        foreach (var item in _all)
        {
            if (MatchesFilter(item))
            {
                item.InView = true;
                Downloads.Add(item);
            }
        }
        ApplySortToView();
        IsEmpty = Downloads.Count == 0;
    }

    // ------------------------------------------------------------------ sorting

    /// <summary>
    /// Header click: first click sorts ascending by that column, a repeat click on
    /// the same column flips the direction. Sorting overrides the aria2 master order
    /// and is re-applied on every poll so live columns (speed, progress) stay ordered.
    /// </summary>
    public void ToggleSort(string key)
    {
        if (SortKey == key)
            SortDescending = !SortDescending;
        else
        {
            SortKey = key;
            SortDescending = false;
        }
        ApplySortToView();
    }

    /// <summary>Reorders the visible collection in place (Move keeps selection/scroll).</summary>
    private void ApplySortToView()
    {
        if (SortKey is null || Downloads.Count < 2)
            return;
        var ordered = Downloads.ToList();
        ordered.Sort(CompareItems);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (!ReferenceEquals(Downloads[i], ordered[i]))
                Downloads.Move(Downloads.IndexOf(ordered[i]), i);
        }
    }

    private int CompareItems(DownloadItemViewModel a, DownloadItemViewModel b)
    {
        int c = SortKey switch
        {
            "name" => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            "size" => a.TotalLength.CompareTo(b.TotalLength),
            "progress" => a.Progress.CompareTo(b.Progress),
            "status" => ((int)a.Category).CompareTo((int)b.Category),
            "seeds" => a.NumSeeders.CompareTo(b.NumSeeders),
            "peers" => a.Connections.CompareTo(b.Connections),
            "down" => a.DownloadSpeed.CompareTo(b.DownloadSpeed),
            "up" => a.UploadSpeed.CompareTo(b.UploadSpeed),
            "eta" => EtaSeconds(a).CompareTo(EtaSeconds(b)),
            "ratio" => Ratio(a).CompareTo(Ratio(b)),
            _ => 0,
        };
        // Stable tiebreaker so equal rows don't shuffle each poll.
        if (c == 0)
            c = string.Compare(a.Gid, b.Gid, StringComparison.Ordinal);
        return SortDescending ? -c : c;
    }

    /// <summary>Seconds left, or long.MaxValue when there is no finite ETA (sorts last).</summary>
    private static long EtaSeconds(DownloadItemViewModel d)
    {
        long remaining = d.TotalLength - d.CompletedLength;
        return d.DownloadSpeed > 0 && remaining > 0 ? remaining / d.DownloadSpeed : long.MaxValue;
    }

    private static double Ratio(DownloadItemViewModel d) =>
        d.CompletedLength > 0 ? d.UploadLength / (double)d.CompletedLength : 0;

    private void UpdateFilterCounts()
    {
        Span<int> counts = stackalloc int[6];
        foreach (var item in _all)
            counts[(int)item.Category]++;
        foreach (var filter in Filters)
        {
            filter.Count = filter.Key switch
            {
                "downloading" => counts[(int)DownloadCategory.Downloading],
                "seeding" => counts[(int)DownloadCategory.Seeding],
                "queued" => counts[(int)DownloadCategory.Queued],
                "paused" => counts[(int)DownloadCategory.Paused],
                "completed" => counts[(int)DownloadCategory.Completed],
                "error" => counts[(int)DownloadCategory.Error],
                _ => _all.Count,
            };
        }
    }

    // ------------------------------------------------------------------ details pane

    private void UpdateDetails()
    {
        var item = SelectedDownload;
        if (item is null || !_byGid.ContainsKey(item.Gid))
        {
            if (FileTree.Count > 0)
            {
                FileTree.Clear();
                _fileLeaves.Clear();
                _fileTreeGid = null;
                _pendingSelection = null;
            }
            if (Peers.Count > 0)
                Peers.Clear();
            return;
        }

        DetSavePath = item.Directory ?? "";
        DetSize = item.TotalLength > 0
            ? $"{FormatUtils.FormatSize(item.CompletedLength)} из {FormatUtils.FormatSize(item.TotalLength)}"
            : FormatUtils.FormatSize(item.CompletedLength);
        DetStatus = item.StatusText;
        DetHash = item.InfoHash ?? "—";
        DetRatio = item.RatioText;
        DetUploaded = FormatUtils.FormatSize(item.UploadLength);
        DetSpeed = $"↓ {FormatUtils.FormatSpeed(item.DownloadSpeed)}   ↑ {FormatUtils.FormatSpeed(item.UploadSpeed)}";
        DetConnections = item.IsTorrent
            ? $"Сиды: {item.NumSeeders} • Пиры: {item.Connections}"
            : $"Соединения: {item.Connections}";
        DetError = item.ErrorMessage ?? "";
        DetHasError = !string.IsNullOrEmpty(item.ErrorMessage);

        UpdateFileTree(item);
        if (DetailsTabIndex == 2)
            _ = RefreshPeersAsync();
    }

    /// <summary>
    /// Builds the folder/file tree for the details "Files" tab the first time a
    /// torrent is selected, then refreshes per-file progress/selection in place on
    /// each poll (rebuilding only when the selection or file set changes), so the
    /// expand state and folder aggregates survive the 1-second refresh.
    /// </summary>
    private void UpdateFileTree(DownloadItemViewModel item)
    {
        var files = item.Files;
        int count = files?.Count ?? 0;
        if (count == 0)
        {
            if (FileTree.Count > 0)
            {
                FileTree.Clear();
                _fileLeaves.Clear();
                _fileTreeGid = null;
                _pendingSelection = null;
            }
            return;
        }

        if (_fileTreeGid != item.Gid || _fileLeaves.Count != count)
            BuildFileTree(item, files!);

        // While a selection the user just made hasn't been confirmed by aria2 yet,
        // show THAT selection (not aria2's lagging snapshot) so the poll can't revert
        // the checkbox the moment it's clicked.
        bool pending = _pendingSelection is { } p && p.Gid == item.Gid;
        HashSet<int>? desired = pending ? _pendingSelection!.Value.Selected : null;

        foreach (var file in files!)
        {
            if (!_fileLeaves.TryGetValue((int)file.Index, out var leaf))
                continue;
            leaf.Length = file.Length;
            leaf.CompletedLength = file.CompletedLength;
            leaf.SizeText = FormatUtils.FormatSize(file.Length);
            leaf.Progress = file.Length > 0 ? file.CompletedLength * 100.0 / file.Length : 0;
            leaf.ProgressText = leaf.Progress.ToString("0.#", CultureInfo.CurrentCulture) + " %";
            leaf.SetSelectedFromSnapshot(desired is not null ? desired.Contains((int)file.Index) : file.Selected);
        }
        foreach (var root in FileTree)
            Aggregate(root);

        if (pending)
            ReconcilePendingSelection(files!, desired!);
    }

    /// <summary>
    /// Holds the user's just-made selection visible until aria2 confirms it. Applying
    /// select-file makes aria2 pause+restart the torrent (~5s, it may re-contact the
    /// tracker), so we must NOT re-push every poll — that would retrigger the restart
    /// and never settle. Just wait; give up after ~15s so a change aria2 won't apply
    /// (e.g. magnet metadata not ready) eventually resyncs to aria2's real state.
    /// </summary>
    private void ReconcilePendingSelection(IReadOnlyList<Aria2File> files, HashSet<int> desired)
    {
        var actual = files.Where(f => f.Selected).Select(f => (int)f.Index).ToHashSet();
        if (actual.SetEquals(desired))
        {
            _pendingSelection = null; // aria2 applied the change
            return;
        }
        var (gid, set, attempts) = _pendingSelection!.Value;
        _pendingSelection = attempts >= 15 ? null : (gid, set, attempts + 1);
    }

    /// <summary>Groups aria2's flat file list into a folder tree by relative path.</summary>
    private void BuildFileTree(DownloadItemViewModel item, IReadOnlyList<Aria2File> files)
    {
        FileTree.Clear();
        _fileLeaves.Clear();
        _fileTreeGid = item.Gid;
        _pendingSelection = null; // a different torrent / changed file set invalidates the pending pick

        string dir = (item.Directory ?? "").Replace('/', '\\').TrimEnd('\\');
        var root = new FileBucket();
        foreach (var file in files)
        {
            string full = file.Path.Replace('/', '\\');
            string rel = dir.Length > 0 && full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)
                ? full[(dir.Length + 1)..]
                : Path.GetFileName(full);
            var segments = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                segments = [Path.GetFileName(full)];

            var bucket = root;
            for (int s = 0; s < segments.Length - 1; s++)
            {
                if (!bucket.Dirs.TryGetValue(segments[s], out var next))
                    bucket.Dirs[segments[s]] = next = new FileBucket();
                bucket = next;
            }
            bucket.Files.Add((segments[^1], file));
        }

        foreach (var node in Materialize(root, item))
            FileTree.Add(node);
    }

    private IEnumerable<FileTreeNodeViewModel> Materialize(FileBucket bucket, DownloadItemViewModel item)
    {
        foreach (var (name, sub) in bucket.Dirs)
        {
            var folder = new FileTreeNodeViewModel { IsFolder = true, Name = name, IsExpanded = false };
            folder.SelectionToggled = () => _ = ApplyFileSelectionAsync(item);
            foreach (var child in Materialize(sub, item))
                folder.Children.Add(child);
            yield return folder;
        }
        foreach (var (name, file) in bucket.Files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var leaf = new FileTreeNodeViewModel { IsFolder = false, Name = name, Index = (int)file.Index };
            leaf.SelectionToggled = () => _ = ApplyFileSelectionAsync(item);
            _fileLeaves[(int)file.Index] = leaf;
            yield return leaf;
        }
    }

    /// <summary>Rolls a folder's children up into its size/progress and three-state checkbox.</summary>
    private static (long Length, long Completed, int Selected, int Total) Aggregate(FileTreeNodeViewModel node)
    {
        if (!node.IsFolder)
            return (node.Length, node.CompletedLength, node.IsSelected == true ? 1 : 0, 1);

        long length = 0, completed = 0;
        int selected = 0, total = 0;
        foreach (var child in node.Children)
        {
            var (l, c, s, t) = Aggregate(child);
            length += l;
            completed += c;
            selected += s;
            total += t;
        }
        node.Length = length;
        node.CompletedLength = completed;
        node.SizeText = FormatUtils.FormatSize(length);
        node.Progress = length > 0 ? completed * 100.0 / length : 0;
        node.ProgressText = node.Progress.ToString("0.#", CultureInfo.CurrentCulture) + " %";
        node.SetSelectedFromSnapshot(selected == 0 ? false : selected == total ? true : null);
        return (length, completed, selected, total);
    }

    /// <summary>Intermediate folder used while grouping path segments into the tree.</summary>
    private sealed class FileBucket
    {
        public SortedDictionary<string, FileBucket> Dirs { get; } = new(StringComparer.CurrentCultureIgnoreCase);
        public List<(string Name, Aria2File File)> Files { get; } = [];
    }

    /// <summary>
    /// Records the user's per-file checkbox state as the pending selection and pushes
    /// it to aria2 (select-file). The pending set keeps the checkboxes from being
    /// reverted by the poll until aria2 confirms it. aria2 needs at least one file
    /// selected, so an all-unchecked state is rejected and reverted to "all".
    /// </summary>
    private Task ApplyFileSelectionAsync(DownloadItemViewModel item)
    {
        if (!ReferenceEquals(SelectedDownload, item))
            return Task.CompletedTask;
        // A folder toggle cascades to leaves; refresh ancestor folder checkboxes
        // before reading which files are now selected.
        foreach (var root in FileTree)
            Aggregate(root);
        var indices = _fileLeaves.Values.Where(f => f.IsSelected == true).Select(f => f.Index).OrderBy(i => i).ToList();
        if (indices.Count == 0)
        {
            // Can't deselect everything — re-check all files.
            _pendingSelection = null;
            foreach (var leaf in _fileLeaves.Values)
                leaf.SetSelectedFromSnapshot(true);
            foreach (var root in FileTree)
                Aggregate(root);
            return Task.CompletedTask;
        }
        _pendingSelection = (item.Gid, [.. indices], 0);
        return PushSelectFileAsync(item.Gid, indices);
    }

    private async Task PushSelectFileAsync(string gid, IReadOnlyList<int> indices)
    {
        try
        {
            await _service.Rpc.ChangeOptionAsync(gid, new Dictionary<string, string>
            {
                ["select-file"] = string.Join(',', indices),
            });
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            // Engine busy/reconnecting — the pending set re-pushes on the next poll.
        }
    }

    private async Task RefreshPeersAsync()
    {
        var item = SelectedDownload;
        if (_peersRefreshing || item is null || !item.IsTorrent || !_service.Rpc.IsConnected)
        {
            if (item is null || !item.IsTorrent)
                Peers.Clear();
            return;
        }
        _peersRefreshing = true;
        try
        {
            var peers = await _service.Rpc.GetPeersAsync(item.Gid);
            if (!ReferenceEquals(SelectedDownload, item))
                return; // selection moved while we were fetching
            for (int i = 0; i < peers.Count; i++)
            {
                PeerRowViewModel row;
                if (i < Peers.Count)
                {
                    row = Peers[i];
                }
                else
                {
                    row = new PeerRowViewModel();
                    Peers.Add(row);
                }
                row.Address = $"{peers[i].Ip}:{peers[i].Port}";
                row.Kind = peers[i].Seeder ? "Сид" : "Пир";
                row.DownSpeedText = FormatUtils.FormatSpeed(peers[i].DownloadSpeed);
                row.UpSpeedText = FormatUtils.FormatSpeed(peers[i].UploadSpeed);
            }
            for (int i = Peers.Count - 1; i >= peers.Count; i--)
                Peers.RemoveAt(i);
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            Peers.Clear();
        }
        finally
        {
            _peersRefreshing = false;
        }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private Task AddDownloadAsync() => AddDownloadRequested?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenSettings() => SettingsOpen = true;

    [RelayCommand]
    private async Task ResumeSelectedAsync()
    {
        foreach (var item in _selection.ToArray())
        {
            if (item.RawStatus == Aria2Status.Paused)
                await GuardedRpcAsync(() => _service.Rpc.UnpauseAsync(item.Gid));
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task PauseSelectedAsync()
    {
        foreach (var item in _selection.ToArray())
        {
            if (item.RawStatus is Aria2Status.Active or Aria2Status.Waiting)
                await GuardedRpcAsync(() => _service.Rpc.PauseAsync(item.Gid));
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        foreach (var item in _selection.ToArray())
            await item.RemoveAsync();
        await RefreshAsync();
    }

    [RelayCommand]
    private Task PauseAllAsync() => GuardedRpcAsync(() => _service.Rpc.PauseAllAsync());

    [RelayCommand]
    private Task ResumeAllAsync() => GuardedRpcAsync(() => _service.Rpc.UnpauseAllAsync());

    [RelayCommand]
    private async Task ClearStoppedAsync()
    {
        await GuardedRpcAsync(() => _service.Rpc.PurgeDownloadResultAsync());
        await RefreshAsync();
    }

    private static async Task GuardedRpcAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException or TimeoutException)
        {
            // Engine busy/reconnecting — UI resyncs via state events and polling.
        }
    }
}
