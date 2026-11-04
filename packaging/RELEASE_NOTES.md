# BiliSnagTide v4.6.0 — GitHub 更新中心

这是 BiliSnagTide 的 Windows x64 自包含版本（.NET 8 / WPF）。第三方非官方 B 站直播辅助工具。

## 新增：软件内检查与下载更新

- 启动后自动检查 GitHub **正式 Releases**（可关闭，最多每 24 小时检查一次）。
- 设置与代理新增「软件更新」区：手动检查、版本信息、更新日志、跳过版本及 GitHub 下载入口。
- 支持软件内下载、实时进度、取消、SHA-256 校验。**任何更新均需手动确认，绝不静默安装。**
- **绿色版**：下载验证后，可在退出时由新 EXE 替换原 EXE 并重新运行；设置、收藏和日志仍存放在程序同目录的 `Data/`。
- **安装版**：下载验证后启动新版安装器；设置数据继续保存在 `%LOCALAPPDATA%\BiliSnagTide`。
- 新增 `SHA256SUMS.txt` 供客户端验证 Release 附件，缺少校验信息时禁用软件内安装。
- GitHub 仓库源码提交和 README 修改不触发更新通知，只有发布新正式版本才会提示。

## 下载

- `BiliSnagTide-v4.6.0-Windows-x64-Portable.exe`：绿色单文件版，直接运行，不需要安装或外部标记文件。
- `BiliSnagTide-v4.6.0-Windows-x64-Setup.exe`：标准安装包版，含开始菜单和卸载功能。
- `SHA256SUMS.txt`：这两个 EXE 的 SHA-256 校验清单。

两个版本都自带 .NET 运行时，不需要 .NET SDK，也**不会使用旧版 `BiliLiveBuildCache`**。PotPlayer / ffprobe 等其他可选工具由用户自行安装。

> 请注意：程序和安装包尚未进行 Authenticode 代码签名，Windows 可能显示未知发布者。仅从本仓库的 Releases 获取文件，并在应用更新前保存重要工作。
