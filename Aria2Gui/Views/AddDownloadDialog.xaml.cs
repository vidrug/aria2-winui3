using Aria2Gui.Helpers;
using Aria2Gui.Services;
using Aria2Gui.Services.Aria2;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Aria2Gui.Views;

/// <summary>
/// Dialog for queueing new downloads: URI/magnet lines and/or a .torrent file with
/// qBittorrent-style per-file selection (tree, folders collapsed, select all/none),
/// plus a save-destination picker.
/// </summary>
public sealed partial class AddDownloadDialog : ContentDialog
{
    /// <summary>TreeView node payload; leaves carry the aria2 1-based file index.</summary>
    private sealed record FileNodeContent(string Title, int? FileIndex)
    {
        public override string ToString() => Title;
    }

    private byte[]? _torrentBytes;
    private TorrentContent? _torrentContent;

    public AddDownloadDialog()
    {
        InitializeComponent();
        DirBox.Text = Aria2Service.Instance.Settings.DownloadDirectory;
    }

    private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                DirBox.Text = folder.Path;
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось открыть выбор папки: {ex.Message}");
        }
    }

    private async void OnPickTorrentClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            picker.FileTypeFilter.Add(".torrent");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            byte[] bytes = await DownloadAdder.ReadTorrentBytesAsync(file);
            var content = TorrentParser.Parse(bytes);

            _torrentBytes = bytes;
            _torrentContent = content;
            TorrentFileName.Text = file.Name;
            ErrorBar.IsOpen = false;
            BuildFileTree(content);
            TorrentFilesPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _torrentBytes = null;
            _torrentContent = null;
            TorrentFileName.Text = "";
            TorrentFilesPanel.Visibility = Visibility.Collapsed;
            ShowError($"Не удалось прочитать torrent-файл: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ file tree

    /// <summary>Intermediate folder while grouping path segments.</summary>
    private sealed class DirBucket
    {
        public SortedDictionary<string, DirBucket> Dirs { get; } = new(StringComparer.CurrentCultureIgnoreCase);
        public List<TorrentFileEntry> Files { get; } = [];

        public long TotalSize() => Files.Sum(f => f.Length) + Dirs.Values.Sum(d => d.TotalSize());
    }

    private void BuildFileTree(TorrentContent content)
    {
        FileTree.RootNodes.Clear();
        FileTree.SelectedNodes.Clear();

        if (content.IsSingleFile)
        {
            var single = content.Files[0];
            FileTree.RootNodes.Add(new TreeViewNode
            {
                Content = new FileNodeContent($"{single.PathSegments[^1]}  ({FormatUtils.FormatSize(single.Length)})", single.Index),
            });
        }
        else
        {
            // Group by folder; the torrent name is the root folder, collapsed initially.
            var root = new DirBucket();
            foreach (var file in content.Files)
            {
                var bucket = root;
                for (int i = 0; i < file.PathSegments.Count - 1; i++)
                {
                    if (!bucket.Dirs.TryGetValue(file.PathSegments[i], out var next))
                        bucket.Dirs[file.PathSegments[i]] = next = new DirBucket();
                    bucket = next;
                }
                bucket.Files.Add(file);
            }

            var rootNode = new TreeViewNode
            {
                Content = new FileNodeContent($"{content.Name}  ({FormatUtils.FormatSize(root.TotalSize())})", null),
                IsExpanded = false,
            };
            AppendBucket(rootNode, root);
            FileTree.RootNodes.Add(rootNode);
        }

        SelectAllNodes();
    }

    private static void AppendBucket(TreeViewNode parent, DirBucket bucket)
    {
        foreach (var (name, dir) in bucket.Dirs)
        {
            var node = new TreeViewNode
            {
                Content = new FileNodeContent($"{name}  ({FormatUtils.FormatSize(dir.TotalSize())})", null),
                IsExpanded = false,
            };
            AppendBucket(node, dir);
            parent.Children.Add(node);
        }
        foreach (var file in bucket.Files.OrderBy(f => f.PathSegments[^1], StringComparer.CurrentCultureIgnoreCase))
        {
            parent.Children.Add(new TreeViewNode
            {
                Content = new FileNodeContent($"{file.PathSegments[^1]}  ({FormatUtils.FormatSize(file.Length)})", file.Index),
            });
        }
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SelectAllNodes();

    private void OnDeselectAllClick(object sender, RoutedEventArgs e) => FileTree.SelectedNodes.Clear();

    private void SelectAllNodes()
    {
        FileTree.SelectedNodes.Clear();
        foreach (var node in FileTree.RootNodes)
            FileTree.SelectedNodes.Add(node); // cascades to descendants
    }

    /// <summary>
    /// aria2 select-file value: null = download everything, "" = nothing selected
    /// (caller shows an error). Walks the tree treating descendants of a selected
    /// node as selected, which covers both materialized and minimal SelectedNodes
    /// semantics of TreeView.
    /// </summary>
    private string? BuildSelectFileOption()
    {
        if (_torrentContent is null || _torrentContent.IsSingleFile)
            return null;

        var selected = new List<int>();
        void Walk(TreeViewNode node, bool parentSelected)
        {
            bool isSelected = parentSelected || FileTree.SelectedNodes.Contains(node);
            if (node.Content is FileNodeContent { FileIndex: int index } && isSelected)
                selected.Add(index);
            foreach (var child in node.Children)
                Walk(child, isSelected);
        }
        foreach (var node in FileTree.RootNodes)
            Walk(node, parentSelected: false);

        if (selected.Count == 0)
            return "";
        if (selected.Count == _torrentContent.Files.Count)
            return null;
        selected.Sort();
        return string.Join(',', selected);
    }

    // ------------------------------------------------------------------ add

    private async void OnAddClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            string text = UrlsBox.Text;
            bool hasUris = !string.IsNullOrWhiteSpace(text);
            if (!hasUris && _torrentBytes is null)
            {
                ShowError("Укажите хотя бы одну ссылку или выберите .torrent-файл.");
                args.Cancel = true;
                return;
            }

            string? directory = string.IsNullOrWhiteSpace(DirBox.Text) ? null : DirBox.Text;

            // The torrent goes first: it's a single atomic add, so a later URI
            // failure can't duplicate it on retry.
            if (_torrentBytes is not null)
            {
                string? selectFile = BuildSelectFileOption();
                if (selectFile is "")
                {
                    ShowError("Выберите хотя бы один файл торрента.");
                    args.Cancel = true;
                    return;
                }
                await DownloadAdder.AddTorrentBytesAsync(_torrentBytes, directory, selectFile);
                _torrentBytes = null;
                _torrentContent = null;
                TorrentFileName.Text = "";
                TorrentFilesPanel.Visibility = Visibility.Collapsed;
            }

            if (hasUris)
            {
                var result = await DownloadAdder.AddUrisAsync(text, directory);
                // Queued lines leave the box — a retry resubmits only the remainder.
                UrlsBox.Text = string.Join(Environment.NewLine, result.Remaining);

                if (result.Error is not null)
                {
                    ShowError($"Добавлено: {result.Added}. Остальные не добавились: {result.Error.Message}");
                    args.Cancel = true;
                    return;
                }
                if (result.Skipped > 0)
                {
                    ShowError($"Строк пропущено: {result.Skipped} — поддерживаются только http, https, ftp и magnet.");
                    args.Cancel = true;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Dialog-local catch-all: an escaped exception here closes the dialog
            // with zero feedback (the deferral completes in finally regardless).
            ShowError($"Не удалось добавить загрузку: {ex.Message}");
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
