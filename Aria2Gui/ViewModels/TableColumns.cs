using System.Globalization;
using System.IO;
using System.Text;
using Aria2Gui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Aria2Gui.ViewModels;

/// <summary>
/// Shared, resizable + toggleable column widths for the downloads table. The header
/// cells and every row template bind to the same instance. All real columns are
/// pixel-sized and a trailing star "spacer" absorbs the leftover width, so dragging
/// any column's right edge — including Name — moves that edge under the cursor while
/// the spacer shrinks/grows. A hidden column collapses to zero width.
///
/// The whole layout (per-column width + visibility) is persisted to
/// <see cref="AppPaths.ColumnLayoutFile"/> and restored on the next launch. Saves are
/// debounced so dragging a divider doesn't hammer the disk.
/// </summary>
public sealed partial class TableColumns : ObservableObject
{
    private const double MinWidth = 36;
    private const double MaxWidth = 760;

    // Last non-zero width per column, so toggling visibility restores the size.
    private readonly Dictionary<string, double> _saved = [];
    private DispatcherQueueTimer? _saveTimer;

    public TableColumns()
    {
        Restore();
        // Subscribe only after restoring, so loading the saved layout doesn't
        // immediately schedule a redundant save.
        PropertyChanged += (_, _) => ScheduleSave();
    }

    [ObservableProperty]
    public partial GridLength NameWidth { get; set; } = new(320);

    [ObservableProperty]
    public partial GridLength SizeWidth { get; set; } = new(80);

    [ObservableProperty]
    public partial GridLength ProgressWidth { get; set; } = new(150);

    [ObservableProperty]
    public partial GridLength StatusWidth { get; set; } = new(100);

    [ObservableProperty]
    public partial GridLength SeedsWidth { get; set; } = new(48);

    [ObservableProperty]
    public partial GridLength PeersWidth { get; set; } = new(48);

    [ObservableProperty]
    public partial GridLength DownWidth { get; set; } = new(88);

    [ObservableProperty]
    public partial GridLength UpWidth { get; set; } = new(88);

    [ObservableProperty]
    public partial GridLength EtaWidth { get; set; } = new(88);

    [ObservableProperty]
    public partial GridLength RatioWidth { get; set; } = new(60);

    [ObservableProperty]
    public partial GridLength UploadedWidth { get; set; } = new(96);

    // Per-column visibility (Name is mandatory and has no toggle).
    [ObservableProperty]
    public partial bool SizeVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool ProgressVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool StatusVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool SeedsVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool PeersVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool DownVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool UpVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool EtaVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool RatioVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool UploadedVisible { get; set; } = true;

    /// <summary>Grows the dragged column by the delta; the trailing star spacer
    /// absorbs the change, so the right edge tracks the cursor. Hidden columns
    /// (width 0) ignore drags.</summary>
    public void ResizeBy(string key, double delta)
    {
        switch (key)
        {
            case "name": NameWidth = Adjust(NameWidth); break;
            case "size" when SizeVisible: SizeWidth = Adjust(SizeWidth); break;
            case "progress" when ProgressVisible: ProgressWidth = Adjust(ProgressWidth); break;
            case "status" when StatusVisible: StatusWidth = Adjust(StatusWidth); break;
            case "seeds" when SeedsVisible: SeedsWidth = Adjust(SeedsWidth); break;
            case "peers" when PeersVisible: PeersWidth = Adjust(PeersWidth); break;
            case "down" when DownVisible: DownWidth = Adjust(DownWidth); break;
            case "up" when UpVisible: UpWidth = Adjust(UpWidth); break;
            case "eta" when EtaVisible: EtaWidth = Adjust(EtaWidth); break;
            case "ratio" when RatioVisible: RatioWidth = Adjust(RatioWidth); break;
            case "uploaded" when UploadedVisible: UploadedWidth = Adjust(UploadedWidth); break;
        }

        GridLength Adjust(GridLength current) =>
            new(Math.Clamp(current.Value + delta, MinWidth, MaxWidth));
    }

