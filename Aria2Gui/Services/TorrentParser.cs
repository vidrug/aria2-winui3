using System.Text;

namespace Aria2Gui.Services;

/// <summary>One file inside a torrent. <see cref="Index"/> is the 1-based position
/// in the torrent's file list — exactly what aria2's select-file option expects.</summary>
public sealed record TorrentFileEntry(int Index, IReadOnlyList<string> PathSegments, long Length);

public sealed record TorrentContent(string Name, bool IsSingleFile, IReadOnlyList<TorrentFileEntry> Files);

/// <summary>
/// Minimal bencode reader — just enough to list the files of a .torrent so the
/// add dialog can offer per-file selection before handing the blob to aria2.
/// </summary>
public static class TorrentParser
{
    public static TorrentContent Parse(byte[] data)
    {
        int pos = 0;
        if (ParseValue(data, ref pos) is not Dictionary<string, object> root)
            throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrInvalidFile"));
        if (!root.TryGetValue("info", out var infoObj) || infoObj is not Dictionary<string, object> info)
            throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrNoInfo"));

        string name = GetUtf8(info, "name.utf-8") ?? GetUtf8(info, "name") ?? "torrent";

        if (info.TryGetValue("files", out var filesObj) && filesObj is List<object> files)
        {
            var entries = new List<TorrentFileEntry>(files.Count);
            int index = 1;
            foreach (var fileObj in files)
            {
                if (fileObj is not Dictionary<string, object> file)
                    throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrBadFileList"));
                long length = file.TryGetValue("length", out var l) && l is long len ? len : 0;
                // Prefer the UTF-8 path, but fall back to the legacy "path" key when the
                // UTF-8 variant is present-but-empty (otherwise we'd show a bogus "fileN").
                string[] segments = ExtractPathSegments(file, "path.utf-8");
                if (segments.Length == 0)
                    segments = ExtractPathSegments(file, "path");
                if (segments.Length == 0)
                    segments = [$"file{index}"];
                entries.Add(new TorrentFileEntry(index, segments, length));
                index++;
            }
            return new TorrentContent(name, IsSingleFile: false, entries);
        }

        long single = info.TryGetValue("length", out var sl) && sl is long s ? s : 0;
        return new TorrentContent(name, IsSingleFile: true, [new TorrentFileEntry(1, [name], single)]);
    }

    private static string? GetUtf8(Dictionary<string, object> dict, string key) =>
        dict.TryGetValue(key, out var value) && value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;

    private static string[] ExtractPathSegments(Dictionary<string, object> file, string key) =>
        file.TryGetValue(key, out var obj) && obj is List<object> list
            ? list.OfType<byte[]>()
                .Select(b => Encoding.UTF8.GetString(b))
                .Where(s => s.Length > 0)
                .ToArray()
            : [];

    // A pathologically nested .torrent (l l l l …) would otherwise recurse until the
    // stack overflows — an uncatchable crash. Real torrents nest only a few levels deep.
    private const int MaxParseDepth = 100;

    /// <summary>Bencode: i…e = int, l…e = list, d…e = dict, &lt;len&gt;:&lt;bytes&gt; = string.</summary>
    private static object ParseValue(byte[] data, ref int pos, int depth = 0)
    {
        if (depth > MaxParseDepth)
            throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrInvalidFile"));
        if (pos >= data.Length)
            throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrUnexpectedEnd"));
        byte marker = data[pos];

        if (marker == (byte)'i')
        {
            pos++;
            int end = Array.IndexOf(data, (byte)'e', pos);
            if (end < 0)
                throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrUnclosedInt"));
            if (!long.TryParse(Encoding.ASCII.GetString(data, pos, end - pos), out long value))
                throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrUnclosedInt"));
            pos = end + 1;
            return value;
        }

        if (marker == (byte)'l')
        {
            pos++;
            var list = new List<object>();
            while (pos < data.Length && data[pos] != (byte)'e')
                list.Add(ParseValue(data, ref pos, depth + 1));
            pos++; // 'e'
            return list;
        }

        if (marker == (byte)'d')
        {
            pos++;
            var dict = new Dictionary<string, object>();
            while (pos < data.Length && data[pos] != (byte)'e')
            {
                if (ParseValue(data, ref pos, depth + 1) is not byte[] keyBytes)
                    throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrKeyNotString"));
                dict[Encoding.UTF8.GetString(keyBytes)] = ParseValue(data, ref pos, depth + 1);
            }
            pos++; // 'e'
            return dict;
        }

        if (marker is >= (byte)'0' and <= (byte)'9')
        {
            int colon = Array.IndexOf(data, (byte)':', pos);
            if (colon < 0)
                throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrBadString"));
            if (!int.TryParse(Encoding.ASCII.GetString(data, pos, colon - pos), out int length))
                throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrBadString"));
            // Compare in long: a declared length near int.MaxValue would wrap the int sum
            // negative, slip past this check, and hit a ~2 GB allocation below instead.
            if (length < 0 || (long)colon + 1 + length > data.Length)
                throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrStringOOB"));
            byte[] bytes = new byte[length];
            Array.Copy(data, colon + 1, bytes, 0, length);
            pos = colon + 1 + length;
            return bytes;
        }

        throw new InvalidDataException(Aria2Gui.Helpers.L.Get("TorrentErrUnexpectedByte", marker.ToString("X2")));
    }
}
