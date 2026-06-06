using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Aria2Gui.ViewModels;

/// <summary>
/// Shared, resizable + toggleable column widths for the downloads table. The header
/// cells and every row template bind to the same instance. All real columns are
/// pixel-sized and a trailing star "spacer" absorbs the leftover width, so dragging
/// any column's right edge — including Name — moves that edge under the cursor while
/// the spacer shrinks/grows. A hidden column collapses to zero width.
/// </summary>
public sealed partial class TableColumns : ObservableObject
{
    private const double MinWidth = 36;
    private const double MaxWidth = 760;

    // Last non-zero width per column, so toggling visibility restores the size.
    private readonly Dictionary<string, double> _saved = [];

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
}