    /// <summary>Shows/hides a column, collapsing it to zero width when hidden and
    /// restoring its previous width when shown again.</summary>
    public void SetVisible(string key, bool visible)
    {
        switch (key)
        {
            case "size": SizeVisible = visible; SizeWidth = Toggle(SizeWidth); break;
            case "progress": ProgressVisible = visible; ProgressWidth = Toggle(ProgressWidth); break;
            case "status": StatusVisible = visible; StatusWidth = Toggle(StatusWidth); break;
            case "seeds": SeedsVisible = visible; SeedsWidth = Toggle(SeedsWidth); break;
            case "peers": PeersVisible = visible; PeersWidth = Toggle(PeersWidth); break;
            case "down": DownVisible = visible; DownWidth = Toggle(DownWidth); break;
            case "up": UpVisible = visible; UpWidth = Toggle(UpWidth); break;
            case "eta": EtaVisible = visible; EtaWidth = Toggle(EtaWidth); break;
            case "ratio": RatioVisible = visible; RatioWidth = Toggle(RatioWidth); break;
            case "uploaded": UploadedVisible = visible; UploadedWidth = Toggle(UploadedWidth); break;
        }

        GridLength Toggle(GridLength current)
        {
            if (!visible)
            {
                if (current.Value > 0)
                    _saved[key] = current.Value;
                return new GridLength(0);
            }
            return new GridLength(_saved.TryGetValue(key, out var w) ? w : 80);
        }
    }

    // ───────────────────────── persistence ─────────────────────────

    /// <summary>Serializes the layout as <c>key=width,visible;…</c> (visible 1/0).
    /// Hidden columns store their real (pre-hide) width so re-showing restores it.</summary>
    private string Serialize()
    {
        var sb = new StringBuilder();
        Add("name", NameWidth, true);
        Add("size", SizeWidth, SizeVisible);
        Add("progress", ProgressWidth, ProgressVisible);
        Add("status", StatusWidth, StatusVisible);
        Add("seeds", SeedsWidth, SeedsVisible);
        Add("peers", PeersWidth, PeersVisible);
        Add("down", DownWidth, DownVisible);
        Add("up", UpWidth, UpVisible);
        Add("eta", EtaWidth, EtaVisible);
        Add("ratio", RatioWidth, RatioVisible);
        Add("uploaded", UploadedWidth, UploadedVisible);
        return sb.ToString();

        void Add(string key, GridLength width, bool visible)
        {
            double real = visible
                ? width.Value
                : (_saved.TryGetValue(key, out var s) ? s : 80);
            if (sb.Length > 0)
                sb.Append(';');
            sb.Append(key).Append('=')
              .Append(((int)real).ToString(CultureInfo.InvariantCulture))
              .Append(',').Append(visible ? '1' : '0');
        }
    }

    private void Restore()
    {
        string data;
        try
        {
            if (!File.Exists(AppPaths.ColumnLayoutFile))
                return;
            data = File.ReadAllText(AppPaths.ColumnLayoutFile);
        }
        catch
        {
            return;
        }

        foreach (var part in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            if (kv.Length != 2)
                continue;
            var wv = kv[1].Split(',');
            if (!double.TryParse(wv[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var width))
                continue;
            bool visible = wv.Length < 2 || wv[1] != "0";
            ApplyRestored(kv[0], Math.Clamp(width, MinWidth, MaxWidth), visible);
        }
    }

    private void ApplyRestored(string key, double width, bool visible)
    {
        _saved[key] = width;
        var len = new GridLength(visible ? width : 0);
        switch (key)
        {
            case "name": NameWidth = new GridLength(width); break;
            case "size": SizeVisible = visible; SizeWidth = len; break;
            case "progress": ProgressVisible = visible; ProgressWidth = len; break;
            case "status": StatusVisible = visible; StatusWidth = len; break;
            case "seeds": SeedsVisible = visible; SeedsWidth = len; break;
            case "peers": PeersVisible = visible; PeersWidth = len; break;
            case "down": DownVisible = visible; DownWidth = len; break;
            case "up": UpVisible = visible; UpWidth = len; break;
            case "eta": EtaVisible = visible; EtaWidth = len; break;
            case "ratio": RatioVisible = visible; RatioWidth = len; break;
            case "uploaded": UploadedVisible = visible; UploadedWidth = len; break;
        }
    }

    /// <summary>Debounced save: restarts a short timer on every change so a drag
    /// writes the file once it settles, not on every pixel.</summary>
    private void ScheduleSave()
    {
        var dispatcher = App.DispatcherQueue;
        if (dispatcher is null)
            return;
        if (_saveTimer is null)
        {
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += OnSaveTick;
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        try
        {
            string tmp = AppPaths.ColumnLayoutFile + ".tmp";
            File.WriteAllText(tmp, Serialize());
            File.Move(tmp, AppPaths.ColumnLayoutFile, overwrite: true);
        }
        catch
        {
            // UI-state persistence is best-effort; never crash on a failed write.
        }
    }
}
