# 构建 NekoPlayer

## 通用要求

- .NET 8 SDK
- Git（仅参与仓库开发时需要）
- Windows x64 或 Linux x64

在项目根目录执行：

```text
dotnet restore NekoPlayer.sln
dotnet build NekoPlayer.sln -c Release --no-restore
dotnet test NekoPlayer.sln -c Release --no-build
```

测试使用临时路径与临时数据库，普通单元测试不会播放声音。

## Windows x64

Windows 音频后端依赖项目随包的 FFmpeg LGPL Shared 运行时和 NAudio。先运行：

```powershell
.\setup-ffmpeg.ps1
```

该脚本从 BtbN/FFmpeg-Builds 的 GitHub Release 下载指定类别的 Windows x64 LGPL Shared 资产，校验上游 digest、实际 SHA-256、必要 DLL、版本输出与许可证配置。二进制保存在被 `.gitignore` 排除的 `tools/ffmpeg` 中。

发布：

```powershell
.\publish-win-x64.ps1
```

输出：

- `artifacts/publish/win-x64`
- `artifacts/release/NekoPlayer-v1.0.0-win-x64.zip`

## Linux x64

Linux 构建机需要 .NET 8 SDK、`ffmpeg`、`ffprobe`、`file` 和基础桌面动态库。发布脚本会生成 self-contained、未裁剪的 ELF x86-64 输出，并用 `file`、`ldd` 和内容扫描检查发布目录。

```bash
chmod +x ./publish-linux-x64.sh ./packaging/linux/*.sh
./publish-linux-x64.sh
```

输出：

- `artifacts/publish/linux-x64`
- `artifacts/release/NekoPlayer-v1.0.0-linux-x64.tar.gz`

Windows 主机可在已配置的 WSL2 中运行：

```powershell
.\publish-linux-x64.ps1
```

Linux 最终包应由真实 Linux 环境或 Ubuntu GitHub Actions Runner 生成；仅在 Windows 上交叉 publish 不作为最终可用性结论。

## 发布参数

两个平台均使用：

- `SelfContained=true`
- `PublishTrimmed=false`
- `DebugType=None`
- `DebugSymbols=false`

项目不强制单文件发布，避免破坏 Avalonia 资源、SQLite 或 FFmpeg 相关加载行为。
