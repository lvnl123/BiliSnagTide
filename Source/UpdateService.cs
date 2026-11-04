using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BiliLiveManager;

/// <summary>
/// Uses only the official GitHub Releases API. No personal access token is required.
/// A downloaded executable is never offered for execution until its SHA-256 passes.
/// </summary>
public static class UpdateService
{
    public const string CurrentVersionText = "4.6.0";
    public static readonly Version CurrentVersion = new(4, 6, 0);
    public const string OfficialReleasesUrl = "https://github.com/lvnl123/BiliSnagTide/releases";
    private const string ApiUrl = "https://api.github.com/repos/lvnl123/BiliSnagTide/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BiliSnagTide/4.6.0 (Windows; update-check)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public sealed record ReleaseInfo(
        Version Version, string Tag, string Notes, string ReleaseUrl,
        string AssetName, string AssetUrl, long AssetSize,
        string? AssetDigest, string? ManifestUrl)
    {
        public bool IsNewer => Version > CurrentVersion;
    }

    public readonly record struct DownloadProgress(long Downloaded, long Total)
    {
        public double Percent => Total > 0 ? Math.Clamp(100d * Downloaded / Total, 0, 100) : 0;
    }

    public static async Task<ReleaseInfo?> CheckLatestAsync(bool portable, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Client.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
        var root = document.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = Text(root, "tag_name");
        if (!TryVersion(tag, out var version)) return null;
        var page = Text(root, "html_url");
        if (!IsReleasePage(page, tag)) throw new InvalidDataException("Release 页面不是指定的 GitHub 仓库。");

        string suffix = portable ? "-Portable.exe" : "-Setup.exe";
        string expectedAsset = $"BiliSnagTide-{tag}-Windows-x64{suffix}";
        string name = "", url = "", digest = "";
        string? manifest = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string assetName = Text(asset, "name");
            string assetUrl = Text(asset, "browser_download_url");
            if (assetName == "SHA256SUMS.txt" && IsOfficialAsset(assetUrl, tag, assetName))
                manifest = assetUrl;
            if (assetName != expectedAsset || !IsOfficialAsset(assetUrl, tag, assetName)) continue;
            name = assetName;
            url = assetUrl;
            digest = Text(asset, "digest");
            if (asset.TryGetProperty("size", out var n) && n.TryGetInt64(out var length)) size = length;
        }

        if (string.IsNullOrEmpty(name))
            return new ReleaseInfo(version!, tag, Text(root, "body"), page, "", "", 0, null, manifest);
        return new ReleaseInfo(version!, tag, Text(root, "body"), page, name, url, size, digest, manifest);
    }

    private static bool Bool(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static bool TryVersion(string tag, out Version? version)
    {
        string normalized = tag.TrimStart('v', 'V');
        if (!Regex.IsMatch(normalized, @"^\d{1,4}\.\d{1,4}\.\d{1,4}(?:\.\d{1,4})?$"))
        {
            version = null;
            return false;
        }
        return Version.TryParse(normalized, out version);
    }

    private static bool IsReleasePage(string link, string tag) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("/lvnl123/BiliSnagTide/releases/tag/" + tag, StringComparison.Ordinal);

    private static bool IsOfficialAsset(string link, string tag, string name) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("/lvnl123/BiliSnagTide/releases/download/" + tag + "/" + name, StringComparison.Ordinal);

    private static async Task<string> ExpectedDigestAsync(ReleaseInfo release, CancellationToken ct)
    {
        string digest = release.AssetDigest ?? "";
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string direct = digest[7..];
            if (Regex.IsMatch(direct, @"\A[0-9a-fA-F]{64}\z")) return direct.ToLowerInvariant();
        }

        if (release.ManifestUrl is null || !IsOfficialAsset(release.ManifestUrl, release.Tag, "SHA256SUMS.txt"))
            throw new InvalidDataException("此版本未附带可验证的 SHA-256 校验信息，请前往 GitHub 手动下载。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Client.GetAsync(release.ManifestUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 16384)
            throw new InvalidDataException("校验文件异常。");
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (text.Length > 16384) throw new InvalidDataException("校验文件过大。");
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), @"^([a-fA-F0-9]{64})  ([A-Za-z0-9._-]+)$");
            if (match.Success && match.Groups[2].Value == release.AssetName)
                return match.Groups[1].Value.ToLowerInvariant();
        }
        throw new InvalidDataException("未找到当前程序对应的 SHA-256 校验值。");
    }

    public static async Task<string> DownloadVerifiedAsync(
        ReleaseInfo release, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!release.IsNewer || release.AssetName.Length == 0
            || !IsOfficialAsset(release.AssetUrl, release.Tag, release.AssetName)
            || release.AssetSize < 1_000_000 || release.AssetSize > 1_000_000_000)
            throw new InvalidDataException("无法确定安全的更新附件。");

        string expectedHash = await ExpectedDigestAsync(release, cancellationToken);
        // Download into a separate staging area; the active executable is never written here.
        string directory = Path.Combine(Storage.DataDir, "Updates");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, release.AssetName);
        string part = output + ".partial";
        try
        {
            using var response = await Client.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.AssetSize;
            if (total < 1_000_000 || total > 1_000_000_000)
                throw new InvalidDataException("更新文件大小异常。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long copied = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    copied += count;
                    if (copied > 1_000_000_000) throw new InvalidDataException("更新文件超过安全上限。");
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report(new DownloadProgress(copied, total));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await using var verifyStream = File.OpenRead(part);
            byte[] hashBytes = await SHA256.HashDataAsync(verifyStream, cancellationToken);
            string actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                throw new InvalidDataException("SHA-256 校验失败，已拒绝运行下载文件。");
            File.Move(part, output, overwrite: true);
            return output;
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
    }

    // The newly downloaded portable EXE runs as a helper after the old process exits.
    // No shell script, SDK installer, or BiliLiveBuildCache directory is used.
    public static void ApplyPortableReplacement(string targetPath, string waitingPid)
    {
        if (!int.TryParse(waitingPid, out int pid) || pid <= 0)
            throw new ArgumentException("无效的更新进程编号。");
        targetPath = Path.GetFullPath(targetPath);
        var name = Path.GetFileName(targetPath);
        if (!name.StartsWith("BiliSnagTide", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(targetPath))
            throw new InvalidOperationException("更新目标不是已有的 BiliSnagTide 可执行文件。");

        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) break;
            }
            catch (ArgumentException) { break; }
            Thread.Sleep(300);
        }
        try
        {
            using var stillRunning = Process.GetProcessById(pid);
            if (!stillRunning.HasExited)
                throw new IOException("旧程序仍在运行，请先完全退出后重试。");
        }
        catch (ArgumentException) { /* Process has exited. */ }

        string? helper = Environment.ProcessPath;
        if (string.IsNullOrEmpty(helper) || !File.Exists(helper))
            throw new FileNotFoundException("更新程序不存在。");

        string stage = Path.Combine(Path.GetDirectoryName(targetPath)!, ".BiliSnagTide-update-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.Copy(helper, stage, overwrite: false);
            File.Move(stage, targetPath, overwrite: true);
            Process.Start(new ProcessStartInfo(targetPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(targetPath)!
            });
        }
        finally { try { if (File.Exists(stage)) File.Delete(stage); } catch { } }
    }
}
