# 第三方组件声明

NekoPlayer 原创代码采用 MIT License。MIT License 不覆盖下列第三方组件、FFmpeg 项目或第三方 FFmpeg 二进制构建。

## FFmpeg

- FFmpeg 项目主页：https://ffmpeg.org/
- FFmpeg 源代码：https://git.ffmpeg.org/ffmpeg.git
- Windows 构建来源：https://github.com/BtbN/FFmpeg-Builds
- GitHub Release：https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest
- 实际资产：`ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`
- 实际版本：`n8.1.2-34-g9b6c8969e0-20260802`
- 构建类型：Windows x64、LGPL、Shared
- 本地 SHA-256：`05da9f6658efc6604cb20d3fa21db3fd768e73f8f7e48c7a63c9872be3099782`
- GitHub API digest：`sha256:05da9f6658efc6604cb20d3fa21db3fd768e73f8f7e48c7a63c9872be3099782`
- digest 校验：已与本地 SHA-256 匹配

运行 `ffmpeg.exe -L` 显示该构建依据 GNU Lesser General Public License version 3 or later 提供；`ffmpeg.exe -version` 的配置包含 `--enable-shared`、`--disable-static`，未发现 `--enable-gpl` 或 `--enable-nonfree`。

BtbN/FFmpeg-Builds 是 FFmpeg 官方下载页面列出的 Windows 构建提供方之一，但仍属于第三方编译构建。选择 LGPL Shared 不自动代表已经履行 FFmpeg 及其依赖的全部许可证义务。任何再分发者仍需根据实际二进制、依赖、修改情况和分发方式自行核对 LGPL 以及其他相关许可证要求。本说明不构成法律意见。

下载包提供的许可证资料保存在发布目录的 `ffmpeg/licenses` 中，来源和哈希保存在 `ffmpeg/SOURCE.txt` 与 `ffmpeg/metadata.json` 中。

## Node.js 及在线音乐网关

- 官方来源：[Node.js](https://nodejs.org/)；固定版本 `v24.21.0`、Windows x64
- 官方 ZIP：[node-v24.21.0-win-x64.zip](https://nodejs.org/dist/v24.21.0/node-v24.21.0-win-x64.zip)
- 官方校验表：[SHASUMS256.txt](https://nodejs.org/dist/v24.21.0/SHASUMS256.txt)
- ZIP SHA-256：`158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541`
- `node.exe` SHA-256：`ba4e6d110e8c1592a1ecd390f6b05f3da124b13871a5be62b341a07a853c6c32`
- 官方 ZIP 中完整 `LICENSE` 的 SHA-256：`ed34dd8e3f0a78dbaf00d0444ce8e285b015b765379c2e17880455f70370f8e9`；长度 160555 字节

Node.js 自身使用 MIT License；官方包内依赖还有各自的许可证与版权声明。开发目录 `tools/node/LICENSE` 和发布目录 `music-gateway/node/LICENSE` 保留官方 ZIP 内完整、未修改的许可证文件。来源与验证信息见同目录 `SOURCE.txt`、`metadata.json` 和 `SHASUMS256.txt`。ZIP 哈希同时匹配脚本内固定 digest 与官方 HTTPS 校验表；此次检查没有验证校验表签名密钥。

在线网关直接使用以下上游 SDK。直接依赖固定版本或 Git 提交，传递依赖由 `package-lock.json` 锁定；发布包保留该文件中的来源与 npm integrity：

| 平台 | SDK 来源 | 固定版本或提交 |
| --- | --- | --- |
| 网易云音乐 | [NeteaseCloudMusicApiEnhanced](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced) / `@neteasecloudmusicapienhanced/api` | `4.41.1` |
| QQ 音乐 | [qq-music-api](https://github.com/sansenjian/qq-music-api) / `@sansenjian/qq-music-api` | `2.6.0` |
| 酷狗音乐 | [KuGouMusicApi](https://github.com/MakcRe/KuGouMusicApi) / `kugou-music-api` | `da5ccfd9304c043085a2fd18e94ebc5c315044ab` |
| 汽水音乐 | [qishui-api](https://github.com/guowenye/qishui-api) / `qishui-api` | `e409c10fe7441a10d370da7da61d62bbc1e5126c` |

四个 SDK 的原始许可证、源码来源、许可证哈希和锁文件 integrity 清单保存在开发目录 `tools/music-gateway/LICENSES`，并复制到发布目录 `music-gateway/LICENSES`。网关运行依赖内的许可证文件随 `node_modules` 保留。根目录 MIT License 只覆盖本项目原创代码；不改变上述 SDK、依赖或在线平台内容的授权条件。

酷我音乐采用本项目独立 Web 适配器，没有把它虚列为上述 npm SDK。适配参考来源及上游公开资源的来源记录和摘要见 `music-gateway/LICENSES/kuwo-web-source.json`；使用平台服务仍受平台内容与使用条件约束。

## 其他组件

- Avalonia UI
- CommunityToolkit.Mvvm
- Microsoft.Extensions.Hosting 与依赖注入组件
- Microsoft.EntityFrameworkCore.Sqlite、SQLitePCLRaw 与 SQLite
- FFMpegCore（FFmpeg 命令行封装，不是 FFmpeg 本体）
- NAudio（Windows 音频输出和 FFT）
- TagLibSharp（音频标签读取）
- Serilog 与 Serilog.Sinks.File
- xUnit 与 Microsoft.NET.Test.Sdk

这些组件分别受其自身许可证约束。使用和分发前应查看对应 NuGet 包、源码仓库和许可证文本。

界面中的猫耳、音符、声波和渐变图形由项目代码绘制，不包含初音未来官方名称、Logo、立绘、服饰图案或其他官方素材。
