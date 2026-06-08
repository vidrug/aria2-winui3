using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aria2Gui.ViewModels;

/// <summary>One status filter in the left NavigationView pane (qBittorrent-style).</summary>
public sealed partial class FilterItemViewModel(string key, string title, string glyph) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    public partial int Count { get; set; }

    /// <summary>Gallery-style: only show the NavigationViewItem's InfoBadge when there's
    /// something to count (a zero badge is noise on every empty filter).</summary>
    public bool HasCount => Count > 0;
}

/// <summary>
/// One node in the details "Files" tab tree: a folder (which aggregates its
/// children's size/progress and shows a three-state checkbox) or a leaf file.
/// The checkbox toggles whether aria2 downloads the file(s) (select-file); a
/// folder cascades its state to all descendants.
/// </summary>
public sealed partial class FileTreeNodeViewModel : ObservableObject
{
    public bool IsFolder { get; init; }

    /// <summary>1-based aria2 file index (leaf nodes only).</summary>
    public int Index { get; init; }

    public ObservableCollection<FileTreeNodeViewModel> Children { get; } = [];

    /// <summary>Raised when the user changes the checkbox (push select-file to aria2).</summary>
    public Action? SelectionToggled { get; set; }

    // Raw sizes used to roll up folder aggregates.
    public long Length { get; set; }
    public long CompletedLength { get; set; }

    private bool _suppress;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    /// <summary>True/false for files; nullable (three-state) for partially selected folders.</summary>
    [ObservableProperty]
    public partial bool? IsSelected { get; set; } = true;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Sets the checked state from a poll/aggregate without firing the toggle callback.</summary>
    public void SetSelectedFromSnapshot(bool? selected)
    {
        _suppress = true;
        IsSelected = selected;
        _suppress = false;
    }

    partial void OnIsSelectedChanged(bool? value)
    {
        if (_suppress)
            return;
        if (IsFolder)
        {
            // A three-state click can land on indeterminate — treat that as "deselect all".
            bool target = value == true;
            foreach (var child in Children)
                child.CascadeSelect(target);
            if (value is null)
                SetSelectedFromSnapshot(false);
        }
        SelectionToggled?.Invoke();
    }

    private void CascadeSelect(bool value)
    {
        SetSelectedFromSnapshot(value);
        foreach (var child in Children)
            child.CascadeSelect(value);
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
