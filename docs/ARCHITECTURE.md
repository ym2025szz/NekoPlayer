# NekoPlayer 架构

依赖方向为 `App -> Audio / Infrastructure -> Core`。Core 保存模型、接口、队列算法、LRC 解析与可测试工具；Infrastructure 负责 SQLite、配置、路径、标签和曲库；Audio 负责 FFMpegCore 管道、有限 PCM 缓冲、NAudio 输出与频谱；App 只编排服务并展示 Avalonia MVVM 界面。

音频链路：本地文件 → FFMpegCore/ffmpeg → `f32le / 48kHz / 双声道` → 有界 Channel → `BufferedWaveProvider` → `WaveOutEvent`。Seek 会先停止输出、取消并等待旧解码任务、清空缓冲，再从目标时间重新启动 FFmpeg。播放位置使用 NAudio 实际输出字节数与 Seek 起点计算，不只依赖墙上时钟。频谱从写入 NAudio 前的 Float32 PCM 抽样，不从扬声器回录。

FFmpeg 运行时采用项目内 Windows x64 LGPL Shared 构建。应用会检查 `ffmpeg.exe`、`ffprobe.exe` 和关键 Shared DLL，并以超时受控的 `ffmpeg -version` 进程验证运行时可以启动。正式发布优先使用 `AppContext.BaseDirectory\ffmpeg`，开发运行向上查找 `tools\ffmpeg`。

数据库使用短生命周期 `NekoPlayerDbContext`，由 `IDbContextFactory` 为每次操作创建，避免跨线程共享。配置使用 JSON 原子替换写入，损坏时记录日志并回退默认值。

## 导入数据流

文件选择/文件夹扫描 → 后台枚举与 Windows 路径规范化 → 批量查询已存在路径 → 后台 TagLib 与封面处理 → 20 秒超时的 FFprobe 分析 → 短生命周期 DbContext 保存 → 独立 DbContext 重新查询确认 → ViewModel 刷新 `_allTracks` → Avalonia UI 线程更新 `ObservableCollection` → 确认列表数据后生成结果提示。

导入状态使用单一 `ImportStage` 状态流：`Idle → Enumerating → ReadingMetadata → AnalyzingMedia → Saving → RefreshingLibrary → Completed`。取消进入 `Cancelled`，完整失败进入 `Failed`，不再依赖多个可能互相矛盾的布尔值判断当前阶段。`ImportProgress` 同时携带发现、处理、新增、更新、跳过、失败数量和当前文件名。

`MusicLibraryService` 通过 `SemaphoreSlim` 保证同一时间只有一个导入任务。目录递归、TagLib、封面解码/缓存和 FFprobe 不在 UI 线程执行；数据库上下文不会跨线程共享。取消令牌贯穿枚举、元数据、FFprobe 和数据库保存，并在单文件安全边界停止。普通取消不记录为错误，真实异常按文件记录并继续下一首。

运行时曲库唯一数据源是 SQLite 的 `Tracks` 表，不存在 Mock、Demo 或 Sample Track 注入。导入成功必须同时满足数据库保存、数据库重查和 ViewModel 列表刷新确认；如果当前搜索条件隐藏新增歌曲，保留搜索词并向用户说明，而不是偷偷清空过滤条件。

## v0.1.3 响应式状态

`ResponsiveLayout.Resolve` 是窗口布局的单一断点来源：内容宽度 `>= 1200` 为 `Wide`，`1000～1199` 为 `Standard`，`< 1000` 为 `Compact`。窗口 `SizeChanged` 只通过 90ms UI 定时器更新布局状态，不查询数据库、不重建 ViewModel，也不改变搜索词、队列或播放状态。Compact 将 220px 文字导航收缩为 78px 图标栏，并改用两行底部播放栏；主页面各自负责纵向滚动，顶栏和播放栏保持固定。

歌曲列表继续使用 Avalonia `ListBox` 默认虚拟化面板，没有替换成 `StackPanel`；封面由统一 `TrackCover` 控件按目标宽度异步解码。搜索保留 250ms 防抖，收藏事件只更新命中的 Track 实例和收藏集合，不重建完整曲库。

