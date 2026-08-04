# 猫娘播放器（NekoPlayer）v1.0.0

猫娘播放器是使用 C#、.NET 8 与 Avalonia 11 构建的跨平台本地音乐播放器，面向 Windows x64 与 Linux x64。它专注于本地曲库、播放队列、收藏、最近播放、歌单、歌词、封面与响应式桌面界面，不上传音乐文件或曲库数据。

创作者：梦怀殇

## 功能

- 导入音频文件或扫描文件夹，支持重复检测与更新已有曲目元数据
- 播放、暂停、恢复、停止、Stop 后从头重播、上一首、下一首、Seek、音量与静音
- 顺序、列表循环、单曲循环和随机播放队列
- 搜索、排序、收藏、最近播放、自定义歌单与批量添加
- Compact、Standard、Wide 三档响应式布局
- 本地 LRC 解析、当前歌词高亮和基础频谱
- SQLite 本地曲库，JSON 设置，队列与上次播放位置恢复
- 真实封面、封面缓存、默认 AppIcon 的统一回退；损坏或丢失封面不会阻止界面加载
- 收藏按钮使用独立矢量图形，不依赖系统字体中的爱心字符
- 关闭窗口时释放播放器、FFmpeg 与数据库相关资源

## 下载与安装

请从 [GitHub Releases](https://github.com/ym2025szz/NekoPlayer/releases) 下载对应平台的完整发布包。不要只复制主程序文件。

### Windows x64

1. 下载 `NekoPlayer-v1.0.0-win-x64.zip`。
2. 将 ZIP 完整解压到普通可写目录。
3. 运行 `NekoPlayer.exe`。
4. 首次启动后，在“本地音乐”中添加音乐文件或文件夹。

Windows 包是 .NET 8 自包含发布，随包提供经过来源与许可证核对的 Windows x64 FFmpeg LGPL Shared 运行时，不要求另装 .NET 或 FFmpeg。

### Linux x64

Linux 包是 .NET 8 自包含发布，但使用系统 FFmpeg/ffprobe 和桌面系统库。Ubuntu/Debian 可先安装：

```bash
sudo apt-get update
sudo apt-get install -y ffmpeg libx11-6 libice6 libsm6 libfontconfig1 libfreetype6 libxrandr2 libxi6 libxcursor1 libxext6 libxrender1 libgl1 libgtk-3-0
```

然后解压并直接运行：

```bash
tar -xzf NekoPlayer-v1.0.0-linux-x64.tar.gz
cd NekoPlayer-v1.0.0-linux-x64
chmod +x NekoPlayer install-linux.sh uninstall-linux.sh
./NekoPlayer
```

也可以安装当前用户的桌面入口，不需要 root：

```bash
./install-linux.sh
```

默认安装到 `~/.local/opt/NekoPlayer`，桌面入口和图标写入 `~/.local/share`。卸载程序文件与桌面入口：

```bash
~/.local/opt/NekoPlayer/uninstall-linux.sh
```

默认卸载不会删除曲库、收藏、歌单、播放历史、设置、缓存或日志。更完整的 Linux 说明见 [docs/LINUX.md](docs/LINUX.md)。

## 基本使用

1. 打开“本地音乐”，添加文件或音乐文件夹。
2. 双击歌曲或使用行内播放按钮开始播放。
3. 使用底部播放栏暂停、恢复、停止、切歌、Seek、调节音量或静音。
4. 使用爱心按钮收藏歌曲；收藏状态会同步到曲库、最近播放、歌单、队列和正在播放页。
5. 在歌单页创建歌单，再从曲库、收藏、最近播放或队列添加歌曲。

快捷键：`Space` 播放/暂停，`Ctrl+F` 聚焦搜索，左右方向键 Seek 5 秒，`Shift+左右方向键` Seek 15 秒，`Esc` 关闭队列或确认层。输入框和进度条聚焦时不会抢占不适合的按键。

## 用户数据与隐私

Windows 保持既有 v1.0.0 数据位置：

- 数据库：`%LocalAppData%\NekoPlayer\Data\nekoplayer.db`
- 配置：`%LocalAppData%\NekoPlayer\Config\settings.json`
- 缓存：`%LocalAppData%\NekoPlayer\Cache`
- 日志：`%LocalAppData%\NekoPlayer\Logs`

Linux 遵循 XDG Base Directory：

- 数据库：`${XDG_DATA_HOME:-~/.local/share}/NekoPlayer/nekoplayer.db`
- 配置：`${XDG_CONFIG_HOME:-~/.config}/NekoPlayer/settings.json`
- 缓存与日志：`${XDG_CACHE_HOME:-~/.cache}/NekoPlayer`

日志可能包含本地音乐路径，仅用于本机排错；公开分享日志前请先检查隐私信息。发布包和 Git 仓库不包含开发者数据库、音乐、封面缓存、播放历史或配置。

## 校验下载

Release 同时提供 `SHA256SUMS.txt`。

Windows PowerShell：

```powershell
Get-FileHash .\NekoPlayer-v1.0.0-win-x64.zip -Algorithm SHA256
```

Linux：

```bash
sha256sum -c SHA256SUMS.txt
```

## 开发与构建

需要 .NET 8 SDK。通用构建与测试：

```powershell
dotnet restore .\NekoPlayer.sln
dotnet build .\NekoPlayer.sln -c Release --no-restore
dotnet test .\NekoPlayer.sln -c Release --no-build
```

Windows 开发或发布前安装已校验的 FFmpeg Shared 运行时：

```powershell
.\setup-ffmpeg.ps1
.\publish-win-x64.ps1
```

Linux 在已安装 `ffmpeg`、`ffprobe`、`file` 和桌面依赖的环境中运行：

```bash
chmod +x ./publish-linux-x64.sh ./packaging/linux/*.sh
./publish-linux-x64.sh
```

Windows 也可通过 WSL 调用：

```powershell
.\publish-linux-x64.ps1
```

构建详情见 [docs/BUILDING.md](docs/BUILDING.md)，发布检查见 [docs/RELEASE-CHECKLIST.md](docs/RELEASE-CHECKLIST.md)。

## 技术结构

- `src/NekoPlayer.App`：Avalonia UI、ViewModel、依赖注入和跨平台启动器
- `src/NekoPlayer.Core`：模型、接口、队列、歌词和纯逻辑工具
- `src/NekoPlayer.Audio`：Windows NAudio 后端、Linux FFmpeg/ALSA 后端和频谱
- `src/NekoPlayer.Infrastructure`：SQLite、XDG/Windows 路径、FFmpeg 定位、元数据和仓储
- `tests/NekoPlayer.Tests`：单元测试与发布契约测试
- `tools/NekoPlayer.PlaybackVerifier`：真实 FFmpeg 播放管线、Seek、Stop 重播与资源释放验证
- `tools/NekoPlayer.ImportVerifier`：临时 SQLite 下的真实导入与重复检测验证
- `tools/NekoPlayer.LibraryVerifier`：临时大曲库查询与状态边界验证
- `.github/workflows/ci.yml`：Windows 与 Ubuntu 双平台构建、测试、GUI 和播放管线验证

详细设计见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## FFmpeg 策略

- Windows：发布包携带 BtbN/FFmpeg-Builds 的 Windows x64 LGPL Shared 构建；版本、下载资产、哈希和上游许可证资料记录在 `tools/ffmpeg` 的文本元数据中。
- Linux：发布包不携带 FFmpeg 二进制，运行时从 `PATH` 查找系统的 `ffmpeg` 与 `ffprobe`，音频输出使用 FFmpeg ALSA 设备；缺失时应用会给出可理解的不可用状态。

详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 当前限制

- 没有在线音乐、在线歌词、桌面歌词、系统级全局快捷键、均衡器、变速或无缝播放
- Linux 自动化验证覆盖解码、进度、暂停、恢复、Seek、Stop、重播、切歌、Xvfb 启动与资源释放，不等于真实音箱的人耳听感验收
- 歌词支持解析与高亮，自动居中滚动和手动滚动暂停跟随仍可继续完善
- 歌单保存顺序字段，但当前不提供拖拽排序

## 许可证

NekoPlayer 原创代码采用 [MIT License](LICENSE)。第三方库、FFmpeg、Inter 字体和其他依赖仍受各自许可证约束，MIT License 不会覆盖它们。
