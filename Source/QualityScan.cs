using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliLiveManager;

/// <summary>One explicit qn request; an HTTP success is not a guarantee that the requested quality is returned.</summary>
public sealed class QualityScanRow
{
    public int RequestedQn { get; set; }
    public string RequestedName { get; set; } = "";
    public string RequestedLabel => (string.IsNullOrWhiteSpace(RequestedName) ? BiliApi.QualityLabel(RequestedQn) : RequestedName) + " (" + RequestedQn + ")";
    public string ActualQualities { get; set; } = "—";
    public string Advertised { get; set; } = "未知";
    public int StreamCount { get; set; }
    public string Formats { get; set; } = "—";
    public string Status { get; set; } = "待扫描";
    public string Detail { get; set; } = "";
}

public sealed class QualityScanResult
{
    public List<StreamEntry> Streams { get; init; } = new();
    public List<QualityScanRow> Rows { get; init; } = new();
    public List<int> AdvertisedQns { get; init; } = new();
    public Dictionary<int,string> ServerQualityNames { get; init; } = new();
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
}

public static class QualityMatrixTools
{
    // UI order: low resolution -> high resolution. QN itself is an ID, not a bitrate.
    // Use the real server label for unusual QNs (for example special 2K labels).
    public static int QualityOrder(int qn, string? serverLabel = null)
    {
        var label = serverLabel ?? "";
        if (label.Contains("杜比", StringComparison.OrdinalIgnoreCase)) return 80000;
        if (label.Contains("8K", StringComparison.OrdinalIgnoreCase)) return 75000;
        if (label.Contains("4K", StringComparison.OrdinalIgnoreCase)) return 70000;
        if (label.Contains("2K", StringComparison.OrdinalIgnoreCase)) return 60000;
        if (label.Contains("1080", StringComparison.OrdinalIgnoreCase)) return 50000;
        if (label.Contains("720", StringComparison.OrdinalIgnoreCase)) return 30000;
        if (label.Contains("480", StringComparison.OrdinalIgnoreCase)) return 20000;
        if (label.Contains("360", StringComparison.OrdinalIgnoreCase)) return 10000;
        return qn switch
        {
            80 => 10000,
            150 => 20000,
            250 => 30000,
            400 => 40000,
            10000 => 50000,
            15000 => 60000,
            20000 => 70000,
            30000 => 80000,
            // Unknown IDs are kept distinct but should never be presented as known resolutions.
            _ => 45000 + Math.Clamp(qn, 0, 4999)
        };
    }

    public static readonly int[] RequestedQns = { 80, 150, 250, 400, 10000, 15000, 20000, 30000 };
    public static string Status(int requested, IReadOnlyCollection<int> actual, bool errored)
        => errored ? "请求失败" : actual.Contains(requested) ? "已获取" : actual.Count > 0 ? "返回其他画质" : "没有返回流";

    // Signed CDN URLs may differ only in the time-sensitive query parameters.
    // Never dedupe across qn, codec, format or absolute media path.
    public static string StreamIdentity(StreamEntry x)
    {
        string path = Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : x.Url;
        return string.Join("|", x.Format, x.Codec, x.Quality, x.Host, path);
    }

    public static List<StreamEntry> DistinctStreams(IEnumerable<StreamEntry> streams)
        => streams.GroupBy(StreamIdentity, StringComparer.Ordinal).Select(g => g.OrderByDescending(x => x.FetchedAt).First())
                  .OrderBy(x => QualityOrder(x.Quality, x.QualityName)).ThenBy(x => x.Quality).ThenBy(x => x.Format).ThenBy(x => x.Codec).ThenBy(x => x.Host).ToList();
}
