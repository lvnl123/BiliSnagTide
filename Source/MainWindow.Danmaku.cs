using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace BiliLiveManager;

public partial class MainWindow
{
    private DanmakuClient? _danmaku;
    private DanmakuFloatWindow? _danmakuFloat;
    private readonly ConcurrentQueue<DanmakuLine> _pendingDanmaku = new();
    private readonly ObservableCollection<DanmakuLine> _visibleDanmaku = new();
    private readonly List<DanmakuLine> _recentDanmaku = new();
    private readonly Queue<DateTimeOffset> _messageTimes = new();
    private readonly DanmakuDuplicateFilter _dupeFilter = new();
    private readonly DispatcherTimer _danmakuTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CancellationTokenSource? _danmakuProbeCts;
    private DanmakuTelemetry? _danmakuSnapshot;
    private string _danmakuLastDiagnostic = "尚未执行诊断。";
    private long _danmakuTotal, _danmakuFiltered, _danmakuDuplicateCount, _danmakuDropped;
    private int _danmakuSession, _danmakuGiftTotal, _danmakuScTotal;
    private bool _danmakuInitialized;
    private ScrollViewer? _danmakuScrollViewer;
    private bool _danmakuFollowScheduled;
    private bool _danmakuProgrammaticScroll;
    private bool _danmakuUserAwayFromBottom;
    private int _danmakuUnread;


    private void InitializeDanmakuUi()
    {
        DanmakuList.ItemsSource = _visibleDanmaku;
        _danmakuInitialized = true;
        DanmakuList.Loaded += (_, _) => { HookDanmakuScrollViewer(); QueueDanmakuFollow(true); };
        DanmakuList.IsVisibleChanged += (_, _) => { if (DanmakuList.IsVisible) QueueDanmakuFollow(); };
        DanmakuList.PreviewMouseWheel += DanmakuList_MouseWheel;
        DanmakuList.PreviewKeyDown += DanmakuList_KeyDown;
        _danmakuTimer.Tick += (_, _) => DrainDanmaku();
        _danmakuTimer.Start();
        DanmakuDiagnosticText.Text = _danmakuLastDiagnostic;
        UpdateDanmakuCounters();
    }
    private string CurrentDanmakuRoom() =>
        !string.IsNullOrWhiteSpace(RoomInput.Text) ? RoomInput.Text : _current?.RoomId ?? "";

