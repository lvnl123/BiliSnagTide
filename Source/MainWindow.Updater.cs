using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace BiliLiveManager;

/// <summary>UI-only update center; live source and danmaku workflows are unchanged.</summary>
public partial class MainWindow
{
    private readonly SemaphoreSlim _updateCheckGate = new(1, 1);
    private UpdateService.ReleaseInfo? _latestRelease;
    private CancellationTokenSource? _updateDownloadCts;
    private string? _verifiedDownloadPath;

    private async Task CheckUpdatesOnLaunchAsync()
    {
        if (!_settings.CheckUpdatesAutomatically) return;
        if (DateTimeOffset.UtcNow - _settings.LastUpdateCheckUtc < TimeSpan.FromHours(24)) return;
        await CheckForAppUpdatesAsync(manual: false);
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) =>
        await CheckForAppUpdatesAsync(manual: true);

    private async Task CheckForAppUpdatesAsync(bool manual)
    {
        if (!await _updateCheckGate.WaitAsync(0)) return;
        UpdateCheckButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查 GitHub 最新正式版本…";
        try
        {
            var release = await UpdateService.CheckLatestAsync(Storage.IsPortable, CancellationToken.None);
            if (!IsLoaded) return;
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            try { SaveData(); } catch (Exception ex) { AppLog.Write("保存更新检查时间失败", ex); }

            _latestRelease = release;
            _verifiedDownloadPath = null;
            UpdateApplyButton.IsEnabled = false;
            UpdateDownloadButton.IsEnabled = false;
            UpdateSkipButton.IsEnabled = false;
            if (release == null)
            {
                UpdateLatestVersionText.Text = "最新版本：暂未找到可用的正式 Release";
                UpdateNotesText.Text = "";
                UpdateStatusText.Text = "暂时无法识别发布版本，请稍后重试或查看 GitHub。";
                return;
            }
            UpdateLatestVersionText.Text = "GitHub 最新正式版本：" + release.Tag;
            UpdateNotesText.Text = string.IsNullOrWhiteSpace(release.Notes)
                ? "本版本尚未填写更新说明。" : release.Notes[..Math.Min(4000, release.Notes.Length)];
            if (!release.IsNewer)
            {
                UpdateStatusText.Text = "当前已是最新版本（或已高于 GitHub 正式发布版本）。";
                return;
            }

            UpdateDownloadButton.IsEnabled = !string.IsNullOrEmpty(release.AssetName);
            UpdateSkipButton.IsEnabled = true;
            UpdateStatusText.Text = release.AssetName.Length == 0
                ? "发现新版本，但未找到适用于本版本类型的更新文件；可前往 GitHub 查看。"
                : $"发现 {release.Tag}，可在软件内下载并在确认后更新，或前往 GitHub。";
            if (manual || _settings.SkippedUpdateTag == release.Tag) return;
            // Never interrupt minimized/tray or background launches with a modal dialog.
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            var choice = System.Windows.MessageBox.Show(this,
                $"发现 BiliSnagTide {release.Tag}！\n\n是否打开「设置与代理」中的软件更新区域？\n\n你可以选择软件内下载或前往 GitHub 下载。",
                "BiliSnagTide · 有新版本", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes) Pages.SelectedIndex = 7;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查更新失败：" + ex.Message + "。可稍后重试或直接前往 GitHub。";
            AppLog.Write("GitHub 更新检测失败", ex);
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
            _updateCheckGate.Release();
        }
    }

    private async void UpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_latestRelease is not { IsNewer: true } release
            || _updateDownloadCts != null) return;

        _updateDownloadCts = new CancellationTokenSource();
        UpdateDownloadButton.IsEnabled = false;
        UpdateCheckButton.IsEnabled = false;
        UpdateApplyButton.IsEnabled = false;
        UpdateCancelButton.IsEnabled = true;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateProgress.IsIndeterminate = true;
        _verifiedDownloadPath = null;

        var progress = new Progress<UpdateService.DownloadProgress>(p =>
        {
            UpdateProgress.IsIndeterminate = p.Total <= 0;
            if (p.Total > 0) UpdateProgress.Value = p.Percent;
            UpdateStatusText.Text = p.Total > 0
                ? $"正在下载：{p.Percent:0.0}%（{p.Downloaded / 1048576d:0.0} / {p.Total / 1048576d:0.0} MB）"
                : $"正在下载：{p.Downloaded / 1048576d:0.0} MB";
        });

        try
        {
            string path = await UpdateService.DownloadVerifiedAsync(release, progress, _updateDownloadCts.Token);
            _verifiedDownloadPath = path;
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = 100;
            UpdateApplyButton.IsEnabled = true;
            UpdateStatusText.Text = "下载完成，SHA-256 校验通过。点击「安装 / 应用更新」后才会执行。";
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "更新下载已取消，未修改正在运行的软件。";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "更新下载失败：" + ex.Message;
            AppLog.Write("软件下载或校验失败", ex);
        }
        finally
        {
            _updateDownloadCts.Dispose();
            _updateDownloadCts = null;
            UpdateCancelButton.IsEnabled = false;
            UpdateCheckButton.IsEnabled = true;
            UpdateDownloadButton.IsEnabled = _latestRelease is { IsNewer: true, AssetName.Length: > 0 };
            if (_verifiedDownloadPath is null) UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCancel_Click(object sender, RoutedEventArgs e) => _updateDownloadCts?.Cancel();

    private void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_latestRelease is not { IsNewer: true } release) return;
        _settings.SkippedUpdateTag = release.Tag;
        try { SaveData(); } catch (Exception ex) { AppLog.Write("保存忽略版本失败", ex); }
        UpdateStatusText.Text = "已跳过 " + release.Tag + "；仍可以随时手动检查或下载。";
    }

    private void UpdateGitHub_Click(object sender, RoutedEventArgs e)
    {
        var link = _latestRelease?.ReleaseUrl ?? UpdateService.OfficialReleasesUrl;
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
        catch (Exception ex) { UpdateStatusText.Text = "无法打开浏览器：" + ex.Message; }
    }

    private void UpdateApply_Click(object sender, RoutedEventArgs e)
    {
        string? updateFile = _verifiedDownloadPath;
        if (updateFile is null || !File.Exists(updateFile) || _latestRelease is not { IsNewer: true }) return;
        string mode = Storage.IsPortable ? "绿色便携版：程序退出后替换旧 EXE 并重新启动。" :
            "安装版：程序退出后运行已下载的新版安装包。";
        var answer = System.Windows.MessageBox.Show(this,
            $"更新包已通过 SHA-256 校验。\n\n{mode}\n\n即将退出 BiliSnagTide，是否继续？",
            "确认安装更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            if (Storage.IsPortable)
            {
                string target = Environment.ProcessPath ??
                    throw new InvalidOperationException("无法获取当前程序路径。");
                // The newly downloaded portable EXE handles replacement after this process exits.
                var start = new ProcessStartInfo(updateFile) { UseShellExecute = true };
                start.ArgumentList.Add("--apply-update");
                start.ArgumentList.Add(target);
                start.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(start);
            }
            else
            {
                Process.Start(new ProcessStartInfo(updateFile) { UseShellExecute = true });
            }
            _closingForReal = true;
            Close();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "启动更新失败：" + ex.Message +
                "。已下载的文件仍保留，可选择前往 GitHub 更新。";
            AppLog.Write("启动更新程序失败", ex);
        }
    }
}
