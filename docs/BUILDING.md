# 猫娘播放器（NekoPlayer）v1.2.0

猫娘播放器是面向 Windows 10 1809 及以上 / Windows 11 x64 的音乐播放器项目，使用 .NET 8、Avalonia 11 与 MVVM 构建。v1.2.0 保留本地播放、导入、Seek、歌单、播放队列和曲库管理，新增通过项目内 Node.js 网关接入网易云音乐、QQ 音乐、酷我音乐、酷狗音乐和汽水音乐的在线搜索与播放链路。

右上角“账号授权”按钮进入“设置 → 账号与授权”，可使用网易云音乐 App、手机 QQ 或酷狗音乐 App 扫码登录；请在手机上确认授权。凭证使用 Windows DPAPI 加密保存在本机，平台版权与会员权限以账号的实时授权为准。酷我和汽水目前保留游客模式。

在线功能依赖平台当前可用的接口、网络、账号和歌曲授权。搜索结果不代表完整音频一定可播放；平台要求登录、权限不足、没有可用音源、返回试听或接口异常时，应以界面实际状态为准。发布包自带 Node.js 和网关依赖，无需全局安装 Node.js，也无需用户另外启动服务。本地曲库和播放不依赖在线网关成功启动。

网易云音乐与 QQ 音乐是本次优先验证的接入链路；酷我的自写游客 HTTP 适配器也已通过指定样例的真实搜索、歌词和无声媒体解码检查。网易云歌曲可能只提供试听，所有音源仍逐首按平台权限解析。酷狗与汽水目前受游客权限或限流限制，不能承诺可播放。五个平台均经同一个本机 Node 网关访问。

界面采用深色青绿色未来风。歌曲没有内嵌封面、封面缓存路径为空、封面文件失效或图片损坏时，会统一回退到猫娘播放器 `AppIcon.png`；有效真实封面仍然优先显示。程序图标使用项目根目录中用户提供的 `图标.png`，项目不对该图片的版权状态作额外推定。

## v1.2.0 播放体验

- 独立范围歌词自动居中，手动浏览两秒后恢复跟随，双击歌词定位；完整歌曲单曲循环只重播当前曲。
- 两行透明桌面歌词支持拖动、字号、透明度、锁定穿透和位置恢复。
- 关闭主窗口到托盘继续播放；托盘提供恢复、播放控制、桌面歌词解锁及彻底退出。
- 系统媒体控制、睡眠定时、队列与歌单调序、歌单内搜索及账号按来源独立恢复。
- 正式包与安装记录保存在 releases，历史正式版独立留存。

## 主要功能