    private void DanmakuStart_Click(object sender, RoutedEventArgs e)
    {
        string room;
        try { room = BiliApi.ParseRoom(CurrentDanmakuRoom()); }
        catch (ArgumentException) { DanmakuStatusText.Text = "请先输入有效的B站直播间号码或链接。"; return; }
        _danmaku?.Stop(); _danmaku?.Dispose(); _danmaku = null;
        _danmakuSession++;
        int id = _danmakuSession;
        ResetDanmakuMessages();
        _danmakuSnapshot = null;
        DanmakuRoomText.Text = "房间 " + room + " · 弹幕接收";
        DanmakuStatusText.Text = "开始连接…";
        var client = new DanmakuClient(_api);
        client.Message += line =>
        {
            if (id != Volatile.Read(ref _danmakuSession)) return;
            if (_pendingDanmaku.Count < 1_200) _pendingDanmaku.Enqueue(line);
            else Interlocked.Increment(ref _danmakuDropped);
        };
        client.State += status => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (id == _danmakuSession) DanmakuStatusText.Text = status;
        }));
        client.Metrics += snap => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (id != _danmakuSession) return;
            _danmakuSnapshot = snap;
            DanmakuPhaseText.Text = snap.Phase;
            DanmakuHostText.Text = "节点：" + snap.Host;
            DanmakuAuthText.Text = snap.Authenticated ? "已认证" : "未认证";
            DanmakuHeartText.Text = "心跳应答：" + snap.HeartbeatReplies;
            DanmakuReconnectText.Text = "连接尝试：" + snap.Attempt;
            DanmakuActivityText.Text = "接收帧：" + snap.Frames + " · 人气参考值：" + snap.Popularity + " · 最近心跳：" + (snap.LastHeartbeat?.ToLocalTime().ToString("HH:mm:ss") ?? "暂无");
        }));
        _danmaku = client;
        client.Start(room);
    }
    private void DanmakuStop_Click(object sender, RoutedEventArgs e)
    {
        _danmakuSession++;
        _danmaku?.Stop(); _danmaku?.Dispose(); _danmaku = null;
        _danmakuSnapshot = null;
        DanmakuPhaseText.Text = "已断开";
        DanmakuStatusText.Text = "已主动断开；列表保留在本次程序内存中。";
    }
    private void DanmakuFloat_Click(object sender, RoutedEventArgs e)
    {
        if (_danmakuFloat is { IsLoaded: true })
        {
            _danmakuFloat.Close(); _danmakuFloat = null;
            DanmakuFloatButton.Content = "打开弹幕浮窗";
            return;
        }
        var popup = new DanmakuFloatWindow
        {
            Opacity = DanmakuOpacitySlider.Value,
            Topmost = DanmakuTopmost.IsChecked == true,
            DisableEntranceMotion = _reduceMotion
        };
        popup.SetFontSize(DanmakuFontSlider.Value);
        popup.SetMaxLines((int)DanmakuFloatCount.Value);
        popup.Closed += (_, _) =>
        {
            if (_danmakuFloat == popup)
            {
                _danmakuFloat = null;
                DanmakuFloatButton.Content = "打开弹幕浮窗";
            }
        };
        _danmakuFloat = popup;
        foreach (var line in _visibleDanmaku.TakeLast(8)) popup.Add(line);
        popup.Show();
        DanmakuFloatButton.Content = "关闭弹幕浮窗";
    }
    private void DanmakuOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_danmakuFloat != null) _danmakuFloat.Opacity = Math.Clamp(e.NewValue, 0.45, 1.0);
    }
    private void DanmakuFont_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _danmakuFloat?.SetFontSize(e.NewValue);
    private void DanmakuFloatCount_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _danmakuFloat?.SetMaxLines((int)e.NewValue);
    private void DanmakuTopmost_Changed(object sender, RoutedEventArgs e)
    {
        if (_danmakuFloat != null) _danmakuFloat.Topmost = DanmakuTopmost.IsChecked == true;
    }
    private void DanmakuKeyword_TextChanged(object sender, TextChangedEventArgs e) => RefreshDanmakuView();
    private void DanmakuFocus_TextChanged(object sender, TextChangedEventArgs e) => RefreshDanmakuView();
    private void DanmakuFilter_Changed(object sender, RoutedEventArgs e) => RefreshDanmakuView();

    private DanmakuFilterRules ReadDanmakuRules() => new()
    {
        Contains = DanmakuKeyword.Text,
        Excludes = DanmakuExclude.Text,
        IgnoredUsers = DanmakuIgnoredUsers.Text,
        ShowGifts = DanmakuShowGift.IsChecked == true,
        ShowSuperChats = DanmakuShowSc.IsChecked == true
    };
    private void RefreshDanmakuView()
    {
        if (!_danmakuInitialized || DanmakuKeyword == null || DanmakuExclude == null ||
            DanmakuIgnoredUsers == null || DanmakuShowGift == null || DanmakuShowSc == null) return;
        var rules = ReadDanmakuRules();
        _visibleDanmaku.Clear(); _danmakuFloat?.ClearMessages();
        foreach (var line in _recentDanmaku.Where(rules.Match).TakeLast(200))
        {
            line.IsHighlighted = DanmakuFocusTerms.IsFocus(line, DanmakuFocusWords.Text);
            _visibleDanmaku.Add(line);
        }
        if (_danmakuFloat != null)
            foreach (var line in _visibleDanmaku.TakeLast(8)) _danmakuFloat.Add(line);
        UpdateDanmakuCounters();
        QueueDanmakuFollow();
    }
    private void DrainDanmaku()
    {
        if (!_danmakuInitialized) return;
        // Freeze stops rendering; upstream receiving is still bounded (max 1200 queued).
        if (DanmakuFreeze.IsChecked == true) { UpdateDanmakuCounters(); return; }
        var rules = ReadDanmakuRules();
        int processed = 0, added = 0;
        while (processed++ < 70 && _pendingDanmaku.TryDequeue(out var line))
        {
            _danmakuTotal++;
            if (line.Kind == "礼物") _danmakuGiftTotal++;
            if (line.Kind == "醒目留言") _danmakuScTotal++;
            var now = DateTimeOffset.UtcNow;
            _messageTimes.Enqueue(now);
            if (DanmakuDedupe.IsChecked == true && _dupeFilter.IsDuplicate(line, now))
            { _danmakuDuplicateCount++; continue; }
            line.IsHighlighted = DanmakuFocusTerms.IsFocus(line, DanmakuFocusWords.Text);
            _recentDanmaku.Add(line);
            if (_recentDanmaku.Count > 300) _recentDanmaku.RemoveAt(0);
            if (!rules.Match(line)) { _danmakuFiltered++; continue; }
            _visibleDanmaku.Add(line);
            if (_visibleDanmaku.Count > 200) _visibleDanmaku.RemoveAt(0);
            _danmakuFloat?.Add(line);
            added++;
        }
        UpdateDanmakuCounters();
        if (added > 0)
        {
            if (DanmakuPaused.IsChecked == true || _danmakuUserAwayFromBottom)
            {
                _danmakuUnread += added;
                UpdateDanmakuFollowButton();
            }
            else QueueDanmakuFollow();
        }
    }
    private void UpdateDanmakuCounters()
    {
        if (!_danmakuInitialized) return;
        var now = DateTimeOffset.UtcNow;
        while (_messageTimes.Count > 0 && now - _messageTimes.Peek() > TimeSpan.FromMinutes(1)) _messageTimes.Dequeue();
        DanmakuCounterText.Text = $"收到 {_danmakuTotal} · 显示 {_visibleDanmaku.Count} · 跳过 {_danmakuFiltered + _danmakuDuplicateCount}";
        DanmakuRateText.Text = _messageTimes.Count + " 条/分钟";
        DanmakuTypeText.Text = $"礼物 {_danmakuGiftTotal} · SC {_danmakuScTotal}";
        DanmakuDroppedText.Text = Interlocked.Read(ref _danmakuDropped) > 0 ? "队列溢出：" + Interlocked.Read(ref _danmakuDropped) : "队列正常";
    }
    private void ResetDanmakuMessages()
    {
        _recentDanmaku.Clear(); _visibleDanmaku.Clear(); _danmakuFloat?.ClearMessages();
        _danmakuUnread = 0;
        _danmakuUserAwayFromBottom = false;
        UpdateDanmakuFollowButton();
        _messageTimes.Clear(); _dupeFilter.Clear();
        while (_pendingDanmaku.TryDequeue(out _)) { }
        _danmakuTotal = _danmakuFiltered = _danmakuDuplicateCount = 0;
        _danmakuGiftTotal = _danmakuScTotal = 0;
        Interlocked.Exchange(ref _danmakuDropped, 0);
        UpdateDanmakuCounters();
    }
    private void DanmakuClear_Click(object sender, RoutedEventArgs e) => ResetDanmakuMessages();
    private void DanmakuDemo_Click(object sender, RoutedEventArgs e)
    {
        var examples = new[]
        {
            new DanmakuLine { Kind = "弹幕", User = "模拟观众A", Text = "[离线模拟] 弹幕显示测试" },
            new DanmakuLine { Kind = "弹幕", User = "模拟观众B", Text = "[离线模拟] 过滤、去重和悬浮窗测试" },
            new DanmakuLine { Kind = "礼物", User = "模拟观众C", Text = "[离线模拟] 送出礼物 ×2" },
            new DanmakuLine { Kind = "醒目留言", User = "模拟观众D", Text = "[离线模拟] 醒目留言测试" },
        };
        foreach (var msg in examples) _pendingDanmaku.Enqueue(msg);
        DanmakuStatusText.Text = "已注入4条本地模拟消息，不代表B站连接成功。";
    }
    // WPF integration smoke test: actually renders rows and verifies scroll offsets.
    // Must run on a Windows desktop; unlike packet fixtures it tests both real visual trees.
    private async void DanmakuScrollSelfTest_Click(object sender, RoutedEventArgs e)
    {
        if (_danmaku != null)
        {
            DanmakuStatusText.Text = "请先断开实时弹幕，再执行滚动自测，避免混入模拟消息。";
            return;
        }
        DanmakuPaused.IsChecked = false;
        _danmakuUserAwayFromBottom = false;
        ResetDanmakuMessages();
        if (_danmakuFloat == null) DanmakuFloat_Click(sender, e);
        if (_danmakuFloat == null) return;
        _danmakuFloat.SetMaxLines(25);
        for (int i = 1; i <= 48; i++)
            _pendingDanmaku.Enqueue(new DanmakuLine
            {
                Kind = "弹幕", User = "自测用户" + i,
                Text = $"[滚动自测 {i}/48] 这是用于测试最新消息跟随与悬浮窗滚动的本地示例内容。"
            });
        DrainDanmaku();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        // Layout can be deferred until next render on some machines; follow again after it.
        QueueDanmakuFollow(true);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        HookDanmakuScrollViewer();
        bool mainOK = _danmakuScrollViewer != null &&
            _danmakuScrollViewer.ScrollableHeight - _danmakuScrollViewer.VerticalOffset <= 3;
        var floatState = _danmakuFloat.GetScrollCheck();
        _danmakuLastDiagnostic = "滚动 UI 自测（生成48条本地模拟消息，不代表B站连接成功）：\n"
            + $"主列表：{(mainOK ? "已跟随到底部" : "未到达底部 / 尚未完成布局")}\n"
            + $"悬浮窗：{(floatState.AtBottom ? "已跟随到底部" : "未到达底部 / 尚未完成布局")}\n"
            + $"悬浮窗系统滚动条：{(floatState.HiddenScrollbar ? "隐藏" : "可见（未通过）")}\n"
            + "结果以当前 Windows 实机测量为准；若刚弹出窗口，可再运行一次。";
        DanmakuDiagnosticText.Text = _danmakuLastDiagnostic;
        DanmakuStatusText.Text = mainOK && floatState.AtBottom && floatState.HiddenScrollbar
            ? "滚动自测已通过（主列表 + 浮窗）。" : "滚动自测发现异常，请查看右侧诊断。";
    }
    private void DanmakuSelfTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var results = DanmakuSelfTest.Run();
            _danmakuLastDiagnostic = "本地离线自测：" + results.Count + " 项通过。\n" + string.Join("\n", results.Select(x => "✓ " + x)) +
                "\n不测试B站实时接口、账号权限或PotPlayer。";
        }
        catch (Exception ex)
        {
            _danmakuLastDiagnostic = "离线自测失败：" + ex.GetType().Name + " / " + ex.Message;
            AppLog.Write("弹幕离线自测失败", ex);
        }
        DanmakuDiagnosticText.Text = _danmakuLastDiagnostic;
    }
    private async void DanmakuNetworkTest_Click(object sender, RoutedEventArgs e)
    {
        if (_danmakuProbeCts != null) { DanmakuDiagnosticText.Text = "网络诊断正在运行，请避免重复请求。"; return; }
        string room;
        try { room = BiliApi.ParseRoom(CurrentDanmakuRoom()); }
        catch (ArgumentException) { DanmakuDiagnosticText.Text = "输入有效房间号后再测试。"; return; }
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _danmakuProbeCts = cts;
        var lines = new List<string>
        {
            "网络诊断（不会连接 WebSocket、不会保存 token）：",
            "步骤1 / 房间号格式：通过（" + room + "）",
            "步骤2 / 本软件登录会话：" + (_api.HasAuthenticatedSession ? "已登录" : "匿名"),
            "步骤3 / 正在请求真实房间号、弹幕令牌和节点…"
        };
        DanmakuDiagnosticText.Text = string.Join("\n", lines);
        try
        {
            var info = await _api.GetDanmakuConnectionAsync(room, cts.Token);
            lines[^1] = "步骤3 / 弹幕连接信息：通过（仅检查是否取得令牌）";
            lines.Add("步骤4 / 真实房间号：" + info.RoomId);
            lines.Add("步骤5 / B站返回节点数：" + info.Hosts.Count);
            lines.Add("步骤6 / 设备标识：" + (string.IsNullOrWhiteSpace(info.Buvid) ? "缺失" : "已获取（不显示）"));
            lines.Add("步骤7 / WebSocket 认证及心跳：未测试（请点击连接弹幕观察）。");
        }
        catch (OperationCanceledException)
        {
            lines[^1] = "步骤3 / 超时（25秒）或主动取消。";
        }
        catch (DanmakuRiskControlException)
        {
            lines[^1] = "步骤3 / B站返回 -352 风控：停止测试，请稍后再试。";
        }
        catch (Exception ex)
        {
            lines[^1] = "步骤3 / 失败：" + ex.GetType().Name + "（详细内部信息不会复制，防止泄露认证参数）";
        }
        finally { cts.Dispose(); _danmakuProbeCts = null; }
        _danmakuLastDiagnostic = string.Join("\n", lines);
        DanmakuDiagnosticText.Text = _danmakuLastDiagnostic;
    }
    private static string SafeDiagnosticError(string raw)
    {
        if (raw.Length > 200) raw = raw[..200] + "…";
        return System.Text.RegularExpressions.Regex.Replace(raw, @"(?i)(https?://[^\s?]+)\?[^\s]+", "$1?[redacted]");
    }
    private string BuildDanmakuReport()
    {
        var snap = _danmakuSnapshot;
        var sb = new StringBuilder();
        sb.AppendLine("BiliSnagTide 弹幕诊断（无Cookie/Token/消息内容）");
        sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("连接阶段：" + (snap?.Phase ?? "未连接"));
        sb.AppendLine("已通过鉴权：" + (snap?.Authenticated == true ? "是" : "否"));
        sb.AppendLine("当前节点：" + (snap?.Host ?? "—"));
        sb.AppendLine("连接尝试：" + (snap?.Attempt ?? 0));
        sb.AppendLine("接收WebSocket帧：" + (snap?.Frames ?? 0));
        sb.AppendLine("已解码消息：" + (snap?.Messages ?? 0));
        sb.AppendLine("心跳回复：" + (snap?.HeartbeatReplies ?? 0));
        sb.AppendLine("上次心跳：" + (snap?.LastHeartbeat?.ToLocalTime().ToString("HH:mm:ss") ?? "尚无"));
        sb.AppendLine("本次界面处理：" + _danmakuTotal + " 条；队列溢出：" + Interlocked.Read(ref _danmakuDropped));
        sb.AppendLine("最后错误类型：" + (snap?.LastError ?? "无"));
        sb.AppendLine("----------------\n" + _danmakuLastDiagnostic);
        return sb.ToString();
    }
    private async void DanmakuCopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        bool ok = await WriteClipboardAsync(BuildDanmakuReport());
        DanmakuStatusText.Text = ok ? "脱敏诊断已复制（不含登录信息和弹幕正文）。" : "剪贴板被占用，请在右侧诊断窗口查看。";
    }
    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
    private void HookDanmakuScrollViewer()
    {
        if (_danmakuScrollViewer != null) return;
        _danmakuScrollViewer = FindVisualChild<ScrollViewer>(DanmakuList);
        if (_danmakuScrollViewer != null)
            _danmakuScrollViewer.ScrollChanged += DanmakuScroll_Changed;
    }
    private void DanmakuScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (_danmakuProgrammaticScroll || Math.Abs(e.VerticalChange) < 0.01 ||
            Math.Abs(e.ExtentHeightChange) > 0.01 || Math.Abs(e.ViewportHeightChange) > 0.01) return;
        if (_danmakuScrollViewer == null) return;
        bool atBottom = _danmakuScrollViewer.ScrollableHeight - _danmakuScrollViewer.VerticalOffset <= 12;
        _danmakuUserAwayFromBottom = !atBottom;
        if (atBottom && DanmakuPaused.IsChecked != true)
        {
            _danmakuUnread = 0;
            UpdateDanmakuFollowButton();
        }
    }
    private void DanmakuList_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            _danmakuUserAwayFromBottom = true;
            UpdateDanmakuFollowButton();
        }
    }
    private void DanmakuList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.PageUp or Key.Up or Key.Home)
        {
            _danmakuUserAwayFromBottom = true;
            UpdateDanmakuFollowButton();
        }
    }
    private void QueueDanmakuFollow(bool force = false)
    {
        if ((!force && (DanmakuPaused.IsChecked == true || _danmakuUserAwayFromBottom)) ||
            _danmakuFollowScheduled || !DanmakuList.IsVisible) return;
        _danmakuFollowScheduled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _danmakuFollowScheduled = false;
            if (!DanmakuList.IsVisible || (!force && (DanmakuPaused.IsChecked == true || _danmakuUserAwayFromBottom))) return;
            HookDanmakuScrollViewer();
            _danmakuProgrammaticScroll = true;
            try
            {
                _danmakuScrollViewer?.ScrollToEnd();
                // WPF virtualized items may materialize on a later layout pass.
                if (_visibleDanmaku.Count > 0) DanmakuList.ScrollIntoView(_visibleDanmaku[^1]);
                _danmakuScrollViewer?.ScrollToEnd();
                _danmakuUnread = 0;
                UpdateDanmakuFollowButton();
            }
            finally
            {
                Dispatcher.BeginInvoke(new Action(() => _danmakuProgrammaticScroll = false), DispatcherPriority.ContextIdle);
            }
        }), DispatcherPriority.Loaded);
    }
    private void UpdateDanmakuFollowButton()
    {
        if (DanmakuJumpLatest == null) return;
        DanmakuJumpLatest.Content = _danmakuUnread > 0
            ? $"↓ {_danmakuUnread} 条新消息 · 回到最新" : "↓ 回到最新";
        DanmakuJumpLatest.Visibility = _danmakuUnread > 0 || _danmakuUserAwayFromBottom || DanmakuPaused.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }
    private void DanmakuPause_Changed(object sender, RoutedEventArgs e)
    {
        if (!_danmakuInitialized) return;
        if (DanmakuPaused.IsChecked != true)
        {
            _danmakuUserAwayFromBottom = false;
            QueueDanmakuFollow(true);
        }
        UpdateDanmakuFollowButton();
    }
    private void DanmakuJumpLatest_Click(object sender, RoutedEventArgs e)
    {
        _danmakuUserAwayFromBottom = false;
        DanmakuPaused.IsChecked = false;
        QueueDanmakuFollow(true);
        UpdateDanmakuFollowButton();
    }
    private async void DanmakuCopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (DanmakuList.SelectedItem is not DanmakuLine item)
        {
            DanmakuStatusText.Text = "先在实时消息列表选中一条弹幕。";
            return;
        }
        var text = $"[{item.TimeLabel}] {item.User}：{item.Text}";
        bool ok = await WriteClipboardAsync(text);
        DanmakuStatusText.Text = ok ? "选中消息已复制到剪贴板。" : "剪贴板忙，复制失败。";
    }
    private void StopDanmakuOnExit()
    {
        _danmakuSession++;
        _danmakuTimer.Stop();
        _danmakuProbeCts?.Cancel();
        _danmaku?.Stop(); _danmaku?.Dispose(); _danmaku = null;
        _danmakuFloat?.Close(); _danmakuFloat = null;
    }
}
