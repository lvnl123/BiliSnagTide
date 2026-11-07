using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace BiliLiveManager;

/// <summary>
/// Stores only the short list of required Bilibili cookies in an encrypted file,
/// scoped to the current Windows user via DPAPI. Never goes in settings.json.
/// A portable folder copied to another Windows account must sign in again.
/// </summary>
internal static class AccountSessionStore
{
    private const int MaxBytes = 128 * 1024;
    private static string SessionPath => Path.Combine(Storage.DataDir, "account-session.dpapi");
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "buvid3", "buvid4" };

    private sealed record SavedCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly);

    public static void Save(IEnumerable<Cookie> cookies)
    {
        var list = cookies.Where(c => Allowed.Contains(c.Name)
               && c.Value.Length is > 0 and <= 4096)
            .Select(c => new SavedCookie(c.Name, c.Value, ".bilibili.com", "/", true, c.HttpOnly))
            .ToArray();
        if (!list.Any(c => c.Name == "SESSDATA"))
            throw new InvalidOperationException("扫码登录未取得有效的 SESSDATA，无法保存会话。");

        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(list);
        try
        {
            byte[] secret = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            try
            {
                Directory.CreateDirectory(Storage.DataDir);
                string temporary = SessionPath + ".tmp";
                File.WriteAllBytes(temporary, secret);
                File.Move(temporary, SessionPath, overwrite: true);
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static List<Cookie> Load()
    {
        if (!File.Exists(SessionPath)) return new();
        try
        {
            var file = new FileInfo(SessionPath);
            if (file.Length <= 0 || file.Length > MaxBytes)
                throw new InvalidDataException("会话文件大小无效。");
            byte[] secret = File.ReadAllBytes(SessionPath);
            byte[] clear;
            try { clear = ProtectedData.Unprotect(secret, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            try
            {
                var saved = JsonSerializer.Deserialize<SavedCookie[]>(clear) ?? Array.Empty<SavedCookie>();
                var cookies = new List<Cookie>();
                foreach (var c in saved)
                {
                    if (!Allowed.Contains(c.Name) || c.Value.Length is < 1 or > 4096) continue;
                    try { cookies.Add(new Cookie(c.Name, c.Value, "/", ".bilibili.com") { Secure = true, HttpOnly = c.HttpOnly }); }
                    catch (CookieException) { }
                }
                return cookies.Any(c => c.Name == "SESSDATA") ? cookies : new();
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // User changed Windows profile, copied portable folder, or file is corrupt.
            // Do not log cookie values or ciphertext.
            AppLog.Write("无法恢复本机加密登录会话，需要重新扫码", new Exception(ex.GetType().Name));
            return new();
        }
    }

    public static void Delete()
    {
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); }
        catch (Exception ex) { AppLog.Write("删除本机登录会话文件失败", new Exception(ex.GetType().Name)); }
    }
}
