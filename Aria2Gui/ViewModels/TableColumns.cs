using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Aria2Gui.ViewModels;

/// <summary>
/// Shared, resizable column widths for the downloads table. The header cells and
/// every row template bind to the same instance, so dragging a header divider
/// resizes the whole column. The Name column is star-sized and absorbs the rest.
/// </summary>
public sealed partial class TableColumns : ObservableObject
{
    private const double MinWidth = 36;
    private const double MaxWidth = 640;

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
    public partial GridLength RatioWidth { get; set; } = new(52);

    /// <summary>Adjusts one column by a drag delta (clamped); key matches the Thumb tag.</summary>
    public void ResizeBy(string key, double delta)
    {
        switch (key)
        {
            case "size": SizeWidth = Adjust(SizeWidth, delta); break;
            case "progress": ProgressWidth = Adjust(ProgressWidth, delta); break;
            case "status": StatusWidth = Adjust(StatusWidth, delta); break;
            case "seeds": SeedsWidth = Adjust(SeedsWidth, delta); break;
            case "peers": PeersWidth = Adjust(PeersWidth, delta); break;
            case "down": DownWidth = Adjust(DownWidth, delta); break;
            case "up": UpWidth = Adjust(UpWidth, delta); break;
            case "eta": EtaWidth = Adjust(EtaWidth, delta); break;
            case "ratio": RatioWidth = Adjust(RatioWidth, delta); break;
        }
    }

    private static GridLength Adjust(GridLength current, double delta) =>
        new(Math.Clamp(current.Value + delta, MinWidth, MaxWidth));
}
