# BiliSnagTide v4.5.3 — Windows x64

Bilibili 直播源提取与弹幕伴侣，第三方非官方工具。

### 下载版本

- **BiliSnagTide-v4.5.3-Windows-x64-Portable.zip** — 绿色便携版，解压运行 `BiliSnagTide.exe`。设置、收藏和日志保存在解压目录的 `Data/` 中（由 `portable.flag` 启用）。
- **BiliSnagTide-v4.5.3-Windows-x64-Setup.exe** — 当前用户安装版，提供开始菜单快捷方式和卸载程序；不需要管理员权限。设置、收藏和日志位于 `%LOCALAPPDATA%\BiliSnagTide`。

两者均为 Windows x64 自包含构建，不需要用户安装 .NET SDK，也**不会创建 `BiliLiveBuildCache`**。外部 PotPlayer 等可选播放器由用户自行安装。

注意：安装包未做代码签名，Windows SmartScreen 可能提示未知发布者。请从本仓库 Release 获取，不要安装来历不明的副本。
