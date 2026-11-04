using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace BiliLiveManager;

/// <summary>Display-only filters. Never sends chat messages or saves viewer content.</summary>
public sealed class DanmakuFilterRules
{
    public string Contains { get; set; } = "";
    public string Excludes { get; set; } = "";
    public string IgnoredUsers { get; set; } = "";
    public bool ShowGifts { get; set; }
    public bool ShowSuperChats { get; set; } = true;

    public bool Match(DanmakuLine line)
    {
        if (line.Kind == "礼物" && !ShowGifts) return false;
        if (line.Kind == "醒目留言" && !ShowSuperChats) return false;
        if (!string.IsNullOrWhiteSpace(Contains) &&
            !Contains.Split(new[] { ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(word => line.Text.Contains(word, StringComparison.OrdinalIgnoreCase) || line.User.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        if (Terms(Excludes).Any(word => line.Text.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        if (Terms(IgnoredUsers).Any(word => string.Equals(line.User.Trim(), word, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }
    private static IEnumerable<string> Terms(string input) => input.Split(
        new[] { ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Display-only focus words. This does not filter or persist chat content.</summary>
public static class DanmakuFocusTerms
{
    public static bool IsFocus(DanmakuLine line, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Split(new[] { ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(term => line.Text.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                         line.User.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Bounded, in-memory recent-message signature suppression.</summary>
public sealed class DanmakuDuplicateFilter
{
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    public bool IsDuplicate(DanmakuLine line, DateTimeOffset now)
    {
        if (_seen.Count > 500)
        {
            foreach (var key in _seen.Where(p => now - p.Value > TimeSpan.FromSeconds(8)).Select(p => p.Key).ToArray()) _seen.Remove(key);
            // Even during high-volume bursts the in-memory index remains bounded.
            foreach (var key in _seen.OrderBy(p => p.Value).Take(Math.Max(0, _seen.Count - 500)).Select(p => p.Key).ToArray()) _seen.Remove(key);
        }
        // Only suppress identical messages from one speaker within five seconds.
        string key2 = line.Kind + "\u001f" + line.User + "\u001f" + line.Text;
        bool duplicate = _seen.TryGetValue(key2, out var last) && now - last < TimeSpan.FromSeconds(5);
        _seen[key2] = now;
        return duplicate;
    }
    public void Clear() => _seen.Clear();
}

/// <summary>Executable offline fixtures; never accesses the network or user's account.</summary>
public static class DanmakuSelfTest
{
    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        void Check(bool good, string label)
        {
            if (!good) throw new InvalidOperationException("离线自测未通过：" + label);
            passed.Add(label);
        }
        var rows = new List<DanmakuLine>();
        byte[] payload = Encoding.UTF8.GetBytes("{\"cmd\":\"DANMU_MSG\",\"info\":[[],\"offline-test\",[7,\"TestUser\"]]}");
        byte[] raw = DanmakuPacket.Encode(5, payload);
        DanmakuPacket.Decode(raw, rows.Add);
        Check(rows.Count == 1 && rows[0].User == "TestUser" && rows[0].Text == "offline-test", "普通弹幕包解析");
        bool? auth = null;
        DanmakuPacket.Decode(DanmakuPacket.Encode(8, Encoding.UTF8.GetBytes("{\"code\":0}")), _ => { }, x => auth = x);
        Check(auth == true, "WebSocket 成功鉴权回包");
        DanmakuPacket.Decode(DanmakuPacket.Encode(8, Encoding.UTF8.GetBytes("{\"code\":-1}")), _ => { }, x => auth = x);
        Check(auth == false, "WebSocket 拒绝鉴权识别");
        var heartbeat = DanmakuPacket.Encode(3, new byte[4]);
        BinaryPrimitives.WriteInt32BigEndian(heartbeat.AsSpan(16, 4), 12345);
        int popularity = -1;
        DanmakuPacket.Decode(heartbeat, _ => { }, onHeartbeat: x => popularity = x);
        Check(popularity == 12345, "心跳应答与人气值解析");
        byte[] Compressed(int proto)
        {
            using var memory = new MemoryStream();
            using (Stream stream = proto == 3
                ? new BrotliStream(memory, CompressionMode.Compress, true)
                : new ZLibStream(memory, CompressionMode.Compress, true)) stream.Write(raw);
            byte[] bytes = new byte[checked((int)memory.Length + 16)];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), bytes.Length);
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(4, 2), 16);
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(6, 2), (short)proto);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 5);
            memory.ToArray().CopyTo(bytes, 16);
            return bytes;
        }
        foreach (int proto in new[] { 2, 3 })
        {
            rows.Clear(); DanmakuPacket.Decode(Compressed(proto), rows.Add);
            Check(rows.Count == 1, proto == 2 ? "Zlib 压缩包解码" : "Brotli 压缩包解码");
        }
        rows.Clear(); DanmakuPacket.Decode(new byte[16], rows.Add);
        Check(rows.Count == 0, "畸形包安全忽略");
        var gift = System.Text.Json.JsonDocument.Parse("{\"cmd\":\"SEND_GIFT\",\"data\":{\"uname\":\"A\",\"giftName\":\"花\",\"num\":2}}");
        using (gift) Check(DanmakuPacket.DecodeMessage(gift.RootElement).Single().Kind == "礼物", "礼物消息识别");
        var sc = System.Text.Json.JsonDocument.Parse("{\"cmd\":\"SUPER_CHAT_MESSAGE\",\"data\":{\"user_info\":{\"uname\":\"A\"},\"message\":\"Hi\",\"price\":30}}");
        using (sc) Check(DanmakuPacket.DecodeMessage(sc.RootElement).Single().Kind == "醒目留言", "醒目留言识别");
        var rules = new DanmakuFilterRules { Excludes = "广告，剧透", IgnoredUsers = "baduser", Contains = "test", ShowGifts = true };
        Check(rules.Match(new DanmakuLine { User = "T", Text = "test ok" }), "组合过滤：允许匹配消息");
        Check(!rules.Match(new DanmakuLine { User = "T", Text = "test 广告" }), "组合过滤：屏蔽词");
        Check(!rules.Match(new DanmakuLine { User = "baduser", Text = "test ok" }), "组合过滤：忽略用户");
        var dupe = new DanmakuDuplicateFilter();
        var msg = new DanmakuLine { User = "A", Text = "repeat" };
        var now = DateTimeOffset.UtcNow;
        Check(!dupe.IsDuplicate(msg, now) && dupe.IsDuplicate(msg, now.AddSeconds(1)) && !dupe.IsDuplicate(msg, now.AddSeconds(7)), "五秒内重复消息去重");
        Check(DanmakuFocusTerms.IsFocus(new DanmakuLine { User = "Tester", Text = "Hello world" }, "world, notice"), "关注词高亮命中");
        Check(!DanmakuFocusTerms.IsFocus(new DanmakuLine { User = "Tester", Text = "Hello world" }, "testXYZ"), "非关注词不误高亮");
        return passed;
    }
}
