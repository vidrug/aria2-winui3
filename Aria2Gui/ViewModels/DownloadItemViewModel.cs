using System.Diagnostics;
using System.Globalization;
using System.Text;
using Aria2Gui.Helpers;
using Aria2Gui.Services.Aria2;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aria2Gui.ViewModels;

/// <summary>
/// Observable wrapper for one aria2 download (keyed by gid). Instances live for the
/// lifetime of the download in the list and are updated in place on every poll tick;
/// [ObservableProperty] setters no-op when the value is unchanged, so a quiet list
/// raises no change notifications at all.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly Aria2Service _service = Aria2Service.Instance;

    public string Gid { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    /// <summary>Composed "status • size • speed • ETA • peers" caption line.</summary>
    [ObservableProperty]
    public partial string DetailsText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusGlyph { get; set; } = "";

    [ObservableProperty]
    public partial string PauseResumeGlyph { get; set; } = "";

    [ObservableProperty]
    public partial string PauseResumeTooltip { get; set; } = "Пауза";

    [ObservableProperty]
    public partial bool CanPauseResume { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    private string _status = "";
    private bool _isStopped;
    private string? _directory;
    private string? _firstFilePath;

    /// <summary>Set once the first completion toast decision was made (dedupes the
    /// second onDownloadComplete aria2 fires when seeding stops).</summary>
    public bool CompletionNotified { get; set; }

    public DownloadItemViewModel(string gid)
    {
        Gid = gid;
    }

    /// <summary>Applies the latest aria2 snapshot entry to the bindable properties.</summary>
    public void UpdateFrom(Aria2Download d)
    {
        _status = d.Status;
        _isStopped = d.Status is Aria2Status.Complete or Aria2Status.Error or Aria2Status.Removed;
        _directory = NormalizePath(d.Dir);

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

        (string statusText, string glyph) = (d.Status, isMetadata, d.Seeder) switch
        {
            (Aria2Status.Active, true, _) => ("Получение метаданных", ""),
            (Aria2Status.Active, _, true) => ("Раздача", ""),
            (Aria2Status.Active, _, _) => ("Загрузка", ""),
            (Aria2Status.Waiting, _, _) => ("В очереди", ""),
            (Aria2Status.Paused, _, _) => ("Пауза", ""),
            (Aria2Status.Complete, _, _) => ("Завершено", ""),
            (Aria2Status.Error, _, _) => (string.IsNullOrEmpty(d.ErrorMessage) ? "Ошибка" : $"Ошибка: {d.ErrorMessage}", ""),
            (Aria2Status.Removed, _, _) => ("Удалено", ""),
            _ => (d.Status, ""),
        };
        StatusGlyph = glyph;
        IsError = d.Status == Aria2Status.Error;

        DetailsText = ComposeDetails(d, statusText);

        bool paused = d.Status == Aria2Status.Paused;
        CanPauseResume = !_isStopped;
        PauseResumeGlyph = paused ? "" : "";
        PauseResumeTooltip = paused ? "Возобновить" : "Пауза";
    }

    private static string ComposeDetails(Aria2Download d, string statusText)
    {
        var sb = new StringBuilder(96);
        sb.Append(statusText);

        if (d.TotalLength > 0)
        {
            sb.Append(" • ").Append(FormatUtils.FormatSize(d.CompletedLength))
              .Append(" / ").Append(FormatUtils.FormatSize(d.TotalLength));
        }
        else if (d.CompletedLength > 0)
        {
            sb.Append(" • ").Append(FormatUtils.FormatSize(d.CompletedLength));
        }

        if (d.Status == Aria2Status.Active)
        {
            if (!d.Seeder && d.DownloadSpeed > 0)
            {
                sb.Append(" • ↓ ").Append(FormatUtils.FormatSpeed(d.DownloadSpeed));
                string eta = FormatUtils.FormatEta(d.TotalLength, d.CompletedLength, d.DownloadSpeed);
                if (eta != "—")
                    sb.Append(" • осталось ").Append(eta);
            }
            if (d.IsTorrent)
            {
                if (d.UploadSpeed > 0)
                    sb.Append(" • ↑ ").Append(FormatUtils.FormatSpeed(d.UploadSpeed));
                sb.Append(" • сиды ").Append(d.NumSeeders).Append(" • пиры ").Append(d.Connections);
            }
            else if (d.Connections > 1)
            {
                sb.Append(" • потоки ").Append(d.Connections);
            }
        }

        if (d.IsTorrent && d.TotalLength > 0 && d.UploadLength > 0)
        {
            double ratio = d.UploadLength / (double)d.TotalLength;
            sb.Append(" • рейтинг ").Append(ratio.ToString("0.00", CultureInfo.CurrentCulture));
        }

        return sb.ToString();
    }

    [RelayCommand]
    private async Task PauseResumeAsync()
    {
        try
        {
            if (_status == Aria2Status.Paused)
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
    private async Task RemoveAsync()
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
    private void OpenFolder()
    {
        try
        {
            if (!string.IsNullOrEmpty(_firstFilePath) && (File.Exists(_firstFilePath) || Directory.Exists(_firstFilePath)))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_firstFilePath}\"") { UseShellExecute = false });
            else if (!string.IsNullOrEmpty(_directory) && Directory.Exists(_directory))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_directory}\"") { UseShellExecute = false });
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
