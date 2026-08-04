# Linux x64 使用说明

## 支持范围

NekoPlayer v1.0.0 提供 glibc Linux x64 的 .NET 8 自包含发布。界面使用 Avalonia/X11，音频由系统 FFmpeg 输出到 ALSA。发布包不携带 Linux FFmpeg 二进制。

## 安装依赖

Ubuntu/Debian：

```bash
sudo apt-get update
sudo apt-get install -y ffmpeg libx11-6 libice6 libsm6 libfontconfig1 libfreetype6 libxrandr2 libxi6 libxcursor1 libxext6 libxrender1 libgl1 libgtk-3-0
```

确认 FFmpeg：

```bash
ffmpeg -version
ffprobe -version
ffmpeg -devices | grep -i alsa
```

其他发行版请安装等价的 FFmpeg、X11、字体、OpenGL 与 GTK 运行库包。FFmpeg 必须同时提供小写无扩展名的 `ffmpeg` 和 `ffprobe` 命令，并支持 ALSA 输出设备。

## 解压后直接运行

```bash
tar -xzf NekoPlayer-v1.0.0-linux-x64.tar.gz
cd NekoPlayer-v1.0.0-linux-x64
chmod +x NekoPlayer install-linux.sh uninstall-linux.sh
./NekoPlayer
```

如果系统没有可用的默认 ALSA 设备，应用可以启动和管理曲库，但播放会显示音频输出错误。PipeWire/PulseAudio 环境通常通过系统 ALSA 兼容层提供 `default` 设备。

## 当前用户桌面安装

```bash
./install-linux.sh
```

默认行为：

- 程序：`~/.local/opt/NekoPlayer`
- 桌面入口：`${XDG_DATA_HOME:-~/.local/share}/applications/nekoplayer.desktop`
- 图标：`${XDG_DATA_HOME:-~/.local/share}/icons/hicolor/256x256/apps/nekoplayer.png`
- 不使用 `sudo`
- 不覆盖没有 NekoPlayer 安装标记的未知目录

可通过 `NEKOPLAYER_INSTALL_ROOT` 指定其他用户可写安装目录。路径包含空格时脚本与桌面入口仍会正确引用主程序。

## 卸载

```bash
~/.local/opt/NekoPlayer/uninstall-linux.sh
```

默认只删除程序目录、桌面入口和安装的图标。用户曲库、收藏、播放历史、歌单、设置、缓存和日志全部保留。

如需彻底删除用户数据，请先关闭 NekoPlayer，再由用户自行检查并删除下列目录；卸载脚本不会自动执行此操作：

- `${XDG_DATA_HOME:-~/.local/share}/NekoPlayer`
- `${XDG_CONFIG_HOME:-~/.config}/NekoPlayer`
- `${XDG_CACHE_HOME:-~/.cache}/NekoPlayer`

## XDG 数据位置

- 数据库：`${XDG_DATA_HOME:-~/.local/share}/NekoPlayer/nekoplayer.db`
- 设置：`${XDG_CONFIG_HOME:-~/.config}/NekoPlayer/settings.json`
- 封面、歌词、日志和临时文件：`${XDG_CACHE_HOME:-~/.cache}/NekoPlayer`

可以为测试进程单独设置 `NEKOPLAYER_DATA_ROOT`，把所有数据重定向到一个隔离根目录。普通用户无需设置它。

## 无显示或 CI 验证

`NEKOPLAYER_LINUX_AUDIO_DEVICE=null` 仅用于自动化环境，让 FFmpeg 使用 null 输出验证解码、进度、暂停、恢复、Seek、Stop、重播、切歌和资源释放。它不代表真实扬声器听感，也不建议作为普通桌面使用配置。

## 故障排查

- “未找到系统 FFmpeg/ffprobe”：确认两条命令都在 `PATH` 中。
- “音频输出进程启动后立即退出”：运行 `ffmpeg -devices | grep -i alsa`，并检查系统默认 ALSA/PipeWire/PulseAudio 兼容设备。
- 界面无法启动：用 `ldd ./NekoPlayer | grep 'not found'` 检查系统动态库。
- 中文显示异常：安装包含中文字符的系统字体；应用内矢量收藏按钮不依赖字体爱心字符。
