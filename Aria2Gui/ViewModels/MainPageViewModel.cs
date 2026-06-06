using System.Collections.ObjectModel;
using Aria2Gui.Helpers;
using Aria2Gui.Services;
using Aria2Gui.Services.Aria2;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace Aria2Gui.ViewModels;

/// <summary>
/// Main page ViewModel: owns the download list, polls aria2 once per second with a
/// single multicall, and merges results into existing item ViewModels in place.
/// </summary>
public sealed partial class MainPageViewModel : ObservableObject
{
    private readonly Aria2Service _service = Aria2Service.Instance;
    private readonly Dictionary<string, DownloadItemViewModel> _byGid = [];
    private DispatcherQueueTimer? _timer;
    private bool _refreshing;
    private bool _initialized;

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

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

    /// <summary>Hooked by MainPage to open the add/settings dialogs (views own dialogs).</summary>
    public Func<Task>? AddDownloadRequested { get; set; }
    public Func<Task>? SettingsRequested { get; set; }

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
                item = new DownloadItemViewModel(download.Gid);
                item.UpdateFrom(download);
                _byGid[download.Gid] = item;
                // A download spawned from a metadata fetch takes its parent's row
                // position instead of jumping to the bottom of the list.
                if (download.Following is { Length: > 0 } parentGid
                    && _byGid.TryGetValue(parentGid, out var parent)
                    && Downloads.IndexOf(parent) is int parentIndex and >= 0)
                {
                    Downloads.Insert(parentIndex, item);
                }
                else
                {
                    Downloads.Add(item);
                }
            }
        }

        // Prune rows only when the snapshot is complete: if the tell* windows were
        // truncated, a missing gid does not mean the download is gone.
        long expectedTotal = snapshot.GlobalStat.NumActive + snapshot.GlobalStat.NumWaiting + snapshot.GlobalStat.NumStopped;
        if (snapshot.Downloads.Count >= expectedTotal)
        {
            for (int i = Downloads.Count - 1; i >= 0; i--)
            {
                if (!seen.Contains(Downloads[i].Gid))
                {
                    _byGid.Remove(Downloads[i].Gid);
                    Downloads.RemoveAt(i);
                }
            }
        }

        IsEmpty = Downloads.Count == 0;
        GlobalSpeedText = $"↓ {FormatUtils.FormatSpeed(snapshot.GlobalStat.DownloadSpeed)}   ↑ {FormatUtils.FormatSpeed(snapshot.GlobalStat.UploadSpeed)}";
        CountsText = $"Активных: {snapshot.GlobalStat.NumActive} • В очереди: {snapshot.GlobalStat.NumWaiting} • Завершённых: {snapshot.GlobalStat.NumStopped}";
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

    [RelayCommand]
    private Task AddDownloadAsync() => AddDownloadRequested?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task OpenSettingsAsync() => SettingsRequested?.Invoke() ?? Task.CompletedTask;

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