## 收藏状态同步

SQLite `Tracks.IsFavorite` 仍是唯一持久化真相。`MusicLibraryService.SetFavoriteAsync` 使用 `SemaphoreSlim` 串行保存，成功后由 `ITrackStateStore` 发布 `FavoriteStateChanged(TrackId, IsFavorite)`。ViewModel 在 Avalonia UI Dispatcher 上把同一 TrackId 同步到 `_allTracks`、曲库、收藏、最近播放、歌单、队列和当前播放项；`Track` 通过 `INotifyPropertyChanged` 刷新爱心。正式 `FavoriteButton` 继承 `ToggleButton`：未收藏使用独立轮廓 Geometry 与灰青色 `#8FAEB2`，已收藏使用独立实心 Geometry 与粉红色 `#FF5D7D`。Hover 时分别变为更亮灰青与更亮粉红，Pressed 只降低透明度，Disabled 保留当前图形和颜色；ToolTip 与 Automation 名称分别为“添加到喜欢”和“取消喜欢”。

取消喜欢只更新 `IsFavorite`，不删除 Track、文件、播放历史、歌单关系或队列，也不停止播放。Snackbar 的撤销动作保存真实 TrackId 和旧状态，再次调用同一持久化服务；新操作会替换旧撤销上下文，避免串线。

## 最近播放边界

最近播放读取独立 `PlaybackHistories` 表，并按 `PlayedAt` 倒序、每首歌只展示最新记录。单条移除会保存该 Track 的完整历史快照后只删除对应历史；撤销按原 Id、时间和位置恢复。批量清空只删除 `PlaybackHistories`，必须经确认层执行，可以不撤销。Track、收藏、歌单、队列和音乐文件均不受影响，本轮没有数据库结构迁移。

## Seek 预览与播放状态

自绘 `PlaybackSeekSlider` 绑定 `SeekSeconds` 预览值，播放器的 150ms `PositionChanged` 只在用户未拖动且没有待提交 Seek 时回写。PointerPressed 进入预览，轨道点击或拖动都只在 PointerReleased 提交最终目标；方向键通过 `SeekRequestCoordinator` 做 180ms 防抖。目标经 `SeekTarget.Clamp` 限制到合法范围，失败时恢复音频服务报告的真实位置并显示错误 Snackbar。

`FfmpegAudioPlayerService.SeekAsync` 明确进入 `Seeking`，停止输出、等待旧解码器退出、清空 PCM 缓冲并从目标重启 FFmpeg。进入 Seek 前是 `Playing` 时完成后继续播放，其他可 Seek 状态完成后保持 `Paused`。Seek 本身不调用 `RecordPlaybackAsync`，因此不增加播放次数、不新增最近播放。

`PlaybackActionMapper` 把状态映射为“点击后动作”：`Playing -> Pause`，`Paused/Stopped/Idle -> Play`，`Loading/Seeking/Buffering -> Busy`，`Error -> Retry`。按钮使用独立 PathIcon 和状态文字，不再显示静态“播放/暂停”混合字符。

## 页面状态、Snackbar 与线程边界

曲库、收藏和最近播放使用 `Loading / Empty / Content / Error` 状态；空收藏和空最近播放不会再显示整页空白。Snackbar 是窗口内非阻塞覆盖层，约 4 秒自动关闭，可选撤销，并以 `Success / Warning / Error` 区分颜色。事件、位置、频谱和收藏同步进入 UI 前统一切回 `Dispatcher.UIThread`；数据库上下文、FFmpeg 解码和防抖等待不占用 UI 线程。

## 临时大曲库验证

`NekoPlayer.LibraryVerifier` 分别创建 500、1000、2000 条虚拟 Track 的独立临时 SQLite 数据库，记录写入、查询、搜索、排序、单条收藏耗时，并验证收藏只修改目标行、移除历史不删除 Track。数据库在每个场景结束后删除，报告写入 `artifacts/test-reports/library-performance.*`。性能报告记录真实机器结果，不设置不稳定的固定毫秒门槛，也不声称无法自动测量的 GUI FPS。

