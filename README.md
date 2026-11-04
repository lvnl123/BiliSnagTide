# BiliSnagTide

Bilibili 直播源提取与弹幕伴侣（Windows / C# / WPF / .NET 8）。

> 非哔哩哔哩官方项目。直播接口和画质可用性会随平台策略、账号权限和时间变化。

## 主要功能

- 获取 B站直播的 FLV / HLS 候选播放源，识别画质与线路信息
- 将播放地址交给 PotPlayer，支持本地播放代理
- 扫码登录与直播画质扫描
- 实时弹幕、收藏、历史、诊断与主题配色

## 源码目录

- `Source/`：WPF 应用 C# / XAML 源码、项目文件与图标资源
- `Themes/`：主题示例配置

**本仓库只保留源码与必要资源**，不包含 EXE、SDK 安装器、发布包、临时缓存或旧版一键构建脚本。

## 开发环境

- Windows 10/11
- .NET 8 SDK
- Visual Studio（含 .NET 桌面开发工作负载）或 .NET CLI

在 Windows 上使用标准 .NET 命令构建：

```powershell
dotnet restore .\Source\BiliSnagTide.csproj
dotnet build .\Source\BiliSnagTide.csproj -c Release
```

以上命令使用 .NET 默认的 `Source/bin`、`Source/obj` 等目录，**不会创建 `D:\BiliLiveBuildCache`**。本仓库没有自动下载 SDK 或生成该目录的脚本。运行时程序会在 `%LOCALAPPDATA%\BiliSnagTide` 保存用户选择的设置、收藏和日志（与编译缓存不同）。

如需生成可分发的自包含版本，可自行运行标准 `dotnet publish`；预编译的 Windows ZIP 和安装包见 GitHub Releases。

## 注意事项

- 直播源 URL 可能过期或需要账号权限；不要公开带签名的播放地址。
- 登录 Cookie、Token 等敏感信息不应提交到仓库。
- 目前提交的是用户确认可以运行的 **v4.5.3 源码**，仓库清理不涉及功能重新开发，尚未在本执行环境中重新完成 Windows 编译验证。

## Windows Releases（已编译版本）

正式程序请在 [Releases](https://github.com/lvnl123/BiliSnagTide/releases) 下载：

- `BiliSnagTide-v4.5.3-Windows-x64-Portable.zip`：解压即用、使用本地 `Data/` 数据目录的绿色版。
- `BiliSnagTide-v4.5.3-Windows-x64-Setup.exe`：当前用户安装版，提供卸载程序，设置保存在 AppData。

两种版本都由 GitHub Actions 的 Windows 构建机使用 .NET 8 自包含发布，不需要使用者安装 .NET SDK，也不使用旧的 `BiliLiveBuildCache`。便携版的 `portable.flag` 不应删除，否则会切换为 AppData 模式。构建工作流见 `.github/workflows/windows-release.yml`。
