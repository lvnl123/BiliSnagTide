using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BiliLiveManager;

public sealed class DanmakuLine
{
    public string Kind { get; init; } = "弹幕";
    public string User { get; init; } = "";
    public string Text { get; init; } = "";
    public bool IsHighlighted { get; set; }
    public DateTime Time { get; init; } = DateTime.Now;
    public string TimeLabel => Time.ToString("HH:mm:ss");
}

/// <summary>Safely decodes Bilibili binary envelopes. No audio/video recording or message export.</summary>
public static class DanmakuPacket
{
    public static byte[] Encode(int op, ReadOnlySpan<byte> body)
    {
        var bytes = new byte[16 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), bytes.Length);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), op);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12, 4), 1);
        body.CopyTo(bytes.AsSpan(16));
        return bytes;
    }
    public static void Decode(ReadOnlySpan<byte> bytes, Action<DanmakuLine> onMessage, Action<bool>? onAuth = null, int nesting = 0, Action<int>? onHeartbeat = null)
    {
        if (nesting > 3 || bytes.Length > 5_000_000) return;
        int offset = 0;
        while (offset + 16 <= bytes.Length)
        {
            int size = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, 4));
            int headerSize = BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(offset + 4, 2));
            int version = BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(offset + 6, 2));
            int op = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset + 8, 4));
            if (size < 16 || size > bytes.Length - offset || headerSize < 16 || headerSize > size) return;
            var body = bytes.Slice(offset + headerSize, size - headerSize);
            if (version is 2 or 3)
            {
                try
                {
                    using var source = new MemoryStream(body.ToArray(), false);
                    using Stream deflate = version == 3 ? new BrotliStream(source, CompressionMode.Decompress) : new ZLibStream(source, CompressionMode.Decompress);
                    using var dest = new MemoryStream();
                    var chunk = new byte[8192];
                    int n;
                    while ((n = deflate.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (dest.Length + n > 5_000_000) break;
                        dest.Write(chunk, 0, n);
                    }
                    if (dest.Length < 5_000_000) Decode(dest.ToArray(), onMessage, onAuth, nesting + 1, onHeartbeat);
                }
                catch (InvalidDataException) { }
                catch (IOException) { }
            }
            else if (op == 3 && body.Length >= 4)
            {
                onHeartbeat?.Invoke(BinaryPrimitives.ReadInt32BigEndian(body[..4]));
            }
            else if (op == 8)
            {
                try { using var auth = JsonDocument.Parse(body.ToArray()); onAuth?.Invoke(BiliApi.GetInt(auth.RootElement, "code") == 0); }
                catch (JsonException) { onAuth?.Invoke(false); }
            }
            else if (op == 5 && body.Length > 1 && body.Length <= 200_000)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body.ToArray());
                    foreach (var line in DecodeMessage(doc.RootElement)) onMessage(line);
                }
                catch (JsonException) { }
            }
            offset += size;
        }
    }
    public static IReadOnlyList<DanmakuLine> DecodeMessage(JsonElement root)
    {
        string cmd = BiliApi.GetString(root, "cmd");
        if (cmd.StartsWith("DANMU_MSG", StringComparison.Ordinal))
        {
            var info = BiliApi.Get(root, "info");
            if (info.ValueKind != JsonValueKind.Array || info.GetArrayLength() < 3) return Array.Empty<DanmakuLine>();
            string message = info[1].ValueKind == JsonValueKind.String ? info[1].GetString() ?? "" : "";
            var author = info[2];
            string name = author.ValueKind == JsonValueKind.Array && author.GetArrayLength() > 1 ? author[1].ToString() : "观众";
            return One("弹幕", name, message);
        }
        if (cmd.StartsWith("SUPER_CHAT_MESSAGE", StringComparison.Ordinal))
        {
            var data = BiliApi.Get(root, "data");
            var user = BiliApi.Get(BiliApi.Get(data, "user_info"), "uname");
            var name = user.ValueKind == JsonValueKind.String ? user.ToString() : "醒目留言";
            string amount = BiliApi.GetString(data, "price");
            return One("醒目留言", name, "[SC ¥" + amount + "] " + BiliApi.GetString(data, "message"));
        }
        if (cmd == "SEND_GIFT")
        {
            var data = BiliApi.Get(root, "data");
            string name = BiliApi.GetString(data, "uname");
            string gift = BiliApi.GetString(data, "giftName");
            string num = BiliApi.GetString(data, "num");
            return One("礼物", name, "送出 " + gift + " ×" + num);
        }
        return Array.Empty<DanmakuLine>();
    }
    private static IReadOnlyList<DanmakuLine> One(string kind, string user, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<DanmakuLine>();
        string Clean(string s, int max) => s.Length > max ? s[..max] + "…" : s;
        return new[] { new DanmakuLine { Kind = kind, User = Clean(string.IsNullOrWhiteSpace(user) ? "观众" : user, 32), Text = Clean(text, 350) } };
    }
}

