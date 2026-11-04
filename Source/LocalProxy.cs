using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace BiliLiveManager;
// Fixed localhost address for PotPlayer / IPTV. No remote binding, public service, or elevated URL ACL.
// HTTP-FLV cannot be seamlessly concatenated after an upstream midstream disconnect:
// re-resolve + failover applies at the NEXT client request; HLS normally recovers at playlist boundaries.
public sealed class LocalProxy : IDisposable
{
    private readonly BiliApi api;
    private readonly SettingsModel settings;
    private readonly object gate = new();
    private readonly Dictionary<string, (DateTime fetched, List<StreamEntry> streams)> cache = new();
    private TcpListener? listener;
    private CancellationTokenSource? runCts;
    public bool IsRunning => listener != null;
    public event Action<string>? Status;
    public LocalProxy(BiliApi api, SettingsModel settings) { this.api = api; this.settings = settings; }
    private void Note(string message) { AppLog.Write("本地代理: " + message); Status?.Invoke(message); }
    public string Link(string roomId) => $"http://127.0.0.1:{settings.ProxyPort}/live/{Uri.EscapeDataString(roomId)}{(settings.ProxyUseHls ? ".m3u8" : ".flv")}?token={settings.ProxyToken}";
    public void Start()
    {
        if (listener != null) return;
        int port = settings.ProxyPort;
        if (port is < 1024 or > 65535) throw new ArgumentException("端口必须在 1024–65535 之间。");
        var socket = new TcpListener(IPAddress.Loopback, port);
        socket.Start(40);
        runCts = new CancellationTokenSource(); listener = socket;
        _ = AcceptLoopAsync(socket, runCts.Token);
        Note("代理已经启动：127.0.0.1:" + port);
    }
    public void Stop()
    {
        runCts?.Cancel(); try { listener?.Stop(); } catch { }
        listener = null; runCts?.Dispose(); runCts = null;
        Note("代理已停止");
    }
    public void Dispose() => Stop();
    private async Task AcceptLoopAsync(TcpListener socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await socket.AcceptTcpClientAsync(ct);
                _ = HandleAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Note("接入错误：" + ex.Message); await Task.Delay(300); }
        }
    }
    private static bool Trusted(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
        string host = u.DnsSafeHost.ToLowerInvariant();
        return host == "bilibili.com" || host.EndsWith(".bilibili.com") || host == "bilivideo.com" || host.EndsWith(".bilivideo.com") || host == "bilivideo.cn" || host.EndsWith(".bilivideo.cn") || host == "bilivideo.net" || host.EndsWith(".bilivideo.net");
    }
    private async Task HandleAsync(TcpClient client, CancellationToken serverCt)
    {
        using (client)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            var ct = linked.Token;
            using var network = client.GetStream();
            bool headersWritten = false;
            try
            {
                using var reader = new StreamReader(network, Encoding.ASCII, false, 8192, true);
                string? line = await reader.ReadLineAsync(ct);
                if (line == null || line.Length > 16384) return;
                var first = line.Split(' ');
                bool headOnly = first.Length >= 2 && first[0] == "HEAD";
                if (first.Length < 2 || (first[0] != "GET" && !headOnly)) { await ErrorAsync(network, 405, "GET or HEAD only", ct); return; }
                int total = 0;
                string? requestedRange = null;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct)))
                {
                    total += line.Length;
                    if (total > 24_576) { await ErrorAsync(network, 431, "headers too large", ct); return; }
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) {
                        var value = line[6..].Trim();
                        // Only a single HTTP byte-range is allowed; never forward arbitrary headers.
                        if (Regex.IsMatch(value, @"^bytes=\d*-\d+$", RegexOptions.IgnoreCase)) requestedRange = value;
                    }
                }
                if (!Uri.TryCreate("http://127.0.0.1" + first[1], UriKind.Absolute, out var requestUri)) { await ErrorAsync(network, 400, "invalid path", ct); return; }
                var query = ParseQuery(requestUri.Query);
                if (!query.TryGetValue("token", out var token) || token != settings.ProxyToken) { await ErrorAsync(network, 403, "invalid token", ct); return; }
                if (headOnly)
                {
                    string mediaType = requestUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "application/vnd.apple.mpegurl" :
                        requestUri.AbsolutePath.EndsWith(".flv", StringComparison.OrdinalIgnoreCase) ? "video/x-flv" : "application/octet-stream";
                    if (!requestUri.AbsolutePath.StartsWith("/live/", StringComparison.Ordinal) && requestUri.AbsolutePath != "/segment")
                    { await WriteHeaderAsync(network, 404, "Not Found", "text/plain", 0, ct); return; }
                    await WriteHeaderAsync(network, 200, "OK", mediaType, null, ct);
                    return;
                }
                if (requestUri.AbsolutePath.StartsWith("/live/", StringComparison.Ordinal))
                {
                    var pathPart = Uri.UnescapeDataString(requestUri.AbsolutePath[6..]);
                    bool hlsRequest = pathPart.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
                    bool flvRequest = pathPart.EndsWith(".flv", StringComparison.OrdinalIgnoreCase);
                    if (!hlsRequest && !flvRequest) { await ErrorAsync(network, 404, "expected .m3u8 or .flv", ct); return; }
                    var room = BiliApi.ParseRoom(pathPart[..^(hlsRequest ? 6 : 4)]);
                    var streams = await GetFreshAsync(room, ct);
                    if (streams.Count == 0) { await ErrorAsync(network, 503, "no public stream", ct); return; }
                    // First try every returned CDN. If *all* fail before any response,
                    // invalidate the cached signature and ask Bilibili for new links once.
                    for (int pass = 0; pass < 2; pass++)
                    {
                        var ordered = hlsRequest
                            ? streams.Where(x => x.Format != "flv").OrderBy(x => x.Format == "ts" ? 0 : 1)
                            : streams.Where(x => x.Format == "flv");
                        foreach (var entry in ordered)
                        {
                            try {
                                if (await FetchAndWriteAsync(network, entry.Url, requestedRange, () => headersWritten = true, ct, room)) return;
                            }
                            catch (Exception ex) {
                                Note("CDN尝试失败：" + entry.Host + " " + ex.GetType().Name);
                                // If client response has already begun, never concatenate a second
                                // CDN's FLV bytes or emit a second HTTP response into one socket.
                                if (headersWritten) return;
                            }
                        }
                        if (pass == 0)
                        {
                            Note("全部CDN在握手阶段失败，强制刷新签名后重试一次。房间：" + room);
                            lock (gate) cache.Remove(room);
                            streams = await GetFreshAsync(room, ct);
                        }
                    }
                    if (!headersWritten) await ErrorAsync(network, 502, "all upstreams failed; retry later", ct);
                }
                else if (requestUri.AbsolutePath == "/segment")
                {
                    if (!query.TryGetValue("url", out var upstream) || !Trusted(upstream)) { await ErrorAsync(network, 403, "upstream rejected", ct); return; }
                    bool ok = await FetchAndWriteAsync(network, upstream, requestedRange, () => headersWritten = true, ct, query.GetValueOrDefault("room"));
                    // A signed segment URL can expire while the player still holds an old playlist.
                    // Re-query and find an EXACT matching resource path in a fresh HLS playlist.
                    // Never substitute a different segment (would corrupt playback silently).
                    if (!ok && !headersWritten && query.TryGetValue("room", out var sourceRoom))
                    {
                        try
                        {
                            foreach (var alternative in await FreshSegmentAlternativesAsync(sourceRoom, upstream, ct))
                            {
                                if (await FetchAndWriteAsync(network, alternative, requestedRange, () => headersWritten = true, ct, sourceRoom))
                                { ok = true; break; }
                                if (headersWritten) break;
                            }
                        }
                        catch (Exception ex) { Note("HLS分片重取失败：" + ex.GetType().Name); }
                    }
                    if (!ok && !headersWritten) await ErrorAsync(network, 502, "segment fetch failed; reload playlist", ct);
                }
                else await ErrorAsync(network, 404, "not found", ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Note("代理请求失败：" + ex.GetType().Name + " " + ex.Message);
                if (!headersWritten) try { await ErrorAsync(network, 502, "proxy request error", CancellationToken.None); } catch { }
            }
        }
    }
    private async Task<List<StreamEntry>> GetFreshAsync(string room, CancellationToken ct)
    {
        lock (gate)
            if (cache.TryGetValue(room, out var info) && DateTime.UtcNow - info.fetched < TimeSpan.FromSeconds(30)
                && info.streams.All(x => x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.Now.AddSeconds(20))) return info.streams.ToList();
        var streams = await api.GetStreamsAsync(room, settings.Quality, ct);
        lock (gate) cache[room] = (DateTime.UtcNow, streams);
        return streams;
    }
    private async Task<bool> FetchAndWriteAsync(Stream network, string url, string? requestedRange, Action onHeadersWritten, CancellationToken ct, string? room = null)
    {
        if (!Trusted(url)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://live.bilibili.com/");
        if (!string.IsNullOrEmpty(requestedRange)) req.Headers.TryAddWithoutValidation("Range", requestedRange);
        using var rsp = await BiliApi.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!rsp.IsSuccessStatusCode) return false;
        bool hls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || (rsp.Content.Headers.ContentType?.MediaType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ?? false);
        if (hls)
        {
            string raw = (await rsp.Content.ReadAsStringAsync(ct)).TrimStart('\uFEFF', ' ', '\r', '\n');
            if (!raw.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)) return false;
            string rewritten = HlsPlaylistTools.Rewrite(raw, new Uri(url), u => Trusted(u.ToString()) ? SegmentUrl(u.ToString(), room) : u.ToString());
            byte[] bytes = Encoding.UTF8.GetBytes(rewritten);
            await WriteHeaderAsync(network, 200, "OK", "application/vnd.apple.mpegurl", bytes.Length, ct);
            onHeadersWritten();
            await network.WriteAsync(bytes, ct);
            return true;
        }
        var content = rsp.Content.Headers.ContentType?.MediaType ?? (url.Contains(".flv", StringComparison.OrdinalIgnoreCase) ? "video/x-flv" : "application/octet-stream");
        var code = (int)rsp.StatusCode;
        await using var upstream = await rsp.Content.ReadAsStreamAsync(ct);
        byte[]? prefix = null;
        // Fail over BEFORE writing HTTP headers if a CDN returns a 200 HTML error page.
        // Byte ranges starting beyond offset zero are valid partial data; they have no FLV header.
        if (url.Contains(".flv", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(requestedRange) || requestedRange.StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase)))
        {
            prefix = new byte[3];
            try { await upstream.ReadExactlyAsync(prefix, ct); }
            catch (EndOfStreamException) { return false; }
            if (!StreamProbe.HasExpectedPrefix("flv", prefix)) return false;
        }
        await WriteHeaderAsync(network, code, code == 206 ? "Partial Content" : "OK", content, rsp.Content.Headers.ContentLength, ct,
            rsp.Content.Headers.ContentRange?.ToString());
        onHeadersWritten();
        if (prefix != null) await network.WriteAsync(prefix, ct);
        try { await upstream.CopyToAsync(network, 64 * 1024, ct); }
        catch (Exception ex) { Note("上游中途断流，需要播放器重新请求；原因：" + ex.GetType().Name); }
        return true;
    }
    private string SegmentUrl(string url, string? room) =>
        $"http://127.0.0.1:{settings.ProxyPort}/segment?token={settings.ProxyToken}&room={Uri.EscapeDataString(room ?? "")}&url={Uri.EscapeDataString(url)}";

    // Recovery after a failed /segment upstream request. Match the absolute resource
    // path and query-independent fragment identity; do not serve a different segment.
    private async Task<List<string>> FreshSegmentAlternativesAsync(string room, string original, CancellationToken ct)
    {
        if (!Uri.TryCreate(original, UriKind.Absolute, out var wanted) || !Trusted(original)) return new();
        room = BiliApi.ParseRoom(room);
        var refreshed = await api.GetStreamsAsync(room, settings.Quality, ct);
        lock (gate) cache[room] = (DateTime.UtcNow, refreshed);
        var matches = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        // In many streams getRoomPlayInfo returns a master playlist; follow at most
        // one variant level so stale HLS media segments can be matched to fresh URLs.
        foreach (var item in refreshed.Where(x => x.Format != "flv").Take(5))
        {
            await InspectPlaylistAsync(item.Url, 0);
            if (matches.Count >= 8) break;
        }
        Note($"HLS分片过期后按同一资源路径找到 {matches.Count} 条候选线路。");
        return matches;

        async Task InspectPlaylistAsync(string url, int depth)
        {
            if (!Trusted(url) || !visited.Add(url) || depth > 1 || visited.Count > 15) return;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri("https://live.bilibili.com/");
                using var rsp = await BiliApi.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!rsp.IsSuccessStatusCode || rsp.Content.Headers.ContentLength > 2_000_000) return;
                string body = await rsp.Content.ReadAsStringAsync(ct);
                if (!body.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal)) return;
                foreach (var uri in HlsPlaylistTools.MediaUris(body, new Uri(url)))
                {
                    if (!Trusted(uri.ToString())) continue;
                    if (uri.AbsolutePath.Equals(wanted.AbsolutePath, StringComparison.Ordinal) &&
                        !uri.Equals(wanted) && !matches.Contains(uri.ToString(), StringComparer.Ordinal))
                        matches.Add(uri.ToString());
                    if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) && depth == 0 && visited.Count < 15)
                        await InspectPlaylistAsync(uri.ToString(), depth + 1);
                    if (matches.Count >= 8) break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { AppLog.Write("HLS候选播放列表读取失败", ex); }
        }
    }
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = part.IndexOf('=');
            if (i < 0) continue;
            dict[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return dict;
    }
    private static async Task WriteHeaderAsync(Stream s, int status, string message, string contentType, long? length, CancellationToken ct, string? contentRange = null)
    {
        var header = $"HTTP/1.1 {status} {message}\r\nContent-Type: {contentType}\r\nAccess-Control-Allow-Origin: http://127.0.0.1\r\nCache-Control: no-store\r\nAccept-Ranges: bytes\r\nConnection: close\r\n";
        if (length.HasValue) header += "Content-Length: " + length.Value + "\r\n";
        if (!string.IsNullOrEmpty(contentRange)) header += "Content-Range: " + contentRange + "\r\n";
        header += "\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
    }
    private static async Task ErrorAsync(Stream network, int status, string message, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(message);
        await WriteHeaderAsync(network, status, message, "text/plain; charset=utf-8", data.Length, ct);
        await network.WriteAsync(data, ct);
    }
}
