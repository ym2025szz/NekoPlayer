# 第三方组件声明

NekoPlayer 原创代码采用 MIT License。该许可证不覆盖下列第三方组件、字体、FFmpeg 项目或第三方 FFmpeg 二进制构建。本文件根据 v1.0.0 项目文件、NuGet 包元数据与实际发布策略整理，不构成法律意见。

## FFmpeg

FFmpeg 项目主页：<https://ffmpeg.org/>
FFmpeg 源代码：<https://git.ffmpeg.org/ffmpeg.git>

### Windows 发布包

- 构建来源：<https://github.com/BtbN/FFmpeg-Builds>
- Release：<https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest>
- 资产：`ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`
- 版本：`n8.1.2-34-g9b6c8969e0-20260803`
- 构建类型：Windows x64、LGPL、Shared
- 下载文件 SHA-256：`9e8e04021c8d563642c526da28c76184786cf98b44d5fc499566ecf8a8ec1c0d`
- GitHub API digest：`sha256:9e8e04021c8d563642c526da28c76184786cf98b44d5fc499566ecf8a8ec1c0d`

实际 `ffmpeg.exe -L` 表明该构建依据 GNU Lesser General Public License version 3 or later 提供；`ffmpeg.exe -version` 配置包含 `--enable-shared`、`--disable-static`，未发现 `--enable-gpl` 或 `--enable-nonfree`。发布包保留构建附带的许可证资料、来源和哈希记录，位置为 `ffmpeg/licenses`、`ffmpeg/SOURCE.txt` 与 `ffmpeg/metadata.json`。

BtbN 是 FFmpeg 官方下载页列出的 Windows 构建提供方之一，但仍属于第三方编译构建。选择 LGPL Shared 不自动代表已经履行 FFmpeg 及其依赖的全部许可证义务；再分发者应按自己的分发方式继续核对许可证要求。

### Linux 发布包

Linux 包不分发 FFmpeg 可执行文件或共享库。程序从用户系统 `PATH` 查找 `ffmpeg` 与 `ffprobe`，因此 Linux 用户实际使用的 FFmpeg 版本、配置和许可证由其发行版软件源或用户选择的安装来源决定。

## 运行时 NuGet 组件

| 组件 | 项目版本 | NuGet 元数据中的许可证 |
| --- | ---: | --- |
| Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent | 11.3.12 | MIT |
| Avalonia.Fonts.Inter | 11.3.12 | MIT（包代码）；所含 Inter 字体遵循 SIL Open Font License 1.1 |
| CommunityToolkit.Mvvm | 8.2.1 | MIT |
| Microsoft.Extensions.Hosting | 8.0.1 | MIT |
| Microsoft.EntityFrameworkCore.Sqlite | 8.0.20 | MIT |
| FFMpegCore | 5.2.0 | MIT |
| NAudio | 2.2.1 | MIT |
| Serilog | 4.3.0 | Apache-2.0 |
| Serilog.Extensions.Hosting | 8.0.0 | Apache-2.0 |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 |
| TagLibSharp | 2.3.0 | LGPL-2.1-only |

SQLite 本体属于 public domain；项目通过 Microsoft.EntityFrameworkCore.Sqlite 与 SQLitePCLRaw 的 NuGet 依赖使用 SQLite。传递依赖仍分别受其自身许可证约束，请以发布包 `.deps.json`、对应 NuGet 包和上游仓库为准。

## 开发与测试组件

xUnit、xunit.runner.visualstudio、Microsoft.NET.Test.Sdk 与 coverlet.collector 仅用于开发和测试，不应进入 Release 发布目录。其 NuGet 元数据分别声明 Apache-2.0 或 MIT 类许可证。

## 项目资源

界面中的猫耳、音符、声波、渐变和收藏按钮图形由项目代码或项目资产生成，不包含第三方角色名称、Logo、立绘或服饰图案。`Assets/AppIcon.png` 与 `Assets/NekoPlayer.ico` 是 NekoPlayer 项目发布资产。