public sealed record DanmakuTelemetry(
    string Phase, string Host, bool Authenticated, int Attempt,
    long Frames, long Messages, long HeartbeatReplies, int Popularity,
    DateTimeOffset? LastFrame, DateTimeOffset? LastHeartbeat, DateTimeOffset? ConnectedAt,
    string LastError);

public sealed class DanmakuAuthDeniedException : Exception
{
    public DanmakuAuthDeniedException() : base("WebSocket 鉴权被拒绝，请重新登录B站后重试。") { }
}

/// <summary>Only receives public chat messages; no sends, recordings, or token persistence.</summary>
public sealed class DanmakuClient : IDisposable
{
    private readonly BiliApi _api;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private string _phase = "未连接", _host = "—", _error = "";
    private bool _authenticated;
    private int _attempt, _popularity;
    private long _frames, _messages, _heartbeatReplies;
    private DateTimeOffset? _lastFrame, _lastHeartbeat, _connectedAt;
    private DateTimeOffset _lastNotification = DateTimeOffset.MinValue;
    public event Action<DanmakuLine>? Message;
    public event Action<string>? State;
    public event Action<DanmakuTelemetry>? Metrics;
    public DanmakuClient(BiliApi api) => _api = api;

    public DanmakuTelemetry Snapshot
    {
        get { lock (_gate) return new(_phase, _host, _authenticated, _attempt, _frames, _messages,
            _heartbeatReplies, _popularity, _lastFrame, _lastHeartbeat, _connectedAt, _error); }
    }