## v0.1.4 GUI Seek 输入流

底部 Compact 与 Standard/Wide 播放栏复用同一个 `PlaybackSeekSlider`。真实 GUI 故障的根因是旧实现继承默认 Avalonia `Slider`：视觉控件虽然有 22px 高，但默认模板只有中间窄轨道参与命中，轨道上下的大部分点击直接落到播放栏外层 `Border`，因此 Slider 的 Tunnel/Bubble 处理器根本不在事件路由中。旧测试只覆盖坐标换算、ViewModel 与播放服务，没有覆盖 Windows 鼠标命中链，所以会出现自动测试通过而真实鼠标无效。

修复后的控件继承 `TemplatedControl` 并自行绘制轨道、进度和 Thumb，同时在完整 Bounds 上绘制透明命中面；它不再依赖默认 Slider 模板。控件通过 `AddHandler` 监听 Pointer 事件并使用 `handledEventsToo=true`，按下时捕获 Pointer 并进入预览；移动只更新 `Value/PreviewSeconds`，不启动 FFmpeg；松开时只触发一次 `SeekCompleted`，CaptureLost 则取消并恢复真实位置。轨道比例按控件本地 X、8px 边距后的有效宽度和 `Maximum` 计算并限制在 0～1。方向键、Shift+方向键、Home 和 End 也走同一最终提交入口。

ViewModel 中 `SeekSeconds` 是可见预览值，音频服务 `Position` 是真实播放位置。`IsUserSeeking` 或待提交 Seek 存在时，150ms 的位置通知只更新真实位置缓存，不覆盖预览；提交成功后以服务位置收敛，失败或取消时恢复真实位置。播放状态提交后继续 Playing，暂停状态提交后保持 Paused。

## Stop 与重新播放状态机

`StopAsync` 调用 `StopCoreAsync(false)`：取消并等待旧解码任务、停止输出、清空 PCM、把位置归零并进入 Stopped，同时保留 `_filePath`、Duration、CurrentTrack 和当前队列索引。`UnloadAsync` 才调用清空媒体语义，用于当前歌曲从曲库移除。`Stopped + CurrentTrack -> Play` 会创建新的 CancellationTokenSource、Channel、FFmpeg 解码会话和输出状态，从 0 开始播放；Stop 不等于 Dispose。

## 歌单选择与 PlaylistTrack 事务

歌单详情的歌曲选择器保存候选 Track、搜索词、已选 TrackId、既有成员和提交状态；跨层命令不依赖 `SelectedItems` 双向绑定。单曲/批量“添加到歌单”选择器保存源 TrackId 和目标 PlaylistId。`PlaylistService.AddTracksAsync` 在短生命周期 DbContext 事务内读取现有关系，按输入顺序追加 `SortOrder`，跳过重复、未知 Track，并返回新增/跳过数量。该流程不复制文件、不创建 Track、不改变收藏、历史或当前播放，保存后由 UI Dispatcher 刷新相关集合。

## 曲库多选与安全移除

本地音乐选择状态位于 ViewModel 的 TrackId 集合，不写入数据库；搜索下的全选只作用于当前过滤结果，刷新会清理已不存在的选择。确认层明确说明不会删除磁盘文件。`MusicLibraryService.RemoveFromLibraryAsync` 在数据库事务中删除选中 Track，EF 级联清理 `PlaylistTrack` 与 `PlaybackHistory`；ViewModel 同步移除队列项并重算序号。若移除当前播放或暂停歌曲，先 `UnloadAsync` 安全结束会话并清空当前项，不自动播放下一首。生产代码不调用 `File.Delete`。

## 队列、导航与图标

`QueueItemViewModel.DisplayIndex` 由当前内存队列顺序每次重建为 1-based 序号，不使用 Track.Id 或容器索引。“立即播放”只设置现有队列项为 Current 并加载播放，不重复插入、不清空后续项，当前高亮随索引更新。

