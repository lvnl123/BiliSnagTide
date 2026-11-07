using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BiliLiveManager;

public partial class MainWindow
{
    private BiliAccount? _loggedInAccount;
    private bool _accountValidationInProgress;

    private async Task RestoreAccountSessionAsync()
    {
        var saved = AccountSessionStore.Load();
        if (saved.Count == 0) { RefreshAccountUi("未登录 · 扫码后可安全保存本机会话"); return; }
        _api.SetAuthenticatedCookies(saved);
        RefreshAccountUi("已恢复本机加密会话，正在验证有效期…");
        _accountValidationInProgress = true;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var profile = await _api.GetLoggedInAccountAsync(limit.Token);
            if (profile == null)
            {
                _api.ClearAuthenticatedCookies();
                AccountSessionStore.Delete();
                _loggedInAccount = null;
                RefreshAccountUi("登录会话已失效，请重新扫码");
            }
            else
            {
                _loggedInAccount = profile;
                RefreshAccountUi("已验证登录 · 下次打开会自动恢复");
            }
        }
        catch (Exception ex)
        {
            // A temporary network outage must NOT erase potentially valid credentials.
            AppLog.Write("恢复登录后暂时无法验证账号（保留加密会话）", new Exception(ex.GetType().Name));
            RefreshAccountUi("网络异常，登录待验证；可稍后重新扫码或检查网络");
        }
        finally { _accountValidationInProgress = false; }
    }

    private void RefreshAccountUi(string status)
    {
        if (_loggedInAccount is { } profile)
        {
            AccountHeaderName.Text = profile.UserName;
            SettingsAccountName.Text = profile.UserName;
            SettingsAccountUid.Text = "UID: " + profile.UserId;
            AccountHeaderButton.ToolTip = "账号管理 · 点击打开设置";
            HeaderAvatarBorder.Visibility = Visibility.Visible;
            SettingsAvatarBorder.Visibility = Visibility.Visible;
            _ = LoadCoverAsync(profile.FaceUrl, AccountHeaderAvatar, CancellationToken.None);
            _ = LoadCoverAsync(profile.FaceUrl, SettingsAccountAvatar, CancellationToken.None);
        }
        else
        {
            AccountHeaderName.Text = _api.HasAuthenticatedSession ? "登录待验证" : "扫码登录";
            SettingsAccountName.Text = _api.HasAuthenticatedSession ? "登录待验证" : "尚未登录";
            SettingsAccountUid.Text = "";
            AccountHeaderButton.ToolTip = _api.HasAuthenticatedSession ? "点击管理账号" : "点击扫码登录";
            AccountHeaderAvatar.Source = null;
            SettingsAccountAvatar.Source = null;
            HeaderAvatarBorder.Visibility = Visibility.Collapsed;
            SettingsAvatarBorder.Visibility = Visibility.Collapsed;
        }
        SettingsAccountStatus.Text = status;
        AccountLogoutButton.IsEnabled = _api.HasAuthenticatedSession;
        AccountSwitchButton.Content = _api.HasAuthenticatedSession ? "更换登录账号" : "扫码登录";
    }

    private void AccountHeader_Click(object sender, RoutedEventArgs e)
    {
        if (!_api.HasAuthenticatedSession && !_accountValidationInProgress)
        {
            BiliLogin_Click(sender, e);
            return;
        }
        Pages.SelectedIndex = 7; // Settings & local proxy
    }

    private void AccountSwitch_Click(object sender, RoutedEventArgs e) =>
        BiliLogin_Click(sender, e);

    private void AccountLogout_Click(object sender, RoutedEventArgs e)
    {
        if (!_api.HasAuthenticatedSession) return;
        var answer = System.Windows.MessageBox.Show(this,
            "确定退出本软件的 B 站账号吗？\n这将删除本机加密登录会话，不影响浏览器中的登录状态。",
            "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) ClearAccountSession();
    }

    private void ClearAccountSession()
    {
        _api.ClearAuthenticatedCookies();
        AccountSessionStore.Delete();
        _loggedInAccount = null;
        RefreshAccountUi("已退出登录，本机保存的会话已清除");
        SetStatus("已退出本软件 B站账号。浏览器登录状态未更改。");
    }

    private async Task FinishAccountLoginAsync(System.Collections.Generic.IEnumerable<Cookie> cookies)
    {
        var snapshot = System.Linq.Enumerable.ToList(cookies);
        _api.SetAuthenticatedCookies(snapshot);
        if (!_api.HasAuthenticatedSession)
        {
            RefreshAccountUi("扫码后未取得有效的登录会话，请重试");
            SetStatus("扫码登录未成功，未取得有效会话。");
            return;
        }
        try
        {
            AccountSessionStore.Save(snapshot);
        }
        catch (Exception ex)
        {
            // Do not report remembered login if DPAPI/file storage failed.
            AppLog.Write("加密保存账号会话失败", new Exception(ex.GetType().Name));
            RefreshAccountUi("当前已登录，但加密保存失败；下次启动可能需要重新扫码");
        }

        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var profile = await _api.GetLoggedInAccountAsync(limit.Token);
            if (profile == null)
            {
                _api.ClearAuthenticatedCookies();
                AccountSessionStore.Delete();
                _loggedInAccount = null;
                RefreshAccountUi("B站接口未确认登录，请刷新二维码后重试");
                return;
            }
            _loggedInAccount = profile;
            RefreshAccountUi("登录成功 · 已使用 Windows 当前用户加密保存会话");
        }
        catch (Exception ex)
        {
            // Retain the just-signed-in session when the profile API fails.
            AppLog.Write("登录成功，但账号信息暂时无法读取", new Exception(ex.GetType().Name));
            _loggedInAccount = null;
            RefreshAccountUi("扫码已完成，但暂时无法获取昵称或头像；请检查网络");
        }
        SetStatus("B站登录会话已同步。现在可重新扫描当前直播间。");
        if (_current != null)
            await RunOperationAsync("使用登录状态重新扫描画质…", async ct =>
            {
                ApplyQualityScan(await _api.ScanAllQualitiesAsync(_current.RoomId, ct));
                SetStatus("登录后画质扫描完成，请查看当前直播源和诊断信息。");
            });
    }
}
