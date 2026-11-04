using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BiliLiveManager;

public sealed record DanmakuConnection(string RoomId, string Token, IReadOnlyList<Uri> Hosts, long Uid, string Buvid);

public sealed class DanmakuRiskControlException : Exception
{
    public DanmakuRiskControlException() : base("B站风控拒绝弹幕认证请求（-352）。请先扫码登录并稍后重试；不要连续频繁连接。") { }
}


public sealed partial class BiliApi
{
    /// <summary>Resolve the real room number and retrieve an expiring chat token.
    /// Never persist or log the token / auth cookies.</summary>
    public async Task<DanmakuConnection> GetDanmakuConnectionAsync(string rawRoomId, CancellationToken ct)
    {
        string requested = ParseRoom(rawRoomId);
        using var init = await LoadJsonAsync("https://api.live.bilibili.com/room/v1/Room/room_init?id=" + requested, ct);
        string roomId = GetString(Data(init), "room_id");
        if (!long.TryParse(roomId, out var parsed) || parsed <= 0)
            throw new InvalidOperationException("弹幕连接无法解析真实房间号。");

        bool hasBuvid = await EnsureDeviceCookieAsync(ct);
        if (!hasBuvid)
            throw new InvalidOperationException("无法从B站获取 buvid3 设备标识，暂不发起弹幕认证请求；请检查网络或稍后重试。");

        // Recent Bilibili web endpoints may require signed WBI parameters.
        // This uses the same documented public WBI signer as existing live search.
        string wts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var args = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = roomId, ["type"] = "0", ["web_location"] = "444.8", ["wts"] = wts
        };
        string query = string.Join("&", args.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(Regex.Replace(p.Value, "[!'()*]", ""))));
        string url = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?" + query;
        try
        {
            using var nav = await LoadJsonAsync("https://api.bilibili.com/x/web-interface/nav", ct);
            var wbi = Get(Data(nav), "wbi_img");
            var image = Path.GetFileNameWithoutExtension(new Uri(GetString(wbi, "img_url")).AbsolutePath);
            var sub = Path.GetFileNameWithoutExtension(new Uri(GetString(wbi, "sub_url")).AbsolutePath);
            string seed = image + sub;
            if (seed.Length < 64) throw new FormatException("WBI 密钥长度不正确。");
            string mixin = string.Concat(WbiOrder.Select(i => seed[i]))[..32];
            string signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + mixin))).ToLowerInvariant();
            url += "&w_rid=" + signature;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Some Bilibili regions/guest sessions still permit unsigned requests.
            // Failure is surfaced to the user instead of claiming authentication.
            AppLog.Write("弹幕 WBI 密钥获取失败，尝试公开接口", ex);
        }
        using var doc = await LoadJsonAsync(url, ct);
        if (GetInt(doc.RootElement, "code") == -352) throw new DanmakuRiskControlException();
        var data = Data(doc);
        string token = GetString(data, "token");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("B站没有返回弹幕连接 token，请先登录或稍后再试。");
        var servers = new List<Uri>();
        foreach (var host in Values(Get(data, "host_list")))
        {
            var domain = GetString(host, "host");
            int port = GetInt(host, "wss_port");
            if (string.IsNullOrWhiteSpace(domain) || port is < 1 or > 65535 ||
                !Regex.IsMatch(domain, "^[a-zA-Z0-9.-]+$") ||
                !(domain.Equals("chat.bilibili.com", StringComparison.OrdinalIgnoreCase) ||
                  domain.EndsWith(".chat.bilibili.com", StringComparison.OrdinalIgnoreCase))) continue;
            servers.Add(new Uri($"wss://{domain}:{port}/sub"));
        }
        if (servers.Count == 0) throw new InvalidOperationException("B站没有返回可用弹幕 WebSocket 服务器。");
        string cookie;
        lock (_cookieLock) cookie = _sessionCookies.GetCookieHeader(new Uri("https://api.live.bilibili.com/"));
        string? ReadCookie(string key) => cookie.Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(p => p.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1);
        long.TryParse(ReadCookie("DedeUserID"), out long uid);
        string buvid = ReadCookie("buvid3") ?? throw new InvalidOperationException("认证请求缺少真实 buvid3 Cookie。");
        return new DanmakuConnection(roomId, token, servers, uid, buvid);
    }
}
