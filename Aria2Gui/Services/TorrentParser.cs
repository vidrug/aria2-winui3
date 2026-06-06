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
            throw new InvalidDataException("Файл не является корректным torrent-файлом.");
        if (!root.TryGetValue("info", out var infoObj) || infoObj is not Dictionary<string, object> info)
            throw new InvalidDataException("В torrent-файле нет секции info.");

        string name = GetUtf8(info, "name.utf-8") ?? GetUtf8(info, "name") ?? "torrent";

        if (info.TryGetValue("files", out var filesObj) && filesObj is List<object> files)
        {
            var entries = new List<TorrentFileEntry>(files.Count);
            int index = 1;
            foreach (var fileObj in files)
            {
                if (fileObj is not Dictionary<string, object> file)
                    throw new InvalidDataException("Повреждённый список файлов торрента.");
                long length = file.TryGetValue("length", out var l) && l is long len ? len : 0;
                object? pathObj = file.TryGetValue("path.utf-8", out var p8) ? p8
                    : file.TryGetValue("path", out var p) ? p : null;
                string[] segments = (pathObj as List<object>)?
                    .OfType<byte[]>()
                    .Select(b => Encoding.UTF8.GetString(b))
                    .Where(s => s.Length > 0)
                    .ToArray() ?? [];
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

    /// <summary>Bencode: i…e = int, l…e = list, d…e = dict, &lt;len&gt;:&lt;bytes&gt; = string.</summary>
    private static object ParseValue(byte[] data, ref int pos)
    {
        if (pos >= data.Length)
            throw new InvalidDataException("Неожиданный конец torrent-файла.");
        byte marker = data[pos];

        if (marker == (byte)'i')
        {
            pos++;
            int end = Array.IndexOf(data, (byte)'e', pos);
            if (end < 0)
                throw new InvalidDataException("Незакрытое целое в bencode.");
            long value = long.Parse(Encoding.ASCII.GetString(data, pos, end - pos));
            pos = end + 1;
            return value;
        }

        if (marker == (byte)'l')
        {
            pos++;
            var list = new List<object>();
            while (pos < data.Length && data[pos] != (byte)'e')
                list.Add(ParseValue(data, ref pos));
            pos++; // 'e'
            return list;
        }

        if (marker == (byte)'d')
        {
            pos++;
            var dict = new Dictionary<string, object>();
            while (pos < data.Length && data[pos] != (byte)'e')
            {
                if (ParseValue(data, ref pos) is not byte[] keyBytes)
                    throw new InvalidDataException("Ключ bencode-словаря не строка.");
                dict[Encoding.UTF8.GetString(keyBytes)] = ParseValue(data, ref pos);
            }
            pos++; // 'e'
            return dict;
        }

        if (marker is >= (byte)'0' and <= (byte)'9')
        {
            int colon = Array.IndexOf(data, (byte)':', pos);
            if (colon < 0)
                throw new InvalidDataException("Повреждённая строка bencode.");
            int length = int.Parse(Encoding.ASCII.GetString(data, pos, colon - pos));
            if (length < 0 || colon + 1 + length > data.Length)
                throw new InvalidDataException("Строка bencode выходит за границы файла.");
            byte[] bytes = new byte[length];
            Array.Copy(data, colon + 1, bytes, 0, length);
            pos = colon + 1 + length;
            return bytes;
        }

        throw new InvalidDataException($"Неожиданный байт bencode: 0x{marker:X2}.");
    }
}
