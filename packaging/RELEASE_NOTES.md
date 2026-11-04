# BiliSnagTide v4.6.1 — 恢复 ZIP 绿色便携版

本次是发行形式调整，保留 v4.6.0 新增的软件内更新中心、直播源解析、弹幕伴侣与主题工作台。

## Windows x64 下载

- **BiliSnagTide-v4.6.1-Windows-x64-Portable.zip** — 免安装绿色版，解压完整 ZIP 后双击 `BiliSnagTide.exe`。请勿只提取 EXE，文件夹中的 DLL、运行时组件和 `portable.flag` 应当保留。设置、收藏及日志在解压目录的 `Data/` 中。
- **BiliSnagTide-v4.6.1-Windows-x64-Setup.exe** — 正常安装版，带快捷方式与卸载功能；用户数据保存在 `%LOCALAPPDATA%\BiliSnagTide`。
- **SHA256SUMS.txt** — 两份分发包的 SHA-256 文件校验值。

两个版本均包含 .NET 8 运行时，普通用户无需安装 .NET SDK，亦不会使用旧版 `BiliLiveBuildCache`。

## 软件内更新适配

- 检查 GitHub 正式 Release、显示更新说明、下载进度、取消与 SHA-256 校验均保留。
- 绿色版的新更新附件改为 **Portable.zip**。验证 ZIP 后，软件会解压至**原程序旁的独立新版本文件夹**，迁移原文件夹 `Data/` 下的设置与收藏，再启动新版。旧版文件夹不自动删除，可用于回退。
- 这不会在软件仍运行时覆盖原程序 DLL。安装版仍下载并启动新的 Setup.exe。
- **从 v4.6.0 单 EXE 便携版升级：** 因该旧版本只识别 `Portable.exe` 附件，首次需要从 GitHub 下载 v4.6.1 ZIP 并手动解压。之前单文件旁的 `Data/` 可复制到解压后的程序文件夹。
- 正式版本更新需用户确认，绝不强制执行。

> 发行包未经代码签名，Windows 可能提示未知发布者。请仅通过本仓库 Releases 下载，保留重要设置备份。