导航外层 `Button.nav`、模板 `ContentPresenter` 和内部 `Grid.navContent` 均为 Stretch；图标、文字、选中条和空白都在同一 Button 内，Hover/Pressed/Selected 背景覆盖完整 46px 行。Compact 使用完整图标按钮面积。普通图标按钮为 40×40、主播放按钮为 48×48，PathIcon 默认为 20×20、`Stretch=Uniform` 的等价几何缩放并在容器中水平垂直居中；响应式布局只切换有限尺寸档位，不逐个处理 SizeChanged。

## 本地问候、UI Dispatcher 与数据保护

问候语由可注入 `TimeProvider` 计算：05:00～10:59 早上、11:00～12:59 中午、13:00～17:59 下午、18:00～22:59 晚上，其余为夜深。窗口启动时刷新，并用单一 UI Timer 每分钟检查一次；关闭时释放，不访问网络或定位。

数据库、播放、Timer 和服务事件进入可观察 UI 集合前统一切回 `Dispatcher.UIThread`。自动测试与验证器只使用临时 SQLite 和临时文件；真实 MP3 只读；正式数据库只允许只读快照；发布包排除测试音频。v1.0.0 没有数据库迁移。

## v1.0.0 默认封面边界

封面优先级为：有效歌曲内嵌图片生成的 `Track.CoverCachePath` → 可读取并可解码的现有封面缓存文件 → `avares://NekoPlayer/Assets/AppIcon.png`。程序集实际名称为 `NekoPlayer`，因此资源 URI 不使用项目目录名 `NekoPlayer.App`。`Track.CoverPath` 不存在于当前模型，现有字段 `Track.CoverCachePath` 只表示真实封面缓存来源；默认 AppIcon 属于 UI 回退，不写入 SQLite，也不复制到每首歌的缓存，更不会写入音频标签。

`TrackCoverProvider.Shared` 通过线程安全 `Lazy<Bitmap?>` 在应用生命周期内只解码一次 AppIcon，所有无封面 Track 共享该只读默认 Bitmap，任一页面都不得 Dispose 它。有效真实封面通过 `Task.Run` 在 UI 线程外读取和按目标宽度解码，返回后由 `Dispatcher.UIThread.InvokeAsync` 设置 `Image.Source`；每个 `TrackCover` 只拥有并释放自己的真实封面 Bitmap。路径为空、文件不存在、权限失败、格式无效、字节损坏或 Bitmap 解码异常都记录可恢复日志并返回共享默认图标。默认资源本身若加载失败，控件底层保留纯 Avalonia 矢量与渐变后备，不改变行高或阻止页面加载。

`TrackCover` 统一用于本地音乐/搜索结果、喜欢、最近播放、歌单详情、播放队列、正在播放页、歌曲选择器和 Compact/Standard/Wide 底部播放栏。默认图标使用 `Stretch=Uniform` 保持纵横比，真实封面使用 `UniformToFill`；圆角裁剪由同一控件处理。已有无封面歌曲不需要重新导入，页面绑定现有空 `CoverCachePath` 时会立即走 UI 回退。

自动 GUI 验证可仅对子进程设置 `NEKOPLAYER_DATA_ROOT`，让 `UserDataPaths` 使用显式绝对临时根目录；普通启动未设置该变量时仍使用 `%LocalAppData%\NekoPlayer`。该开关不修改系统环境变量，发布版用户数据语义不变，主要用于避免播放、收藏和歌单 GUI 回归污染正式 SQLite。

## v1.0.0 版本展示与发布资源

产品、包和信息版本为 `1.0.0`，程序集与 Windows FileVersion 为 `1.0.0.0`。面向用户的左下角与设置页只显示 `v1.0.0`，不显示版本名称、开发代号、阶段说明或 ProductVersion/FileVersion 双重信息；设置页继续保留“创作者：梦怀殇”。发布脚本检查 Windows 文件属性、FFmpeg Shared、`NekoPlayer.deps.json` 中不存在 `Avalonia.Diagnostics`，并验证 AppIcon 的 AvaloniaResource 声明。历史 v0.1.4 验收文档和技术章节保留，不改名冒充 v1.0.0。
