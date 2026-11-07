using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BiliLiveManager;
public partial class MainWindow : Window
{
    private readonly BiliApi _api = new();
    private readonly StreamProbe _probe = new();
    private readonly SettingsModel _settings;
    private readonly LocalProxy _proxy;
    private PlaybackSupervisor? _supervisor;
    private readonly ObservableCollection<StreamEntry> _streams = new();
    private readonly ObservableCollection<Favorite> _favorites = new();
    private readonly ObservableCollection<HistoryEntry> _history = new();
    private readonly ObservableCollection<HistoryEntry> _batch = new();
    private readonly ObservableCollection<RoomInfo> _search = new();
    private readonly Dictionary<string, StreamEntry> _batchRaw = new();
    private readonly List<StreamEntry> _all = new();
    private readonly ObservableCollection<QualityScanRow> _qualityRows = new();
    private QualityScanResult? _qualityScan;
    private readonly SemaphoreSlim _monitorGate = new(1, 1);
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(120) };
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _logFollowTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime _logLastWriteUtc = DateTime.MinValue;
    private long _logLastLength = -1;
    private bool _logFollowLatest = true;
    private bool _logLayoutUpdating;

    private Forms.NotifyIcon? _tray;
    private RoomInfo? _current;
    private CancellationTokenSource? _operation;
    private bool _isBusy;
    private bool _closingForReal;
    private bool _clipboardChecking;
    private string _clipboardSeen = "";
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => SetupFluentMotion();
        Loaded += async (_, _) => await CheckUpdatesOnLaunchAsync();
        _settings = Storage.Load();
        bool safeMode = Environment.GetCommandLineArgs().Contains("--safe-mode", StringComparer.OrdinalIgnoreCase);
        if (safeMode)
        {
            _settings.MinimizeToTray = false;
            _settings.ClipboardDetect = false;
            _settings.NotifyLive = false;
            AppLog.Write("以安全模式启动：禁用托盘隐藏、剪贴板轮询、自动收藏监测。");
        }
        _proxy = new LocalProxy(_api, _settings);
        _proxy.Status += message => Dispatcher.BeginInvoke(new Action(() => { ProxyStatusText.Text = message; SetStatus(message); }));
        StreamGrid.ItemsSource = _streams;
        QualityGrid.ItemsSource = _qualityRows;
        FavoriteGrid.ItemsSource = _favorites; HistoryGrid.ItemsSource = _history;
        BatchGrid.ItemsSource = _batch; SearchGrid.ItemsSource = _search;
        foreach (var f in _settings.Favorites) _favorites.Add(f);
        foreach (var h in _settings.History.OrderByDescending(x => x.Time).Take(300)) _history.Add(h);
        PopulateSettings();
        InitializeDanmakuUi();
        // Internal TextBox ScrollViewer reports manual scrollbar drags and mouse-wheel movement.
        // New log text is followed only while the user is actually at the tail.
        LogText.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(LogText_ScrollChanged), true);
        _logFollowTimer.Tick += (_, _) => { if (Pages.SelectedIndex == 8) RefreshLog(); };

        ApplyTheme(_settings.DarkTheme);
        if (!safeMode)
        {
            try { InitTray(); }
            catch (Exception ex)
            {
                // Tray creation must never block the main window from opening.
                AppLog.Write("系统托盘初始化失败；已禁用最小化到托盘", ex);
                _settings.MinimizeToTray = false;
                _tray?.Dispose();
                _tray = null;
            }
        }
        _monitorTimer.Tick += async (_, _) => await MonitorFavoritesAsync(false);
        _clipboardTimer.Tick += async (_, _) => await CheckClipboardAsync();
        _monitorTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.PollSeconds, 60, 3600));
        if (!safeMode) { _monitorTimer.Start(); _clipboardTimer.Start(); }
        Loaded += (_, _) => {
            SetStatus("全部核心模块已加载。PotPlayer 路径可在设置中指定；本地代理可按需启动。");
            if (!safeMode && Environment.GetCommandLineArgs().Contains("--minimized") && _tray?.Visible == true) { Hide(); }
            if (!safeMode && _favorites.Count > 0) _ = MonitorFavoritesAsync(false);
        };
        AppLog.Write("BiliSnagTide v4.6.2 启动");
    }
    private async void BiliLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) { SetStatus("请先结束当前画质扫描，再同步登录状态。"); return; }
        try
        {
            var dialog = new BiliLoginWindow(_current?.RoomId ?? "") { Owner = this };
            bool accepted = dialog.ShowDialog() == true;
            if (dialog.ClearLoginRequested)
            {
                _api.ClearAuthenticatedCookies();
                SetStatus("已清除本软件的B站登录状态。其他浏览器不受影响。");
                return;
            }
            if (!accepted) return;
            _api.SetAuthenticatedCookies(dialog.Cookies);
            if (!_api.HasAuthenticatedSession)
            {
                SetStatus("扫码登录未成功，仍采用匿名请求。请刷新二维码并在B站 App 确认。");
                return;
            }
            SetStatus("B站登录状态已在本软件内同步。现在重新获取该直播间画质。");
            if (_current != null)
                await RunOperationAsync("使用已登录状态重新扫描画质…", async ct => {
                    ApplyQualityScan(await _api.ScanAllQualitiesAsync(_current.RoomId, ct));
                    SetStatus("已登录扫描完成。请查看画质与源诊断；是否能拿到2K仍由B站接口决定。");
                });
        }
        catch (Exception ex)
        {
            AppLog.Write("B站扫码登录窗口异常", new Exception(ex.GetType().Name));
            SetStatus("B站扫码登录窗口启动失败：" + ex.Message);
        }
    }

    private void InitTray()
    {
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application; }
        catch { icon = System.Drawing.SystemIcons.Application; }
        _tray = new Forms.NotifyIcon { Icon = icon, Text = "BiliSnagTide · 开播监控", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => Dispatcher.BeginInvoke(new Action(RestoreWindow)));
        menu.Items.Add("刷新收藏状态", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => { _ = MonitorFavoritesAsync(true); })));
        menu.Items.Add("退出软件", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => { _closingForReal = true; Close(); })));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreWindow));
    }
    private void RestoreWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray && _tray?.Visible == true) Hide();
    }
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_closingForReal && _settings.MinimizeToTray && _tray?.Visible == true)
        {
            e.Cancel = true; Hide();
            _tray?.ShowBalloonTip(2000, "BiliSnagTide", "窗口已收起，收藏开播监控仍在运行。托盘右键可退出。", Forms.ToolTipIcon.Info);
            return;
        }
        _operation?.Cancel(); _monitorTimer.Stop(); _clipboardTimer.Stop(); _logFollowTimer.Stop();
        StopDanmakuOnExit();
        _supervisor?.Dispose(); _proxy.Dispose(); _tray?.Dispose();
        try { CaptureSettings(); SaveData(); } catch (Exception ex) { AppLog.Write("退出时保存数据失败", ex); }
    }
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        AnimateStatusChange();
        ConnectionText.Text = DateTime.Now.ToString("HH:mm:ss");
        AppLog.Write(text);
    }
    private void AddHistory(string room, string title, string action, string format = "")
    {
        _history.Insert(0, new HistoryEntry { RoomId = room, Title = title, Action = action, Format = format });
        while (_history.Count > 300) _history.RemoveAt(_history.Count - 1);
        SaveData();
    }
    private void SaveData()
    {
        _settings.Favorites = _favorites.ToList();
        _settings.History = _history.ToList();
        Storage.Save(_settings);
    }
    private async Task RunOperationAsync(string title, Func<CancellationToken, Task> work)
    {
        if (_isBusy) { SetStatus("已有任务在运行，可点击“停止请求”取消。"); return; }
        _isBusy = true; BusyBar.Visibility = Visibility.Visible;
        FetchButton.IsEnabled = false;
        _operation = new CancellationTokenSource();
        SetStatus(title);
        try { await work(_operation.Token); }
        catch (OperationCanceledException) { SetStatus("任务已取消。"); }
        catch (Exception ex) { SetStatus("失败：" + ex.Message); AppLog.Write(title + "失败", ex); System.Windows.MessageBox.Show(this, ex.Message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
        finally
        {
            _operation?.Dispose(); _operation = null;
            _isBusy = false; BusyBar.Visibility = Visibility.Collapsed; FetchButton.IsEnabled = true;
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private async void Fetch_Click(object sender, RoutedEventArgs e) => await RunOperationAsync("正在读取房间与直播源…", ct => LoadRoomAsync(RoomInput.Text, ct));
    private async void RoomInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await RunOperationAsync("正在获取直播源…", ct => LoadRoomAsync(RoomInput.Text, ct)); }
    }
    private async Task LoadRoomAsync(string raw, CancellationToken ct)
    {
        string id = BiliApi.ParseRoom(raw);
        RoomInput.Text = "https://live.bilibili.com/" + id;
        var info = await _api.GetRoomAsync(id, ct);
        _current = info;
        RoomInput.Text = "https://live.bilibili.com/" + info.RoomId;
        RoomTitle.Text = string.IsNullOrWhiteSpace(info.Title) ? "B站直播间 " + info.RoomId : info.Title;
        RoomOwner.Text = "主播：" + (string.IsNullOrEmpty(info.Uname) ? "未知" : info.Uname) + "     房间号：" + info.RoomId;
        RoomStatus.Text = info.StatusLabel + (string.IsNullOrWhiteSpace(info.LiveTime) ? "" : "    开播时间：" + info.LiveTime);
        _ = LoadCoverAsync(info.CoverUrl, RoomCover, ct);
        _ = LoadCoverAsync(info.FaceUrl, RoomAvatar, ct);
        _all.Clear(); _streams.Clear(); _qualityRows.Clear(); _qualityScan = null; UrlPreview.Text = "";
        QualitySummaryText.Text = "正在逐一请求各档画质…";
        QualityAdvertisedText.Text = "接口报告的可选画质：扫描中";
        QualityScanTimeText.Text = "";
        if (info.LiveStatus == 1)
        {
            ApplyQualityScan(await _api.ScanAllQualitiesAsync(info.RoomId, ct));
            if (_settings.AutoProbe && _streams.Count > 0) await TestEntryAsync(_streams[0], ct);
            SetStatus(_all.Count > 0
                ? $"扫描 {_qualityScan?.Rows.Count ?? 0} 档画质，取得 {_all.Count} 条直播源；登录状态：{(_api.HasAuthenticatedSession ? "已同步" : "匿名")}。"
                : "所有画质请求都没有返回媒体 URL。请打开「画质与源诊断」查看每档错误与是否被回退。");
        }
        else SetStatus("房间信息已读取，但主播当前没有正常开播。收藏后可监控开播状态。");
        AddHistory(info.RoomId, info.Title, "提取直播源", info.LiveStatus == 1 ? _all.Count + " 条" : "未开播");
        Pages.SelectedItem = MainTab;
    }
    private void ApplyQualityScan(QualityScanResult scan)
    {
        _qualityScan = scan;
        _qualityRows.Clear();
        foreach (var row in scan.Rows.OrderBy(x => QualityMatrixTools.QualityOrder(x.RequestedQn, x.RequestedName)).ThenBy(x => x.RequestedQn))
            _qualityRows.Add(row);
        _all.Clear(); _all.AddRange(scan.Streams);
        UpdateQualityFilterOptions(scan);
        ApplyFilters();
        var actual = scan.Streams.Select(x => x.Quality).Distinct().OrderBy(x => QualityMatrixTools.QualityOrder(x, scan.ServerQualityNames.GetValueOrDefault(x))).ToArray();
        QualitySummaryText.Text = "已扫描 " + scan.Rows.Count + " 档请求画质 · 实际取得 " + actual.Length + " 档画质 · " + scan.Streams.Count + " 条去重线路";
        QualityAdvertisedText.Text = "登录状态：" + (_api.HasAuthenticatedSession ? "已同步" : "匿名（可能缺少高画质权限）") + " · 接口候选画质：" + (scan.AdvertisedQns.Count == 0 ? "未提供" : string.Join("、", scan.AdvertisedQns.Select(n => (scan.ServerQualityNames.TryGetValue(n, out var label) ? label : BiliApi.QualityLabel(n)) + " (" + n + ")")));
        QualityScanTimeText.Text = "扫描时间：" + scan.ScannedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · 返回的 current_qn 才是实际画质";
    }

    // Quality choices follow actual returned streams rather than fixed labels.
    // Keep the integer QN in Tag; never use the display label as an API parameter.
    private void UpdateQualityFilterOptions(QualityScanResult scan)
    {
        var existing = (QualityChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        QualityChoice.SelectionChanged -= Filter_Changed;
        try
        {
            QualityChoice.Items.Clear();
            QualityChoice.Items.Add(new ComboBoxItem { Content = "全部画质", Tag = "all" });
            foreach (var qn in scan.Streams.Select(x => x.Quality).Distinct().OrderBy(x => QualityMatrixTools.QualityOrder(x, scan.ServerQualityNames.GetValueOrDefault(x))))
            {
                string title = scan.ServerQualityNames.TryGetValue(qn, out var description)
                    ? description : BiliApi.QualityLabel(qn);
                QualityChoice.Items.Add(new ComboBoxItem { Content = title + " (" + qn + ")", Tag = qn.ToString() });
            }
            var candidate = QualityChoice.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (item.Tag?.ToString() ?? "") == existing);
            QualityChoice.SelectedItem = candidate ?? QualityChoice.Items[0];
        }
        finally { QualityChoice.SelectionChanged += Filter_Changed; }
    }

    private void QualityDiagOpen_Click(object sender, RoutedEventArgs e) { Pages.SelectedIndex = 6; }
    private async void ScanQuality_Click(object sender, RoutedEventArgs e)
    {
        string room = _current?.RoomId ?? "";
        if (string.IsNullOrWhiteSpace(room)) { SetStatus("请先在顶部获取直播间。"); return; }
        await RunOperationAsync("正在逐档扫描画质…", async ct => {
            ApplyQualityScan(await _api.ScanAllQualitiesAsync(room, ct));
            SetStatus(_all.Count > 0 ? "画质矩阵已刷新：" + _all.Count + " 条去重直播源，详细结果见画质诊断页面。"
                : "扫描完成，但没有媒体 URL；请查看诊断表中的原因。");
        });
    }
    private async void CopyQuality_Click(object sender, RoutedEventArgs e)
    {
        if (_qualityScan == null) { SetStatus("请先扫描直播画质。"); return; }
        string report = "BiliSnagTide 画质诊断（不含签名 URL）" + Environment.NewLine
          + "房间：" + (_current?.RoomId ?? "未知") + Environment.NewLine
          + "扫描时间：" + _qualityScan.ScannedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
          + "去重线路数：" + _qualityScan.Streams.Count + Environment.NewLine
          + string.Join(Environment.NewLine, _qualityScan.Rows.Select(r => r.RequestedLabel + " | " + r.Status + " | 实际: " + r.ActualQualities + " | " + r.StreamCount + " 条 | " + r.Detail));
        SetStatus(await WriteClipboardAsync(report) ? "画质报告已复制（不含签名地址）。" : "剪贴板被占用，未能复制画质报告。");
    }

    private async Task LoadCoverAsync(string coverUrl, System.Windows.Controls.Image target, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri)) { target.Source = null; return; }
            if (uri.Scheme == "http" && (uri.Host.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
                uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
            if (uri.Scheme != "https") { target.Source = null; return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(5_000);
            var bytes = await BiliApi.Client.GetByteArrayAsync(uri, timeout.Token);
            if (ct.IsCancellationRequested) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = target == RoomAvatar ? 96 : 310; bitmap.EndInit(); bitmap.Freeze();
            target.Source = bitmap;
        }
        catch { target.Source = null; }
    }
    private void ApplyFilters()
    {
        if (StreamGrid == null || FormatChoice == null || CodecChoice == null) return;
        var previous = SelectedStream();
        string fmt = ((FormatChoice.SelectedItem as ComboBoxItem)?.Tag ?? "all").ToString()!;
        string codec = ((CodecChoice.SelectedItem as ComboBoxItem)?.Tag ?? "all").ToString()!;
        string quality = ((QualityChoice.SelectedItem as ComboBoxItem)?.Tag ?? "all").ToString()!;
        _streams.Clear();
        foreach (var e in _all.Where(x => (fmt == "all" || x.Format == fmt) && (codec == "all" || x.Codec == codec) && (quality == "all" || x.Quality.ToString() == quality))
                              .OrderBy(x => QualityMatrixTools.QualityOrder(x.Quality, x.QualityName))
                              .ThenBy(x => x.Quality).ThenBy(x => x.Format).ThenBy(x => x.Codec).ThenBy(x => x.Host))
            _streams.Add(e);
        var selected = _streams.FirstOrDefault(x => x.StableKey == previous?.StableKey) ?? _streams.FirstOrDefault();
        StreamGrid.SelectedItem = selected;
        UrlPreview.Text = selected?.Url ?? "没有匹配的直播线路。";
        ConnectionText.Text = _streams.Count + " 条线路";
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _settings == null) return;
        if (sender == QualityChoice)
        {
            if (int.TryParse((QualityChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var q)) _settings.Quality = q;
            SetStatus("已筛选实际返回的画质。获取直播源时会逐档扫描；没有返回该画质请查看「画质与源诊断」。");
        }
        if (FormatChoice != null) _settings.Format = (FormatChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        if (CodecChoice != null) _settings.Codec = (CodecChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        ApplyFilters();
    }
    private void UpdateRefreshedQuality(StreamEntry selected, List<StreamEntry> fresh)
    {
        _all.RemoveAll(x => x.Quality == selected.Quality);
        _all.AddRange(fresh);
        ApplyFilters();
    }
    private StreamEntry? SelectedStream() => StreamGrid.SelectedItem as StreamEntry;
    private void StreamGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UrlPreview == null) return;
        UrlPreview.Text = (StreamGrid.SelectedItem as StreamEntry)?.Url ?? "";
    }

    private async void StreamGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (SelectedStream() != null) await PlaySelectedAsync(); }
    private async void Play_Click(object sender, RoutedEventArgs e) => await PlaySelectedAsync();
    private async Task PlaySelectedAsync()
    {
        var selected = SelectedStream(); if (selected == null || _current == null) { SetStatus("先选择直播间与线路。"); return; }
        await RunOperationAsync("播放前正在重新提取可用地址…", async ct => {
            var fresh = (await _api.GetStreamsAsync(_current.RoomId, selected.Quality, ct)).Where(x => x.Quality == selected.Quality).ToList();
            var candidates = fresh.OrderBy(x => x.StableKey == selected.StableKey ? 0 : x.Format == selected.Format && x.Codec == selected.Codec ? 1 : 2).ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("刷新后已没有可用直播源。请确认主播仍在开播。");
            StreamEntry entry = candidates[0];
            // HTTP probe is not a decoder test. Do not reject all playback merely due
            // to rate limiting / Range rejection by upstream CDNs.
            foreach (var candidate in candidates.Take(6)) {
                var probe = await _probe.CheckAsync(candidate, Math.Min(_settings.ProbeLimitKb, 16), ct);
                if (probe.Reachable) { entry = candidate; break; }
            }
            UpdateRefreshedQuality(entry, fresh);
            StreamGrid.SelectedItem = _streams.FirstOrDefault(x => x.StableKey == entry.StableKey) ?? _streams.FirstOrDefault();
            StartPlayer(entry.Url);
            AddHistory(_current.RoomId, _current.Title, "PotPlayer 播放", entry.FormatLabel);
            SetStatus("已将最新 " + entry.FormatLabel + " 源发送到 PotPlayer；若实际黑屏，请换 CDN/编码或尝试本地代理。");
        });
    }
    private async void SmartPlay_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedStream();
        if (_current == null || selected == null) { SetStatus("请先获取直播源并选择线路。"); return; }
        string roomId = _current.RoomId;
        await RunOperationAsync("启动受监控的 PotPlayer 播放…", async ct =>
        {
            var player = FindPlayer();
            if (player == null) throw new InvalidOperationException("请先在“设置与代理”选择 PotPlayerMini64.exe 路径，再使用智能播放。");
            var fresh = (await _api.GetStreamsAsync(roomId, selected.Quality, ct)).Where(x => x.Quality == selected.Quality).ToList();
            var first = fresh.FirstOrDefault(x => x.StableKey == selected.StableKey)
                ?? fresh.FirstOrDefault(x => x.Format == selected.Format && x.Codec == selected.Codec)
                ?? fresh.FirstOrDefault() ?? throw new InvalidOperationException("直播间未返回可用线路。");
            _supervisor?.Dispose();
            var supervisor = new PlaybackSupervisor(player, async token => (await _api.GetStreamsAsync(roomId, selected.Quality, token)).Where(x => x.Quality == selected.Quality).ToList());
            supervisor.Status += msg => Dispatcher.BeginInvoke(new Action(() =>
            {
                MonitorStatusText.Text = msg;
                SetStatus(msg);
            }));
            supervisor.Start(first);
            _supervisor = supervisor;
            AddHistory(roomId, _current.Title, "智能播放与监测", first.FormatLabel);
            SetStatus("已启动独立 PotPlayer 窗口；监控依据非官方视频解码接口，播放器版本不同可能不支持。");
        });
    }
    private void StopSmart_Click(object sender, RoutedEventArgs e)
    {
        _supervisor?.Dispose(); _supervisor = null;
        MonitorStatusText.Text = "已停止监测（不会关闭 PotPlayer）";
        SetStatus("智能恢复监测已停止，播放器继续运行。");
    }
    private string? FindPlayer()
    {
        var specified = PlayerPathInput.Text.Trim();
        if (File.Exists(specified)) return specified;
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        var filenames = new[] { "PotPlayerMini64.exe", "PotPlayerMini.exe" };
        foreach (var root in roots)
        foreach (var file in filenames)
        {
            var candidate = Path.Combine(root, "DAUM", "PotPlayer", file);
            if (File.Exists(candidate)) { PlayerPathInput.Text = candidate; _settings.PlayerPath = candidate; return candidate; }
        }
        return null;
    }
    private void StartPlayer(string url)
    {
        // A new direct/regular launch supersedes any earlier recovery monitor.
        _supervisor?.Dispose(); _supervisor = null;
        MonitorStatusText.Text = "未监测（普通播放）";
        string? path = FindPlayer();
        if (path == null)
        {
            var file = new Microsoft.Win32.OpenFileDialog { Filter = "PotPlayer 程序 (*.exe)|*.exe", Title = "指定 PotPlayerMini64.exe" };
            if (file.ShowDialog(this) != true) throw new InvalidOperationException("尚未指定 PotPlayer 程序路径。");
            path = file.FileName;
            PlayerPathInput.Text = path;
            _settings.PlayerPath = path;
        }
        var start = new ProcessStartInfo { FileName = path, UseShellExecute = false };
        start.ArgumentList.Add(url);
        // PotPlayer supports these network request switches (also documented in CmdLine64.txt).
        start.ArgumentList.Add("/referer=https://live.bilibili.com/");
        start.ArgumentList.Add("/user_agent=Mozilla/5.0");
        Process.Start(start);
    }
    private void BrowseFfprobe_Click(object sender, RoutedEventArgs e)
    {
        var file = new Microsoft.Win32.OpenFileDialog { Filter = "ffprobe 可执行文件 (*.exe)|*.exe", Title = "选择 ffprobe.exe（来自 FFmpeg 工具包）" };
        if (file.ShowDialog(this) == true) FfprobePathInput.Text = file.FileName;
    }
    private async void MediaProbe_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedStream(); if (entry == null) { SetStatus("请先选择线路。"); return; }
        await RunOperationAsync("正在使用 ffprobe 探测真实媒体分辨率…", async ct => {
            var (size, bitrate) = await MediaProbe.CheckAsync(FfprobePathInput.Text.Trim(), entry.Url, ct);
            entry.VideoSize = size; entry.VideoBitrate = bitrate;
            System.Windows.Data.CollectionViewSource.GetDefaultView(StreamGrid.ItemsSource)?.Refresh();
            SetStatus("媒体探测：" + size + " · " + bitrate + "（依赖直播源可访问及 ffprobe 正常工作）。");
        });
    }
    private void BrowsePlayer_Click(object sender, RoutedEventArgs e)
    {
        var file = new Microsoft.Win32.OpenFileDialog { Filter = "PotPlayer (*.exe)|*.exe", Title = "选择播放器" };
        if (file.ShowDialog(this) == true) { PlayerPathInput.Text = file.FileName; SetStatus("已指定 PotPlayer，保存设置后会长期记住。"); }
    }
    private void DetectPlayer_Click(object sender, RoutedEventArgs e) => SetStatus(FindPlayer() == null ? "未自动检测到 PotPlayer，可点击浏览指定。" : "检测到 PotPlayer：" + PlayerPathInput.Text);
    private async Task<bool> WriteClipboardAsync(string content)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() => {
            bool ok = false;
            for (int i = 0; i < 6; i++)
            {
                try { System.Windows.Clipboard.SetDataObject(content, true); ok = true; break; }
                catch (ExternalException) { Thread.Sleep(80 + 50 * i); }
                catch (InvalidOperationException) { Thread.Sleep(80 + 50 * i); }
                catch { break; }
            }
            source.TrySetResult(ok);
        }) { IsBackground = true, Name = "BiliSnagTide Clipboard STA" };
        t.SetApartmentState(ApartmentState.STA); t.Start();
        return await source.Task;
    }
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var original = SelectedStream();
        if (original == null || _current == null) { SetStatus("请先选择一条直播线路。"); return; }
        await RunOperationAsync("正在刷新原始地址并复制…", async ct => {
            StreamEntry entry = original;
            bool refreshed = false;
            try {
                var fresh = (await _api.GetStreamsAsync(_current.RoomId, original.Quality, ct)).Where(x => x.Quality == original.Quality).ToList();
                entry = fresh.FirstOrDefault(x => x.StableKey == original.StableKey)
                    ?? fresh.FirstOrDefault(x => x.Format == original.Format && x.Codec == original.Codec)
                    ?? fresh.FirstOrDefault() ?? throw new InvalidOperationException("没有返回可用线路");
                UpdateRefreshedQuality(entry, fresh);
                StreamGrid.SelectedItem = _streams.FirstOrDefault(x => x.StableKey == entry.StableKey) ?? _streams.FirstOrDefault();
                refreshed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { AppLog.Write("复制前刷新失败，保留已有URL", ex); }
            bool ok = await WriteClipboardAsync(entry.Url);
            SetStatus(ok ? refreshed ? "新鲜直播 URL 已复制，可在 PotPlayer 中 Ctrl + U 播放。" : "已复制缓存 URL，但重新提取失败，地址可能过期。"
                : "剪贴板暂时不可用，可保存 URL 为 TXT；尝试关闭占用剪贴板的程序。 ");
        });
    }
    private void SaveTxt_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedStream(); if (selected == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "BiliSnagTide_" + (_current?.RoomId ?? "stream") + ".txt", Filter = "文本文件|*.txt" };
        if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, selected.Url, Encoding.UTF8); SetStatus("原始链接已保存（可能过期）。"); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedStream(); if (entry == null) return;
        SavePlaylist(new[] { (name: _current?.Title ?? "B站直播", url: entry.Url) }, "B站直播源.m3u", "原始 URL 可能会过期；之后请重新提取。");
    }
    private void SavePlaylist(IEnumerable<(string name, string url)> streams, string filename, string success)
    {
        var list = streams.Where(x => !string.IsNullOrWhiteSpace(x.url)).ToList();
        if (list.Count == 0) { SetStatus("没有可导出的链接。"); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = filename, Filter = "M3U 播放列表|*.m3u", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var sb = new StringBuilder("#EXTM3U\r\n");
        foreach (var (name,url) in list) sb.Append("#EXTINF:-1,").Append(name.Replace('\r',' ').Replace('\n',' ').Replace(',',' ')).Append("\r\n").Append(url).Append("\r\n");
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
        SetStatus(success + $" 共 {list.Count} 条。");
    }
    private async Task TestEntryAsync(StreamEntry entry, CancellationToken ct)
    {
        var result = await _probe.CheckAsync(entry, _settings.ProbeLimitKb, ct);
        entry.HttpReachable = result.Reachable; entry.HttpStatus = result.HttpCode;
        entry.LatencyMs = result.LatencyMs; entry.SampleKbps = result.SampleKbps; entry.TestStatus = result.Status;
        System.Windows.Data.CollectionViewSource.GetDefaultView(StreamGrid.ItemsSource)?.Refresh();
    }
    private async void ProbeAll_Click(object sender, RoutedEventArgs e)
    {
        if (_streams.Count == 0) { SetStatus("先获取直播源。"); return; }
        await RunOperationAsync("正在限量检测 CDN…", async ct => {
            int finished = 0; int total = _streams.Count;
            using var sem = new SemaphoreSlim(3);
            await Task.WhenAll(_streams.ToList().Select(async entry => {
                await sem.WaitAsync(ct);
                try {
                    var result = await _probe.CheckAsync(entry, _settings.ProbeLimitKb, ct);
                    await Dispatcher.InvokeAsync(() => {
                        entry.HttpReachable = result.Reachable; entry.HttpStatus = result.HttpCode; entry.LatencyMs = result.LatencyMs; entry.SampleKbps = result.SampleKbps; entry.TestStatus = result.Status;
                        System.Windows.Data.CollectionViewSource.GetDefaultView(StreamGrid.ItemsSource)?.Refresh();
                                    SetStatus($"已检测 {Interlocked.Increment(ref finished)} / {total} 条（HTTP 可达 ≠ 解码播放成功）");
                    });
                } finally { sem.Release(); }
            }));
            SetStatus($"检测完毕：{_streams.Count(x => x.HttpReachable)}/{_streams.Count} 条可收到 HTTP 数据，不保证播放器解码成功。");
        });
    }
    private async void ProbeSelected_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedStream(); if (entry == null) return;
        await RunOperationAsync("正在检测选中线路…", async ct => { await TestEntryAsync(entry, ct); SetStatus("检测结果：" + entry.TestStatus + "；仅验证 HTTP 数据，不验证解码。"); });
    }
    private void ChooseBest_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _streams.Where(x => x.HttpReachable).OrderBy(x => x.LatencyMs ?? double.MaxValue).ThenByDescending(x => x.SampleKbps ?? 0).FirstOrDefault();
        if (chosen == null) { SetStatus("尚无 HTTP 可达的测试结果。先点击“全部限量检测”。"); return; }
        Pages.SelectedItem = MainTab; StreamGrid.SelectedItem = chosen; StreamGrid.ScrollIntoView(chosen);
        SetStatus("已按连通性和首字节延迟选中线路；实际稳定性仍以播放为准。");
    }
    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) { SetStatus("请先选择直播间。"); return; }
        var item = _favorites.FirstOrDefault(x => x.RoomId == _current.RoomId);
        if (item != null) { _favorites.Remove(item); SetStatus("已取消收藏：" + _current.RoomId); }
        else { _favorites.Add(new Favorite { RoomId = _current.RoomId, Name = _current.Uname, Title = _current.Title, FaceUrl = _current.FaceUrl, LiveTime = _current.LiveTime, LastStatus = _current.LiveStatus, LastChecked = DateTime.Now, Status = _current.StatusLabel }); SetStatus("已收藏：" + _current.Display); }
        FavoriteGrid.Items.Refresh(); SaveData();
    }
    private async Task MonitorFavoritesAsync(bool forced)
    {
        if (!await _monitorGate.WaitAsync(0)) return;
        try
        {
            foreach (var f in _favorites.ToList())
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    var info = await _api.GetRoomAsync(f.RoomId, cts.Token);
                    bool newlyLive = f.LastStatus == 0 && info.LiveStatus == 1;
                    f.LastStatus = info.LiveStatus;
                    f.Status = info.StatusLabel;
                    if (!string.IsNullOrWhiteSpace(info.Uname)) f.Name = info.Uname;
                    if (!string.IsNullOrWhiteSpace(info.Title)) f.Title = info.Title;
                    if (!string.IsNullOrWhiteSpace(info.FaceUrl)) f.FaceUrl = info.FaceUrl;
                    f.LiveTime = info.LiveStatus == 1 ? info.LiveTime : "";
                    f.LastChecked = DateTime.Now;
                    if (newlyLive && _settings.NotifyLive && !f.Muted)
                    {
                        _tray?.ShowBalloonTip(6000, "B站直播开播提醒", (f.Name.Length > 0 ? f.Name + "：" : "") + f.Title, Forms.ToolTipIcon.Info);
                        AppLog.Write("检测到开播：房间 " + f.RoomId);
                    }
                }
                catch (Exception ex) { f.Status = "检查失败"; f.LastChecked = DateTime.Now; AppLog.Write("收藏检测失败：房间 " + f.RoomId, ex); }
                await Task.Delay(120);
            }
            FavoriteGrid.Items.Refresh(); SaveData();
            if (forced) SetStatus("收藏状态已更新。每条结果都显示实际检查时间；失败项未被标记为未开播。");
        }
        finally { _monitorGate.Release(); }
    }
    private async void RefreshFav_Click(object sender, RoutedEventArgs e) => await MonitorFavoritesAsync(true);
    private async void OpenFav_Click(object sender, RoutedEventArgs e) => await OpenFavoriteAsync();
    private async void FavoriteGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenFavoriteAsync();
    private async Task OpenFavoriteAsync()
    {
        if (FavoriteGrid.SelectedItem is not Favorite f) return;
        await RunOperationAsync("正在刷新收藏并调用 PotPlayer…", async ct => {
            await LoadRoomAsync(f.RoomId, ct);
            if (_current?.LiveStatus != 1) { SetStatus("该主播目前未开播，已显示房间资料。"); return; }
            var preferred = ChoosePreferred(_streams.ToList());
            if (preferred == null) throw new InvalidOperationException("没有可用于播放的公开直播源。");
            StartPlayer(preferred.Url);
            AddHistory(f.RoomId, _current.Title, "收藏快捷播放", preferred.FormatLabel);
            SetStatus("已刷新收藏并发送直播地址至 PotPlayer。实际解码仍取决于播放器和 CDN。");
        });
    }
    private void RemoveFav_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is Favorite f) { _favorites.Remove(f); SaveData(); SetStatus("收藏已移除。"); }
    }
    private void MuteFav_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is Favorite f) { f.Muted = !f.Muted; FavoriteGrid.Items.Refresh(); SaveData(); SetStatus(f.Muted ? "该主播通知已关闭。" : "该主播通知已开启。"); }
    }
    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        string input = SearchInput.Text.Trim();
        await RunOperationAsync("搜索 B站直播间…", async ct => {
            var results = await _api.SearchRoomsAsync(input, ct);
            _search.Clear(); foreach (var item in results) _search.Add(item);
            SetStatus($"搜索完成，返回 {results.Count} 条直播间。双击一条即可获取直播源。");
        });
    }
    private void BrowserSearch_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://search.bilibili.com/live?keyword=" + Uri.EscapeDataString(SearchInput.Text.Trim())) { UseShellExecute = true });
    }
    private async void SearchGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchGrid.SelectedItem is RoomInfo room) await RunOperationAsync("打开搜索结果…", ct => LoadRoomAsync(room.RoomId, ct));
    }
    private async void BatchFetch_Click(object sender, RoutedEventArgs e)
    {
        var ids = GetBatchRooms(); if (ids.Count == 0) { SetStatus("请填写房间号，或在收藏中添加直播间。"); return; }
        await RunOperationAsync("批量解析直播间…", async ct => {
            _batch.Clear(); _batchRaw.Clear(); int index = 0;
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                index++; string title = "";
                try
                {
                    var info = await _api.GetRoomAsync(id, ct); title = info.Title;
                    if (info.LiveStatus != 1) { _batch.Add(new HistoryEntry { RoomId = info.RoomId, Title = title, Action = "未开播" }); continue; }
                    var list = await _api.GetStreamsAsync(info.RoomId, _settings.Quality, ct);
                    var chosen = ChoosePreferred(list);
                    if (chosen != null) { _batchRaw[info.RoomId] = chosen; _batch.Add(new HistoryEntry { RoomId = info.RoomId, Title = title, Format = chosen.FormatLabel, Action = "已解析" }); }
                    else _batch.Add(new HistoryEntry { RoomId = info.RoomId, Title = title, Action = "无可用格式" });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _batch.Add(new HistoryEntry { RoomId = id, Title = title, Action = "失败: " + ex.GetType().Name }); AppLog.Write("批量解析失败：房间 " + id, ex); }
                SetStatus($"批量解析进度 {index}/{ids.Count}，成功 {_batchRaw.Count} 间。");
                await Task.Delay(130, ct);
            }
        });
    }
    private List<string> GetBatchRooms()
    {
        var lines = string.IsNullOrWhiteSpace(BatchInput.Text) ? _favorites.Select(f => f.RoomId) : BatchInput.Text.Split(new[] { '\n','\r',';',',' }, StringSplitOptions.RemoveEmptyEntries);
        var ids = new List<string>();
        foreach (string line in lines)
        {
            try { ids.Add(BiliApi.ParseRoom(line)); }
            catch { /* Other non-room text is ignored; UI reports count of accepted IDs. */ }
        }
        return ids.Distinct().Take(50).ToList();
    }
    private static StreamEntry? ChoosePreferred(List<StreamEntry> source) => source.OrderBy(x => x.Format == "ts" ? 0 : x.Format == "flv" ? 1 : 2).ThenBy(x => x.Codec == "avc" ? 0 : 1).FirstOrDefault();
    private void FillBatch_Click(object sender, RoutedEventArgs e) => BatchInput.Text = string.Join(Environment.NewLine, _favorites.Select(x => x.RoomId));
    private void BatchExport_Click(object sender, RoutedEventArgs e)
    {
        SavePlaylist(_batchRaw.Select(x => (_batch.FirstOrDefault(h => h.RoomId == x.Key)?.Title ?? x.Key, x.Value.Url)), "BiliSnagTide_批量原始源.m3u", "已导出当前时效的原始播放列表（地址不会自动更新）");
    }
    private void BatchProxyExport_Click(object sender, RoutedEventArgs e)
    {
        try { EnsureProxy(); } catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "代理启动失败"); return; }
        var ids = _batch.Where(x => x.Action == "已解析").Select(x => (x.RoomId, x.Title)).ToList();
        if (ids.Count == 0) ids = GetBatchRooms().Select(x => (RoomId: x, Title: "B站 " + x)).ToList();
        SavePlaylist(ids.Select(x => (x.Title, _proxy.Link(x.RoomId))), "BiliSnagTide_本机固定代理.m3u", "已导出本机固定地址；软件代理需要保持运行，不能跨设备直接用");
    }
    private void BatchClear_Click(object sender, RoutedEventArgs e) { _batch.Clear(); _batchRaw.Clear(); SetStatus("批量结果已清空。"); }
    private void HistoryOpen_Click(object sender, RoutedEventArgs e) => _ = OpenHistoryAsync();
    private async void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenHistoryAsync();
    private async Task OpenHistoryAsync()
    {
        if (HistoryGrid.SelectedItem is HistoryEntry h) await RunOperationAsync("打开历史直播间…", ct => LoadRoomAsync(h.RoomId, ct));
    }
    private void HistoryClear_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "确定清空本机操作历史吗？", "确认操作", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes) { _history.Clear(); SaveData(); }
    }
    private void OpenRoom_Click(object sender, RoutedEventArgs e)
    {
        string id = _current?.RoomId ?? "";
        if (string.IsNullOrEmpty(id)) try { id = BiliApi.ParseRoom(RoomInput.Text); } catch { }
        if (string.IsNullOrEmpty(id)) { SetStatus("请先输入房间号。"); return; }
        Process.Start(new ProcessStartInfo("https://live.bilibili.com/" + id) { UseShellExecute = true });
    }
    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Pages == null || _logFollowTimer == null) return;
        if (Pages.SelectedIndex == 8)
        {
            RefreshLog(force: true);
            _logFollowTimer.Start();
        }
        else _logFollowTimer.Stop();
        AnimateSelectedWorkspace();
    }
    private void PopulateSettings()
    {
        PlayerPathInput.Text = _settings.PlayerPath;
        FfprobePathInput.Text = _settings.FfprobePath;
        PollInput.Text = _settings.PollSeconds.ToString(); ProxyPortInput.Text = _settings.ProxyPort.ToString(); ProbeLimitInput.Text = _settings.ProbeLimitKb.ToString();
        ClipboardToggle.IsChecked = _settings.ClipboardDetect; NotificationsToggle.IsChecked = _settings.NotifyLive;
        TrayToggle.IsChecked = _settings.MinimizeToTray; StartupToggle.IsChecked = _settings.StartWithWindows;
        AutoProbeToggle.IsChecked = _settings.AutoProbe; DarkToggle.IsChecked = _settings.DarkTheme; HlsPreferToggle.IsChecked = _settings.ProxyUseHls;
        ReduceMotionToggle.IsChecked = _settings.ReduceMotion;
        UpdateAutoToggle.IsChecked = _settings.CheckUpdatesAutomatically;
        UpdateCurrentVersionText.Text = "当前版本：v" + UpdateService.CurrentVersionText;
        SelectTag(QualityChoice, "all"); SelectTag(FormatChoice, _settings.Format); SelectTag(CodecChoice, _settings.Codec);
    }
    private static void SelectTag(System.Windows.Controls.ComboBox cb, string tag)
    {
        foreach (var item in cb.Items.OfType<ComboBoxItem>()) if (item.Tag?.ToString() == tag) { cb.SelectedItem = item; return; }
    }
    private void CaptureSettings()
    {
        if (!int.TryParse(ProxyPortInput.Text, out int port) || port < 1024 || port > 65535) throw new ArgumentException("代理端口须为 1024–65535。");
        if (!int.TryParse(PollInput.Text, out int poll)) throw new ArgumentException("收藏检查间隔必须是秒数。");
        if (!int.TryParse(ProbeLimitInput.Text, out int kb)) throw new ArgumentException("采样上限必须是整数 KB。");
        if (_proxy.IsRunning && port != _settings.ProxyPort) throw new InvalidOperationException("更改代理端口前请先停止代理，否则当前播放地址会改变。");
        _settings.ProxyPort = port; _settings.PollSeconds = Math.Clamp(poll, 60, 3600);
        _settings.ProbeLimitKb = Math.Clamp(kb, 8, 256);
        _settings.PlayerPath = PlayerPathInput.Text.Trim();
        _settings.FfprobePath = FfprobePathInput.Text.Trim();
        _settings.ClipboardDetect = ClipboardToggle.IsChecked == true;
        _settings.NotifyLive = NotificationsToggle.IsChecked == true;
        _settings.MinimizeToTray = TrayToggle.IsChecked == true;
        _settings.AutoProbe = AutoProbeToggle.IsChecked == true;
        _settings.ProxyUseHls = HlsPreferToggle.IsChecked == true;
        _settings.DarkTheme = DarkToggle.IsChecked == true;
        _settings.ReduceMotion = ReduceMotionToggle.IsChecked == true;
        _settings.StartWithWindows = StartupToggle.IsChecked == true;
        _settings.CheckUpdatesAutomatically = UpdateAutoToggle.IsChecked == true;
        _monitorTimer.Interval = TimeSpan.FromSeconds(_settings.PollSeconds);
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try { CaptureSettings(); SetStartup(_settings.StartWithWindows); SaveData(); ApplyTheme(_settings.DarkTheme); SetStatus("设置已保存。监控间隔至少 60 秒，避免高频查询。"); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "设置无效", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }
    private void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) throw new InvalidOperationException("无法访问 Windows 自启动注册表位置。");
        if (enabled)
        {
            key.SetValue("BiliSnagTide", $"\"{Environment.ProcessPath}\" --minimized");
            key.DeleteValue("BiliLiveManager", false);
        }
        else
        {
            key.DeleteValue("BiliSnagTide", false);
            // An old optional startup entry may exist after settings migration.
            key.DeleteValue("BiliLiveManager", false);
        }
    }
    private void ApplyTheme(bool dark)
    {
        ThemeCatalog.Apply(ThemeCatalog.Resolve(_settings, dark));
    }
    private void OpenThemeEditor_Click(object sender, RoutedEventArgs e)
    {
        // Editor owns isolated working copies. Cancel restores the exact previous palette.
        bool previousMode = DarkToggle.IsChecked == true;
        var editor = new ThemeEditorWindow(_settings, previousMode)
        {
            Owner = this
        };
        if (editor.ShowDialog() == true)
        {
            DarkToggle.IsChecked = editor.SavedDarkMode;
            ApplyTheme(editor.SavedDarkMode);
            SaveData();
            SetStatus("自定义配色已保存；浅色与深色方案分别保留。");
        }
        else
        {
            DarkToggle.IsChecked = previousMode;
            ApplyTheme(previousMode);
        }
    }
    private void Theme_Click(object sender, RoutedEventArgs e) { ApplyTheme(DarkToggle.IsChecked == true); }
    private void EnsureProxy()
    {
        if (_proxy.IsRunning) return;
        CaptureSettings(); _proxy.Start();
        ProxyStatusText.Text = "代理运行中 " + _settings.ProxyPort;
    }
    private void ProxyStart_Click(object sender, RoutedEventArgs e)
    {
        try { EnsureProxy(); SetStatus("本地代理已启动。代理 HLS 新列表会自动重取 URL；FLV 中途断线仍需播放器重新连接。"); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "无法启动本地代理"); }
    }
    private void ProxyStop_Click(object sender, RoutedEventArgs e) { _proxy.Stop(); ProxyStatusText.Text = "代理未启动"; }
    private async void ProxyCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) { SetStatus("请先选择直播间。"); return; }
        try { EnsureProxy(); } catch (Exception ex) { SetStatus("代理启动失败：" + ex.Message); return; }
        bool ok = await WriteClipboardAsync(_proxy.Link(_current.RoomId));
        SetStatus(ok ? "本机固定代理地址已复制（请勿公开包含访问令牌的 URL）。" : "剪贴板暂时被占用，复制失败。");
    }
    private void ProxyPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) { SetStatus("请先获取直播间。"); return; }
        try
        {
            EnsureProxy(); StartPlayer(_proxy.Link(_current.RoomId));
            AddHistory(_current.RoomId, _current.Title, "代理播放", "127.0.0.1");
            SetStatus("已发送固定本机代理地址至 PotPlayer。断流可重新打开同一个地址；并非无缝连接保证。");
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "代理播放失败"); }
    }
    private static Task<string?> ReadClipboardAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() => {
            try { tcs.TrySetResult(System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null); }
            catch { tcs.TrySetResult(null); }
        }) { IsBackground = true, Name = "BiliSnagTide Clipboard Reader STA" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        return tcs.Task;
    }
    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        var text = await ReadClipboardAsync();
        if (!string.IsNullOrWhiteSpace(text)) RoomInput.Text = text.Trim();
        else SetStatus("剪贴板没有文本，或者暂时被其他进程占用。");
    }
    private async Task CheckClipboardAsync()
    {
        if (!_settings.ClipboardDetect || _clipboardChecking) return;
        _clipboardChecking = true;
        try
        {
            string? raw = await ReadClipboardAsync();
            if (string.IsNullOrWhiteSpace(raw) || raw == _clipboardSeen || RoomInput.IsKeyboardFocused) return;
            _clipboardSeen = raw;
            if (!Regex.IsMatch(raw.Trim(), @"^(https?://)?live\.bilibili\.com/(?:blanc/)?\d+", RegexOptions.IgnoreCase)) return;
            try { _ = BiliApi.ParseRoom(raw); RoomInput.Text = raw.Trim(); SetStatus("发现剪贴板中的B站直播间链接，可点击“获取 / 刷新”；不会自动播放。"); }
            catch { }
        }
        finally { _clipboardChecking = false; }
    }
    // Reload only when the underlying file changes. Avoid rebuilding a large TextBox every two seconds.
    private void RefreshLog(bool force = false)
    {
        if (LogText == null) return;
        try
        {
            string path = AppLog.LogFile;
            if (!File.Exists(path))
            {
                if (LogText.Text != "暂无日志。") LogText.Text = "暂无日志。";
                return;
            }
            var fileInfo = new FileInfo(path);
            fileInfo.Refresh();
            if (!force && fileInfo.Length == _logLastLength && fileInfo.LastWriteTimeUtc == _logLastWriteUtc)
                return;
            _logLastLength = fileInfo.Length;
            _logLastWriteUtc = fileInfo.LastWriteTimeUtc;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 125_000) fs.Seek(-125_000, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string contents = reader.ReadToEnd();
            if (LogText.Text == contents) return;
            double previousOffset = LogText.VerticalOffset;
            _logLayoutUpdating = true;
            LogText.Text = contents;
            if (_logFollowLatest)
                ScrollLogToLatest();
            else
            {
                // Do not snap to the top while the user reads an earlier log section.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { LogText.ScrollToVerticalOffset(previousOffset); }
                    finally { _logLayoutUpdating = false; UpdateLogFollowStatus(); }
                }), DispatcherPriority.ContextIdle);
            }
        }
        catch (Exception ex)
        {
            _logLayoutUpdating = false;
            LogText.Text = "无法读取日志：" + ex.Message;
        }
    }

    private void ScrollLogToLatest()
    {
        if (LogText == null) return;
        _logLayoutUpdating = true;
        // Deferred until layout; synchronous ScrollToEnd can run before the TextBox has its scroll extent.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { LogText.ScrollToEnd(); }
            finally { _logLayoutUpdating = false; UpdateLogFollowStatus(); }
        }), DispatcherPriority.ContextIdle);
    }

    private void LogText_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_logLayoutUpdating || LogAutoFollowToggle == null) return;
        // Reflow and append events are not user navigation.
        if (Math.Abs(e.VerticalChange) < 0.1 || Math.Abs(e.ExtentHeightChange) > 0.1 || Math.Abs(e.ViewportHeightChange) > 0.1)
            return;
        bool atBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2;
        if (atBottom == _logFollowLatest) return;
        _logFollowLatest = atBottom;
        LogAutoFollowToggle.IsChecked = atBottom;
        UpdateLogFollowStatus();
    }

    private void LogAutoFollow_Changed(object sender, RoutedEventArgs e)
    {
        if (LogText == null || LogAutoFollowToggle == null) return;
        _logFollowLatest = LogAutoFollowToggle.IsChecked == true;
        if (_logFollowLatest) ScrollLogToLatest();
        else UpdateLogFollowStatus();
    }

    private void LogToBottom_Click(object sender, RoutedEventArgs e)
    {
        _logFollowLatest = true;
        LogAutoFollowToggle.IsChecked = true;
        ScrollLogToLatest();
    }

    private void UpdateLogFollowStatus()
    {
        if (LogFollowStatus != null)
            LogFollowStatus.Text = _logFollowLatest ? "正在跟随最新日志 · 每 2 秒检查更新" : "已暂停自动跟随 · 正在阅读历史日志";
    }

    private void LogRefresh_Click(object sender, RoutedEventArgs e) => RefreshLog(force: true);
    private void LogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Storage.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", Storage.DataDir) { UseShellExecute = true });
    }
    private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        string report = $"BiliSnagTide v4.0\r\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\nWindows: {Environment.OSVersion}\r\nRoom: {_current?.RoomId ?? "n/a"}\r\nLive status: {_current?.StatusLabel ?? "n/a"}\r\nExtracted: {_all.Count}\r\nHTTP reached: {_all.Count(x => x.HttpReachable)}\r\nProxy running: {_proxy.IsRunning}\r\nProxy mode: {(_settings.ProxyUseHls ? "HLS" : "FLV")}\r\nLast status: {StatusText.Text}\r\nLog: {AppLog.LogFile}\r\n";
        SetStatus(await WriteClipboardAsync(report) ? "诊断摘要已复制，不包含签名直播源 URL。" : "诊断摘要复制失败，可直接在日志页面查看。");
    }
}
