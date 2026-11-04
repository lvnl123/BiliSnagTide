# BiliSnagTide v4.5.4 — Windows x64

第三方非官方 Bilibili 直播源提取与弹幕伴侣。

### 两个可下载 EXE

- **BiliSnagTide-v4.5.4-Windows-x64-Portable.exe** — 单文件绿色版，无需安装或解压。直接双击启动，程序会在 EXE 同目录创建 `Data/` 保存设置、收藏及日志；请放在有写入权限的目录。
- **BiliSnagTide-v4.5.4-Windows-x64-Setup.exe** — 当前用户安装版，支持开始菜单快捷方式与卸载，配置保存在 `%LOCALAPPDATA%\BiliSnagTide`。

两者都采用 Windows x64/.NET 8 自包含发布；无需 .NET SDK，也不创建 `BiliLiveBuildCache`。部分原生组件可能在运行时解压到 Windows 临时目录，这是 .NET 单文件机制，不需要用户处理。

注意：程序和安装包目前未做代码签名，Windows SmartScreen 可能提示未知发布者。PotPlayer 等第三方播放器需自行安装。
