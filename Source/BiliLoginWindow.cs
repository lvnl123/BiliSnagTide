using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using QRCoder;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfImage = System.Windows.Controls.Image;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace BiliLiveManager;

/// <summary>
/// Bilibili web QR sign-in. No embedded browser and no password collection.
/// QR keys and refresh tokens are never written to settings or logs.
/// After successful QR sign-in, the main window may save a minimal
/// DPAPI-encrypted account session for the current Windows user.
/// </summary>
public sealed class BiliLoginWindow : Window
{
    private readonly CookieContainer _jar = new();
    private readonly HttpClient _http;
    private CancellationTokenSource? _qrCancellation;
    private readonly WpfImage _qrImage;
    private readonly TextBlock _status;
    private readonly WpfButton _refresh;
    public List<Cookie> Cookies { get; private set; } = new();
    public bool ClearLoginRequested { get; private set; }

    public BiliLoginWindow(string roomId)
    {
        Title = "B站扫码登录 · BiliSnagTide";
        Width = 530; Height = 655; MinWidth = 460; MinHeight = 565;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Match the active main-window theme; the QR itself remains white for scanning.
        System.Windows.Media.Brush Role(string key) =>
            System.Windows.Application.Current.Resources[key] as System.Windows.Media.Brush ?? WpfBrushes.Gray;
        Background = Role("DialogBg");
        var handler = new HttpClientHandler
        {
            UseCookies = true, CookieContainer = _jar,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(16) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var root = new StackPanel { Margin = new Thickness(35, 29, 35, 27), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        Content = root;
        root.Children.Add(new TextBlock { Text = "使用 B站 App 扫码登录", FontSize = 23, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Foreground = Role("MainText") });
        root.Children.Add(new TextBlock { Text = "请使用手机 B站 App 扫描下方二维码并在手机上确认", FontSize = 13,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 16), Foreground = Role("SubText"), TextWrapping = TextWrapping.Wrap });
        var qrFrame = new Border { Padding = new Thickness(13), BorderBrush = Role("LineBrush"),
            BorderThickness = new Thickness(1), Background = WpfBrushes.White, CornerRadius = new CornerRadius(12), HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        _qrImage = new WpfImage { Width = 260, Height = 260, Stretch = Stretch.Uniform };
        qrFrame.Child = _qrImage;
        root.Children.Add(qrFrame);
        _status = new TextBlock { Text = "正在向B站申请二维码…", TextAlignment = TextAlignment.Center, FontSize = 13,
            Foreground = Role("BlueBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 12) };
        root.Children.Add(_status);
        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        _refresh = new WpfButton { Content = "刷新二维码", Padding = new Thickness(20, 10, 20, 10), Margin = new Thickness(0, 0, 10, 0) };
        _refresh.Click += async (_, _) => await BeginNewQrAsync();
        var logout = new WpfButton { Content = "退出本软件登录", Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 0) };
        logout.Click += (_, _) => { ClearLoginRequested = true; _qrCancellation?.Cancel(); Close(); };
        var close = new WpfButton { Content = "取消", Padding = new Thickness(25, 10, 25, 10) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_refresh); buttons.Children.Add(logout); buttons.Children.Add(close);
        root.Children.Add(buttons);
        root.Children.Add(new TextBlock {
            Text = "仅连接 B站官网接口；不会获取账号密码。登录凭据可使用 Windows 当前用户加密保存在本机；不会写入普通设置、诊断日志或 M3U。",
            Foreground = Role("SubText"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 22, 0, 0)
        });
        Loaded += (_, _) =>
        {
            if (Owner != null && !MotionSettings.GetReducedMotion(Owner))
                BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(180))
                { FillBehavior = FillBehavior.Stop });
        };
        Loaded += async (_, _) => await BeginNewQrAsync();
        Closed += (_, _) => { _qrCancellation?.Cancel(); _qrCancellation?.Dispose(); _http.Dispose(); };
    }

    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task BeginNewQrAsync()
    {
        _qrCancellation?.Cancel();
        _qrCancellation?.Dispose();
        _qrCancellation = new CancellationTokenSource();
        var token = _qrCancellation.Token;
        _refresh.IsEnabled = false;
        _qrImage.Source = null;
        _status.Text = "正在向 B站申请新的登录二维码…";
        try
        {
            using var doc = await ReadJsonAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", token);
            var root = doc.RootElement;
            if (BiliApi.GetInt(root, "code") != 0)
                throw new InvalidOperationException("申请二维码被B站拒绝：" + BiliApi.GetString(root, "message"));
            var data = BiliApi.Get(root, "data");
            string qrUrl = BiliApi.GetString(data, "url");
            string key = BiliApi.GetString(data, "qrcode_key");
            if (key.Length != 32 || !Uri.TryCreate(qrUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || !(uri.Host == "passport.bilibili.com" || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("B站返回的登录二维码格式不符合预期。");
            using var generator = new QRCodeGenerator();
            using var dataQr = generator.CreateQrCode(qrUrl, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(dataQr).GetGraphic(9);
            using var bytes = new MemoryStream(png);
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = bytes; bmp.EndInit(); bmp.Freeze();
            if (token.IsCancellationRequested) return;
            _qrImage.Source = bmp;
            _status.Text = "二维码有效期约 3 分钟，请使用 B站 App 扫码";
            _ = PollAsync(key, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) _status.Text = "无法获取二维码：" + ex.Message; }
        finally { if (!token.IsCancellationRequested) _refresh.IsEnabled = true; }
    }

    private async Task PollAsync(string key, CancellationToken ct)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(3);
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < expires)
        {
            try
            {
                await Task.Delay(2000, ct);
                string url = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(key);
                using var doc = await ReadJsonAsync(url, ct);
                var root = doc.RootElement;
                if (BiliApi.GetInt(root, "code") != 0)
                    throw new InvalidOperationException("扫码状态请求失败：" + BiliApi.GetString(root, "message"));
                var data = BiliApi.Get(root, "data");
                switch (BiliApi.GetInt(data, "code"))
                {
                    case 86101: _status.Text = "等待手机扫码…"; break;
                    case 86090: _status.Text = "扫码成功，请在手机B站 App上确认登录"; break;
                    case 86038: _status.Text = "二维码已过期，请点击刷新二维码"; return;
                    case 0:
                        var collected = CollectLoginCookies(BiliApi.GetString(data, "url"));
                        if (!collected.Any(x => x.Name == "SESSDATA"))
                        {
                            _status.Text = "B站已确认扫码，但未返回有效的登录会话；请刷新二维码重试";
                            return;
                        }
                        Cookies = collected;
                        _status.Text = "扫码登录成功";
                        DialogResult = true;
                        return;
                    default:
                        _status.Text = "扫码状态：" + BiliApi.GetString(data, "message");
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _status.Text = "查询扫码状态失败：" + ex.Message + "。请稍后刷新二维码"; return; }
        }
        if (!ct.IsCancellationRequested) _status.Text = "二维码已超时，请点击刷新二维码";
    }

    private List<Cookie> CollectLoginCookies(string ssoUrl)
    {
        // The poll endpoint may return both Set-Cookie and an SSO URL with login cookies.
        // Never follow a third-party URL, and never log the returned URL.
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "buvid3", "buvid4" };
        var map = new Dictionary<string, Cookie>(StringComparer.Ordinal);
        foreach (string host in new[] { "https://passport.bilibili.com/", "https://www.bilibili.com/", "https://live.bilibili.com/" })
            foreach (Cookie c in _jar.GetCookies(new Uri(host)))
                if (allowed.Contains(c.Name))
                    map[c.Name] = new Cookie(c.Name, c.Value, "/", ".bilibili.com") { Secure = true };
        if (Uri.TryCreate(ssoUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            (uri.Host == "passport.bilibili.com" || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "passport.biligame.com"))
        {
            foreach (var entry in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int at = entry.IndexOf('=');
                if (at < 0) continue;
                var name = Uri.UnescapeDataString(entry[..at]);
                if (!allowed.Contains(name)) continue;
                var value = Uri.UnescapeDataString(entry[(at + 1)..]);
                if (value.Length == 0 || value.Length > 4096) continue;
                try { map[name] = new Cookie(name, value, "/", ".bilibili.com") { Secure = true }; }
                catch (CookieException) { }
            }
        }
        return map.Values.ToList();
    }
}
