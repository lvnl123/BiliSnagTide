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
/// A downloaded package is not offered for use until its SHA-256 passes.
/// </summary>
public static class UpdateService
{
    public const string CurrentVersionText = "4.6.2";
    public static readonly Version CurrentVersion = new(4, 6, 2);
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BiliSnagTide/4.6.1 (Windows; update-check)");
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

        string suffix = portable ? "-Portable.zip" : "-Setup.exe";
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
        // Download into the local Data/Updates cache; never write over the active application.
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

    /// <summary>
    /// A verified portable ZIP is unpacked beside the existing installation, NOT on
    /// top of running EXE/DLL files. Copy settings to new Data directory and retain
    /// the old folder for rollback. The caller decides when to launch the new app.
    /// </summary>
    public static async Task<string> PreparePortableZipUpgradeAsync(
        ReleaseInfo release, string verifiedZip, string currentExe, CancellationToken ct)
    {
        if (!Storage.IsPortable)
            throw new InvalidOperationException("仅绿色便携版支持 ZIP 解压升级。");
        if (!release.IsNewer || !release.AssetName.EndsWith("-Portable.zip", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(verifiedZip), release.AssetName, StringComparison.Ordinal)
            || !File.Exists(verifiedZip))
            throw new InvalidDataException("绿色版更新包无效。");

        // Verify once again at the moment of applying, not only at download time.
        var expected = await ExpectedDigestAsync(release, ct);
        await using (var input = File.OpenRead(verifiedZip))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidDataException("更新文件 SHA-256 校验失败，无法解压。");
        }
        ct.ThrowIfCancellationRequested();

        string currentDir = Path.GetDirectoryName(Path.GetFullPath(currentExe))
            ?? throw new InvalidOperationException("找不到当前程序文件夹。");
        string parent = Directory.GetParent(currentDir)?.FullName
            ?? throw new InvalidOperationException("无法确定更新目标文件夹。");
        string targetDir = Path.Combine(parent, "BiliSnagTide-" + release.Tag);
        if (Directory.Exists(targetDir))
            throw new IOException("目标版本文件夹已存在，请先备份或重命名该文件夹：" + targetDir);

        bool created = false;
        try
        {
            // ZIP-slip and zip-bomb checks BEFORE extraction. The package is
            // expected to contain normal runtime files at the archive root.
            using (var archive = System.IO.Compression.ZipFile.OpenRead(verifiedZip))
            {
                if (archive.Entries.Count > 4000)
                    throw new InvalidDataException("ZIP 内文件数量异常。");
                long totalUncompressed = 0;
                string fullRoot = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var full = Path.GetFullPath(Path.Combine(targetDir, relative));
                    if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                        || relative.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || relative.Equals("Data", StringComparison.OrdinalIgnoreCase)
                        || entry.FullName.Contains(':'))
                        throw new InvalidDataException("ZIP 包含无效的文件路径。");
                    if (entry.Length > 500_000_000)
                        throw new InvalidDataException("ZIP 文件体积异常。");
                    totalUncompressed = checked(totalUncompressed + entry.Length);
                    if (totalUncompressed > 1_500_000_000)
                        throw new InvalidDataException("ZIP 解压后体积超过限制。");
                }
            }

            Directory.CreateDirectory(targetDir);
            created = true;
            System.IO.Compression.ZipFile.ExtractToDirectory(verifiedZip, targetDir, overwriteFiles: false);
            string nextExe = Path.Combine(targetDir, "BiliSnagTide.exe");
            if (!File.Exists(nextExe))
                throw new InvalidDataException("ZIP 中缺少主程序 EXE。");
// v4.6.2 portable EXE is compiled with PORTABLE_BUILD; no flag required.
// Previous portable ZIPs with portable.flag remain supported by Storage.

            // Favorites, history, theme and proxy settings all live in settings.json.
            // Do not copy the downloaded ZIP or the Updates cache into the new build.
            string nextData = Path.Combine(targetDir, "Data");
            Directory.CreateDirectory(nextData);
            foreach (var name in new[] { "settings.json", "diagnostics.log", "diagnostics.log.old", "account-session.dpapi" })
            {
                ct.ThrowIfCancellationRequested();
                string source = Path.Combine(Storage.DataDir, name);
                if (File.Exists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0)
                    File.Copy(source, Path.Combine(nextData, name), overwrite: false);
            }
            return nextExe;
        }
        catch
        {
            // A partial extraction must never be presented as a valid new version.
            if (created)
            {
                try { Directory.Delete(targetDir, recursive: true); } catch { }
            }
            throw;
        }
    }
}
