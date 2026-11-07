using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliLiveManager;
public sealed partial class BiliApi
{
    public static readonly HttpClient Client = CreateClient();
    private readonly object _cookieLock = new();
    private CookieContainer _sessionCookies = new();
    public bool HasAuthenticatedSession { get; private set; }

    // QR sign-in cookies are kept in memory for API calls; a minimal encrypted
    // session snapshot may be saved separately by AccountSessionStore (DPAPI).
    // Cookies never enter settings.json, diagnostics or exported stream URLs.
    public void SetAuthenticatedCookies(IEnumerable<Cookie> source)
    {
        var next = new CookieContainer();
        foreach (var input in source)
        {
            string domain = input.Domain.Trim().TrimStart('.');
            if (!domain.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) &&
                !domain.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsNeededCookie(input.Name)) continue;
            try
            {
                var copy = new Cookie(input.Name, input.Value, string.IsNullOrEmpty(input.Path) ? "/" : input.Path, input.Domain)
                { Secure = input.Secure, HttpOnly = input.HttpOnly };
                next.Add(copy);
            }
            catch (CookieException) { /* Malformed cookie is not forwarded. */ }
        }
        bool signed = next.GetCookieHeader(new Uri("https://api.live.bilibili.com/"))
            .Split(';', StringSplitOptions.TrimEntries).Any(x => x.StartsWith("SESSDATA=", StringComparison.Ordinal));
        lock (_cookieLock) { _sessionCookies = next; HasAuthenticatedSession = signed; }
    }
    public void ClearAuthenticatedCookies()
    {
        lock (_cookieLock) { _sessionCookies = new CookieContainer(); HasAuthenticatedSession = false; }
    }
    // Use the official navigation API to verify the session and fetch the
    // actual account nickname and avatar. Never infer login from a stored UID.
    public async Task<BiliAccount?> GetLoggedInAccountAsync(CancellationToken ct)
    {
        if (!HasAuthenticatedSession) return null;
        using var doc = await LoadJsonAsync("https://api.bilibili.com/x/web-interface/nav", ct);
        var root = doc.RootElement;
        if (GetInt(root, "code") != 0)
            throw new InvalidOperationException("获取账号资料失败（B站接口返回非零状态）。");
        var data = Get(root, "data");
        if (Get(data, "isLogin").ValueKind != JsonValueKind.True) return null;
        string id = GetString(data, "mid");
        string name = GetString(data, "uname");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;
        return new BiliAccount(id, name, GetString(data, "face"));
    }

    // Bilibili live getDanmuInfo expects a real buvid3 cookie even for many
    // anonymous sessions. Never use an invented random GUID as buvid3.
    public async Task<bool> EnsureDeviceCookieAsync(CancellationToken ct)
    {
        var host = new Uri("https://api.live.bilibili.com/");
        lock (_cookieLock)
            if (_sessionCookies.GetCookieHeader(host).Contains("buvid3=", StringComparison.Ordinal)) return true;
        try
        {
            using var doc = await LoadJsonAsync("https://api.bilibili.com/x/frontend/finger/spi", ct);
            var root = doc.RootElement;
            if (GetInt(root, "code") != 0) return false;
            var data = Get(root, "data");
            var three = GetString(data, "b_3");
            var four = GetString(data, "b_4");
            if (three.Length < 8 || three.Length > 512) return false;
            lock (_cookieLock)
            {
                _sessionCookies.Add(new Cookie("buvid3", three, "/", ".bilibili.com") { Secure = true });
                if (four.Length is > 7 and < 512)
                    _sessionCookies.Add(new Cookie("buvid4", four, "/", ".bilibili.com") { Secure = true });
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Write("B站设备标识初始化失败（不记录标识内容）", new Exception(ex.GetType().Name));
            return false;
        }
    }

    private static bool IsNeededCookie(string name) =>
        name is "SESSDATA" or "bili_jct" or "DedeUserID" or "DedeUserID__ckMd5" or "buvid3" or "buvid4" or "dy_did" ||
        name.StartsWith("acf_", StringComparison.OrdinalIgnoreCase);
    private static bool IsBilibiliApiHost(string host) => host.Equals("api.live.bilibili.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("api.bilibili.com", StringComparison.OrdinalIgnoreCase) || host.Equals("live.bilibili.com", StringComparison.OrdinalIgnoreCase);

    // Requesting an unsupported quality is never treated as proof that it is playable.
    public static IReadOnlyDictionary<int,string> ReadQualityDescriptions(JsonElement roomData)
    {
        var play = Get(Get(roomData, "playurl_info"), "playurl");
        var result = new Dictionary<int,string>();
        foreach (var q in Values(Get(play, "g_qn_desc")))
        {
            int id = GetInt(q, "qn"); string label = QualityNames.FromApiDescriptor(q);
            if (id > 0 && label.Length > 0 && label.Length < 60) result[id] = label;
        }
        return result;
    }

    private static readonly int[] WbiOrder = { 46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13,37,48,7,16,24,55,40,61,26,17,0,1,60,51,30,4,22,25,54,21,56,59,6,63,57,62,11,36,20,34,44,52 };
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, AllowAutoRedirect = true, PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 12 };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(16) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
        c.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");
        return c;
    }
    public static string ParseRoom(string raw)
    {
        raw = (raw ?? "").Trim();
        if (Regex.IsMatch(raw, @"^\d{1,15}$")) return raw;
        var m = Regex.Match(raw, @"(?:^|https?://)(?:live\.)?bilibili\.com/(?:blanc/)?(?<id>\d{1,15})(?:[/?#]|$)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups["id"].Value;
        throw new ArgumentException("请输入数字房间号或 https://live.bilibili.com/数字；关键词请使用搜索功能。");
    }
    public static string QualityLabel(int qn) => QualityNames.Fallback(qn);
    public async Task<JsonDocument> LoadJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Do not send account cookies to signed CDN URLs or unrelated hosts.
        if (req.RequestUri is { Scheme: "https" } uri && IsBilibiliApiHost(uri.Host))
        {
            string header;
            lock (_cookieLock) header = _sessionCookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header)) req.Headers.TryAddWithoutValidation("Cookie", header);
        }
        using var rsp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        rsp.EnsureSuccessStatusCode();
        await using var body = await rsp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }
    private static JsonElement Data(JsonDocument document)
    {
        var root = document.RootElement;
        if (GetInt(root, "code") != 0) throw new InvalidOperationException("B站接口返回：" + GetString(root, "message") + " (code " + GetInt(root, "code") + ")");
        if (!root.TryGetProperty("data", out var val) || val.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("接口响应不包含 data 对象。");
        return val;
    }
    public static JsonElement Get(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : default;
    public static string GetString(JsonElement obj, string name)
    {
        var v = Get(obj, name);
        return v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
    }
    public static int GetInt(JsonElement obj, string name) => int.TryParse(GetString(obj, name), out int n) ? n : 0;
    public static IEnumerable<JsonElement> Values(JsonElement obj) => obj.ValueKind == JsonValueKind.Array ? obj.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string FormatLiveTime(string raw) => long.TryParse(raw, out var sec) && sec > 1_000_000_000 && sec < 5_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : raw;
    public async Task<RoomInfo> GetRoomAsync(string input, CancellationToken ct)
    {
        var id = ParseRoom(input);
        RoomInfo info;
        try
        {
            using var doc = await LoadJsonAsync("https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + id, ct);
            var d = Data(doc);
            info = new RoomInfo {
                RoomId = GetString(d, "room_id"), Uid = GetString(d, "uid"), LiveStatus = GetInt(d, "live_status"), Title = GetString(d, "title"),
                CoverUrl = GetString(d, "user_cover"), LiveTime = FormatLiveTime(GetString(d, "live_time"))
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A reachable public playback endpoint may remain available when a legacy
            // room-metadata endpoint is unavailable. Fallback preserves extraction.
            AppLog.Write("房间详情接口失败，尝试播放信息接口降级", ex);
            using var fallback = await LoadJsonAsync("https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id=" + id + "&protocol=0%2C1&format=0%2C1%2C2&codec=0%2C1&qn=150&platform=web&ptype=8", ct);
            var d = Data(fallback);
            info = new RoomInfo { RoomId = GetString(d, "room_id"), Uid = GetString(d, "uid"), LiveStatus = GetInt(d, "live_status"), Title = "B站直播间 " + id };
        }
        if (string.IsNullOrEmpty(info.RoomId) || info.RoomId == "0") throw new InvalidOperationException("未找到这个房间号。");
        if (!string.IsNullOrEmpty(info.Uid))
        {
            try
            {
                using var who = await LoadJsonAsync("https://api.live.bilibili.com/live_user/v1/Master/info?uid=" + Uri.EscapeDataString(info.Uid), ct);
                var d2 = Data(who); var anchor = Get(d2, "info");
                info.Uname = GetString(anchor, "uname"); info.FaceUrl = GetString(anchor, "face");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { AppLog.Write("主播信息未能读取（不影响直播源）", ex); }
        }
        return info;
    }
    public async Task<List<StreamEntry>> GetStreamsAsync(string roomId, int qn, CancellationToken ct)
    {
        var id = ParseRoom(roomId);
        string url = "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id=" + id + "&protocol=0%2C1&format=0%2C1%2C2&codec=0%2C1&qn=" + qn + "&platform=web&ptype=8";
        using var doc = await LoadJsonAsync(url, ct);
        var d = Data(doc);
        var entries = ParseStreams(d);
        var names = ReadQualityDescriptions(d);
        foreach (var entry in entries) if (names.TryGetValue(entry.Quality, out var label)) entry.QualityName = label;
        return entries;
    }
    /// <summary>
    /// Scan static fallback QNs plus server-advertised QNs. The browser's 2K
    /// label may map to a code other than the legacy QN names, so record the
    /// server's g_qn_desc and the current_qn of each returned codec.
    /// </summary>
    public async Task<QualityScanResult> ScanAllQualitiesAsync(string roomId, CancellationToken ct)
    {
        string id = ParseRoom(roomId);
        using var gate = new SemaphoreSlim(3, 3);
        async Task<(List<StreamEntry> Streams, HashSet<int> Qns, Dictionary<int,string> Names, QualityScanRow Row)> ScanOneAsync(int qn)
        {
            await gate.WaitAsync(ct);
            try
            {
                string url = "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id="
                    + id + "&protocol=0%2C1&format=0%2C1%2C2&codec=0%2C1&qn=" + qn + "&platform=web&ptype=8";
                using var doc = await LoadJsonAsync(url, ct);
                var d = Data(doc);
                var names = new Dictionary<int,string>(ReadQualityDescriptions(d));
                var streams = ParseStreams(d);
                var advertised = new HashSet<int>(names.Keys);
                var playurl = Get(Get(d, "playurl_info"), "playurl");
                foreach (var st in Values(Get(playurl, "stream")))
                foreach (var fm in Values(Get(st, "format")))
                foreach (var co in Values(Get(fm, "codec")))
                foreach (var a in Values(Get(co, "accept_qn")))
                    if (int.TryParse(a.ToString(), out int n) && n > 0) advertised.Add(n);
                foreach (var stream in streams)
                    if (names.TryGetValue(stream.Quality, out var serverName)) stream.QualityName = serverName;
                int[] actual = streams.Select(x => x.Quality).Distinct().OrderByDescending(x => x).ToArray();
                return (streams, advertised, names, new QualityScanRow {
                    RequestedQn = qn,
                    RequestedName = names.TryGetValue(qn, out var label) ? label : "",
                    ActualQualities = actual.Length == 0 ? "—" : string.Join("、", actual.Select(n =>
                        (names.TryGetValue(n, out var name) ? name : QualityLabel(n)) + " (" + n + ")")),
                    StreamCount = streams.Count,
                    Formats = streams.Count == 0 ? "—" : string.Join(" / ", streams.Select(x => x.FormatLabel).Distinct()),
                    Status = QualityMatrixTools.Status(qn, actual, false),
                    Detail = actual.Contains(qn) ? "获得该 QN 的 URL；实际分辨率/帧率需媒体探测" :
                        streams.Count > 0 ? "请求已成功，但服务端返回其他 QN（可能受账号或房间策略限制）" : "未返回媒体 URL"
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppLog.Write("画质请求失败 qn=" + qn, ex);
                return (new List<StreamEntry>(), new HashSet<int>(), new Dictionary<int,string>(), new QualityScanRow {
                    RequestedQn = qn, Status = "请求失败", Detail = ex.GetType().Name + ": " + ex.Message
                });
            }
            finally { gate.Release(); }
        }
        var original = await Task.WhenAll(QualityMatrixTools.RequestedQns.Select(ScanOneAsync));
        var advertised = original.SelectMany(x => x.Qns).ToHashSet();
        // The web player may expose a new QN not present in older API docs.
        var extraQns = advertised.Where(q => q > 0 && !QualityMatrixTools.RequestedQns.Contains(q))
            .OrderByDescending(q => q).Take(12).ToArray();
        var extra = await Task.WhenAll(extraQns.Select(ScanOneAsync));
        var response = original.Concat(extra).ToArray();
        var names = new Dictionary<int,string>();
        foreach (var item in response)
            foreach (var name in item.Names) names[name.Key] = name.Value;
        advertised.UnionWith(response.SelectMany(x => x.Qns));
        var streams = QualityMatrixTools.DistinctStreams(response.SelectMany(x => x.Streams));
        foreach (var stream in streams)
            if (names.TryGetValue(stream.Quality, out var serverName)) stream.QualityName = serverName;
        var rows = response.Select(x => x.Row).ToList();
        foreach (var row in rows)
        {
            row.Advertised = advertised.Count == 0 ? "未提供" : advertised.Contains(row.RequestedQn) ? "是" : "否";
            if (names.TryGetValue(row.RequestedQn, out var serverName)) row.RequestedName = serverName;
        }
        return new QualityScanResult {
            Streams = streams, Rows = rows,
            AdvertisedQns = advertised.OrderBy(x => QualityMatrixTools.QualityOrder(x, names.GetValueOrDefault(x))).ThenBy(x => x).ToList(),
            ServerQualityNames = names
        };
    }

    // Exposed for deterministic, offline schema tests.
    public static List<StreamEntry> ParseStreams(JsonElement d)
    {
        if (GetInt(d, "live_status") != 1) throw new InvalidOperationException("该直播间当前没有正常开播。");
        var play = Get(Get(d, "playurl_info"), "playurl");
        var result = new List<StreamEntry>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var st in Values(Get(play, "stream")))
        foreach (var fm in Values(Get(st, "format")))
        foreach (var co in Values(Get(fm, "codec")))
        {
            string format = GetString(fm, "format_name");
            if (format is not ("flv" or "ts" or "fmp4")) continue;
            string baseUrl = GetString(co, "base_url");
            foreach (var u in Values(Get(co, "url_info")))
            {
                string raw = GetString(u, "host") + baseUrl + GetString(u, "extra");
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !seen.Add(raw)) continue;
                int ttl = GetInt(u, "stream_ttl");
                var streamEntry = new StreamEntry { Format = format, Codec = GetString(co, "codec_name"), Quality = GetInt(co, "current_qn"), Host = uri.Host, Url = raw, TtlSeconds = ttl };
                streamEntry.ExpiresAt = GuessExpiration(uri, ttl, streamEntry.FetchedAt);
                result.Add(streamEntry);
            }
        }
        return result.OrderBy(x => x.Format == "flv" ? 0 : x.Format == "ts" ? 1 : 2).ThenBy(x => x.Codec == "avc" ? 0 : 1).ThenBy(x => x.Host).ToList();
    }
    public static DateTimeOffset? GuessExpiration(Uri url, int ttlSeconds, DateTimeOffset fetched)
    {
        // Expiry is advisory only. Some CDN URL formats have no public expiry metadata.
        foreach (var param in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = param.Split('=', 2);
            if (parts.Length == 2 && (parts[0].Equals("expires", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("deadline", StringComparison.OrdinalIgnoreCase)))
            {
                if (long.TryParse(Uri.UnescapeDataString(parts[1]), out long timestamp) && timestamp is > 1_500_000_000 and < 4_000_000_000)
                    return DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
        }
        return ttlSeconds is > 0 and <= 86400 ? fetched.AddSeconds(ttlSeconds) : null;
    }
    public async Task<List<RoomInfo>> SearchRoomsAsync(string keyword, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyword)) throw new ArgumentException("请输入搜索关键词。");
        // Bilibili's signed WBI API. Sign only public search parameters; no bypass of access policies.
        using var nav = await LoadJsonAsync("https://api.bilibili.com/x/web-interface/nav", ct);
        var wbi = Get(Data(nav), "wbi_img");
        string img = Path.GetFileNameWithoutExtension(new Uri(GetString(wbi, "img_url")).AbsolutePath);
        string sub = Path.GetFileNameWithoutExtension(new Uri(GetString(wbi, "sub_url")).AbsolutePath);
        string raw = img + sub;
        if (raw.Length < 64) throw new InvalidOperationException("WBI 公钥未能获取，B站可能要求登录或已调整接口。");
        string mixin = string.Concat(WbiOrder.Select(i => raw[i]))[..32];
        string wts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var args = new SortedDictionary<string, string> { ["keyword"] = keyword, ["search_type"] = "live_room", ["page"] = "1", ["wts"] = wts };
        var ordered = string.Join("&", args.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(Regex.Replace(p.Value, "[!'()*]", ""))));
        string sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(ordered + mixin))).ToLowerInvariant();
        string url = "https://api.bilibili.com/x/web-interface/wbi/search/type?" + ordered + "&w_rid=" + sign;
        using var doc = await LoadJsonAsync(url, ct);
        var d = Data(doc);
        var list = new List<RoomInfo>();
        foreach (var item in Values(Get(d, "result")))
        {
            var id = GetString(item, "roomid"); if (string.IsNullOrWhiteSpace(id)) id = GetString(item, "room_id");
            if (!Regex.IsMatch(id, @"^\d+$")) continue;
            list.Add(new RoomInfo { RoomId = id, Title = WebUtility.HtmlDecode(Regex.Replace(GetString(item, "title"), "<.*?>", "")), Uname = GetString(item, "uname"), LiveStatus = GetInt(item, "live_status"), CoverUrl = GetString(item, "cover") });
        }
        return list;
    }
}

public sealed class ProbeResult
{
    public string Status { get; set; } = "未测试";
    public int? HttpCode { get; set; }
    public bool Reachable { get; set; }
    public double LatencyMs { get; set; }
    public double SampleKbps { get; set; }
}
public sealed class StreamProbe
{
    // A CDN can return HTTP 200 with an HTML error page. Mark it playable only if
    // the response actually starts with the expected media/playlist signature.
    public static bool HasExpectedPrefix(string format, ReadOnlySpan<byte> bytes)
    {
        if (format == "flv") return bytes.Length >= 3 && bytes[0] == (byte)'F' && bytes[1] == (byte)'L' && bytes[2] == (byte)'V';
        if (format is "ts" or "fmp4")
        {
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            return text.StartsWith("#EXTM3U", StringComparison.Ordinal);
        }
        return false;
    }
    public async Task<ProbeResult> CheckAsync(StreamEntry entry, int maxKb, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        var watch = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, entry.Url);
            req.Headers.Range = new RangeHeaderValue(0, Math.Max(1, Math.Min(maxKb, 256)) * 1024 - 1);
            req.Headers.Referrer = new Uri("https://live.bilibili.com/");
            using var rsp = await BiliApi.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            double latency = watch.Elapsed.TotalMilliseconds;
            if (!rsp.IsSuccessStatusCode) return new ProbeResult { Status = "HTTP " + (int)rsp.StatusCode, HttpCode = (int)rsp.StatusCode, LatencyMs = latency };
            await using var stream = await rsp.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[8192];
            var head = new byte[64]; int headCount = 0;
            int total = 0, limit = Math.Clamp(maxKb, 8, 256) * 1024;
            while (total < limit)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - total)), timeout.Token);
                if (count == 0) break;
                int take = Math.Min(count, head.Length - headCount);
                if (take > 0) { buffer.AsSpan(0, take).CopyTo(head.AsSpan(headCount)); headCount += take; }
                total += count;
            }
            bool signature = HasExpectedPrefix(entry.Format, head.AsSpan(0, headCount));
            return new ProbeResult { Status = total == 0 ? "响应空数据" : signature ? "源签名有效·未验解码" : "返回非预期媒体数据", HttpCode = (int)rsp.StatusCode, Reachable = signature, LatencyMs = latency, SampleKbps = total / 1024.0 / Math.Max(watch.Elapsed.TotalSeconds, .001) };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new ProbeResult { Status = "连接超时", LatencyMs = watch.Elapsed.TotalMilliseconds }; }
        catch (Exception e) { return new ProbeResult { Status = "连接错误: " + e.GetType().Name, LatencyMs = watch.Elapsed.TotalMilliseconds }; }
    }
}

public sealed record BiliAccount(string UserId, string UserName, string FaceUrl);
