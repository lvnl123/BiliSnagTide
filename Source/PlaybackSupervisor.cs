using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BiliLiveManager;

/// <summary>
/// Opt-in PotPlayer recovery. Uses its published but unofficial WM_USER video-width query.
/// A successful width query is evidence of video decoding; zero/unsupported is NOT proof
/// of stream failure. Never restarts a manually closed player or an unrecognized window.
/// </summary>
public sealed class PlaybackSupervisor : IDisposable
{
    private readonly string _path;
    private readonly Func<CancellationToken, Task<List<StreamEntry>>> _refresh;
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private StreamEntry? _current;
    private Task? _loop;
    private bool _everDecoded;
    private DateTime _lastHealthy;
    private int _failovers;
    public event Action<string>? Status;
    public bool Running => _loop != null && !_loop.IsCompleted && !_stop.IsCancellationRequested;
    public PlaybackSupervisor(string path, Func<CancellationToken, Task<List<StreamEntry>>> refresh)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 PotPlayer.exe", path);
        _path = path;
        _refresh = refresh;
    }
    private void Note(string text) { AppLog.Write("PotPlayer监测: " + text); Status?.Invoke(text); }
    public void Start(StreamEntry source)
    {
        if (_loop != null) throw new InvalidOperationException("监测已经启动。");
        _current = source;
        _process = Launch(source.Url);
        _lastHealthy = DateTime.UtcNow;
        _loop = WatchAsync(_stop.Token);
        Note("已用独立 PotPlayer 窗口启动；正在检测视频是否成功解码。");
    }
    private Process Launch(string url)
    {
        var start = new ProcessStartInfo { FileName = _path, UseShellExecute = false };
        start.ArgumentList.Add(url);
        start.ArgumentList.Add("/referer=https://live.bilibili.com/");
        start.ArgumentList.Add("/user_agent=Mozilla/5.0");
        start.ArgumentList.Add("/new"); // own a separate instance, never hijack other playback
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动 PotPlayer。");
    }
    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            int missed = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                var p = _process;
                if (p == null || p.HasExited)
                {
                    Note("播放器已关闭，自动监测停止（不会擅自重新打开）。");
                    return;
                }
                var detection = PotPlayerWindowProbe.Detect(p.Id);
                if (detection == PotPlayerVideoState.Decoding)
                {
                    if (!_everDecoded) Note("已从播放器窗口读取到有效视频宽度，监测已生效。");
                    _everDecoded = true;
                    _lastHealthy = DateTime.UtcNow;
                    missed = 0;
                }
                else if (detection == PotPlayerVideoState.Unknown)
                {
                    // If window class or WM_USER version differs, do not conflate it with playback failure.
                    if ((DateTime.UtcNow - _lastHealthy).TotalSeconds >= 24 && !_everDecoded)
                    {
                        Note("当前 PotPlayer 版本无法可靠报告解码状态；停止自动干预，可手动刷新换线。");
                        return;
                    }
                }
                else
                {
                    missed++;
                    // Only auto-restart after we have definitely seen this player decode a frame before.
                    // During startup, width=0 could simply mean buffering or incompatible WM_USER API.
                    if (!_everDecoded && missed >= 7)
                    {
                        if (_failovers >= 2)
                        {
                            Note("启动阶段尚未检测到视频解码，已试过两次替换源；停止重试以避免无限弹窗。请手动检查播放器。");
                            return;
                        }
                        Note("启动后持续没有解码画面，尝试更换 CDN 并重新开启当前监控窗口。");
                        await RecoverAsync(ct).ConfigureAwait(false);
                        missed = 0;
                    }
                    if (_everDecoded && missed >= 4 && (DateTime.UtcNow - _lastHealthy).TotalSeconds >= 16)
                    {
                        if (_failovers >= 3) { Note("已达到自动恢复上限（3 次），避免反复弹出播放器；可手动恢复。"); return; }
                        Note("持续未检测到视频解码，尝试刷新线路并恢复播放…");
                        await RecoverAsync(ct).ConfigureAwait(false);
                        missed = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note("监测失败：" + ex.GetType().Name + "（详见诊断日志）"); AppLog.Write("播放监测错误", ex); }
    }
    private async Task RecoverAsync(CancellationToken ct)
    {
        ++_failovers;
        var fresh = await _refresh(ct).ConfigureAwait(false);
        if (fresh.Count == 0) { Note("刷新失败：当前直播间没有返回公开直播流。"); return; }
        var previous = _current;
        var next = fresh.OrderBy(x => x.StableKey == previous?.StableKey ? 2 :
            x.Format == previous?.Format && x.Codec == previous?.Codec ? 0 : 1).First();
        var old = _process;
        if (old == null || old.HasExited) return;
        // Do not forcibly terminate any player. If it cannot close, skip this recovery.
        if (!old.CloseMainWindow()) { Note("播放器不接受关闭窗口请求，已取消自动重启。 "); return; }
        using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeCts.CancelAfter(TimeSpan.FromSeconds(5));
        try { await old.WaitForExitAsync(closeCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Note("旧播放器未关闭，不创建多个窗口。请手动换线。"); return; }
        _process = Launch(next.Url);
        _current = next;
        _lastHealthy = DateTime.UtcNow;
        Note($"恢复尝试 {_failovers}/3：切换到 {next.FormatLabel} · {next.Host}，待确认视频解码。");
    }
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        // Deliberately do not close PotPlayer: stopping a monitor must not stop user's video.
    }
}

public enum PotPlayerVideoState { Unknown, NoVideo, Decoding }

public static class PotPlayerWindowProbe
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    public static PotPlayerVideoState Detect(int processId)
    {
        PotPlayerVideoState state = PotPlayerVideoState.Unknown;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != (uint)processId) return true;
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (!className.ToString().StartsWith("PotPlayer", StringComparison.OrdinalIgnoreCase)) return true;
            // PotPlayer's unofficial interface: WM_USER, GET_CURRENT_VIDEO_WIDTH=24624.
            if (SendMessageTimeout(hwnd, 0x400, (UIntPtr)24624, IntPtr.Zero, 0x2, 300, out var width) == IntPtr.Zero) return true;
            var pixels = width.ToUInt64();
            if (pixels >= 128 && pixels <= 16384) { state = PotPlayerVideoState.Decoding; return false; }
            state = PotPlayerVideoState.NoVideo;
            return false;
        }, IntPtr.Zero);
        return state;
    }
}
