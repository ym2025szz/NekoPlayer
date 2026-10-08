<p align="center"><img src="src/NekoPlayer.App/Assets/AppIcon.png" width="112" alt="猫娘播放器图标"></p>

# 猫娘播放器 · NekoPlayer

一个以猫娘与青绿色界面为主题的 Windows 音乐播放器，把本地曲库、多平台搜索和桌面歌词放在一起。

**当前正式版：v1.2.0** · **Windows 10 1809+ / Windows 11 x64** · **维护者：[@ym2025szz](https://github.com/ym2025szz)**

[下载正式版](https://github.com/ym2025szz/NekoPlayer/releases/latest) · [更新记录](CHANGELOG.md) · [开发与构建](docs/BUILDING.md) · [第三方组件](THIRD-PARTY-NOTICES.md)

## 可以做什么

- **本地曲库**：导入音乐、扫描文件夹、读取标签与封面，支持收藏、最近播放、自建歌单和搜索排序。
- **多平台搜索**：接入网易云音乐、QQ 音乐、酷我、酷狗和汽水；各来源独立加载，保留歌曲来源与版本信息。
- **账号授权**：网易云、QQ、酷狗支持扫码登录，凭证使用 Windows DPAPI 加密保存在本机。
- **歌词体验**：歌词在独立区域中跟随当前句，双击歌词可以跳转；透明双行桌面歌词支持拖动、字号调整、锁定和鼠标穿透。
- **播放控制**：顺序播放、列表循环、单曲循环和随机播放，支持进度跳转、媒体键、托盘后台运行和睡眠定时。
- **队列与歌单**：拖动或按钮调序、歌单内搜索、批量加入队列，以及歌单移除撤销。
- **便携运行**：正式包自带 .NET、FFmpeg Shared、Node.js 和音乐网关依赖，无需另装运行环境。

## 开始使用

1. 从 [Releases](https://github.com/ym2025szz/NekoPlayer/releases/latest) 下载 `NekoPlayer-v1.2.0-win-x64.zip`。
2. 完整解压，运行目录中的 `NekoPlayer.exe`。
3. 导入本地音乐，或进入“在线搜索”；右上角“账号授权”可管理平台登录。
4. 在“正在播放”或“设置”开启桌面歌词。关闭主窗口默认收起到托盘，托盘菜单可彻底退出。

用户曲库、歌单、设置和授权凭证保存在 `%LOCALAPPDATA%\NekoPlayer`。启动时不会自动播放音乐；更新时请保留完整目录，并从托盘退出旧版本后再替换程序文件。

在线音源按平台实际返回的版权、账号和试听权限播放，搜索到歌曲不代表能够完整播放。酷我、汽水目前保留游客模式；部分接口可能受限流或平台变化影响。

## 技术与验证

采用 **C# / .NET 8、Avalonia 11、MVVM、SQLite、FFmpeg、NAudio 与 Node.js**。界面、播放协调、曲库和平台网关分层实现。

v1.2.0 本地验收已通过 **501 项 .NET 测试、28 项网关测试、100 次模拟重播和 20 次真实 FFmpeg EOF 循环**。桌面歌词跨进程穿透也已验证。详见 [v1.2.0 验收记录](docs/MANUAL-ACCEPTANCE-v1.2.0.md)。

```powershell
# 开发需要 .NET 8 SDK
.\setup-ffmpeg.ps1
.\setup-music-gateway.ps1
dotnet restore .\NekoPlayer.sln
dotnet build .\NekoPlayer.sln -c Release --no-restore
dotnet test .\NekoPlayer.sln -c Release --no-build
```

源码目录：`src/NekoPlayer.App`（界面）、`Core`（模型与播放协调）、`Audio`（解码与输出）、`Infrastructure`（存储与在线服务），测试位于 `tests`，网关位于 `tools/music-gateway`。

## 正式版本与许可证

正式便携包通过 GitHub Releases 分版本保存；源码仓库不存放安装包、用户数据库、登录凭证、缓存或本机日志。历史 v1.0.0 的 Windows / Linux 包保留在对应 Release，当前 v1.2.0 面向 Windows。

原创代码采用 [MIT License](LICENSE)。FFmpeg、Node.js 和平台 SDK 的许可证与来源分别保留在 [第三方组件声明](THIRD-PARTY-NOTICES.md) 及相关许可证目录中。