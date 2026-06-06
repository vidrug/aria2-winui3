using CommunityToolkit.Mvvm.ComponentModel;

namespace Aria2Gui.ViewModels;

/// <summary>One status filter in the left sidebar (qBittorrent-style).</summary>
public sealed partial class FilterItemViewModel(string key, string title, string glyph) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    [ObservableProperty]
    public partial int Count { get; set; }
}

/// <summary>One file row in the details pane, updated in place per tick.</summary>
public sealed partial class FileRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";
}

/// <summary>TreeView node payload for the torrent file picker; leaves carry the
/// aria2 1-based file index.</summary>
public sealed class TorrentNodeContent(string label, int? fileIndex)
{
    public string Label { get; } = label;
    public int? FileIndex { get; } = fileIndex;
}

/// <summary>One peer row in the details pane.</summary>
public sealed partial class PeerRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    public partial string Kind { get; set; } = "";

    [ObservableProperty]
    public partial string DownSpeedText { get; set; } = "";

    [ObservableProperty]
    public partial string UpSpeedText { get; set; } = "";
}
