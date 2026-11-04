using System.Diagnostics;
using System.Text.Json;

namespace BiliLiveManager;
public static class MediaProbe
{
    public static double? ParseFps(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bits = value.Split('/');
        if (!double.TryParse(bits[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double n)) return null;
        if (bits.Length == 2)
        {
            if (!double.TryParse(bits[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double denom) || denom == 0) return null;
            n /= denom;
        }
        return double.IsFinite(n) && n > 0 ? n : null;
    }
    // ffprobe is optional; it is never bundled or downloaded silently.
    // Probe must not confuse a failed decoder with a confirmed media resolution.
    public static async Task<(string size, string bitrate)> CheckAsync(string file, string url, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(file)) file = "ffprobe.exe"; // use executable on PATH
        var start = new ProcessStartInfo { FileName = file, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-headers", "Referer: https://live.bilibili.com/\r\nUser-Agent: Mozilla/5.0\r\n", "-analyzeduration", "1000000", "-probesize", "524288", "-show_entries", "stream=codec_type,codec_name,width,height,bit_rate,avg_frame_rate,r_frame_rate", "-of", "json", url }) start.ArgumentList.Add(a);
        using var proc = Process.Start(start) ?? throw new InvalidOperationException("ffprobe 无法启动。请设置 ffprobe.exe 的完整路径。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            // Drain both pipes concurrently: ffprobe can otherwise block when stderr fills.
            Task<string> errorReader = proc.StandardError.ReadToEndAsync(timeout.Token);
            string output = await proc.StandardOutput.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            _ = await errorReader; // Never log stderr: some builds echo signed media URLs.
            if (proc.ExitCode != 0) throw new InvalidOperationException("ffprobe 无法解析该直播线路（可能是访问限制或超时）。");
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("ffprobe 未返回媒体流信息。");
            foreach (var stream in streams.EnumerateArray())
            {
                if (BiliApi.GetString(stream, "codec_type") != "video") continue;
                int w = BiliApi.GetInt(stream, "width"), h = BiliApi.GetInt(stream, "height");
                string bps = BiliApi.GetString(stream, "bit_rate");
                string bitrate = long.TryParse(bps, out var n) && n > 0 ? (n / 1000.0).ToString("0") + " kbps" : "未提供";
                double? fps = ParseFps(BiliApi.GetString(stream, "avg_frame_rate"))
                    ?? ParseFps(BiliApi.GetString(stream, "r_frame_rate"));
                string size = w > 0 && h > 0 ? $"{w}×{h}" : "未知";
                if (fps is > 0.0 and < 500.0) size += $" · {fps:0.#} fps";
                return (size, bitrate);
            }
            return ("未识别视频流", "未知");
        }
        finally
        {
            if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
        }
    }
}
