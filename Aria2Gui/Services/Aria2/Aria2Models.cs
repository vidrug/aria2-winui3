using System.Text.Json.Serialization;

namespace Aria2Gui.Services.Aria2;

/// <summary>Status values aria2 reports for a download.</summary>
public static class Aria2Status
{
    public const string Active = "active";
    public const string Waiting = "waiting";
    public const string Paused = "paused";
    public const string Error = "error";
    public const string Complete = "complete";
    public const string Removed = "removed";
}

/// <summary>
/// One download entry as returned by aria2.tellActive / tellWaiting / tellStopped.
/// aria2 serializes all numbers as JSON strings; the converters registered on
/// <see cref="Aria2JsonContext"/> translate them transparently.
/// </summary>
public sealed class Aria2Download
{
    [JsonPropertyName("gid")]
    public string Gid { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("totalLength")]
    public long TotalLength { get; set; }

    [JsonPropertyName("completedLength")]
    public long CompletedLength { get; set; }

    [JsonPropertyName("uploadLength")]
    public long UploadLength { get; set; }

    [JsonPropertyName("downloadSpeed")]
    public long DownloadSpeed { get; set; }

    [JsonPropertyName("uploadSpeed")]
    public long UploadSpeed { get; set; }

    [JsonPropertyName("connections")]
    public long Connections { get; set; }

    [JsonPropertyName("numSeeders")]
    public long NumSeeders { get; set; }

    [JsonPropertyName("seeder")]
    public bool Seeder { get; set; }

    [JsonPropertyName("infoHash")]
    public string? InfoHash { get; set; }

    [JsonPropertyName("dir")]
    public string? Dir { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("bittorrent")]
    public Aria2BitTorrent? BitTorrent { get; set; }

    [JsonPropertyName("files")]
    public List<Aria2File>? Files { get; set; }

    /// <summary>GIDs spawned by this download (e.g. magnet metadata → real torrent).</summary>
    [JsonPropertyName("followedBy")]
    public List<string>? FollowedBy { get; set; }

    /// <summary>Reverse link of <see cref="FollowedBy"/>.</summary>
    [JsonPropertyName("following")]
    public string? Following { get; set; }

    /// <summary>True for torrent downloads (including magnet metadata fetches).</summary>
    [JsonIgnore]
    public bool IsTorrent => BitTorrent is not null || InfoHash is not null;

    /// <summary>Human-readable name: torrent name → first file name → first URI → gid.</summary>
    public string GetDisplayName()
    {
        if (BitTorrent?.Info?.Name is { Length: > 0 } torrentName)
            return torrentName;

        var firstFile = Files?.FirstOrDefault();
        if (!string.IsNullOrEmpty(firstFile?.Path))
        {
            var name = Path.GetFileName(firstFile.Path);
            return string.IsNullOrEmpty(name) ? firstFile.Path : name;
        }

        var uri = firstFile?.Uris?.FirstOrDefault()?.Uri;
        if (!string.IsNullOrEmpty(uri))
            return uri;

        return Gid;
    }
}

public sealed class Aria2BitTorrent
{
    [JsonPropertyName("info")]
    public Aria2BitTorrentInfo? Info { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}

public sealed class Aria2BitTorrentInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class Aria2File
{
    [JsonPropertyName("index")]
    public long Index { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("length")]
    public long Length { get; set; }

    [JsonPropertyName("completedLength")]
    public long CompletedLength { get; set; }

    [JsonPropertyName("selected")]
    public bool Selected { get; set; }

    [JsonPropertyName("uris")]
    public List<Aria2Uri>? Uris { get; set; }
}

public sealed class Aria2Uri
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

/// <summary>One connected peer as returned by aria2.getPeers (BitTorrent only).</summary>
public sealed class Aria2Peer
{
    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonPropertyName("port")]
    public long Port { get; set; }

    [JsonPropertyName("downloadSpeed")]
    public long DownloadSpeed { get; set; }

    [JsonPropertyName("uploadSpeed")]
    public long UploadSpeed { get; set; }

    [JsonPropertyName("seeder")]
    public bool Seeder { get; set; }
}

/// <summary>Result of aria2.getGlobalStat.</summary>
public sealed class Aria2GlobalStat
{
    [JsonPropertyName("downloadSpeed")]
    public long DownloadSpeed { get; set; }

    [JsonPropertyName("uploadSpeed")]
    public long UploadSpeed { get; set; }

    [JsonPropertyName("numActive")]
    public long NumActive { get; set; }

    [JsonPropertyName("numWaiting")]
    public long NumWaiting { get; set; }

    [JsonPropertyName("numStopped")]
    public long NumStopped { get; set; }
}

/// <summary>Result of aria2.getVersion.</summary>
public sealed class Aria2VersionInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("enabledFeatures")]
    public List<string>? EnabledFeatures { get; set; }
}

/// <summary>Combined snapshot fetched in a single system.multicall round-trip.</summary>
public sealed record Aria2Snapshot(List<Aria2Download> Downloads, Aria2GlobalStat GlobalStat);
