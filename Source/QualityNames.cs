using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BiliLiveManager;

/// <summary>
/// Bilibili live QN is a quality-tier ID, NOT a bitrate or a fixed resolution.
/// UI should first use g_qn_desc[].media_base_desc.detail_desc (incl. tags).
/// Known resolution mappings are explicitly marked reference/typical only.
/// Do not mix this with VOD DASH QN IDs (64/80/120...).
/// </summary>
public static class QualityNames
{
    public static string Fallback(int qn) => qn switch
    {
        80 => "360P 流畅（参考）",
        150 => "480P 高清（参考）",
        250 => "720P 超清（参考）",
        400 => "1080P 蓝光（参考）",
        10000 => "原画（实际分辨率/帧率待检测）",
        15000 => "2K 极清（参考，实际规格待检测）",
        20000 => "4K 超高清（待检测）",
        30000 => "杜比（实际媒体规格待检测）",
        _ => "画质 QN " + qn + "（未收录）"
    };

    private static JsonElement Prop(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;

    private static string Text(JsonElement element, string key)
    {
        var value = Prop(element, key);
        return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
    }

    private static string CombineBrief(string brief, string badge)
    {
        if (brief.Length == 0) return badge;
        if (badge.Length == 0 || brief.Contains(badge, StringComparison.OrdinalIgnoreCase)) return brief;
        return brief + " " + badge;
    }

    /// <summary>Read the quality label actually offered by this room, including FPS/HDR tags when stated.</summary>
    public static string FromApiDescriptor(JsonElement item)
    {
        var qnElement = Prop(item, "qn");
        int qn = qnElement.ValueKind == JsonValueKind.Number && qnElement.TryGetInt32(out var n) ? n : 0;
        string general = Text(item, "desc");
        string hdr = Text(item, "hdr_desc");
        var media = Prop(item, "media_base_desc");
        var detail = Prop(media, "detail_desc");
        var brief = Prop(media, "brief_desc");
        string precise = Text(detail, "desc");
        string abbreviated = CombineBrief(Text(brief, "desc"), Text(brief, "badge"));
        // Keep the explicit player label, including a room's special "2K 极清".
        // If the server explicitly says 2K, never overwrite it with a guessed 1080P.
        string result = precise.Length > 0 ? precise :
            ContainsResolution(general) ? general :
            abbreviated.Length > 0 ? abbreviated :
            GetNamedFallback(qn, general);

        var tags = Prop(detail, "tag");
        if (tags.ValueKind == JsonValueKind.Array)
        {
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.String) continue;
                string name = (tag.GetString() ?? "").Trim();
                if (name.Length is < 1 or > 24 || result.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                distinct.Add(name);
            }
            if (distinct.Count > 0) result += "（" + string.Join("、", distinct) + "）";
        }
        if (hdr.Length > 0 && hdr.Length < 20 && !result.Contains(hdr, StringComparison.OrdinalIgnoreCase))
            result += " · " + hdr;
        return result;
    }

    private static bool ContainsResolution(string value) =>
        value.Contains("P", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("2K", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("4K", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("8K", StringComparison.OrdinalIgnoreCase);

    private static string GetNamedFallback(int qn, string general)
    {
        if (general.Length == 0) return Fallback(qn);
        // For legacy generic names, provide a clearly non-authoritative hint.
        return qn switch
        {
            80 when general == "流畅" => Fallback(qn),
            150 when general == "高清" => Fallback(qn),
            250 when general == "超清" => Fallback(qn),
            400 when general == "蓝光" => Fallback(qn),
            10000 when general == "原画" => Fallback(qn),
            20000 when general == "4K" => Fallback(qn),
            30000 when general == "杜比" => Fallback(qn),
            _ => general
        };
    }
}
