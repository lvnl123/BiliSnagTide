using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BiliLiveManager;
public sealed class StreamEntry
{
    public string Format { get; set; } = "";
    public string Codec { get; set; } = "";
    public int Quality { get; set; }
    public string QualityName { get; set; } = "";
    public string Host { get; set; } = "";
    public string Url { get; set; } = "";
    public int TtlSeconds { get; set; }
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? ExpiresAt { get; set; }
    public string ExpiresLabel => ExpiresAt is not { } expiry ? "未知" : expiry <= DateTimeOffset.Now ? "可能过期" : expiry.ToLocalTime().ToString("HH:mm:ss");
    public string TestStatus { get; set; } = "未测试";
    public string VideoSize { get; set; } = "未检测";
    public string VideoBitrate { get; set; } = "未知";
    public int? HttpStatus { get; set; }
    public double? LatencyMs { get; set; }
    public double? SampleKbps { get; set; }
    public bool HttpReachable { get; set; }
    public string FormatLabel => Format == "flv" ? "HTTP-FLV" : Format == "ts" ? "HLS · TS" : "HLS · fMP4";
    public string CodecLabel => Codec == "avc" ? "H.264 / AVC" : Codec == "hevc" ? "H.265 / HEVC" : Codec;
    public string QualityLabel => (string.IsNullOrWhiteSpace(QualityName) ? BiliApi.QualityLabel(Quality) : QualityName) + " (" + Quality + ")";
    public string LatencyLabel => LatencyMs.HasValue ? $"{LatencyMs:0} ms" : "—";
    public string SpeedLabel => SampleKbps.HasValue ? $"{SampleKbps:0} KB/s" : "—";
    public string StableKey => Format + "|" + Codec + "|" + Quality + "|" + Host;
}
public sealed class RoomInfo
{
    public string RoomId { get; set; } = "";
    public string Uid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Uname { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string FaceUrl { get; set; } = "";
    public string LiveTime { get; set; } = "";
    public int LiveStatus { get; set; }
    public string StatusLabel => LiveStatus == 1 ? "● 直播中" : LiveStatus == 2 ? "轮播中" : "未开播";
    public string Display => string.IsNullOrWhiteSpace(Uname) ? Title : Uname + " · " + Title;
}
public sealed class Favorite
{
    public string RoomId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string FaceUrl { get; set; } = "";
    public string LiveTime { get; set; } = "";
    public string Status { get; set; } = "未知";
    public bool Muted { get; set; }
    public int LastStatus { get; set; } = -1;
    public DateTime LastChecked { get; set; }
    public string CheckedText => LastChecked == default ? "尚未检查" : LastChecked.ToString("MM-dd HH:mm:ss");
}
public sealed class HistoryEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string RoomId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
    public string Format { get; set; } = "";
    public string TimeText => Time.ToString("MM-dd HH:mm:ss");
}
public sealed class SettingsModel
{
    public int Quality { get; set; } = 10000;
    public string Format { get; set; } = "all";
    public string Codec { get; set; } = "all";
    public string PlayerPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public bool DarkTheme { get; set; } = false;
    // Independent light/dark presets with per-role overrides (no credentials stored in theme files).
    public string LightThemePreset { get; set; } = ThemeCatalog.DefaultPreset;
    public string DarkThemePreset { get; set; } = ThemeCatalog.DefaultPreset;
    public Dictionary<string, string> LightThemeColors { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> DarkThemeColors { get; set; } = new(StringComparer.Ordinal);
    // One-time v4.5.2 visual reset. Old user presets remain untouched on later launches.
    public bool ClassicDefaultV452Applied { get; set; } = false;
    // One-time upgrade: open the classic LIGHT appearance on the first v4.5.3 launch.
    // Later user changes are honored when saved from Settings.
    public bool DefaultLightV453Applied { get; set; } = false;
    public bool SidebarCollapsed { get; set; } = false;
    public bool ReduceMotion { get; set; } = false;
    public bool ClipboardDetect { get; set; } = true;
    public bool NotifyLive { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public int PollSeconds { get; set; } = 120;
    public int ProxyPort { get; set; } = 18888;
    public bool ProxyUseHls { get; set; } = true;
    public bool AutoProbe { get; set; } = true;
    public int ProbeLimitKb { get; set; } = 64;
    public string ProxyToken { get; set; } = Guid.NewGuid().ToString("N");
    public List<Favorite> Favorites { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
}
public static class Storage
{
    // Dedicated PortableBuild makes the single EXE portable without sidecar files.
    // For compatibility the old v4.5.3 ZIP still accepts portable.flag.
#if PORTABLE_BUILD
    public static readonly bool IsPortable = true;
#else
    public static readonly bool IsPortable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));
#endif
    public static readonly string DataDir = IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliSnagTide");
    private static readonly string LegacyConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliLiveManager", "settings.json");
    private static readonly string ConfigPath = Path.Combine(DataDir, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static SettingsModel Load()
    {
        try
        {
            // One-time, non-destructive settings migration: never delete legacy data.
            // Deliberately do not copy logs, cookies, or cache files.
            if (!IsPortable && !File.Exists(ConfigPath) && File.Exists(LegacyConfigPath))
            {
                Directory.CreateDirectory(DataDir);
                File.Copy(LegacyConfigPath, ConfigPath, overwrite: false);
            }
            if (!File.Exists(ConfigPath)) return new SettingsModel { ClassicDefaultV452Applied = true, DefaultLightV453Applied = true };
            var model = JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(ConfigPath), JsonOptions) ?? new();
            model.ProxyToken = !string.IsNullOrEmpty(model.ProxyToken) && Regex.IsMatch(model.ProxyToken, "^[a-fA-F0-9]{32}$") ? model.ProxyToken : Guid.NewGuid().ToString("N");
            model.Favorites ??= new(); model.History ??= new();
            model.LightThemePreset = ThemeCatalog.SafePreset(model.LightThemePreset);
            model.DarkThemePreset = ThemeCatalog.SafePreset(model.DarkThemePreset);
            model.LightThemeColors ??= new(StringComparer.Ordinal);
            model.DarkThemeColors ??= new(StringComparer.Ordinal);
            // One-time upgrade: select the user's requested Classic Default palette for
            // both modes, including installations that previously saved Nebula Purple.
            // Keep favorites, playback settings, history and unrelated settings.
            // The user can choose a new palette later without it being reset again.
            bool changed = false;
            if (!model.ClassicDefaultV452Applied)
            {
                model.LightThemePreset = ThemeCatalog.DefaultPreset;
                model.DarkThemePreset = ThemeCatalog.DefaultPreset;
                model.LightThemeColors = new(StringComparer.Ordinal);
                model.DarkThemeColors = new(StringComparer.Ordinal);
                model.SidebarCollapsed = false;
                model.ClassicDefaultV452Applied = true;
                changed = true;
            }
            // Previous releases could force DarkTheme=true when migrating from
            // BiliLiveManager. Apply the requested classic LIGHT startup once,
            // including when v4.5.2 has already written settings.json.
            if (!model.DefaultLightV453Applied)
            {
                model.DarkTheme = false;
                model.DefaultLightV453Applied = true;
                changed = true;
            }
            if (changed) Save(model);
            return model;
        }
        catch (Exception ex) { AppLog.Write("读取设置失败，使用默认设置", ex); return new SettingsModel(); }
    }
    public static void Save(SettingsModel settings)
    {
        Directory.CreateDirectory(DataDir);
        string temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, ConfigPath, true);
    }
}
public static class AppLog
{
    private static readonly object Gate = new();
    public static string LogFile => Path.Combine(Storage.DataDir, "diagnostics.log");
    public static void Write(string text, Exception? exception = null)
    {
        try
        {
            // Protect signed CDN URLs: never write raw query strings to the log.
            var safe = Regex.Replace((text + (exception == null ? "" : ": " + exception.GetType().Name + " " + exception.Message)), @"https?://[^\s]+", match => {
                var raw = match.Value; var cut = raw.IndexOf('?'); return cut < 0 ? raw : raw[..cut] + "?[redacted]";
            });
            lock (Gate)
            {
                Directory.CreateDirectory(Storage.DataDir);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 2_000_000) File.Move(LogFile, LogFile + ".old", true);
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {safe}{Environment.NewLine}");
            }
        }
        catch { /* Never crash on diagnostic write. */ }
    }
}