- 侧栏和首页提供在线搜索入口，可按本地及五个平台筛选；各来源独立加载、取消、重试和分页，搜索历史支持选用、单条删除与清空
- 在线结果显示歌曲、歌手、专辑、来源、版本、时长和权限状态，完整播放与试听分别提交；“找其他来源或版本”由用户明确触发
- 收藏、最近播放、自建歌单和队列可保存本地与在线歌曲；在线记录以平台和歌曲 ID 保存，播放时重新解析媒体地址，临时 URL、请求头和令牌不作为持久歌曲数据
- 导入一个或多个音频文件，扫描文件夹并可选递归子目录
- 导入阶段、当前文件、确定/不确定进度、数量统计、取消和结果摘要
- 数据库保存并重新查询、刷新 UI 且确认真实列表数据后，才报告完整成功
- 重复且未变化的文件计入跳过；已变化的文件更新元数据并保留收藏、播放次数和 Track Id
- 导入期间导航、播放栏和其他页面保持可用
- SQLite 本地曲库、TagLibSharp 标签、FFprobe 媒体信息和内嵌封面缓存
- 播放、暂停、恢复、停止、上一首、下一首、音量、静音和 Seek
- 顺序、列表循环、单曲循环、随机四种队列模式
- 搜索、排序、收藏、最近播放、自建歌单和播放队列
- Wide（≥1200）、Standard（1000～1199）和 Compact（<1000）三档响应式布局；最小窗口为 860×600
- 空心灰青色 `#8FAEB2` 矢量爱心表示未收藏，实心粉红色 `#FF5D7D` 矢量爱心表示已收藏；Hover、Pressed 和 Disabled 状态仍保留图形与颜色差异，收藏状态通过轻量事件同步到曲库、喜欢、最近播放、歌单、队列、正在播放页、选择器和底部播放栏
- 无封面、失效封面路径或损坏封面数据会显示共享的猫娘播放器默认图标；该回退不写入 Track 数据库记录，也不修改或嵌入用户音频文件
- 喜欢页面可以取消喜欢；该操作只修改收藏状态，不删除 Track、音乐文件、播放历史、歌单关系或当前播放
- 最近播放支持单条移除、撤销和二次确认后清空；这些操作只处理播放历史
- 自绘进度条使用完整 22px 控件区域接收鼠标，支持点击轨道、拖动预览和松开后提交最终 Seek；播放状态 Seek 后继续播放，暂停状态 Seek 后保持暂停
- Stop 会结束当前解码、清空 PCM 并把位置归零，但保留当前歌曲和队列位置；再次播放会创建新解码会话并从头开始
- 歌单详情支持搜索、单选和多选本地歌曲；批量写入 `PlaylistTrack` 时跳过重复关系，不复制音频文件或创建重复 Track
- 本地音乐、喜欢、最近播放、播放队列和正在播放页提供“添加到歌单”入口
- 本地音乐支持单选、多选、全选当前结果和取消选择；“从音乐库移除”只删除数据库记录及关联数据，绝不删除磁盘音频文件
- 播放队列显示从 1 开始的连续序号，并可立即播放目标项而不重复插入或清空队列
- 左侧导航按钮及其内容容器均横向拉伸，图标、文字与左右空白位于同一个命中区域；功能按钮统一使用居中的矢量 PathIcon
- 首页按本地时间显示问候语和 24 小时时间，设置页显示“创作者：梦怀殇”
- 主按钮显示“点击后将执行的动作”：播放中显示暂停图标，暂停/停止显示播放图标，加载和定位时禁止重复提交
- 快捷键：`Space` 播放/暂停、`Ctrl+F` 聚焦搜索、左右方向键 Seek 5 秒、`Shift+左右方向键` Seek 15 秒、`Esc` 关闭队列或确认层；输入框与进度条聚焦时不会抢键
- 文件不存在时歌曲行会变灰并显示状态，播放不会启动，也不会自动删除曲库记录
- 本地 LRC 解析和当前歌词高亮
- 从 Float32 PCM 取样的基础 FFT 频谱
- JSON 设置、队列和上次播放位置恢复，启动后不会自动发声
- Serilog 滚动日志、FFmpeg 完整性检测和 win-x64 自包含发布

## 技术栈

C# 12、.NET 8、Avalonia 11、CommunityToolkit.Mvvm、FFMpegCore 5.2.0、NAudio 2.2.1、TagLibSharp、EF Core SQLite、Microsoft.Extensions.Hosting、Serilog、System.Text.Json、xUnit、Node.js 24 与四个平台 SDK。

## 目录结构

- `src/NekoPlayer.App`：Avalonia 界面、ViewModel、依赖注入
- `src/NekoPlayer.Core`：模型、接口、队列、歌词和可测试工具
- `src/NekoPlayer.Audio`：FFmpeg 管道、有限 PCM 缓冲、NAudio 输出和频谱
- `src/NekoPlayer.Infrastructure`：SQLite、配置、FFmpeg 检测、元数据和仓储
- `tests/NekoPlayer.Tests`：默认不播放声音的单元测试
- `tools/NekoPlayer.PlaybackVerifier`：复用正式音频服务的真实播放技术验证工具
- `tools/NekoPlayer.ImportVerifier`：使用临时 SQLite 数据库的真实双次导入验证工具
- `tools/NekoPlayer.LibraryVerifier`：使用 500/1000/2000 条临时 SQLite Track 的查询、搜索、排序和数据边界性能验证工具
- `tools/generate-app-icon.ps1`：从用户 PNG 生成 Avalonia PNG 与多尺寸 Windows ICO
- `tools/ffmpeg`：项目内 FFmpeg Shared 运行时、来源与许可证资料
- `tools/node`：从 Node.js 官方 ZIP 校验安装的 v24.21.0 Windows x64 运行时、完整 LICENSE 与来源记录
- `tools/music-gateway`：在线平台适配器、请求网关、锁定依赖与第三方许可证
- `setup-music-gateway.ps1`：安装项目内 Node.js 与网关运行依赖
- `docs`：架构和人工验收说明