    public void Start(string room)
    {
        if (_worker is { IsCompleted: false }) throw new InvalidOperationException("已有弹幕连接。");
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        lock (_gate)
        {
            _phase = "初始化"; _host = "—"; _error = ""; _authenticated = false;
            _attempt = 0; _frames = _messages = _heartbeatReplies = 0;
            _popularity = 0; _lastFrame = _lastHeartbeat = _connectedAt = null;
        }
        _worker = Task.Run(() => RunAsync(room, _cts.Token));
    }
    public void Stop() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }

    private void SetPhase(string phase, string? error = null)
    {
        lock (_gate) { _phase = phase; if (error != null) _error = error; }
        State?.Invoke(phase + (string.IsNullOrEmpty(error) ? "" : " · " + error));
        Notify(force: true);
    }
    private void Notify(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!force && (now - _lastNotification) < TimeSpan.FromSeconds(1)) return;
            _lastNotification = now;
        }
        Metrics?.Invoke(Snapshot);
    }

    private async Task RunAsync(string room, CancellationToken ct)
    {
        int failures = 0;
        try
        {
            while (!ct.IsCancellationRequested && failures < 6)
            {
                try
                {
                    SetPhase("正在获取弹幕服务器及令牌");
                    var connection = await _api.GetDanmakuConnectionAsync(room, ct);
                    Exception? last = null;
                    foreach (var host in connection.Hosts.Take(5))
                    {
                        ct.ThrowIfCancellationRequested();
                        var began = DateTimeOffset.UtcNow;
                        try
                        {
                            lock (_gate) { _attempt++; _host = host.Host; _authenticated = false; _connectedAt = null; }
                            await ConnectOnceAsync(connection, host, ct);
                            lock (_gate) { _authenticated = false; _connectedAt = null; }
                            if (ct.IsCancellationRequested) break;
                            // A stable 2-minute connection resets the consecutive failure budget.
                            failures = DateTimeOffset.UtcNow - began >= TimeSpan.FromMinutes(2) ? 0 : failures + 1;
                            last = null;
                            break;
                        }
                        catch (DanmakuAuthDeniedException) { throw; }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            last = ex;
                            failures++;
                            AppLog.Write("弹幕节点连接失败（不包含 token/会话）", new Exception(ex.GetType().Name));
                            SetPhase("节点连接失败，尝试其他节点", ex.GetType().Name);
                            if (failures >= 6) break;
                        }
                    }
                    if (ct.IsCancellationRequested) break;
                    SetPhase("连接中断，准备重新获取令牌", last?.GetType().Name);
                }
                catch (DanmakuRiskControlException ex) { SetPhase("B站风控：自动重连已停止", ex.Message); return; }
                catch (DanmakuAuthDeniedException ex) { SetPhase("弹幕认证失败：自动重连已停止", ex.Message); return; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    failures++;
                    AppLog.Write("弹幕令牌或节点请求失败", new Exception(ex.GetType().Name));
                    SetPhase("令牌请求失败", ex.GetType().Name);
                }
                if (failures >= 6) break;
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(3 * Math.Max(failures, 1), 18)), ct); }
                catch (OperationCanceledException) { break; }
            }
            SetPhase(ct.IsCancellationRequested ? "已主动断开" : "重连达到上限，已停止");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetPhase("已主动断开"); }
    }

    private async Task ConnectOnceAsync(DanmakuConnection info, Uri host, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(12));
        SetPhase("WebSocket 正在连接");
        await ws.ConnectAsync(host, connectTimeout.Token);
        lock (_gate) _connectedAt = DateTimeOffset.UtcNow;
        SetPhase("WebSocket 已连接，等待认证结果");
        var auth = JsonSerializer.Serialize(new
        {
            uid = info.Uid,
            roomid = long.Parse(info.RoomId),
            protover = 3, buvid = info.Buvid,
            support_ack = true, queue_uuid = Guid.NewGuid().ToString("N")[..8],
            scene = "", platform = "web", type = 2,
            key = info.Token
        });
        await SendAsync(ws, DanmakuPacket.Encode(7, Encoding.UTF8.GetBytes(auth)), ct);
        using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task heartbeats = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                while (await timer.WaitForNextTickAsync(keepAlive.Token))
                {
                    if (ws.State == WebSocketState.Open)
                        await SendAsync(ws, DanmakuPacket.Encode(2, Array.Empty<byte>()), keepAlive.Token);
                    // An unresponsive connection should be retried, without spamming token endpoint.
                    var snap = Snapshot;
                    if (snap.ConnectedAt is { } connected && !snap.Authenticated &&
                        DateTimeOffset.UtcNow - connected > TimeSpan.FromSeconds(65))
                    { ws.Abort(); break; }
                    if ((snap.LastHeartbeat is { } heartbeat &&
                        DateTimeOffset.UtcNow - heartbeat > TimeSpan.FromSeconds(105)) ||
                        (snap.LastHeartbeat == null && snap.ConnectedAt is { } opened &&
                        DateTimeOffset.UtcNow - opened > TimeSpan.FromSeconds(105)))
                    { ws.Abort(); break; }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }, keepAlive.Token);
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var packet = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (packet.Length + result.Count > 5_000_000) throw new InvalidDataException("弹幕消息超过 5 MB 限制");
                    packet.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Binary) continue;
                lock (_gate) { _frames++; _lastFrame = DateTimeOffset.UtcNow; }
                bool? authAccepted = null;
                DanmakuPacket.Decode(packet.ToArray(),
                    line => { lock (_gate) _messages++; Message?.Invoke(line); },
                    accepted => { authAccepted = accepted; },
                    onHeartbeat: popularity =>
                    {
                        lock (_gate)
                        {
                            _heartbeatReplies++; _lastHeartbeat = DateTimeOffset.UtcNow;
                            _popularity = Math.Max(0, popularity);
                        }
                    });
                if (authAccepted == false) throw new DanmakuAuthDeniedException();
                if (authAccepted == true)
                {
                    lock (_gate) _authenticated = true;
                    SetPhase("● 已认证 · 实时接收中");
                }
                Notify();
            }
        }
        finally
        {
            keepAlive.Cancel();
            try { await heartbeats; } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            try { if (ws.State == WebSocketState.Open) await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "close", CancellationToken.None); } catch { }
        }
    }
    private static Task SendAsync(ClientWebSocket ws, byte[] payload, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, ct);
    public void Dispose() { Stop(); }
}
