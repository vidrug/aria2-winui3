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

/// <summary>One file row in the details pane, updated in place per tick. The
/// checkbox toggles whether aria2 downloads this file (select-file).</summary>
public sealed partial class FileRowViewModel : ObservableObject
{
    /// <summary>1-based file index aria2 uses in select-file.</summary>
    public int Index { get; set; }

    /// <summary>Raised when the user toggles the checkbox (not on snapshot refresh).</summary>
    public Action? SelectionToggled { get; set; }

    private bool _suppress;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    /// <summary>Updates the checked state from a poll snapshot without firing the toggle callback.</summary>
    public void SetSelectedFromSnapshot(bool selected)
    {
        _suppress = true;
        IsSelected = selected;
        _suppress = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_suppress)
            SelectionToggled?.Invoke();
    }
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