## 开发、还原和构建

安装 .NET 8 SDK，在项目根目录运行：

```powershell
.\setup-music-gateway.ps1
dotnet restore .\NekoPlayer.sln
dotnet build .\NekoPlayer.sln -c Release --no-restore
dotnet test .\NekoPlayer.sln -c Release --no-build
```

普通 `dotnet test` 不依赖本地测试音频，也不会突然从扬声器播放声音。运行时分发检查使用安装脚本准备的 `tools\node`，验证来源、许可证与不依赖全局 Node 的启动行为。

本轮 Release 构建为零警告、零错误；.NET 回归测试 **501/501**、Node 网关测试 **28/28** 通过。真实游客联网验证中，网易云、QQ 与酷我搜索和歌词均通过；三个指定曲目完成 FFprobe 时长探测、起始 2 秒与末段 Seek 后 2 秒解码，每段各产生 64000 字节 PCM。酷狗返回游客权限 403，最新汽水请求返回限流 429，按上游限制报告；验证器退出码 1 表示仍有平台受限，三个媒体样例没有解码失败。报告见 `artifacts/test-reports/online-verification.md` 与 `.json`，回归结果见 `artifacts/test-reports/v1.2.0/v1.2.0-regression.trx`。

已在隔离数据根、静音的 1280×760 实际窗口确认中文搜索渐进结果、真实封面、首个可用来源自动选择、QQ“夜雨 / 陈滢竹”进入设备 Playing 且位置推进至 123 秒；暂停后定位到 180 秒仍保持 Paused，重启恢复队列元数据与 180 秒位置且不启动解码、不自动发声。860×600 首页和更多操作已检查；该尺寸在线搜索、完整高 DPI 矩阵、人工听感与干净 Windows 机器仍待验收。完整记录见 `docs/MANUAL-ACCEPTANCE-v1.1.0.md`。这些有界解码与静音操作没有验证整首连续播放或实际听感。

开发启动使用 `.\run-dev.ps1`，可加 `-Configuration Release`。项目内在线运行时缺失时脚本给出提示，继续启动本地播放器。

运行时曲库只读取 SQLite 中用户实际导入的 Track，不注入 Mock、Sample、Demo 或随机歌曲。LRC、PNG、TXT 等非音频文件不会被当作歌曲导入。

## 安装项目内 FFmpeg

运行：

```powershell
.\setup-ffmpeg.ps1
```

强制重新解压和安装当前 GitHub Release 资产：

```powershell
.\setup-ffmpeg.ps1 -Force
```

脚本通过 GitHub API 查询 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) 的最新 Release，选择 Windows x64、LGPL、Shared 的稳定版本 ZIP。BtbN 是 FFmpeg 官方下载页面列出的 Windows 构建提供方之一，但仍是第三方 Windows 二进制构建提供方，并不是 FFmpeg 项目官方直接发布的 Windows 二进制。

本版本实际使用 `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`。选择 Shared 构建是为了保留动态链接运行时结构；它不仅需要 `ffmpeg.exe` 和 `ffprobe.exe`，还必须携带同目录的 `avcodec-*.dll`、`avformat-*.dll`、`avutil-*.dll`、`swresample-*.dll` 等 DLL。

安装后目录为：

```text
tools\ffmpeg\
  ffmpeg.exe
  ffprobe.exe
  *.dll
  metadata.json
  SOURCE.txt
  README.md
  licenses\
```

