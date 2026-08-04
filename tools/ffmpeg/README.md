# 项目内 FFmpeg 运行时

本目录中的 FFmpeg 二进制文件不是 NekoPlayer 原创代码。项目使用 `setup-ffmpeg.ps1` 从 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) 的 GitHub Release 下载 Windows x64、LGPL、Shared 构建。BtbN 是 FFmpeg 官方下载页面列出的 Windows 构建提供方之一，但仍属于第三方编译和发布的二进制构建。

Shared 构建运行时不仅需要 `ffmpeg.exe` 和 `ffprobe.exe`，还必须保留同一 `bin` 目录内的 `avcodec-*.dll`、`avformat-*.dll`、`avutil-*.dll`、`swresample-*.dll` 等 DLL。不要单独移动两个 EXE。

重新下载或刷新：

```powershell
.\setup-ffmpeg.ps1
.\setup-ffmpeg.ps1 -Force
```

检查版本与许可证配置：

```powershell
.\tools\ffmpeg\ffmpeg.exe -version
.\tools\ffmpeg\ffprobe.exe -version
.\tools\ffmpeg\ffmpeg.exe -L
```

`metadata.json` 和 `SOURCE.txt` 记录实际资产、下载时间、SHA-256 与上游 digest 验证结果；`licenses` 目录保留下载包提供的许可证或构建说明。LGPL Shared 的选择不自动代表已经履行全部分发义务，发布者仍需核对 FFmpeg 及其依赖的实际许可证要求。