下载的 ZIP 保留在 `artifacts\downloads` 以便追溯。`metadata.json` 记录资产、版本、本地 SHA-256 和 GitHub API digest 验证结果。

## 安装项目内在线音乐网关

首次开发或发布前运行：

```powershell
.\setup-music-gateway.ps1
```

脚本从 [Node.js 官方发布目录](https://nodejs.org/dist/v24.21.0/) 下载 `node-v24.21.0-win-x64.zip`，比对固定 SHA-256 与官方 HTTPS `SHASUMS256.txt` 后安装到 `tools\node`。原始 ZIP 与官方校验表保留在 `artifacts\downloads`；完整 Node LICENSE、来源与各文件哈希保留在 `tools\node`。此检查没有验证校验表的签名密钥，`SOURCE.txt` 和 `metadata.json` 明确记录验证范围。

依赖安装通过该目录的 `node.exe` 和官方包内 npm 执行 `npm ci --omit=dev --ignore-scripts --no-audit --no-fund`，使用 `tools\music-gateway\package-lock.json` 中固定的版本、下载来源与 integrity。不会调用旧项目的 Node 18/14 打包脚本，也不修改全局 PATH。

```powershell
.\setup-music-gateway.ps1 -SkipDependencies # 只安装、验证内置 Node
.\setup-music-gateway.ps1 -Force            # 重新下载并安装固定版本
```

开发时应用管理 `tools\node\node.exe` 与 `tools\music-gateway\server.mjs`。发布时应用管理 `music-gateway\node\node.exe` 与 `music-gateway\server.mjs`。在线网关仅供本机应用使用；应用关闭后释放其子进程。已完成安装的发布包启动时无需再下载 Node 或执行 npm，因此断网首次启动仍可使用本地功能。安装脚本的首次下载与依赖安装需要网络。

四个平台的直接依赖来源与原始许可证见 `tools\music-gateway\LICENSES`。使用各平台服务时仍需遵守其账号、内容授权与使用条件。

酷我使用网关内独立 Web 适配器，其参考来源与上游公开资源摘要见 `tools\music-gateway\LICENSES\kuwo-web-source.json`。在线入口合计五个平台，四个第三方 SDK 加上酷我适配器。

## 真实播放技术验证

准备至少两首 MP3，然后运行：

```powershell
.\test-real-playback.ps1 -AudioDirectory "C:\路径\到\测试音频" -Volume 0.15
```

脚本复用正式 `FfmpegAudioPlayerService`，检查 FFprobe、PCM 数据、NAudio 默认设备初始化、播放位置、暂停、恢复、Seek、停止、切歌、快速切歌和 FFmpeg 子进程释放。结果写入：

```text
artifacts\test-reports\playback-verification.json
artifacts\test-reports\playback-verification.md
artifacts\test-reports\playback-verification.log
```

测试音频只从用户指定目录读取，不会被修改、复制到源码或打包进最终发布目录。技术验证成功只能证明 FFmpeg、PCM 和 NAudio 调用链工作，不能代替用户确认是否实际可听、是否存在爆音、杂音、延迟或切歌残音。

## 真实导入技术验证

```powershell
dotnet run --project .\tools\NekoPlayer.ImportVerifier\NekoPlayer.ImportVerifier.csproj -c Release -- --audio-directory "C:\路径\到\测试音频"
```

ImportVerifier 使用独立临时 SQLite 数据库和封面缓存，第一次导入验证新增记录与数据库重查，第二次导入验证重复文件只计入跳过且不产生重复 Track。临时数据库在退出时清理，报告保留在 `artifacts\test-reports\import-verification.*`，不会写入用户正式曲库。

导入完成提示遵循以下规则：新增成功时显示新增数量；部分失败时同时显示失败数量；全部重复时显示“没有新增歌曲”；取消时显示已完成数量；数据库或 UI 刷新未确认时不会显示完整成功。搜索词不会被偷偷清空，被当前搜索条件隐藏的新歌曲会给出明确提示。

## 大曲库性能验证

```powershell
dotnet run --project .\tools\NekoPlayer.LibraryVerifier\NekoPlayer.LibraryVerifier.csproj -c Release
```

验证器分别创建 500、1000、2000 条记录的独立临时 SQLite 数据库，测量查询、内存搜索、排序和单条收藏更新时间，并确认收藏一首不会修改其他 Track、移除最近播放不会删除 Track。临时数据库完成后删除，报告保留在 `artifacts\test-reports\library-performance.*`。报告记录真实耗时，但不使用脆弱的固定毫秒阈值，也不虚构 GUI 滚动帧率。

## 发布 win-x64

```powershell
.\publish-win-x64.ps1
```

发布脚本先检查 FFmpeg、内置 Node、网关源码与许可证，再依次执行还原、Release 构建、单元测试和 self-contained win-x64 发布。清理前验证完整目标路径位于 `artifacts` 内，只清理 `artifacts\publish\win-x64` 本次目标目录。运行时缺失或哈希不匹配时发布会失败并要求先运行对应安装脚本。

发布结构包含：

```text
artifacts\publish\win-x64\
  NekoPlayer.exe
  LICENSE
  README.md
  THIRD-PARTY-NOTICES.md
  runtime-manifest.json
  docs\
    MANUAL-ACCEPTANCE-v1.1.0.md
    RELEASE-NOTES-v1.1.0.md
  music-gateway\
    server.mjs
    package.json
    package-lock.json
    src\
    node_modules\
    LICENSES\
    node\
      node.exe
      LICENSE
      SOURCE.txt
      metadata.json
      SHASUMS256.txt
  ffmpeg\
    ffmpeg.exe
    ffprobe.exe
    *.dll
    metadata.json
    SOURCE.txt
    README.md
    licenses\
```

`runtime-manifest.json` 记录应用版本、网关锁文件哈希与发布文件的 SHA-256。发布脚本检查 EXE 文件版本、产品版本、图标和运行时完整性，并检查 Release 依赖和文件中不存在 `Avalonia.Diagnostics`。运行时复制来源限定为本项目的 FFmpeg、Node 和网关目录；测试音频、SDK 测试夹具、用户曲库、日志、Cookie 文件、`.env` 与凭据文件不进入发布包。原始下载 ZIP 和开发用 npm 不需要进入发布包。

## 用户数据

- 数据库：`%LocalAppData%\NekoPlayer\Data\nekoplayer.db`
- 配置：`%LocalAppData%\NekoPlayer\Config\settings.json`
- 封面缓存：`%LocalAppData%\NekoPlayer\Cache\Covers`
- 日志：`%LocalAppData%\NekoPlayer\Logs\nekoplayer-.log`
- 临时目录：`%LocalAppData%\NekoPlayer\Temp`

日志可能包含本地音乐文件路径，仅用于本机排错；公开分享前请检查隐私信息。

## 当前限制

- 技术导入、性能、网关和播放验证不能代替 GUI、高 DPI、视觉与人工听感验收，当前版本步骤见 `docs/MANUAL-ACCEPTANCE-v1.1.0.md`；发布说明中的记录只代表明确执行的检查
- 五个平台的在线搜索与媒体解析受上游接口和授权限制；接口失败会影响对应在线请求，本地播放继续可用
- 没有桌面歌词、系统级全局快捷键、均衡器、变速、无缝播放或安装包
- 歌词支持解析与高亮，但自动居中滚动和手动滚动暂停跟随仍需完善
- 有效真实封面按目标宽度在后台解码；默认 AppIcon 由应用生命周期共享一次，磁盘封面缓存仍保留原始嵌入图片编码
- 歌单数据支持顺序字段，但暂不提供拖拽排序

## 开源与许可证

NekoPlayer 原创代码采用 MIT License，详见 `LICENSE`。MIT License 只覆盖本项目原创代码，不覆盖 FFmpeg、BtbN 构建或其他第三方依赖。FFmpeg 的 LGPL Shared 选择不意味着自动履行所有许可证义务，发布者仍需核对所用构建和依赖的实际要求，详见 `THIRD-PARTY-NOTICES.md`。

创作者：梦怀殇
